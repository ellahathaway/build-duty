using Microsoft.TeamFoundation.Build.WebApi;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace BuildDuty.Core;

public record AzureDevOpsPipelineInfo(Build Build, List<TimelineRecord> TimelineRecords);

public sealed class AzureDevOpsPipelineSignal : Signal
{
    public override SignalType Type => SignalType.AzureDevOpsPipeline;

    [SetsRequiredMembers]
    public AzureDevOpsPipelineSignal(Build build, List<TimelineRecord> timelineRecords)
    {
        TypedInfo = new AzureDevOpsPipelineInfo(build, timelineRecords);
    }

    public AzureDevOpsPipelineSignal() { }

    [System.Text.Json.Serialization.JsonIgnore]
    public AzureDevOpsPipelineInfo TypedInfo
    {
        get => System.Text.Json.JsonSerializer.Deserialize<AzureDevOpsPipelineInfo>(Info.GetRawText())!;
        set => Info = System.Text.Json.JsonSerializer.SerializeToElement(value);
    }
}
