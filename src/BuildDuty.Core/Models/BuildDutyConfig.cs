using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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

    /// <summary>
    /// Load a <see cref="BuildDutyConfig"/> from a YAML file, validating that a
    /// non-empty <c>name</c> field is present.
    /// </summary>
    public static BuildDutyConfig LoadFromFile(string path)
    {
        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<BuildDutyConfig>(yaml)
            ?? throw new InvalidOperationException(
                $"Failed to parse config file: {path}");

        if (string.IsNullOrWhiteSpace(config.Name))
        {
            throw new InvalidOperationException(
                $"Config file '{path}' is missing a required top-level 'name' field.");
        }

        return config;
    }
}
