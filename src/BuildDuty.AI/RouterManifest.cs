using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BuildDuty.AI;

/// <summary>
/// Loads the built-in router manifest (router.yaml) and resolves job names to bundled skills.
/// </summary>
public sealed class RouterManifest
{
    private readonly Dictionary<string, string> _jobToSkill;

    private RouterManifest(Dictionary<string, string> jobToSkill)
    {
        _jobToSkill = jobToSkill;
    }

    public IReadOnlyDictionary<string, string> Jobs => _jobToSkill;

    /// <summary>
    /// Resolves a job name to the corresponding bundled skill name.
    /// </summary>
    public string ResolveSkill(string jobName)
    {
        if (_jobToSkill.TryGetValue(jobName, out var skill))
            return skill;

        throw new InvalidOperationException(
            $"Unknown job '{jobName}'. Available jobs: {string.Join(", ", _jobToSkill.Keys)}");
    }

    public bool TryResolveSkill(string jobName, out string? skill)
    {
        return _jobToSkill.TryGetValue(jobName, out skill);
    }

    /// <summary>
    /// Loads the router manifest from a YAML file path.
    /// </summary>
    public static RouterManifest LoadFromFile(string path)
    {
        var yaml = File.ReadAllText(path);
        return LoadFromYaml(yaml);
    }

    /// <summary>
    /// Loads the router manifest from a YAML string.
    /// </summary>
    public static RouterManifest LoadFromYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var root = deserializer.Deserialize<RouterYamlRoot>(yaml)
            ?? throw new InvalidOperationException("Failed to parse router manifest.");

        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.Ai?.Jobs is not null)
        {
            foreach (var (job, entry) in root.Ai.Jobs)
            {
                mapping[job] = entry.Skill;
            }
        }

        return new RouterManifest(mapping);
    }

    /// <summary>
    /// Discovers the router.yaml path relative to the tool installation / repo root.
    /// </summary>
    public static string DiscoverManifestPath(string? basePath = null)
    {
        basePath ??= AppContext.BaseDirectory;
        var candidate = Path.Combine(basePath, "assets", "ai", "router.yaml");
        if (File.Exists(candidate))
            return candidate;

        // Fallback: search up from base
        var dir = new DirectoryInfo(basePath);
        while (dir is not null)
        {
            candidate = Path.Combine(dir.FullName, "assets", "ai", "router.yaml");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate assets/ai/router.yaml");
    }

    // Internal YAML deserialization models
    private sealed class RouterYamlRoot
    {
        public AiSection? Ai { get; set; }
    }

    private sealed class AiSection
    {
        public string? Provider { get; set; }
        public Dictionary<string, JobEntry>? Jobs { get; set; }
    }

    private sealed class JobEntry
    {
        public string Skill { get; set; } = string.Empty;
    }
}
