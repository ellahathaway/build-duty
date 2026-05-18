using System.Diagnostics;
using Maestro.Common;
using Maestro.Common.AzureDevOpsTokens;

namespace BuildDuty.Core;

public class GeneralTokenProvider : IRemoteTokenProvider
{
    private readonly IAzureDevOpsTokenProvider _azdoTokenProvider;
    private readonly GitHubTokenProvider _gitHubTokenProvider;

    public GeneralTokenProvider()
    {
        var options = new AzureDevOpsTokenProviderOptions
        {
            ["default"] = new AzureDevOpsCredentialResolverOptions()
        };
        _azdoTokenProvider = AzureDevOpsTokenProvider.FromStaticOptions(options);
        _gitHubTokenProvider = new GitHubTokenProvider();
    }

    public string GetTokenForRepository(string repoUri)
    {
        if (repoUri.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            repoUri.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            return _azdoTokenProvider.GetTokenForRepository(repoUri) ?? throw new InvalidOperationException($"Failed to retrieve Azure DevOps token for repository: {repoUri}");
        }
        else if (repoUri.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return _gitHubTokenProvider.GetTokenForRepository(repoUri);
        }
        else
        {
            throw new NotSupportedException($"Unsupported repository URI: {repoUri}");
        }
    }

    public Task<string?> GetTokenForRepositoryAsync(string repoUri)
    {
        if (repoUri.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            repoUri.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            return _azdoTokenProvider.GetTokenForRepositoryAsync(repoUri);
        }
        else if (repoUri.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return _gitHubTokenProvider.GetTokenForRepositoryAsync(repoUri);
        }
        else
        {
            throw new NotSupportedException($"Unsupported repository URI: {repoUri}");
        }
    }
}
