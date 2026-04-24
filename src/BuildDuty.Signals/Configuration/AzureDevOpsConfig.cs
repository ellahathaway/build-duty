using Dotnet.Release;
using Microsoft.TeamFoundation.Build.WebApi;

namespace BuildDuty.Signals.Configuration;

public sealed class AzureDevOpsConfig
{
    public List<AzureDevOpsOrganizationConfig> Organizations { get; set; } = [];
}

public sealed class AzureDevOpsOrganizationConfig
{
    public string Url { get; set; } = string.Empty;

    public List<AzureDevOpsProjectConfig> Projects { get; set; } = [];
}

public sealed class AzureDevOpsProjectConfig
{
    public string Name { get; set; } = string.Empty;

    public List<AzureDevOpsPipelineConfig> Pipelines { get; set; } = [];
}

public sealed class AzureDevOpsPipelineConfig
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<string> Branches { get; set; } = [];

    /// <summary>
    /// When set, auto-discovers release branches from the given repository
    /// instead of using the static <see cref="Branches"/> list.
    /// </summary>
    public ReleaseBranchConfig? Release { get; set; }

    /// <summary>
    /// Pipeline run result statuses that should produce signals.
    /// Only non-successful results are valid: Failed, PartiallySucceeded, Canceled.
    /// When empty after deserialization, defaults are applied automatically.
    /// </summary>
    public List<BuildResult> Status { get; set; } = [];

    /// <summary>
    /// Maximum age of pipeline runs to consider (e.g. "7d", "24h", "2d12h").
    /// Runs older than this are ignored.
    /// </summary>
    public string? Age { get; set; }

    /// <summary>
    /// Timeline record result statuses that should produce signals when analyzing build failures.
    /// When empty after deserialization, defaults are applied automatically.
    /// </summary>
    public List<TaskResult> TimelineResults { get; set; } = [];

    /// <summary>
    /// Timelines to focus on when analyzing build failures.
    /// When empty, all timelines are considered.
    /// </summary>
    public List<TimelineFilter>? TimelineFilters { get; set; } = [];

    public string? Context { get; set; }
}

/// <summary>
/// Filter for a pipeline timeline. Matches timeline types by name patterns.
/// </summary>
public sealed class TimelineFilter
{
    /// <summary>
    /// Timeline name regex patterns.
    /// </summary>
    public List<string> Names { get; set; } = [];

    /// <summary>
    /// Timeline record type (Stage or Job).
    /// </summary>
    public TimelineRecordType Type { get; set; }

    /// <summary>
    /// Timeline record result statuses to include.
    /// When empty after deserialization, defaults are applied automatically.
    /// </summary>
    public List<TaskResult> Status { get; set; } = [];
}

/// <summary>
/// Configuration for auto-discovering .NET release branches.
/// </summary>
public sealed class ReleaseBranchConfig
{
    /// <summary>Azure DevOps Git repository name (e.g. "dotnet-dotnet").</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>
    /// .NET support phases to include.
    /// </summary>
    public List<SupportPhase> SupportPhases { get; set; } = [];

    /// <summary>Minimum major .NET version to include (e.g. 8).</summary>
    public int MinVersion { get; set; }
}

// ── Enums (no ADO SDK equivalent) ──

public enum TimelineRecordType
{
    Stage,
    Job
}
