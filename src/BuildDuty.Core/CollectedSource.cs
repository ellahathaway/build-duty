using System.Text.Json;

namespace BuildDuty.Core;

/// <summary>
/// A collected source — the raw data from a source before AI triage.
/// </summary>
public sealed class CollectedSource
{
    /// <summary>Proposed work item ID (e.g. wi_ado_12345).</summary>
    public required string Id { get; init; }

    /// <summary>Proposed title.</summary>
    public required string Title { get; init; }

    /// <summary>Correlation ID for auto-resolution.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Source type (e.g. ado-pipeline-run, github-issue, github-pr).</summary>
    public required string SourceType { get; init; }

    /// <summary>Reference URL to the source item.</summary>
    public required string SourceRef { get; init; }

    /// <summary>Current status of the source (e.g. "failed", "open", "merged").</summary>
    public required string Status { get; init; }

    /// <summary>When the source was last modified (build finish, issue/PR updated_at).</summary>
    public DateTime? SourceUpdatedAtUtc { get; init; }

    /// <summary>Additional metadata for AI context.</summary>
    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>
/// Result of a deterministic work item collection phase.
/// </summary>
public sealed class CollectionResult
{
    public required string Source { get; init; }
    public bool Success { get; init; } = true;
    public string? Error { get; init; }
    public List<CollectedSource> Sources { get; init; } = [];
    public int Created { get; set; }
    public int Resolved { get; set; }
    public TimeSpan Duration { get; set; }

    public string ToJson() => JsonSerializer.Serialize(Sources, new JsonSerializerOptions { WriteIndented = true });
}
