using Maestro.Common.AzureDevOpsTokens;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.OAuth;

namespace BuildDuty.Core;

/// <summary>
/// Provides Azure DevOps <see cref="VssCredentials"/> for connecting to
/// organizations. Bridges arcade-services' <see cref="IAzureDevOpsTokenProvider"/>
/// token strings into the VSS credential types expected by <c>VssConnection</c>.
/// </summary>
public interface IAzureDevOpsCredentialProvider
{
    Task<VssCredentials> GetCredentialsAsync(string organizationUrl, CancellationToken ct = default);
}

/// <summary>
/// Default implementation that delegates token acquisition to the arcade-services
/// <see cref="IAzureDevOpsTokenProvider"/> and wraps the result as an OAuth bearer
/// credential for <c>VssConnection</c>.
/// </summary>
public sealed class AzureDevOpsCredentialProvider : IAzureDevOpsCredentialProvider
{
    private readonly IAzureDevOpsTokenProvider _tokenProvider;

    public AzureDevOpsCredentialProvider(IAzureDevOpsTokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// Creates a provider configured for interactive (developer) use.
    /// Uses browser auth, cached Azure CLI sessions, or Visual Studio credentials.
    /// </summary>
    public static AzureDevOpsCredentialProvider CreateInteractive()
    {
        var options = new AzureDevOpsTokenProviderOptions
        {
            ["default"] = new AzureDevOpsCredentialResolverOptions
            {
                DisableInteractiveAuth = false,
            }
        };
        return new AzureDevOpsCredentialProvider(
            AzureDevOpsTokenProvider.FromStaticOptions(options));
    }

    /// <summary>
    /// Creates a provider configured for CI/pipeline use.
    /// Checks <c>SYSTEM_ACCESSTOKEN</c> and <c>AZURE_DEVOPS_PAT</c> env vars
    /// first, then falls back to <c>AzureCliCredential</c> (service connections).
    /// </summary>
    public static AzureDevOpsCredentialProvider CreateForCi()
    {
        // Check for explicit tokens from the environment.
        var pat = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN")
               ?? Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT");

        var options = new AzureDevOpsTokenProviderOptions
        {
            ["default"] = pat is not null
                ? new AzureDevOpsCredentialResolverOptions { Token = pat }
                : new AzureDevOpsCredentialResolverOptions { DisableInteractiveAuth = true }
        };

        return new AzureDevOpsCredentialProvider(
            AzureDevOpsTokenProvider.FromStaticOptions(options));
    }

    public async Task<VssCredentials> GetCredentialsAsync(
        string organizationUrl, CancellationToken ct = default)
    {
        var token = await _tokenProvider.GetTokenForAccountAsync(
            ExtractAccountName(organizationUrl));
        return new VssOAuthAccessTokenCredential(token);
    }

    public static string ExtractAccountName(string organizationUrl)
    {
        // https://dev.azure.com/myorg → myorg
        var uri = new Uri(organizationUrl.TrimEnd('/'));
        return uri.AbsolutePath.Trim('/').Split('/')[0];
    }
}
