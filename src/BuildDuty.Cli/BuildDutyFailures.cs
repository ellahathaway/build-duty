using System.Collections.Concurrent;
using Spectre.Console;

namespace BuildDuty.Cli;

/// <summary>
/// Thrown when BuildDuty detects failures across multiple items.
/// </summary>
internal sealed class BuildDutyException : Exception
{
    public IReadOnlyList<string> FailedItems { get; }

    public BuildDutyException(string phaseName, IReadOnlyList<string> failedItems)
        : base($"BuildDuty failed for '{phaseName}' with {failedItems.Count} error(s).")
    {
        FailedItems = failedItems;
    }
}

/// <summary>
/// Thread-safe collector for BuildDuty failures across parallel agent sessions.
/// </summary>
internal sealed class BuildDutyFailures
{
    private readonly ConcurrentBag<(string ItemId, string Message)> _failures = new();

    public bool HasFailures => !_failures.IsEmpty;

    public void Add(string itemId, string message) => _failures.Add((itemId, message));

    public IReadOnlyList<(string ItemId, string Message)> GetFailures() => _failures.ToList();
}
