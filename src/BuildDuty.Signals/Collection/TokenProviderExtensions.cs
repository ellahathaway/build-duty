using Maestro.Common;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

namespace BuildDuty.Signals.Collection;

internal static class TokenProviderExtensions
{
    public static async Task<VssConnection> GetAzureDevOpsConnection(this IRemoteTokenProvider tokenProvider, string organizationUrl)
    {
        var token = await tokenProvider.GetTokenForRepositoryAsync(organizationUrl.TrimEnd('/') + "/");
        var credentials = new VssOAuthAccessTokenCredential(token);
        return new VssConnection(new Uri(organizationUrl), credentials);
    }

    public static async Task<BuildHttpClient> GetAzureDevOpsBuildClient(this IRemoteTokenProvider tokenProvider, string organizationUrl)
    {
        var connection = await tokenProvider.GetAzureDevOpsConnection(organizationUrl);
        return await connection.GetClientAsync<BuildHttpClient>();
    }

    public static async Task<GitHttpClient> GetAzureDevOpsGitClient(this IRemoteTokenProvider tokenProvider, string organizationUrl)
    {
        var connection = await tokenProvider.GetAzureDevOpsConnection(organizationUrl);
        return await connection.GetClientAsync<GitHttpClient>();
    }
}
