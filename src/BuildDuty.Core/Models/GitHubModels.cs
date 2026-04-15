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

    [YamlMember(Alias = "context")]
    public string? Context { get; set; }

    /// <summary>
    /// Optional list of authors to include. Use <c>app/&lt;name&gt;</c> for GitHub Apps.
    /// If empty, issues from all authors are included.
    /// </summary>
    [YamlMember(Alias = "authors")]
    public List<string> Authors { get; set; } = [];

    /// <summary>
    /// Optional list of labels to exclude. Issues with any of these labels are filtered out.
    /// If empty, no issues are excluded based on labels.
    /// </summary>
    [YamlMember(Alias = "excludeLabels")]
    public List<string> ExcludeLabels { get; set; } = [];
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

    [YamlMember(Alias = "context")]
    public string? Context { get; set; }

    /// <summary>
    /// Optional list of authors to include. Use <c>app/&lt;name&gt;</c> for GitHub Apps.
    /// If empty, PRs from all authors are included.
    /// </summary>
    [YamlMember(Alias = "authors")]
    public List<string> Authors { get; set; } = [];

    /// <summary>
    /// Optional list of labels to include. Only PRs with at least one of these labels are included.
    /// If empty, PRs with any labels (or none) are included.
    /// </summary>
    [YamlMember(Alias = "labels")]
    public List<string> Labels { get; set; } = [];

    /// <summary>
    /// Optional list of labels to exclude. PRs with any of these labels are filtered out.
    /// If empty, no PRs are excluded based on labels.
    /// </summary>
    [YamlMember(Alias = "excludeLabels")]
    public List<string> ExcludeLabels { get; set; } = [];
}
