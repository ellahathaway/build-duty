using System.Xml.Serialization;
using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.Signals;

/// <summary>
/// Signal for an Azure DevOps pipeline run.
/// Contains strongly-typed build and timeline information.
/// </summary>
public sealed class AzureDevOpsPipelineSignal : Signal
{
    public override SignalType Type => SignalType.AzureDevOpsPipeline;

    [XmlAttribute]
    public string OrganizationUrl { get; set; } = string.Empty;

    [XmlAttribute]
    public string ProjectName { get; set; } = string.Empty;

    [XmlAttribute]
    public int PipelineId { get; set; }

    [XmlElement("Build")]
    public AzureDevOpsBuildInfo? Build { get; set; }

    [XmlArray("TimelineRecords")]
    [XmlArrayItem("Record")]
    public List<AzureDevOpsTimelineRecordInfo> TimelineRecords { get; set; } = new List<AzureDevOpsTimelineRecordInfo>();
}

public sealed class AzureDevOpsBuildInfo
{
    [XmlAttribute]
    public int Id { get; set; }

    [XmlAttribute]
    public BuildResult Result { get; set; }

    [XmlAttribute]
    public int DefinitionId { get; set; }

    [XmlAttribute]
    public string SourceBranch { get; set; } = string.Empty;

    [XmlIgnore]
    public DateTime? FinishTime { get; set; }

    /// <summary>
    /// XML proxy for the nullable <see cref="FinishTime"/> property.
    /// </summary>
    [XmlAttribute("FinishTime")]
    public string? FinishTimeString
    {
        get => FinishTime?.ToString("o");
        set => FinishTime = string.IsNullOrEmpty(value) ? null : DateTime.Parse(value);
    }

    public bool ShouldSerializeFinishTimeString() => FinishTime.HasValue;
}

public sealed class AzureDevOpsTimelineRecordInfo
{
    [XmlAttribute]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute]
    public TaskResult Result { get; set; }

    [XmlAttribute]
    public string RecordType { get; set; } = string.Empty;

    [XmlAttribute]
    public string Name { get; set; } = string.Empty;

    [XmlArray("Parents")]
    [XmlArrayItem("Parent")]
    public List<AzureDevOpsTimelineParentInfo> Parents { get; set; } = [];

    [XmlAttribute]
    public int LogId { get; set; }

    public bool ShouldSerializeLogId() => LogId > 0;
}

public sealed class AzureDevOpsTimelineParentInfo
{
    [XmlAttribute]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute]
    public string Type { get; set; } = string.Empty;

    [XmlAttribute]
    public int LogId { get; set; }

    public bool ShouldSerializeLogId() => LogId > 0;
}
