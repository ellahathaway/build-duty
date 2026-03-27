namespace BuildDuty.Core;

public interface ISignal
{
    string Id { get; }
    SignalType Type { get; }
    object Info { get; }
    string? Summary { get; set; }
    List<string> WorkItemIds { get; }
}

public abstract class Signal<TInfo> : ISignal
    where TInfo : class
{
    public string Id { get; init; } = IdGenerator.NewSignalId();

    public abstract SignalType Type { get; }

    public required TInfo Info { get; set; }

    public string? Summary { get; set; }

    public List<string> WorkItemIds { get; set; } = [];

    object ISignal.Info => Info;
}

public enum SignalType
{
    GitHubIssue,
    GitHubPullRequest,
    AzureDevOpsPipeline
}
