using Maestro.Common;

namespace BuildDuty.Cli.Infrastructure;

public interface IGeneralTokenProvider
{
    string GetTokenForRepository(string repoUri);
    Task<string?> GetTokenForRepositoryAsync(string repoUri);
}

public sealed class GeneralTokenProvider(
    IRemoteTokenProvider azureDevOpsTokenProvider,
    IRemoteTokenProvider githubTokenProvider) : IGeneralTokenProvider
{
    public string GetTokenForRepository(string repoUri)
        => GetProvider(repoUri).GetTokenForRepository(repoUri)
            ?? throw new InvalidOperationException($"No token available for repository '{repoUri}'.");

    public Task<string?> GetTokenForRepositoryAsync(string repoUri)
        => GetProvider(repoUri).GetTokenForRepositoryAsync(repoUri);

    private IRemoteTokenProvider GetProvider(string repoUri)
    {
        if (!Uri.TryCreate(repoUri, UriKind.Absolute, out var uri))
        {
            return githubTokenProvider;
        }

        if (uri.Host.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            return azureDevOpsTokenProvider;
        }

        return githubTokenProvider;
    }
}
