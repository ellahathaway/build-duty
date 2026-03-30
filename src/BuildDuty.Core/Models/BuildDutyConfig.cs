using YamlDotNet.Serialization;

namespace BuildDuty.Core.Models;

/// <summary>
/// Represents the top-level .build-duty.yml configuration.
/// The <c>name</c> field is required; it drives local storage isolation.
/// </summary>
public sealed class BuildDutyConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "azureDevOps")]
    public AzureDevOpsConfig? AzureDevOps { get; set; }

    [YamlMember(Alias = "github")]
    public GitHubConfig? GitHub { get; set; }

    [YamlMember(Alias = "ai")]
    public AiConfig? Ai { get; set; }
}
