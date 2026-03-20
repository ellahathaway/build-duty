namespace BuildDuty.Cli;

/// <summary>
/// Resolves well-known paths for BuildDuty artifacts and assets.
/// </summary>
internal static class Paths
{
    public static string RepoRoot()
    {
        // Walk up from CWD looking for .build-duty.yml or BuildDuty.slnx
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".build-duty.yml")) ||
                File.Exists(Path.Combine(dir.FullName, "BuildDuty.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Returns the path to .build-duty.yml in the current working directory, or null if it does not exist.
    /// </summary>
    public static string? ConfigPath()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), ".build-duty.yml");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Root of config-scoped local storage: .build-duty/&lt;configName&gt;/
    /// </summary>
    public static string DataDir(string configName) =>
        Path.Combine(RepoRoot(), ".build-duty", configName);

    public static string WorkItemsDir(string configName) =>
        Path.Combine(DataDir(configName), "workitems");

    public static string AiRunsDir(string configName) =>
        Path.Combine(DataDir(configName), "ai-runs");

    public static string AssetsDir()
    {
        // First check relative to the exe (packaged tool scenario)
        var exeDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(exeDir, "assets", "ai");
        if (Directory.Exists(candidate))
            return Path.Combine(exeDir, "assets");

        // Fallback to repo-root layout (development scenario)
        return Path.Combine(RepoRoot(), "assets");
    }

    public static string RouterYamlPath() => Path.Combine(AssetsDir(), "ai", "router.yaml");
}
