using System.Text.RegularExpressions;
using BuildDuty.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BuildDuty.Core;

public interface IBuildDutyConfigProvider
{
    string? ConfigPath { get; set; }
    BuildDutyConfig GetConfig();
}

public sealed class BuildDutyConfigProvider : IBuildDutyConfigProvider
{
    private BuildDutyConfig? _config;

    public string? ConfigPath { get; set; }

    public BuildDutyConfig GetConfig()
    {
        if (_config is not null)
        {
            return _config;
        }

        var path = ConfigPath ?? DiscoverConfigPath()
            ?? throw new InvalidOperationException(
                "No .build-duty.yml found in the current directory. Use --config to specify a path.");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Config file not found: {path}", path);
        }

        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new RegexYamlTypeConverter())
            .IgnoreUnmatchedProperties()
            .Build();

        _config = deserializer.Deserialize<BuildDutyConfig>(yaml)
            ?? throw new InvalidOperationException(
                $"Failed to parse config file: {path}");

        if (string.IsNullOrWhiteSpace(_config.Name))
        {
            throw new InvalidOperationException(
                $"Config file '{path}' is missing a required top-level 'name' field.");
        }

        return _config;
    }

    private static string? DiscoverConfigPath()
    {
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
        return null;
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
