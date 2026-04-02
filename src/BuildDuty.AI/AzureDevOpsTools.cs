using BuildDuty.Core;
using Maestro.Common;
using Microsoft.Extensions.AI;
using Microsoft.TeamFoundation.Build.WebApi;
using System.Text.Json;

namespace BuildDuty.AI;

public class AzureDevOpsTools
{
    private readonly IStorageProvider _storageProvider;
    private readonly IRemoteTokenProvider _tokenProvider;
    private record GetLogResult(string? FilePath, string? Error);
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
                async (string signalId, int logId) =>
                {
                    try
                    {
                        var context = await GetPipelineContextAsync(signalId);
                        Guid projectId = context.Signal.TypedInfo.Build.Project.Id;
                        int buildId = context.Signal.TypedInfo.Build.Id;
                        string signalLogId = IdGenerator.NewLogId(logId);
                        string? logPath = null;
                        try
                        {
                            logPath = await _storageProvider.GetSignalLogPathAsync(signalId, signalLogId);
                        }
                        catch (FileNotFoundException)
                        {
                            using var stream = await context.BuildClient.GetBuildLogAsync(projectId, buildId, logId);
                            logPath = await _storageProvider.SaveSignalLogAsync(signalId, signalLogId, stream);
                        }

                        return new GetLogResult(logPath, null);
                    }
                    catch (Exception ex)
                    {
                        return new GetLogResult(null, $"Error retrieving log ID {logId} for signal ID {signalId}: {ex.Message}");
                    }
                },
                "get_pipeline_log",
                "Retrieve a build log file path for an Azure DevOps pipeline signal by signal ID and log ID."),

            AIFunctionFactory.Create(
                async (string logPath, int chunkOffsetFromBottom, int chunkSize) =>
                {
                    try
                    {
                        int safeChunkOffset = Math.Max(0, chunkOffsetFromBottom);
                        int safeChunkSize = Math.Clamp(chunkSize, 10, 500);

                        var chunkLines = await ReadChunkFromBottomAsync(logPath, safeChunkOffset, safeChunkSize);
                        int totalLines = await CountLinesAsync(logPath);
                        int consumed = (safeChunkOffset + 1) * safeChunkSize;
                        bool hasMoreChunks = consumed < totalLines;

                        return JsonSerializer.SerializeToElement(new
                        {
                            LogPath = logPath,
                            ChunkOffsetFromBottom = safeChunkOffset,
                            ChunkSize = safeChunkSize,
                            HasMoreChunks = hasMoreChunks,
                            LineCount = chunkLines.Count,
                            Lines = chunkLines,
                            Error = (string?)null,
                        });
                    }
                    catch (Exception ex)
                    {
                        return JsonSerializer.SerializeToElement(new
                        {
                            LogPath = logPath,
                            ChunkOffsetFromBottom = 0,
                            ChunkSize = 0,
                            HasMoreChunks = false,
                            LineCount = 0,
                            Lines = Array.Empty<string>(),
                            Error = $"Error reading Azure DevOps log chunk from '{logPath}': {ex.Message}",
                        });
                    }
                },
                "read_pipeline_log_chunk",
                "Read one bottom-up chunk of a pipeline log file. chunkOffsetFromBottom=0 reads the last chunk, 1 reads the next chunk up. Use to inspect logs incrementally before broader analysis."),
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

    private static async Task<List<string>> ReadChunkFromBottomAsync(string logPath, int chunkIndex, int chunkSize)
    {
        if (!File.Exists(logPath))
        {
            throw new FileNotFoundException($"Log file '{logPath}' not found.");
        }

        var lines = await File.ReadAllLinesAsync(logPath);
        int total = lines.Length;
        if (total == 0)
        {
            return [];
        }

        int endExclusive = Math.Max(0, total - (chunkIndex * chunkSize));
        int startInclusive = Math.Max(0, endExclusive - chunkSize);
        if (startInclusive >= endExclusive)
        {
            return [];
        }

        return lines[startInclusive..endExclusive].ToList();
    }

    private static async Task<int> CountLinesAsync(string logPath)
    {
        var lines = await File.ReadAllLinesAsync(logPath);
        return lines.Length;
    }
}
