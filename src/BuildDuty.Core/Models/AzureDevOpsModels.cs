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
    /// Returns the effective status filter, defaulting to
    /// <c>["failed", "partiallySucceeded"]</c>.
    /// </summary>
    public IReadOnlyList<string> EffectiveStatus =>
        Status is { Count: > 0 } ? Status : ["failed", "partiallySucceeded"];
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
