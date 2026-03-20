using Octokit;

namespace BuildDuty.Core;

/// <summary>
/// Provides GitHub credentials for Octokit clients.
/// </summary>
public interface IGitHubCredentialProvider
{
    Credentials GetCredentials();

    /// <summary>
    /// Returns the raw token string for use with non-Octokit clients.
    /// </summary>
    string GetToken();
}

/// <summary>
/// Resolves GitHub credentials from the <c>gh</c> CLI token or
/// <c>GITHUB_TOKEN</c> environment variable.
/// </summary>
public sealed class GitHubCredentialProvider : IGitHubCredentialProvider
{
    private readonly Lazy<string?> _token;

    private GitHubCredentialProvider(Func<string?> tokenFactory)
    {
        _token = new Lazy<string?>(tokenFactory);
    }

    /// <summary>
    /// Creates a provider that checks <c>GITHUB_TOKEN</c> first, then
    /// falls back to the <c>gh auth token</c> CLI command.
    /// </summary>
    public static GitHubCredentialProvider Create()
    {
        return new GitHubCredentialProvider(() =>
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

            if (string.IsNullOrWhiteSpace(token))
                token = ResolveFromGhCli();

            return string.IsNullOrWhiteSpace(token) ? null : token;
        });
    }

    public Credentials GetCredentials()
    {
        var token = _token.Value;
        return token is null ? Credentials.Anonymous : new Credentials(token);
    }

    public string GetToken() =>
        _token.Value ?? throw new InvalidOperationException(
            "No GitHub token available. Set GITHUB_TOKEN or run 'gh auth login'.");

    private static string? ResolveFromGhCli()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
