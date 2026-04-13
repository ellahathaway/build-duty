using BuildDuty.Core;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Maestro.Common;

namespace BuildDuty.AI;

public class StorageTools
{
    private readonly IStorageProvider _storageProvider;

    public StorageTools(
        IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    public ICollection<AIFunction> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(
                async () =>
                {
                    return await _storageProvider.GetWorkItemsAsync();
                },
                "list_work_items",
                "List all work items as JSON."),

            AIFunctionFactory.Create(
                async (string workItemId) =>
                {
                    return await _storageProvider.GetWorkItemAsync(workItemId);
                },
                "get_work_item",
                "Get full work item details by ID as JSON."),

            AIFunctionFactory.Create(
                async (string signalId) =>
                {
                    return await _storageProvider.GetSignalAsync(signalId);
                },
                "get_signal",
                "Get full signal details by ID as JSON."),

            AIFunctionFactory.Create(
                async (string signalId, JsonElement selectors) =>
                {
                    var signal = await _storageProvider.GetSignalAsync(signalId);
                    return SelectFields(JsonSerializer.SerializeToElement(signal), selectors);
                },
                "select_signal_fields",
                "Select fields from a signal JSON using selectors as an object map of outputName:path or an array of paths."),

            AIFunctionFactory.Create(
                async (string filePath, string path) =>
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
                    string triageId,
                    string summary,
                    List<string> signalIds,
                    string issueSignature,
                    string correlationRationale,
                    string resolutionCriteria) =>
                {
                    var distinctSignalIds = signalIds
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    var workItem = new WorkItem
                    {
                        Id = IdGenerator.NewWorkItemId(),
                        TriageId = triageId,
                        Summary = string.IsNullOrWhiteSpace(summary) ? null : summary,
                        IssueSignature = string.IsNullOrWhiteSpace(issueSignature) ? null : issueSignature,
                        CorrelationRationale = string.IsNullOrWhiteSpace(correlationRationale) ? null : correlationRationale,
                        ResolutionCriteria = string.IsNullOrWhiteSpace(resolutionCriteria) ? null : resolutionCriteria,
                        SignalIds = distinctSignalIds,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    };

                    await _storageProvider.SaveWorkItemAsync(workItem);

                    foreach (var signalId in distinctSignalIds)
                    {
                        var signal = await _storageProvider.GetSignalAsync(signalId);
                        if (!signal.WorkItemIds.Contains(workItem.Id, StringComparer.Ordinal))
                        {
                            signal.WorkItemIds.Add(workItem.Id);
                            await _storageProvider.SaveSignalAsync(signal);
                        }
                    }

                    return workItem;
                },
                "create_work_item",
                "Creates a new work item with incident metadata and related signal IDs; links both sides. Use when a new issue should be tracked."),

            AIFunctionFactory.Create(
                async (string workItemId, string signalId) =>
                {
                    var workItem = await _storageProvider.GetWorkItemAsync(workItemId);
                    var signal = await _storageProvider.GetSignalAsync(signalId);

                    if (!workItem.SignalIds.Contains(signalId, StringComparer.Ordinal))
                    {
                        workItem.SignalIds.Add(signalId);
                        workItem.UpdatedAt = DateTime.UtcNow;
                        await _storageProvider.SaveWorkItemAsync(workItem);
                    }

                    if (!signal.WorkItemIds.Contains(workItemId, StringComparer.Ordinal))
                    {
                        signal.WorkItemIds.Add(workItemId);
                        await _storageProvider.SaveSignalAsync(signal);
                    }

                    return workItem;
                },
                "link_signal_to_work_item",
                "Link an existing signal to an existing work item and persist both sides."),

            AIFunctionFactory.Create(
                async (
                    string workItemId,
                    string? summary,
                    string? issueSignature,
                    string? correlationRationale,
                    string? resolutionCriteria) =>
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

                    if (!string.IsNullOrWhiteSpace(correlationRationale))
                    {
                        workItem.CorrelationRationale = correlationRationale;
                    }

                    if (!string.IsNullOrWhiteSpace(resolutionCriteria))
                    {
                        workItem.ResolutionCriteria = resolutionCriteria;
                    }

                    workItem.UpdatedAt = DateTime.UtcNow;
                    await _storageProvider.SaveWorkItemAsync(workItem);
                    return workItem;
                },
                "update_work_item",
                "Update metadata on an existing work item (summary/signature/rationale/resolution criteria)."),

            AIFunctionFactory.Create(
                async (string workItemId, string resolutionReason) =>
                {
                    var workItem = await _storageProvider.GetWorkItemAsync(workItemId);
                    workItem.Resolved = true;
                    workItem.ResolutionReason = resolutionReason;
                    workItem.ResolvedAt = DateTime.UtcNow;
                    workItem.UpdatedAt = DateTime.UtcNow;
                    await _storageProvider.SaveWorkItemAsync(workItem);
                    return workItem;
                },
                "resolve_work_item",
                "Mark an existing work item resolved with an explicit reason."),

            AIFunctionFactory.Create(
                async (string workItemId, string reopenReason) =>
                {
                    var workItem = await _storageProvider.GetWorkItemAsync(workItemId);
                    workItem.Resolved = false;
                    workItem.ResolutionReason = string.IsNullOrWhiteSpace(reopenReason)
                        ? null
                        : $"Reopened: {reopenReason}";
                    workItem.ResolvedAt = null;
                    workItem.UpdatedAt = DateTime.UtcNow;
                    await _storageProvider.SaveWorkItemAsync(workItem);
                    return workItem;
                },
                "reopen_work_item",
                "Reopen a previously resolved work item when new evidence indicates the issue is still active."),

            AIFunctionFactory.Create(
                async (string signalId, string cause, string effect, string evidence) =>
                {
                    var signal = await _storageProvider.GetSignalAsync(signalId);

                    signal.Cause = string.IsNullOrWhiteSpace(cause) ? null : cause;
                    signal.Effect = string.IsNullOrWhiteSpace(effect) ? null : effect;
                    signal.Evidence = string.IsNullOrWhiteSpace(evidence) ? null : evidence;
                    await _storageProvider.SaveSignalAsync(signal);

                    return;
                },
                "update_signal_analysis",
                "Persist cause/effect/evidence analysis for a signal. Cause: why this signal is in its current state. Effect: what the failure means. Evidence: raw details (error messages, build numbers, issue/PR references) for cross-signal correlation."),

        ];
    }

    private static Dictionary<string, JsonElement> SelectFields(JsonElement source, JsonElement selectors)
    {
        if (selectors.ValueKind != JsonValueKind.Object && selectors.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("selectors must be either an object map or an array of path strings.");
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (selectors.ValueKind == JsonValueKind.Object)
        {
            foreach (var selector in selectors.EnumerateObject())
            {
                if (selector.Value.ValueKind != JsonValueKind.String)
                {
                    result[selector.Name] = JsonSerializer.SerializeToElement<object?>(null);
                    continue;
                }

                var path = selector.Value.GetString()!;
                result[selector.Name] = TryGetByPath(source, path, out var value)
                    ? value
                    : JsonSerializer.SerializeToElement<object?>(null);
            }

            return result;
        }

        foreach (var selector in selectors.EnumerateArray())
        {
            if (selector.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var path = selector.GetString()!;
            result[path] = TryGetByPath(source, path, out var value)
                ? value
                : JsonSerializer.SerializeToElement<object?>(null);
        }

        return result;
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
