using BuildDuty.Core;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Tools for Step 3: AI triage of work items.
/// Updates statuses, cross-references related items, and resolves stale items.
/// </summary>
public static class WorkItemTriageTools
{
    /// <summary>Skills used for AI work item triage sessions.</summary>
    public static readonly IReadOnlyList<string> Skills =
    [
        "skills/triage",
    ];

    public static IReadOnlyList<AIFunction> Create(WorkItemStore store)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string id, string reason) =>
                {
                    var item = await store.LoadAsync(id);
                    if (item is null)
                        return $"Work item '{id}' not found.";
                    if (item.IsResolved)
                        return $"Work item '{id}' is already resolved (status: {item.Status}).";

                    item.SetStatus("resolved", reason);
                    item.TriagedAtUtc = DateTime.UtcNow;
                    item.State = "stable";
                    await store.SaveAsync(item);
                    return $"Resolved work item '{id}': {reason}";
                },
                "resolve_work_item",
                "Resolve an existing work item by setting its status to resolved with a reason."),

            AIFunctionFactory.Create(
                async (string id, string status) =>
                {
                    var item = await store.LoadAsync(id);
                    if (item is null)
                        return $"Work item '{id}' not found.";

                    var old = item.Status;
                    item.SetStatus(status);
                    item.TriagedAtUtc = DateTime.UtcNow;
                    item.State = "stable";
                    await store.SaveAsync(item);
                    return $"Updated '{id}' status: {old} → {status}";
                },
                "update_work_item_status",
                "Update the type-specific status of a work item (e.g. 'needs-review', 'tracked', 'test-failures')."),

            AIFunctionFactory.Create(
                async (string id, string linkedId) =>
                {
                    var item = await store.LoadAsync(id);
                    if (item is null)
                        return $"Work item '{id}' not found.";

                    var linked = await store.LoadAsync(linkedId);
                    if (linked is null)
                        return $"Work item '{linkedId}' not found.";

                    if (!item.LinkedItems.Contains(linkedId))
                    {
                        item.LinkedItems.Add(linkedId);
                        item.TriagedAtUtc = DateTime.UtcNow;
                        await store.SaveAsync(item);
                    }
                    if (!linked.LinkedItems.Contains(id))
                    {
                        linked.LinkedItems.Add(id);
                        linked.TriagedAtUtc = DateTime.UtcNow;
                        await store.SaveAsync(linked);
                    }

                    return $"Linked '{id}' ↔ '{linkedId}'.";
                },
                "link_work_items",
                "Create a bidirectional link between two work items (e.g. pipeline failure ↔ related PR)."),
        ];
    }
}

/// <summary>
/// Tools for Step 2: AI summarization of work items.
/// Provides deterministic pipeline failure lookups alongside the summary setter.
/// </summary>
public static class SummarizeTools
{
    /// <summary>Skills used for AI summarization sessions.</summary>
    public static readonly IReadOnlyList<string> Skills =
    [
        "skills/summarize",
    ];

    public static IReadOnlyList<AIFunction> Create(WorkItemStore store)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string id, string summary) =>
                {
                    var item = await store.LoadAsync(id);
                    if (item is null)
                        return $"Work item '{id}' not found.";

                    item.Summary = summary;
                    item.SummarizedAtUtc = DateTime.UtcNow;
                    await store.SaveAsync(item);
                    return $"Set summary for '{id}'.";
                },
                "set_work_item_summary",
                "Set a brief summary describing the work item's source (build failure reason, issue description, PR changes)."),

            AIFunctionFactory.Create(
                async (string buildUrl, int logId, int? tailLines = null) =>
                {
                    var parsed = AzureDevOpsBuildClient.ParseBuildUrl(buildUrl);
                    if (parsed is null)
                        return $"Could not parse build URL: {buildUrl}";

                    var (orgUrl, project, buildId) = parsed.Value;
                    return await AzureDevOpsBuildClient.GetTaskLogAsync(
                        orgUrl, project, buildId, logId, tailLines ?? 50);
                },
                "get_task_log",
                "Fetch the tail of a build task log by log ID from the failure details. Returns the last N lines (default 50). ALWAYS call this for each failed task to get full error context."),
        ];
    }
}
