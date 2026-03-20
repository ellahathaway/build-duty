using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace BuildDuty.Core;

/// <summary>
/// Creates <see cref="BuildHttpClient"/> instances for a given Azure DevOps organization URL.
/// </summary>
public interface IBuildHttpClientFactory
{
    Task<BuildHttpClient> CreateAsync(string organizationUrl, CancellationToken ct = default);
    Task<GitHttpClient> CreateGitClientAsync(string organizationUrl, CancellationToken ct = default);
}

/// <summary>
/// Creates <see cref="BuildHttpClient"/> instances by acquiring credentials
/// from an <see cref="IAzureDevOpsCredentialProvider"/>.
/// </summary>
public sealed class BuildHttpClientFactory : IBuildHttpClientFactory
{
    private readonly IAzureDevOpsCredentialProvider _credentialProvider;

    public BuildHttpClientFactory(IAzureDevOpsCredentialProvider credentialProvider)
    {
        _credentialProvider = credentialProvider;
    }

    public async Task<BuildHttpClient> CreateAsync(string organizationUrl, CancellationToken ct = default)
    {
        var connection = await ConnectAsync(organizationUrl);
        return connection.GetClient<BuildHttpClient>();
    }

    public async Task<GitHttpClient> CreateGitClientAsync(string organizationUrl, CancellationToken ct = default)
    {
        var connection = await ConnectAsync(organizationUrl);
        return connection.GetClient<GitHttpClient>();
    }

    private async Task<VssConnection> ConnectAsync(string organizationUrl)
    {
        var credentials = await _credentialProvider.GetCredentialsAsync(organizationUrl);
        return new VssConnection(new Uri(organizationUrl), credentials);
    }
}
