using BuildDuty.Core;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Tools for AI triage sessions.
/// The AI receives pre-collected signals and uses these tools to create/resolve work items.
/// </summary>
public static class ScanTools
{
    /// <summary>Skills used for AI scanning sessions.</summary>
    public static readonly IReadOnlyList<string> Skills =
    [
        "skills/scan-signals",
    ];

    public static IReadOnlyList<AIFunction> Create(WorkItemStore store)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string id, string title, string correlationId, string signalType, string signalRef) =>
                {
                    if (store.Exists(id))
                        return $"Work item '{id}' already exists — skipped.";

                    var item = new WorkItem
                    {
                        Id = id,
                        Status = "new",
                        Title = title,
                        CorrelationId = correlationId,
                        Signals = [new SignalReference { Type = signalType, Ref = signalRef }]
                    };

                    await store.SaveAsync(item);
                    return $"Created work item '{id}'.";
                },
                "create_work_item",
                "Create a new tracked work item with the given ID, title, correlation ID, signal type, and signal reference URL."),

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
        ];
    }
}
