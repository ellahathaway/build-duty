using System.Diagnostics.CodeAnalysis;
using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.Core;

public record AzureDevOpsPipelineInfo(Build Build, List<TimelineRecord>? TimelineRecords);

public sealed class AzureDevOpsPipelineSignal : Signal<AzureDevOpsPipelineInfo>
{
    public override SignalType Type => SignalType.AzureDevOpsPipeline;

    [SetsRequiredMembers]
    public AzureDevOpsPipelineSignal(AzureDevOpsPipelineInfo info)
    {
        if (info == null)
        {
            throw new ArgumentNullException(nameof(info));
        }
        Info = info;
    }
}
