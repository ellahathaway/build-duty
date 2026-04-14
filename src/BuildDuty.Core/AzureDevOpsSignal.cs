using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.Core;

public record AzureDevOpsTimelineParentInfo(string Name, string Type, int? LogId);

public record AzureDevOpsTimelineRecordInfo(
    Guid Id,
    TaskResult? Result,
    string RecordType,
    string Name,
    List<AzureDevOpsTimelineParentInfo> Parents,
    int? LogId);

public record AzureDevOpsBuildInfo(
    int Id,
    BuildResult? Result,
    int DefinitionId,
    string SourceBranch,
    DateTime? FinishTime);

public record AzureDevOpsPipelineInfo(
    string OrganizationUrl,
    Guid ProjectId,
    AzureDevOpsBuildInfo Build,
    List<AzureDevOpsTimelineRecordInfo> TimelineRecords,
    List<BuildResult> MonitoredStatuses);

public sealed class AzureDevOpsPipelineSignal : Signal
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public override SignalType Type => SignalType.AzureDevOpsPipeline;

    [SetsRequiredMembers]
    public AzureDevOpsPipelineSignal(AzureDevOpsPipelineInfo typedInfo, Uri url)
    {
        Info = JsonSerializer.SerializeToElement(typedInfo, s_jsonOptions);
        Url = url;
    }

    public AzureDevOpsPipelineSignal() { }

    [JsonIgnore]
    public AzureDevOpsPipelineInfo TypedInfo => JsonSerializer.Deserialize<AzureDevOpsPipelineInfo>(Info.GetRawText(), s_jsonOptions)!;
}
