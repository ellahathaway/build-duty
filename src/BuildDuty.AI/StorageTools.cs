using BuildDuty.Core;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Maestro.Common;
using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.AI;

public class StorageTools
{
    private readonly IStorageProvider _storageProvider;
    private readonly IRemoteTokenProvider _tokenProvider;
    private record GetLogResult(string? FilePath, string? Error);
    private record PipelineContext(AzureDevOpsPipelineSignal Signal, BuildHttpClient BuildClient);

    public StorageTools(
        IStorageProvider storageProvider,
        IRemoteTokenProvider tokenProvider)
    {
        _storageProvider = storageProvider;
        _tokenProvider = tokenProvider;
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
                async (string signalId, string summary) =>
                {
                    var signal = await _storageProvider.GetSignalAsync(signalId);

                    signal.Summary = summary;
                    await _storageProvider.SaveSignalAsync(signal);

                    return;
                },
                "update_signal_summary",
                "Update a signal summary by signal ID."),

            AIFunctionFactory.Create(
                async (string signalId) =>
                {
                    try
                    {
                        var context = await GetPipelineContextAsync(signalId);
                        Guid projectId = context.Signal.TypedInfo.Build.Project.Id;
                        int buildId = context.Signal.TypedInfo.Build.Id;
                        var logs = await context.BuildClient.GetBuildLogsAsync(projectId, buildId);
                        var logPaths = new List<GetLogResult>();
                        foreach (var log in logs)
                        {
                            var result = await GetOrSaveLogAsync(
                                signalId,
                                log.Id,
                                () => context.BuildClient.GetBuildLogAsync(projectId, buildId, log.Id),
                                $"Error retrieving log ID {log.Id} for build ID {buildId} in signal ID {signalId}");
                            logPaths.Add(result);
                        }
                        return logPaths;
                    }
                    catch (Exception ex)
                    {
                        return new List<GetLogResult> { new GetLogResult(null, $"Error retrieving logs for signal ID {signalId}: {ex.Message}") };
                    }
                },
                "get_build_logs",
                "Retrieve build logs for a given Azure DevOps pipeline signal. Returns a list of file paths containing the build logs."),

            AIFunctionFactory.Create(
                async (string signalId) =>
                {
                    try
                    {
                        var context = await GetPipelineContextAsync(signalId);
                        Guid projectId = context.Signal.TypedInfo.Build.Project.Id;
                        var logPaths = new List<GetLogResult>();
                        foreach (var record in context.Signal.TypedInfo.TimelineRecords)
                        {
                            var log = record.Log;
                            if (log != null)
                            {
                                var result = await GetOrSaveLogAsync(
                                    signalId,
                                    log.Id,
                                    () => context.BuildClient.GetBuildLogAsync(projectId, context.Signal.TypedInfo.Build.Id, log.Id),
                                    $"Error retrieving log ID {log.Id} for timeline record ID {record.Id} in signal ID {signalId}");
                                logPaths.Add(result);
                            }
                        }
                        return logPaths;
                    }
                    catch (Exception ex)
                    {
                        return new List<GetLogResult> { new GetLogResult(null, $"Error retrieving timeline record logs for signal ID {signalId}: {ex.Message}") };
                    }
                },
                "get_timeline_record_logs",
                "Retrieve build logs for a given Azure DevOps pipeline signal. Returns a list of file paths containing the build logs."),
        ];
    }

    private async Task<PipelineContext> GetPipelineContextAsync(string signalId)
    {
        Signal signal = await _storageProvider.GetSignalAsync(signalId);
        if (signal.Type != SignalType.AzureDevOpsPipeline)
        {
            throw new ArgumentException($"Signal with ID {signalId} is not an Azure DevOps pipeline signal.");
        }

        var pipelineSignal = signal as AzureDevOpsPipelineSignal
            ?? throw new InvalidOperationException($"Failed to cast signal with ID {signalId} to AzureDevOpsPipelineSignal.");

        var buildClient = await _tokenProvider.GetAzureDevOpsBuildClientAsync(pipelineSignal.TypedInfo.OrganizationUrl);
        return new PipelineContext(pipelineSignal, buildClient);
    }

    private async Task<GetLogResult> GetOrSaveLogAsync(
        string signalId,
        int logId,
        Func<Task<Stream>> fetchLogStream,
        string errorPrefix)
    {
        string signalLogId = IdGenerator.NewLogId(logId);
        try
        {
            var logPath = await _storageProvider.GetSignalLogPathAsync(signalId, signalLogId);
            return new GetLogResult(logPath, null);
        }
        catch (FileNotFoundException)
        {
            var stream = await fetchLogStream();
            var logPath = await _storageProvider.SaveSignalLogAsync(signalId, signalLogId, stream);
            return new GetLogResult(logPath, null);
        }
        catch (Exception ex)
        {
            return new GetLogResult(null, $"{errorPrefix}: {ex.Message}");
        }
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

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
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
