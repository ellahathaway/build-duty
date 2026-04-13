using Maestro.Common;
using Microsoft.DotNet.DarcLib.Helpers;

namespace BuildDuty.Core;

public sealed class GitHubTokenProvider(IProcessManager processManager) : IRemoteTokenProvider
{
    public string GetTokenForRepository(string repoUri)
        => GetTokenForRepositoryAsync(repoUri).GetAwaiter().GetResult()!;

    public async Task<string?> GetTokenForRepositoryAsync(string repoUri)
    {
        var result = await processManager.Execute("gh", ["auth", "token"], timeout: TimeSpan.FromSeconds(10));

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to retrieve GitHub token via 'gh auth token' (exit code {result.ExitCode}). " +
                "Ensure the GitHub CLI is installed and authenticated via 'gh auth login'.");
        }

        var token = result.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "The 'gh auth token' command returned an empty token. Run 'gh auth login' to authenticate.");
        }

        return token;
    }
}
