using BuildDuty.Core;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Creates <see cref="AIFunction"/> tools for the Copilot session that
/// provide access to build-duty work item data.
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
                "Get full details of a work item by ID, including signals and history."),

            AIFunctionFactory.Create(
                async (string state, int limit) =>
                {
                    WorkItemState? filter = state.ToLowerInvariant() switch
                    {
                        "unresolved" => WorkItemState.Unresolved,
                        "inprogress" => WorkItemState.InProgress,
                        "resolved" => WorkItemState.Resolved,
                        _ => null
                    };

                    var items = await store.ListAsync(filter, limit);
                    return items.Count == 0
                        ? "No work items found."
                        : string.Join("\n---\n", items.Select(FormatWorkItem));
                },
                "list_work_items",
                "List work items, optionally filtered by state (unresolved, inprogress, resolved)."),

            AIFunctionFactory.Create(
                async (string workItemId) =>
                {
                    var item = await store.LoadAsync(workItemId);
                    if (item is null)
                        return $"Work item '{workItemId}' not found.";
                    if (item.Signals.Count == 0)
                        return "No signals collected for this work item.";

                    return string.Join('\n', item.Signals.Select(s => $"- [{s.Type}] {s.Ref}"));
                },
                "get_signals",
                "Get the collected signals (pipeline URLs, issues, PRs) for a work item. Use the Azure DevOps MCP server to query build details from the URL.")
        ];
    }

    private static string FormatWorkItem(WorkItem item)
    {
        var lines = new List<string>
        {
            $"## {item.Title}",
            $"- **ID:** {item.Id}",
            $"- **State:** {item.State}",
            $"- **Correlation ID:** {item.CorrelationId ?? "(none)"}",
        };

        if (item.Signals.Count > 0)
        {
            lines.Add("- **Signals:**");
            foreach (var s in item.Signals)
                lines.Add($"  - [{s.Type}] {s.Ref}");
        }

        if (item.History.Count > 0)
        {
            lines.Add("- **History:**");
            foreach (var h in item.History)
                lines.Add($"  - {h.TimestampUtc:u} {h.Action}: {h.From} → {h.To} ({h.Note ?? "—"})");
        }

        return string.Join('\n', lines);
    }
}
