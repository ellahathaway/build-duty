using Microsoft.TeamFoundation.Build.WebApi;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BuildDuty.Signals.Configuration;

public interface IConfigProvider
{
    BuildDutyConfig Get(string configPath);
}

public sealed class ConfigProvider : IConfigProvider
{
    private string? _configPath;
    private BuildDutyConfig? _cachedConfig;

    public BuildDutyConfig Get(string configPath)
    {
        if (_cachedConfig is not null && string.Equals(_configPath, configPath, StringComparison.OrdinalIgnoreCase))
        {
            return _cachedConfig;
        }

        _configPath = configPath;
        _cachedConfig = LoadFromFile(configPath);
        return _cachedConfig;
    }

    /// <summary>
    /// Loads a <see cref="BuildDutyConfig"/> from a YAML string.
    /// </summary>
    public static BuildDutyConfig LoadFromYaml(string yaml)
    {
        var config = BuildConfigDeserializer().Deserialize<BuildDutyConfig>(yaml)
            ?? throw new InvalidOperationException("Deserialization returned null.");

        ValidateConfig(config, "string input");
        return config;
    }

    /// <summary>
    /// Loads a <see cref="BuildDutyConfig"/> from a YAML file path.
    /// </summary>
    public static BuildDutyConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found: {path}", path);
        }

        string yaml;
        try
        {
            yaml = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not read config file '{path}': {ex.Message}", ex);
        }

        BuildDutyConfig config;
        try
        {
            config = BuildConfigDeserializer().Deserialize<BuildDutyConfig>(yaml)
                ?? throw new InvalidOperationException("Deserialization returned null.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to parse config file '{path}': {ex.Message}", ex);
        }

        ValidateConfig(config, path);
        return config;
    }

    private static IDeserializer BuildConfigDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    private static void ValidateConfig(BuildDutyConfig config, string source)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
        {
            throw new InvalidOperationException(
                $"Config from '{source}' is missing a required top-level 'name' field.");
        }

        ApplyDefaults(config);
    }

    /// <summary>
    /// Applies default values for lists that are empty
    /// when the YAML doesn't include them.
    /// </summary>
    private static void ApplyDefaults(BuildDutyConfig config)
    {
        if (config.AzureDevOps is not null)
        {
            foreach (var org in config.AzureDevOps.Organizations)
            {
                foreach (var project in org.Projects)
                {
                    foreach (var pipeline in project.Pipelines)
                    {
                        if (pipeline.Status.Count == 0)
                        {
                            pipeline.Status.AddRange([
                                BuildResult.Failed,
                                BuildResult.PartiallySucceeded,
                                BuildResult.Canceled
                            ]);
                        }

                        if (pipeline.TimelineResults.Count == 0)
                        {
                            pipeline.TimelineResults.AddRange([
                                TaskResult.Failed,
                                TaskResult.SucceededWithIssues,
                                TaskResult.Canceled,
                                TaskResult.Abandoned
                            ]);
                        }

                        if (pipeline.TimelineFilters is not null)
                        {
                            foreach (var filter in pipeline.TimelineFilters)
                            {
                                if (filter.Names.Count == 0)
                                {
                                    filter.Names.Add(".*");
                                }

                                if (filter.Status.Count == 0)
                                {
                                    filter.Status.AddRange([
                                        TaskResult.Failed,
                                        TaskResult.SucceededWithIssues,
                                        TaskResult.Canceled,
                                        TaskResult.Abandoned
                                    ]);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
