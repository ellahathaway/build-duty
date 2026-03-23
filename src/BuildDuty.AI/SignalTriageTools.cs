using BuildDuty.Core;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Tools for Step 3: AI triage of collected signals.
/// Creates/resolves work items, updates statuses, and cross-references related items.
/// </summary>
public static class SignalTriageTools
{
    /// <summary>Skills used for AI signal triage sessions.</summary>
    public static readonly IReadOnlyList<string> Skills =
    [
        "skills/triage-signals",
    ];

    public static IReadOnlyList<AIFunction> Create(WorkItemStore store)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string id, string title, string correlationId, string signalType, string signalRef, string? stageFilters = null) =>
                {
                    if (store.Exists(id))
                        return $"Work item '{id}' already exists — skipped.";

                    var metadata = string.IsNullOrWhiteSpace(stageFilters)
                        ? null
                        : new Dictionary<string, string> { ["stageFilters"] = stageFilters };

                    var item = new WorkItem
                    {
                        Id = id,
                        Status = "new",
                        Title = title,
                        CorrelationId = correlationId,
                        Signals = [new SignalReference { Type = signalType, Ref = signalRef, Metadata = metadata }]
                    };

                    await store.SaveAsync(item);
                    return $"Created work item '{id}'.";
                },
                "create_work_item",
                "Create a new tracked work item with the given ID, title, correlation ID, signal type, signal reference URL, and optional stageFilters."),

            AIFunctionFactory.Create(
                async (string id, string reason) =>
                {
                    var item = await store.LoadAsync(id);
                    if (item is null)
                        return $"Work item '{id}' not found.";
                    if (item.IsResolved)
                        return $"Work item '{id}' is already resolved (status: {item.Status}).";

                    item.SetStatus("resolved", reason);
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
                        await store.SaveAsync(item);
                    }
                    if (!linked.LinkedItems.Contains(id))
                    {
                        linked.LinkedItems.Add(id);
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
                    await store.SaveAsync(item);
                    return $"Set summary for '{id}'.";
                },
                "set_work_item_summary",
                "Set a brief summary describing the work item's source (build failure reason, issue description, PR changes)."),

            AIFunctionFactory.Create(
                async (string buildUrl, string? stageFilters = null) =>
                {
                    var parsed = AzureDevOpsBuildClient.ParseBuildUrl(buildUrl);
                    if (parsed is null)
                        return $"Could not parse build URL: {buildUrl}";

                    var (orgUrl, project, buildId) = parsed.Value;
                    var info = await AzureDevOpsBuildClient.GetFailedTasksAsync(
                        orgUrl, project, buildId, stageFilters);

                    if (info.Error is not null)
                        return info.Error;

                    if (info.FailedTasks.Count == 0)
                        return "No failed tasks found (or all were filtered out by stage/job patterns).";

                    var lines = info.FailedTasks.Select(t =>
                    {
                        var path = string.Join(" > ",
                            new[] { t.StageName, t.JobName, t.TaskName }.Where(s => s is not null));
                        var errors = t.ErrorMessages.Count > 0
                            ? "\n  Errors:\n    " + string.Join("\n    ", t.ErrorMessages)
                            : "";
                        var logRef = t.LogId.HasValue ? $" [logId={t.LogId}]" : "";
                        return $"- {path}{logRef}{errors}";
                    });

                    return $"Failed tasks ({info.FailedTasks.Count}):\n{string.Join("\n", lines)}";
                },
                "get_pipeline_failures",
                "Get failed tasks from an ADO pipeline build. Returns task names, stage/job hierarchy, log IDs, and inline error messages. Pass the build URL and optional stageFilters string."),

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
                "Fetch the tail of a build task log by log ID. Use logId from get_pipeline_failures. Returns the last N lines (default 50)."),
        ];
    }
}
