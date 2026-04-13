using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.Core;

public abstract class AzureDevOpsSignal<TInfo> : Signal<AzureDevOpsSignalType, TInfo> where TInfo : class;

public record AzureDevOpsPipelineInfo(Build Build, List<TimelineRecord>? TimelineRecords);

public sealed class AzureDevOpsPipelineSignal : AzureDevOpsSignal<AzureDevOpsPipelineInfo>
{
    public override AzureDevOpsSignalType Type => AzureDevOpsSignalType.Pipeline;

    public static AzureDevOpsPipelineSignal Create(
        Build build,
        List<TimelineRecord>? timelineRecords = null,
        List<string>? workItemIds = null)
    {
        return new AzureDevOpsPipelineSignal
        {
            Info = new AzureDevOpsPipelineInfo(build, timelineRecords),
            WorkItemIds = workItemIds ?? [],
        };
    }
}

public enum AzureDevOpsSignalType
{
    Pipeline
}
