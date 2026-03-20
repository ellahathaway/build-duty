using YamlDotNet.Serialization;

namespace BuildDuty.Core.Models;

/// <summary>
/// Configuration for the AI analysis provider.
/// Uses the GitHub Copilot SDK by default.
/// </summary>
public sealed class AiConfig
{
    /// <summary>
    /// The model ID to use for chat completions (e.g. "gpt-4o-mini", "gpt-4o").
    /// When null, the Copilot SDK will use its default model.
    /// </summary>
    [YamlMember(Alias = "model")]
    public string? Model { get; set; }
}
