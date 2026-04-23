using System.Text.RegularExpressions;
using Octokit;
using YamlDotNet.Serialization;

namespace BuildDuty.Core.Models;

public sealed class GitHubConfig
{
    [YamlMember(Alias = "organizations")]
    public List<GitHubOrganizationConfig> Organizations { get; set; } = [];
}

/// <summary>
/// An organization (or owner) containing multiple repositories to scan.
/// </summary>
public sealed class GitHubOrganizationConfig
{
    [YamlMember(Alias = "organization")]
    public string Organization { get; set; } = string.Empty;

    [YamlMember(Alias = "repositories")]
    public List<GitHubRepositoryConfig> Repositories { get; set; } = [];
}

public sealed class GitHubRepositoryConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "issues")]
    public List<GitHubItemConfig>? Issues { get; set; }

    [YamlMember(Alias = "prs")]
    public List<GitHubItemConfig>? PullRequests { get; set; }
}

public sealed class GitHubItemConfig
{
    [YamlMember(Alias = "name")]
    public Regex Name { get; set; } = new Regex(".*");

    [YamlMember(Alias = "context")]
    public string? Context { get; set; }

    [YamlMember(Alias = "authors")]
    public List<string> Authors { get; set; } = [];

    [YamlMember(Alias = "labels")]
    public List<string> Labels { get; set; } = [];

    [YamlMember(Alias = "excludeLabels")]
    public List<string> ExcludeLabels { get; set; } = [];
}
