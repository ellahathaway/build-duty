using BuildDuty.Core;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Tools for Phase 3: AI correlation of work items.
/// Handles status updates and cross-referencing — summaries are handled in Phase 4.
/// </summary>
public static class CorrelationTools
{
    /// <summary>Skills used for AI correlation sessions.</summary>
    public static readonly IReadOnlyList<string> Skills =
    [
        "skills/correlate-signals",
    ];

    public static IReadOnlyList<AIFunction> Create(WorkItemStore store)
    {
        return
        [
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
/// Tools for Phase 4: AI summarization of work items.
/// Fetches source details (build logs, issue body, PR details) and writes summaries.
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
        ];
    }
}
