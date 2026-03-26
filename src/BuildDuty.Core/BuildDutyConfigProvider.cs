using System.Text.RegularExpressions;
using BuildDuty.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BuildDuty.Core;

public interface IBuildDutyConfigProvider
{
    BuildDutyConfig GetConfig();
    BuildDutyConfig InitializeConfig(string configPath);
}

public sealed class BuildDutyConfigProvider : IBuildDutyConfigProvider
{
    private BuildDutyConfig? _config;

    public BuildDutyConfig GetConfig()
    {
        if (_config is null)
        {
            throw new InvalidOperationException(
                "Config has not been initialized. Call InitializeConfig() with a valid config path before accessing Config.");
        }

        return _config;
    }

    public BuildDutyConfig InitializeConfig(string configPath)
    {
        if (_config != null)
        {
            throw new InvalidOperationException("Config has already been initialized.");
        }

        var yaml = File.ReadAllText(configPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new RegexYamlTypeConverter())
            .IgnoreUnmatchedProperties()
            .Build();

        _config = deserializer.Deserialize<BuildDutyConfig>(yaml)
            ?? throw new InvalidOperationException(
                $"Failed to parse config file: {configPath}");

        if (string.IsNullOrWhiteSpace(_config.Name))
        {
            throw new InvalidOperationException(
                $"Config file '{configPath}' is missing a required top-level 'name' field.");
        }
        return _config;
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
