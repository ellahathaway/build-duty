using Azure.Core;
using Azure.Identity;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.OAuth;

namespace BuildDuty.Core;

/// <summary>
/// Provides Azure DevOps <see cref="VssCredentials"/> for connecting to
/// organizations.
/// </summary>
public interface IAzureDevOpsCredentialProvider
{
    Task<VssCredentials> GetCredentialsAsync(string organizationUrl, CancellationToken ct = default);
}

/// <summary>
/// Acquires Azure DevOps tokens using <c>Azure.Identity</c>. Tries
/// <see cref="AzureCliCredential"/> first (fast, no UI), then falls back to
/// <see cref="InteractiveBrowserCredential"/> (opens a browser window).
/// This matches the credential chain used by the Azure DevOps MCP server.
/// </summary>
public sealed class AzureDevOpsCredentialProvider : IAzureDevOpsCredentialProvider
{
    private static readonly string[] Scopes = ["https://app.vssps.visualstudio.com/.default"];

    private readonly TokenCredential _credential;

    public AzureDevOpsCredentialProvider(TokenCredential credential)
    {
        _credential = credential;
    }

    /// <summary>
    /// Creates a provider configured for interactive (developer) use.
    /// Tries Azure CLI first, then opens a browser window for login.
    /// </summary>
    public static AzureDevOpsCredentialProvider CreateInteractive()
    {
        var credential = new ChainedTokenCredential(
            new AzureCliCredential(),
            new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                AdditionallyAllowedTenants = { "*" },
            }));

        return new AzureDevOpsCredentialProvider(credential);
    }

    /// <summary>
    /// Creates a provider configured for CI/pipeline use.
    /// Checks <c>SYSTEM_ACCESSTOKEN</c> and <c>AZURE_DEVOPS_PAT</c> env vars
    /// first, then falls back to <c>AzureCliCredential</c> (no interactive auth).
    /// </summary>
    public static AzureDevOpsCredentialProvider CreateForCi()
    {
        var pat = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN")
               ?? Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT");

        if (pat is not null)
        {
            // PATs are basic auth, not OAuth — wrap as VssBasicCredential
            return new AzureDevOpsCredentialProvider(new PatTokenCredential(pat));
        }

        return new AzureDevOpsCredentialProvider(new AzureCliCredential());
    }

    public async Task<VssCredentials> GetCredentialsAsync(
        string organizationUrl, CancellationToken ct = default)
    {
        if (_credential is PatTokenCredential patCred)
            return new VssBasicCredential(string.Empty, patCred.Pat);

        var tokenResult = await _credential.GetTokenAsync(
            new TokenRequestContext(Scopes), ct);
        return new VssOAuthAccessTokenCredential(tokenResult.Token);
    }

    public static string ExtractAccountName(string organizationUrl)
    {
        var uri = new Uri(organizationUrl.TrimEnd('/'));
        return uri.AbsolutePath.Trim('/').Split('/')[0];
    }

    /// <summary>
    /// Sentinel credential for PAT-based auth (CI scenarios).
    /// </summary>
    private sealed class PatTokenCredential : TokenCredential
    {
        public string Pat { get; }

        public PatTokenCredential(string pat) => Pat = pat;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new NotSupportedException("Use GetCredentialsAsync directly for PAT auth.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new NotSupportedException("Use GetCredentialsAsync directly for PAT auth.");
    }
}
