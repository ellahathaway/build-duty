using BuildDuty.Core;
using System.Text.Json.Serialization;

namespace BuildDuty.AI;

/// <summary>
/// Result of a single AI invocation against a signal.
/// </summary>
public sealed class SignalResult
{
    [JsonPropertyName("signal")]
    public required ISignal Signal { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("response")]
    public required string Response { get; init; }
}
