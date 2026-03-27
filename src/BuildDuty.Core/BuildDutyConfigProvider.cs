using System.Text.RegularExpressions;
using BuildDuty.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BuildDuty.Core;

public interface IBuildDutyConfigProvider
{
    BuildDutyConfig Get();
    void SetConfigPath(string? configPath);
}

public sealed class BuildDutyConfigProvider : IBuildDutyConfigProvider
{
    private string? _configPath;
    private BuildDutyConfig? _cachedConfig;

    public BuildDutyConfigProvider()
    {
    }

    public void SetConfigPath(string? configPath)
    {
        var normalized = string.IsNullOrWhiteSpace(configPath) ? null : configPath;
        if (string.Equals(_configPath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _configPath = normalized;
        _cachedConfig = null;
    }

    public BuildDutyConfig Get()
    {
        if (_cachedConfig is not null)
        {
            return _cachedConfig;
        }

        string path = DiscoverConfigPath();
        _cachedConfig = LoadFromFile(path);
        return _cachedConfig;
    }

    private static BuildDutyConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Config file not found: {path}", path);
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

        if (string.IsNullOrWhiteSpace(config.Name))
        {
            throw new InvalidOperationException(
                $"Config file '{path}' is missing a required top-level 'name' field.");
        }

        return config;
    }

    private static IDeserializer BuildConfigDeserializer()
    {
        return new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new RegexYamlTypeConverter())
            .IgnoreUnmatchedProperties()
            .Build();
    }

    private string DiscoverConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(_configPath))
        {
            return _configPath;
        }

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, ".build-duty.yml");
            if (File.Exists(path))
            {
                return path;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "No .build-duty.yml found in the current directory or any parent directories. Use --config to specify a path.");
    }
}

internal sealed class RegexYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Regex);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        return new Regex(scalar.Value, RegexOptions.IgnoreCase);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var regex = (Regex)value!;
        emitter.Emit(new Scalar(regex.ToString()));
    }
}
