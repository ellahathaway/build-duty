using YamlDotNet.Serialization;

namespace BuildDuty.Core.Models;

public sealed class GitHubConfig
{
    [YamlMember(Alias = "repositories")]
    public List<GitHubRepositoryConfig> Repositories { get; set; } = [];
}

public sealed class GitHubRepositoryConfig
{
    [YamlMember(Alias = "owner")]
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    /// Alias for <see cref="Owner"/> — accepts "organization" in YAML as well.
    /// </summary>
    [YamlMember(Alias = "organization")]
    public string? Organization
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(Owner))
                Owner = value;
        }
    }

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "issues")]
    public GitHubIssueConfig? Issues { get; set; }

    [YamlMember(Alias = "pullRequests")]
    public GitHubPullRequestConfig? PullRequests { get; set; }
}

public sealed class GitHubIssueConfig
{
    [YamlMember(Alias = "labels")]
    public List<string> Labels { get; set; } = [];

    [YamlMember(Alias = "state")]
    public string? State { get; set; }

    /// <summary>
    /// Returns the effective state filter, defaulting to <c>open</c>.
    /// </summary>
    public string EffectiveState => string.IsNullOrWhiteSpace(State) ? "open" : State;
}

public sealed class GitHubPullRequestConfig
{
    [YamlMember(Alias = "labels")]
    public List<string> Labels { get; set; } = [];

    [YamlMember(Alias = "state")]
    public string? State { get; set; }

    /// <summary>
    /// Returns the effective state filter, defaulting to <c>open</c>.
    /// </summary>
    public string EffectiveState => string.IsNullOrWhiteSpace(State) ? "open" : State;
}
