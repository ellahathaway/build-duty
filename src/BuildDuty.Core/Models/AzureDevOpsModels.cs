using YamlDotNet.Serialization;

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
    /// Pipeline run result statuses that should produce work items. Accepted values:
    /// <c>failed</c>, <c>partiallySucceeded</c>, <c>canceled</c>, <c>succeeded</c>.
    /// Defaults to <c>["failed", "partiallySucceeded"]</c> when omitted.
    /// </summary>
    [YamlMember(Alias = "status")]
    public List<string>? Status { get; set; }

    /// <summary>
    /// Maximum age of pipeline runs to consider (e.g. "7d", "24h", "2d12h").
    /// Runs older than this are ignored. Omit or leave empty for no age limit.
    /// Supported suffixes: <c>d</c> (days), <c>h</c> (hours), <c>m</c> (minutes).
    /// </summary>
    [YamlMember(Alias = "age")]
    public string? Age { get; set; }

    /// <summary>
    /// Stage filters to focus on when analyzing build failures.
    /// Each entry specifies a stage name pattern and optionally job patterns
    /// within that stage. When empty, all stages are considered.
    /// </summary>
    [YamlMember(Alias = "stages")]
    public List<StageFilterConfig>? Stages { get; set; }

    /// <summary>
    /// Returns the effective status filter, defaulting to
    /// <c>["failed", "partiallySucceeded", "canceled"]</c>.
    /// Canceled covers builds that timed out.
    /// </summary>
    public IReadOnlyList<string> EffectiveStatus =>
        Status is { Count: > 0 } ? Status : ["failed", "partiallySucceeded", "canceled"];

    /// <summary>
    /// Parses the <see cref="Age"/> string into a <see cref="TimeSpan"/>.
    /// Returns <c>null</c> when no age limit is configured.
    /// </summary>
    public TimeSpan? ParsedAge => ParseAge(Age);

    private static TimeSpan? ParseAge(string? age)
    {
        if (string.IsNullOrWhiteSpace(age))
        {
            return null;
        }

        var span = TimeSpan.Zero;
        var num = 0;

        foreach (var c in age)
        {
            if (char.IsDigit(c))
            {
                num = num * 10 + (c - '0');
            }
            else
            {
                span += c switch
                {
                    'd' or 'D' => TimeSpan.FromDays(num),
                    'h' or 'H' => TimeSpan.FromHours(num),
                    'm' or 'M' => TimeSpan.FromMinutes(num),
                    _ => TimeSpan.Zero,
                };
                num = 0;
            }
        }

        return span > TimeSpan.Zero ? span : null;
    }
}

/// <summary>
/// Filter for a pipeline stage. Matches stages by name pattern and optionally
/// restricts which jobs within the stage are investigated.
/// </summary>
public sealed class StageFilterConfig
{
    /// <summary>
    /// Stage name pattern. Supports glob-style wildcards (<c>*</c>).
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Job/leg name patterns within this stage. When empty, all jobs in the
    /// stage are investigated. Supports glob-style wildcards (<c>*</c>).
    /// </summary>
    [YamlMember(Alias = "jobs")]
    public List<string>? Jobs { get; set; }
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
    public string Repository { get; set; } = "dotnet-dotnet";

    /// <summary>
    /// .NET support phases to include. Defaults to all phases:
    /// <c>active</c>, <c>maintenance</c>, <c>preview</c>, <c>go-live</c>, <c>rc</c>.
    /// </summary>
    [YamlMember(Alias = "supportPhases")]
    public List<string>? SupportPhases { get; set; }

    /// <summary>Minimum major version to include (e.g. 8).</summary>
    [YamlMember(Alias = "minVersion")]
    public int? MinVersion { get; set; }
}
