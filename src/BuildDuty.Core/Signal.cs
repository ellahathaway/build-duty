using System.Text.Json;

namespace BuildDuty.Core;

public abstract class Signal
{
    public string Id { get; set; } = IdGenerator.NewSignalId();

    public abstract SignalType Type { get; }

    public required JsonElement Info { get; set; }

    public string? Context { get; set; }

    public string? Cause { get; set; }

    public string? Effect { get; set; }

    public string? Evidence { get; set; }

    public List<string> WorkItemIds { get; set; } = new();
}

public enum SignalType
{
    GitHubIssue,
    GitHubPullRequest,
    AzureDevOpsPipeline
}
