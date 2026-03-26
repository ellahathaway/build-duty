using YamlDotNet.Serialization;
using Microsoft.TeamFoundation.Build.WebApi;
using System.Text.RegularExpressions;

namespace BuildDuty.Core.Models;

public sealed class AzureDevOpsConfig
{
    [YamlMember(Alias = "organizations")]
    public List<AzureDevOpsOrganizationConfig> Organizations { get; set; } = [];
}

public sealed class AzureDevOpsOrganizationConfig
{
    [YamlMember(Alias = "url")]
    public string Url { get; set; } = string.Empty;

    [YamlMember(Alias = "projects")]
    public List<AzureDevOpsProjectConfig> Projects { get; set; } = [];
}

public sealed class AzureDevOpsProjectConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "pipelines")]
    public List<AzureDevOpsPipelineConfig> Pipelines { get; set; } = [];
}

public sealed class AzureDevOpsPipelineConfig
{
    [YamlMember(Alias = "id")]
    public int Id { get; set; }

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "branches")]
    public List<string> Branches { get; set; } = [];

    /// <summary>
    /// When set, auto-discovers release branches from the given repository
    /// instead of using the static <see cref="Branches"/> list.
    /// </summary>
    [YamlMember(Alias = "release")]
    public ReleaseBranchConfig? Release { get; set; }

    /// <summary>
    /// Pipeline run result statuses that should produce work items.
    /// Defaults to <c>["failed", "partiallySucceeded", "canceled"]</c> when omitted.
    /// </summary>
    [YamlMember(Alias = "status")]
    public List<BuildResult> Status { get; set; } = new List<BuildResult> { BuildResult.Failed, BuildResult.PartiallySucceeded, BuildResult.Canceled };

    /// <summary>
    /// Maximum age of pipeline runs to consider (e.g. "7d", "24h", "2d12h").
    /// Runs older than this are ignored. Omit or leave empty for no age limit.
    /// Supported suffixes: <c>d</c> (days), <c>h</c> (hours), <c>m</c> (minutes).
    /// </summary>
    [YamlMember(Alias = "age")]
    public string? Age { get; set; }

    /// <summary>
    /// Timelines to focus on when analyzing build failures.
    /// When empty, all timelines are considered.
    /// </summary>
    [YamlMember(Alias = "timelineFilters")]
    public List<TimelineFilter>? TimelineFilters { get; set; } = [];
}

/// <summary>
/// Filter for a pipeline timeline. Matches timeline types by name patterns.
/// </summary>
public sealed class TimelineFilter
{
    /// <summary>
    /// Timeline name patterns. Supports glob-style wildcards (<c>*</c>).
    /// </summary>
    [YamlMember(Alias = "names")]
    public List<Regex> Names { get; set; } = new List<Regex> { new Regex(".*", RegexOptions.IgnoreCase) };

    /// <summary>
    /// Timeline type
    /// </summary>
    [YamlMember(Alias = "type")]
    public TimelineRecordType Type { get; set; }
}

/// <summary>
/// Configuration for auto-discovering .NET release branches from an Azure DevOps
/// Git repository. Filters branches to those matching supported .NET channels from
/// the <c>dotnet/core</c> releases index.
/// </summary>
public sealed class ReleaseBranchConfig
{
    /// <summary>Azure DevOps Git repository name (e.g. "dotnet-dotnet").</summary>
    [YamlMember(Alias = "repository")]
    public required string Repository { get; set; }

    /// <summary>
    /// .NET support phases to include
    /// </summary>
    [YamlMember(Alias = "supportPhases")]
    public required List<string> SupportPhases { get; set; }

    /// <summary>Minimum major version to include (e.g. 8).</summary>
    [YamlMember(Alias = "minVersion")]
    public int? MinVersion { get; set; }
}

public enum TimelineRecordType
{
    Stage,
    Job,
}
