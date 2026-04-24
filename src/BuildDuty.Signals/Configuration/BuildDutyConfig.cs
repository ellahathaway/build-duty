namespace BuildDuty.Signals.Configuration;

/// <summary>
/// Top-level build-duty YAML configuration.
/// The <c>Name</c> property is required and identifies this configuration.
/// </summary>
public sealed class BuildDutyConfig
{
    public string Name { get; set; } = string.Empty;

    public AzureDevOpsConfig? AzureDevOps { get; set; }

    public GitHubConfig? GitHub { get; set; }

    public AiConfig? Ai { get; set; }
}

/// <summary>
/// Configuration for the AI analysis provider.
/// </summary>
public sealed class AiConfig
{
    /// <summary>
    /// The model ID to use for chat completions (e.g. "gpt-4o-mini", "gpt-4o").
    /// </summary>
    public string? Model { get; set; }
}
