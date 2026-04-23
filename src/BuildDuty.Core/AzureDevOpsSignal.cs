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
    string ProjectName,
    int PipelineId,
    AzureDevOpsBuildInfo? Build,
    List<AzureDevOpsTimelineRecordInfo>? TimelineRecords);

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

    public void AsResolved(Build build, string? context = null)
        => AsResolved(new AzureDevOpsBuildInfo(build.Id, build.Result, build.Definition.Id, build.SourceBranch, build.FinishTime), context);

    public void AsResolved(AzureDevOpsBuildInfo buildInfo, string? context = null)
    {
        var current = TypedInfo;
        var newInfo = new AzureDevOpsPipelineInfo(current.OrganizationUrl, current.ProjectName, current.PipelineId, buildInfo, []);
        AsResolved(JsonSerializer.SerializeToElement(newInfo, s_jsonOptions), BuildUrl(current.Build?.Id, buildInfo.Id), context);
    }

    public void AsUpdated(Build build, List<AzureDevOpsTimelineRecordInfo> timelineRecords, string? context = null)
    {
        var current = TypedInfo;
        var buildInfo = new AzureDevOpsBuildInfo(build.Id, build.Result, build.Definition.Id, build.SourceBranch, build.FinishTime);
        var newInfo = new AzureDevOpsPipelineInfo(current.OrganizationUrl, current.ProjectName, current.PipelineId, buildInfo, timelineRecords);
        AsUpdated(JsonSerializer.SerializeToElement(newInfo, s_jsonOptions), BuildUrl(current.Build?.Id, buildInfo.Id), context);
    }

    private Uri BuildUrl(int? oldBuildId, int newBuildId)
        => oldBuildId != newBuildId
            ? new Uri(Url.ToString().Replace($"buildId={oldBuildId}", $"buildId={newBuildId}"))
            : Url;
}
