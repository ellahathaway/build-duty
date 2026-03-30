using BuildDuty.Core;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

/// <summary>
/// Tools and skills specific to AI triage sessions.
/// </summary>
public static class TriageTools
{
    /// <summary>Skills used for AI triage sessions.</summary>
    public static readonly IReadOnlyList<string> Skills =
    [
        "skills/summarize",
        "skills/diagnose-build-break",
        "skills/cluster-incidents",
        "skills/suggest-next-actions",
    ];

    public static IReadOnlyList<AIFunction> Create(WorkItemStore store)
    {
        return
        [
            AIFunctionFactory.Create(
                async (string workItemId) =>
                {
                    var item = await store.LoadAsync(workItemId);
                    if (item is null)
                    {
                        return $"Work item '{workItemId}' not found.";
                    }
                    if (item.Sources.Count == 0)
                    {
                        return "No sources collected for this work item.";
                    }
                    return string.Join('\n', item.Sources.Select(s => $"- [{s.Type}] {s.Ref}"));
                },
                "get_sources",
                "Get the collected sources (pipeline URLs, issues, PRs) for a work item. Use the Azure DevOps MCP server to query build details from the URL."),
        ];
    }
}
