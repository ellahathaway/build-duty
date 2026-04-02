using Microsoft.TeamFoundation.Build.WebApi;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildDuty.Core;

public record AzureDevOpsTimelineParentInfo(string Name, string Type, int? LogId);

public record AzureDevOpsTimelineRecordInfo(
    Guid Id,
    TaskResult? Result,
    string RecordType,
    string Name,
    List<AzureDevOpsTimelineParentInfo> Parents,
    int? LogId);

public record AzureDevOpsPipelineInfo(string OrganizationUrl, Build Build, List<AzureDevOpsTimelineRecordInfo> TimelineRecords);

public sealed class AzureDevOpsPipelineSignal : Signal
{
    public override SignalType Type => SignalType.AzureDevOpsPipeline;

    [SetsRequiredMembers]
    public AzureDevOpsPipelineSignal(string organizationUrl, Build build, List<AzureDevOpsTimelineRecordInfo> timelineRecords)
    {
        TypedInfo = new AzureDevOpsPipelineInfo(organizationUrl, build, timelineRecords);
    }

    public AzureDevOpsPipelineSignal() { }

    [JsonIgnore]
    public AzureDevOpsPipelineInfo TypedInfo
    {
        get => JsonSerializer.Deserialize<AzureDevOpsPipelineInfo>(Info.GetRawText())!;
        set => Info = JsonSerializer.SerializeToElement(value);
    }
}
