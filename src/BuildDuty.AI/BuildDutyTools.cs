using BuildDuty.Core;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Shared tools available to all AI sessions (scan and triage).
/// Provides read-only work item data access.
/// </summary>
public static class BuildDutyTools
{
    public static IReadOnlyList<AIFunction> Create(WorkItemStore store)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string workItemId) =>
                {
                    var item = await store.LoadAsync(workItemId);
                    return item is null
                        ? $"Work item '{workItemId}' not found."
                        : FormatWorkItem(item);
                },
                "get_work_item",
                "Get full details of a work item by ID, including sources and history."),

            AIFunctionFactory.Create(
                async (string? status, int? limit) =>
                {
                    bool? resolved = status?.ToLowerInvariant() switch
                    {
                        "resolved" => true,
                        "unresolved" or "active" => false,
                        _ => null
                    };

                    var items = await store.ListAsync(resolved, limit ?? 200);
                    if (items.Count == 0)
                    {
                        return "No work items found.";
                    }
                    return string.Join("\n", items.Select(i =>
                        $"- {i.Id} [{i.Status}] {i.Title} (corr: {i.CorrelationId ?? "none"})"));
                },
                "list_work_items",
                "List tracked work items, optionally filtered by status ('resolved' or 'unresolved') and limited to a count."),

            AIFunctionFactory.Create(
                (string id) => store.Exists(id)
                    ? $"Work item '{id}' exists."
                    : $"Work item '{id}' does not exist.",
                "work_item_exists",
                "Check whether a work item with the given ID is already tracked."),
        ];
    }

    internal static string FormatWorkItem(WorkItem item)
    {
        var lines = new List<string>
        {
            $"## {item.Title}",
            $"- **ID:** {item.Id}",
            $"- **Status:** {item.Status}",
            $"- **Correlation ID:** {item.CorrelationId ?? "(none)"}",
        };

        if (!string.IsNullOrWhiteSpace(item.Summary))
        {
            lines.Add($"- **Summary:** {item.Summary}");
        }

        if (item.LinkedItems.Count > 0)
        {
            lines.Add($"- **Linked:** {string.Join(", ", item.LinkedItems)}");
        }

        if (item.Sources.Count > 0)
        {
            lines.Add("- **Sources:**");
            foreach (var s in item.Sources)
            {
                lines.Add($"  - [{s.Type}] {s.Ref}");
            }
        }

        if (item.History.Count > 0)
        {
            lines.Add("- **History:**");
            foreach (var h in item.History)
            {
                lines.Add($"  - {h.TimestampUtc:u} {h.Action}: {h.From} → {h.To} ({h.Note ?? "—"})");
            }
        }

        return string.Join('\n', lines);
    }
}
