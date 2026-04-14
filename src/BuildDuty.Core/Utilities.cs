namespace BuildDuty.Core;

public static class Utilities
{
    public static string? FindOnPath(string executable)
    {
        var dirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
