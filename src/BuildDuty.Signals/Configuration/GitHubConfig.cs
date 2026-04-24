using YamlDotNet.Serialization;

namespace BuildDuty.Signals.Configuration;

public sealed class GitHubConfig
{
    public List<GitHubOrganizationConfig> Organizations { get; set; } = [];
}

/// <summary>
/// A GitHub organization (or owner) containing multiple repositories to scan.
/// </summary>
public sealed class GitHubOrganizationConfig
{
    public string Name { get; set; } = string.Empty;

    public List<GitHubRepositoryConfig> Repositories { get; set; } = [];
}

public sealed class GitHubRepositoryConfig
{
    public string Name { get; set; } = string.Empty;

    public List<GitHubItemConfig>? Issues { get; set; }

    [YamlMember(Alias = "prs")]
    public List<GitHubItemConfig>? PullRequests { get; set; }
}

public sealed class GitHubItemConfig
{
    /// <summary>
    /// Regex pattern to match item titles.
    /// </summary>
    public string Name { get; set; } = ".*";

    public string? Context { get; set; }

    public List<string> Authors { get; set; } = [];

    public List<string> Labels { get; set; } = [];

    public List<string> ExcludeLabels { get; set; } = [];
}
