using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildDuty.Core;
using Maestro.Common;
using Microsoft.Extensions.AI;
using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.AI.Tools;

public class AzureDevOpsTools
{
    private readonly IStorageProvider _storageProvider;
    private readonly IRemoteTokenProvider _tokenProvider;
    private record PipelineContext(AzureDevOpsPipelineSignal Signal, BuildHttpClient BuildClient);

    public AzureDevOpsTools(
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
                async (
                    [Description("The ID of the signal")] string signalId,
                    [Description("The ID of the pipeline log")] int logId,
                    [Description("Optional: regex filter to apply to the log lines, eg 'ERROR|WARNING'")] string? filter) =>
                {
                    // Load the log
                    var context = await GetPipelineContextAsync(signalId);
                    Guid projectId = context.Signal.TypedInfo.ProjectId;
                    int buildId = context.Signal.TypedInfo.Build.Id;
                    using var stream = await context.BuildClient.GetBuildLogAsync(projectId, buildId, logId);

                    // Filter the log if filters were provided, otherwise return the full log
                    Regex? rx = null;
                    if (!string.IsNullOrWhiteSpace(filter))
                    {
                        rx = new Regex(
                            filter,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                            matchTimeout: TimeSpan.FromSeconds(2));
                    }

                    using var reader = new StreamReader(stream);
                    var sb = new System.Text.StringBuilder();

                    string? line;
                    while ((line = await reader.ReadLineAsync()) is not null)
                    {
                        if (rx is null || rx.IsMatch(line))
                        {
                            if (sb.Length > 0)
                            {
                                sb.AppendLine();
                            }
                            sb.Append(line);
                        }
                    }

                    return sb.ToString();
                },
                "read_pipeline_log",
                "Retrieve and read log file path for an Azure DevOps pipeline signal by signal ID and log ID. Optionally apply a regex filter to return only matching lines."),

            AIFunctionFactory.Create(
                async (
                    [Description("The ID of the signal")] string signalId,
                    [Description("Optional: filter by record name, supports regex")] string? recordName,
                    [Description("Optional: filter by record type (e.g. 'Task', 'Job', 'Stage')")] string? recordType,
                    [Description("Optional: filter by result (e.g. 'failed', 'succeededWithIssues', 'canceled')")] string? result) =>
                {
                    try
                    {
                        var context = await GetPipelineContextAsync(signalId);
                        var info = context.Signal.TypedInfo;
                        var timeline = await context.BuildClient.GetBuildTimelineAsync(info.ProjectId, info.Build.Id)
                            ?? throw new InvalidOperationException($"No timeline for build {info.Build.Id}.");

                        var allRecords = timeline.Records?.ToList() ?? [];
                        var recordsById = allRecords.ToDictionary(r => r.Id, r => r);

                        IEnumerable<TimelineRecord> filtered = allRecords;
                        if (!string.IsNullOrWhiteSpace(recordType))
                        {
                            filtered = filtered.Where(r =>
                                r.RecordType.Equals(recordType, StringComparison.OrdinalIgnoreCase));
                        }
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            filtered = filtered.Where(r =>
                                r.Result?.ToString().Equals(result, StringComparison.OrdinalIgnoreCase) == true);
                        }

                        var rx = !string.IsNullOrWhiteSpace(recordName)
                            ? new Regex(recordName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeout: TimeSpan.FromSeconds(2))
                            : null;
                        if (rx is not null)
                        {
                            filtered = filtered.Where(r => rx.IsMatch(r.Name));
                        }

                        return JsonSerializer.SerializeToElement(new
                        {
                            SignalId = signalId,
                            BuildId = info.Build.Id,
                            ReturnedRecords = filtered.Count(),
                            Records = filtered,
                            Error = (string?)null,
                        });
                    }
                    catch (Exception ex)
                    {
                        return JsonSerializer.SerializeToElement(new
                        {
                            SignalId = signalId,
                            BuildId = (int?)null,
                            ReturnedRecords = 0,
                            Records = Array.Empty<TimelineRecord>(),
                            Error = $"Error retrieving timeline records for signal {signalId}: {ex.Message}",
                        });
                    }
                },
                "get_timeline_records",
                "Get all timeline records for an Azure DevOps pipeline signal's build with optional filtering by record name, type, and result."),
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

        var connection = await _tokenProvider.GetAzureDevOpsConnectionAsync(pipelineSignal.TypedInfo.OrganizationUrl);
        var buildClient = connection.GetClient<BuildHttpClient>();
        return new PipelineContext(pipelineSignal, buildClient);
    }
}
