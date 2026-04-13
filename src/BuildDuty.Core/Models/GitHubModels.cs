using YamlDotNet.Serialization;
using System.Text.RegularExpressions;
using Octokit;

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
    public GitHubIssueConfig? Issues { get; set; }

    [YamlMember(Alias = "prs")]
    public List<GitHubPullRequestPattern>? PullRequests { get; set; }
}

public sealed class GitHubIssueConfig
{
    [YamlMember(Alias = "labels")]
    public List<string> Labels { get; set; } = [];

    [YamlMember(Alias = "state")]
    public ItemStateFilter State { get; set; } = ItemStateFilter.Open;
}

/// <summary>
/// A PR name pattern to match. Prefix with <c>*</c> for suffix matching.
/// </summary>
public sealed class GitHubPullRequestPattern
{
    [YamlMember(Alias = "name")]
    public Regex Name { get; set; } = new Regex(".*");

    [YamlMember(Alias = "state")]
    public ItemStateFilter State { get; set; } = ItemStateFilter.Open;
}
