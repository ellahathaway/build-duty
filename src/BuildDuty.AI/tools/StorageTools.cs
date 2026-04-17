using System.ComponentModel;
using System.Text.Json;
using BuildDuty.Core;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI.Tools;

public class StorageTools
{
    private readonly IStorageProvider _storageProvider;

    public StorageTools(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    public ICollection<AIFunction> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(
                async (
                    [Description("Filter by work item state: 'unresolved', 'resolved', or 'all'. Defaults to all")] string state = "all") =>
                {
                    var workItems = (await _storageProvider.GetWorkItemsAsync())
                        .Where(wi => state switch
                        {
                            "resolved" => wi.Resolved,
                            "unresolved" => !wi.Resolved,
                            "all" => true,
                            _ => throw new ArgumentException($"Invalid state filter: {state}. Must be 'unresolved', 'resolved', or 'all'."),
                        })
                        .Select(wi => new { wi.Id, wi.Resolved })
                        .ToList();

                    return workItems;
                },
                "list_work_items",
                "List all work items. Filter by state: 'unresolved', 'resolved', or 'all'."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the triage run")] string triageRunId) =>
                {
                    var workItems = (await _storageProvider.GetWorkItemsAsync())
                        .Where(wi => wi.LastTriageId == triageRunId)
                        .Select(wi => new { wi.Id, wi.Resolved })
                        .ToList();

                    return workItems;
                },
                "list_work_items_for_triage",
                "Get work items whose last triage ID matches the specified triage run."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the triage run")] string triageRunId,
                    [Description("Filter by linked status: 'linked', 'unlinked', or 'all'. Defaults to all")] string linkedStatus = "all") =>
                {
                    var triageRun = await _storageProvider.GetTriageRunAsync(triageRunId);
                    var workItems = await _storageProvider.GetWorkItemsAsync();

                    var result = new List<object>();
                    foreach (var signalId in triageRun.SignalIds)
                    {
                        var signal = await _storageProvider.GetSignalAsync(signalId);
                        var changed = signal.Analyses
                            .Where(a => a.LastTriageId == triageRunId)
                            .Where(a => linkedStatus switch
                            {
                                "linked" => workItems
                                    .Any(wi => wi.LinkedAnalyses.Any(la => la.SignalId == signalId && la.AnalysisIds.Contains(a.Id))),
                                "unlinked" => !workItems
                                    .Any(wi => wi.LinkedAnalyses.Any(la => la.SignalId == signalId && la.AnalysisIds.Contains(a.Id))),
                                "all" => true,
                                _ => throw new ArgumentException($"Invalid linkedStatus filter: {linkedStatus}. Must be 'linked', 'unlinked', or 'all'."),
                            })
                            .Select(a => new { a.Id, a.Status })
                            .ToList();

                        if (changed.Count > 0)
                        {
                            result.Add(new { SignalId = signalId, Analyses = changed });
                        }
                    }

                    return result;
                },
                "list_analyses_for_triage",
                "List analyses that were created, updated, or resolved during the specified triage run. Returns entries of { signalId, analyses[] } where each analysis has id and status. Filter on linked, unlinked, or all analyses"),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the signal")] string signalId,
                    [Description("The ID of the analysis")] string analysisId) =>
                {
                    var workItems = await _storageProvider.GetWorkItemsAsync();
                    return workItems
                        .Where(wi => wi.LinkedAnalyses.Any(la => la.SignalId == signalId && la.AnalysisIds.Contains(analysisId)))
                        .Select(wi => new { wi.Id, wi.Resolved })
                        .ToList();
                },
                "get_work_items_for_analysis",
                "Get work items linked to a specific analysis. Returns a list of { id, resolved }."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the signal")] string signalId) =>
                {
                    return await _storageProvider.GetSignalAsync(signalId);
                },
                "get_signal",
                "Get a signal by ID."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the work item")] string workItemId) =>
                {
                    return await _storageProvider.GetWorkItemAsync(workItemId);
                },
                "get_work_item",
                "Get a single work item by ID."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the signal")] string signalId,
                    [Description("The ID of the analysis")] string analysisId) =>
                {
                    var signal = await _storageProvider.GetSignalAsync(signalId);
                    return signal.Analyses.FirstOrDefault(a => a.Id == analysisId)
                        ?? throw new InvalidOperationException($"Analysis '{analysisId}' not found on signal '{signalId}'.");
                },
                "get_analysis",
                "Get a specific analysis entry from a signal."),

            AIFunctionFactory.Create(
                async (
                    [Description("The path to the JSON file")] string filePath,
                    [Description("The dot path to the value")] string path) =>
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(filePath);
                        var root = JsonSerializer.Deserialize<JsonElement>(json);

                        var found = TryGetByPath(root, path, out var value);
                        return JsonSerializer.SerializeToElement(new
                        {
                            FilePath = filePath,
                            Path = path,
                            Found = found,
                            Value = found ? value : JsonSerializer.SerializeToElement<object?>(null),
                            Error = (string?)null,
                        });
                    }
                    catch (Exception ex)
                    {
                        return JsonSerializer.SerializeToElement(new
                        {
                            FilePath = filePath,
                            Path = path,
                            Found = false,
                            Value = JsonSerializer.SerializeToElement<object?>(null),
                            Error = $"Failed to read JSON value from '{filePath}': {ex.Message}",
                        });
                    }
                },
                "get_json_value",
                "Get a value from a JSON file by dot path (supports object properties and array indexes like Records.0.Id)."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the triage run")] string triageId,
                    [Description("The summary of the work item")] string summary,
                    [Description("The linked analyses: list of { signalId, analysisIds[] } objects")] List<LinkedAnalysis> linkedAnalyses,
                    [Description("The issue signature of the work item")] string issueSignature) =>
                {
                    var workItem = new WorkItem
                    {
                        Id = IdGenerator.NewWorkItemId(),
                        Summary = string.IsNullOrWhiteSpace(summary) ? null : summary,
                        IssueSignature = string.IsNullOrWhiteSpace(issueSignature) ? null : issueSignature,
                        LinkedAnalyses = linkedAnalyses,
                        LastTriageId = triageId,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    };

                    await _storageProvider.SaveWorkItemAsync(workItem);

                    return workItem;
                },
                "create_work_item",
                "Creates a new work item."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the triage run")] string triageId,
                    [Description("The ID of the work item")] string workItemId,
                    [Description("The ID of the signal")] string signalId,
                    [Description("The analysis IDs on the signal that correlate with this work item")] List<string> analysisIds) =>
                {
                    var workItem = await _storageProvider.GetWorkItemAsync(workItemId);
                    var existing = workItem.LinkedAnalyses.FirstOrDefault(la => la.SignalId == signalId);

                    if (existing is not null)
                    {
                        var merged = existing.AnalysisIds.Union(analysisIds, StringComparer.Ordinal).ToList();
                        workItem.LinkedAnalyses.Remove(existing);
                        workItem.LinkedAnalyses.Add(new LinkedAnalysis(signalId, merged));
                    }
                    else
                    {
                        workItem.LinkedAnalyses.Add(new LinkedAnalysis(signalId, analysisIds));
                    }

                    workItem.LastTriageId = triageId;
                    workItem.UpdatedAt = DateTime.UtcNow;
                    await _storageProvider.SaveWorkItemAsync(workItem);
                    return "linked";
                },
                "link_analysis_to_work_item",
                "Link specific signal analyses to a work item. Merges analysis IDs if the signal is already linked."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the triage run")] string triageId,
                    [Description("The ID of the work item")] string workItemId,
                    [Description("The ID of the signal")] string signalId,
                    [Description("The ID of the analysis to unlink")] string analysisId) =>
                {
                    var workItem = await _storageProvider.GetWorkItemAsync(workItemId);
                    var existing = workItem.LinkedAnalyses.FirstOrDefault(la => la.SignalId == signalId);

                    if (existing is null || string.IsNullOrWhiteSpace(analysisId) || !existing.AnalysisIds.Contains(analysisId))
                    {
                        return "cannot unlink - analysis not found in work item";
                    }

                    var remaining = existing.AnalysisIds.Except(new[] { analysisId }, StringComparer.Ordinal).ToList();
                    workItem.LinkedAnalyses.Remove(existing);
                    if (remaining.Count > 0)
                    {
                        workItem.LinkedAnalyses.Add(new LinkedAnalysis(signalId, remaining));
                    }
                    else
                    {
                        // No analyses remain linked for this signal, so it's fully unlinked from the work item.
                    }

                    workItem.LastTriageId = triageId;
                    workItem.UpdatedAt = DateTime.UtcNow;
                    await _storageProvider.SaveWorkItemAsync(workItem);
                    return "unlinked";
                },
                "unlink_analysis_from_work_item",
                "Unlink specific signal analyses from a work item. If the signal has no remaining analyses, it is fully unlinked."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the triage run")] string triageId,
                    [Description("The ID of the work item")] string workItemId,
                    [Description("The summary of the work item")] string? summary,
                    [Description("The issue signature of the work item")] string? issueSignature,
                    [Description("The correlation rationale of the work item")] string? correlationRationale,
                    [Description("The resolution criteria of the work item")] string? resolutionCriteria,
                    [Description("The suggested next action for the work item")] string? suggestedNextAction) =>
                {
                    var workItem = await _storageProvider.GetWorkItemAsync(workItemId);

                    if (!string.IsNullOrWhiteSpace(summary))
                    {
                        workItem.Summary = summary;
                    }

                    if (!string.IsNullOrWhiteSpace(issueSignature))
                    {
                        workItem.IssueSignature = issueSignature;
                    }

                    workItem.LastTriageId = triageId;
                    workItem.UpdatedAt = DateTime.UtcNow;
                    await _storageProvider.SaveWorkItemAsync(workItem);
                    return "updated";
                },
                "update_work_item",
                "Update metadata on an existing work item (summary/signature/rationale/resolution criteria/suggested next action)."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the work item")] string workItemId,
                    [Description("The reason for resolving the work item")] string resolutionReason) =>
                {
                    var workItem = await _storageProvider.GetWorkItemAsync(workItemId);

                    if (workItem.Resolved)
                    {
                        return "cannot resolve - work item is already resolved";
                    }

                    workItem.Resolved = true;
                    workItem.ResolvedAt = DateTime.UtcNow;
                    workItem.UpdatedAt = DateTime.UtcNow;
                    await _storageProvider.SaveWorkItemAsync(workItem);

                    return "resolved";
                },
                "resolve_work_item",
                "Mark an existing work item resolved with an explicit reason."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the triage run")] string triageId,
                    [Description("The ID of the signal")] string signalId,
                    [Description("The data related to the signal analysis, in JSON format")] JsonElement analysisData,
                    [Description("The string analysis of the data")] string analysis) =>
                {
                    var signal = await _storageProvider.GetSignalAsync(signalId);

                    var signalAnalysis = new SignalAnalysis(analysisData, analysis) { LastTriageId = triageId };
                    signal.Analyses.Add(signalAnalysis);
                    await _storageProvider.SaveSignalAsync(signal);

                    return signalAnalysis.Id;
                },
                "create_signal_analysis",
                "Persist a single analysis to a signal record. Returns the generated analysis ID."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the triage run")] string triageId,
                    [Description("The ID of the signal")] string signalId,
                    [Description("The ID of the analysis to resolve")] string analysisId,
                    [Description("The criteria that were met for resolution (e.g. pipeline succeeded, issue closed via PR #123, fix merged)")] string resolutionCriteria) =>
                {
                    var signal = await _storageProvider.GetSignalAsync(signalId);
                    var existing = signal.Analyses.FirstOrDefault(a => a.Id == analysisId);

                    if (existing is null)
                    {
                        return "cannot resolve - analysis not found on signal";
                    }

                    var index = signal.Analyses.IndexOf(existing);
                    signal.Analyses[index] = existing with { Status = AnalysisStatus.Resolved, ResolutionCriteria = resolutionCriteria, LastTriageId = triageId };
                    await _storageProvider.SaveSignalAsync(signal);
                    await UpdateLinkedWorkItemTriageIdAsync(signalId, analysisId, triageId);
                    return "resolved";
                },
                "resolve_signal_analysis",
                "Mark an analysis as resolved. Use when the issue is no longer active (pipeline recovered, issue closed, fix merged) OR when the analysis has been superseded by a more accurate one. The analysis is preserved for provenance. Provide the resolution criteria that were met."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the triage run")] string triageId,
                    [Description("The ID of the signal")] string signalId,
                    [Description("The ID of the analysis to update")] string analysisId,
                    [Description("The updated data related to the signal analysis, in JSON format. Omit to keep existing data.")] JsonElement? analysisData = null,
                    [Description("The updated string analysis. Omit to keep existing text.")] string? analysis = null) =>
                {
                    var signal = await _storageProvider.GetSignalAsync(signalId);
                    var existing = signal.Analyses.FirstOrDefault(a => a.Id == analysisId);

                    if (existing is null)
                    {
                        return "cannot update - analysis not found on signal";
                    }

                    var updatedSignalAnalysis = new SignalAnalysis(
                        existing.Id,
                        analysisData ?? existing.RelevantInfo,
                        analysis ?? existing.Analysis,
                        AnalysisStatus.Updated,
                        existing.ResolutionCriteria,
                        triageId);

                    var index = signal.Analyses.IndexOf(existing);
                    signal.Analyses[index] = updatedSignalAnalysis;
                    await _storageProvider.SaveSignalAsync(signal);
                    await UpdateLinkedWorkItemTriageIdAsync(signalId, analysisId, triageId);
                    return "updated";
                },
                "update_signal_analysis",
                "Update an existing analysis on a signal. Omit analysisData and analysis to keep existing content while stamping the current triage run. Sets status to Updated."),
        ];
    }

    private async Task UpdateLinkedWorkItemTriageIdAsync(string signalId, string analysisId, string triageId)
    {
        var workItems = await _storageProvider.GetWorkItemsAsync();
        foreach (var wi in workItems)
        {
            if (wi.LinkedAnalyses.Any(la => la.SignalId == signalId && la.AnalysisIds.Contains(analysisId)))
            {
                wi.LastTriageId = triageId;
                wi.UpdatedAt = DateTime.UtcNow;
                await _storageProvider.SaveWorkItemAsync(wi);
            }
        }
    }

    private static bool TryGetByPath(JsonElement source, string path, out JsonElement value)
    {
        value = default;
        var current = source;

        if (string.IsNullOrWhiteSpace(path))
        {
            value = current.Clone();
            return true;
        }

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
            {
                if (index < 0 || index >= current.GetArrayLength())
                {
                    return false;
                }

                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            JsonElement? next = null;
            foreach (var property in current.EnumerateObject())
            {
                if (string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = property.Value;
                    break;
                }
            }

            if (next is null)
            {
                return false;
            }

            current = next.Value;
        }

        value = current.Clone();
        return true;
    }
}
