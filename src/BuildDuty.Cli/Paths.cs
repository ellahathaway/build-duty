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

    public static string WorkItemsDir() => Path.Combine(RepoRoot(), "artifacts", "workitems");
    public static string AiRunsDir() => Path.Combine(RepoRoot(), "artifacts", "ai");

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
