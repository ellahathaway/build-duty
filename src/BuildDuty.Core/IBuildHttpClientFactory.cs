using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace BuildDuty.Core;

/// <summary>
/// Creates <see cref="BuildHttpClient"/> instances for a given Azure DevOps organization URL.
/// </summary>
public interface IBuildHttpClientFactory
{
    Task<BuildHttpClient> CreateAsync(string organizationUrl, CancellationToken ct = default);
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
        var credentials = await _credentialProvider.GetCredentialsAsync(organizationUrl);
        var connection = new VssConnection(new Uri(organizationUrl), credentials);
        return connection.GetClient<BuildHttpClient>();
    }
}
