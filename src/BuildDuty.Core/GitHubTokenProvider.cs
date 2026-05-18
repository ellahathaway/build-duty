using System.Diagnostics;
using Maestro.Common;

namespace BuildDuty.Core;

/// <summary>
/// Token provider for GitHub APIs using the gh CLI for authentication.
/// Requires the user to be logged in via <c>gh auth login</c>.
/// </summary>
internal class GitHubTokenProvider : IRemoteTokenProvider
{
    public string GetTokenForRepository(string repoUri)
        => GetTokenForRepositoryAsync(repoUri).GetAwaiter().GetResult()!;

    public async Task<string?> GetTokenForRepositoryAsync(string repoUri)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to retrieve GitHub token via 'gh auth token' (exit code {process.ExitCode}). " +
                $"Ensure the GitHub CLI is installed and authenticated via 'gh auth login'. Error: {error}");
        }

        return output.Trim();
    }
}
