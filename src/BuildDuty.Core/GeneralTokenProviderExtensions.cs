using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;
using Octokit;

namespace BuildDuty.Core;

public static class GeneralTokenProviderExtensions
{
    public static async Task<VssConnection> GetAzureDevOpsConnectionAsync(this IGeneralTokenProvider tokenProvider, string organizationUrl)
    {
        // Ensure trailing slash so Maestro's AzureDevOpsTokenProvider regex can extract the account name
        var repoUrl = organizationUrl.TrimEnd('/') + "/";
        var token = await tokenProvider.GetTokenForRepositoryAsync(repoUrl);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"No access token available for Azure DevOps organization '{organizationUrl}'.");
        }

        var credentials = new VssOAuthAccessTokenCredential(token);
        return new VssConnection(new Uri(organizationUrl), credentials);
    }

    public static async Task<GitHubClient> GetGitHubClientAsync(this IGeneralTokenProvider tokenProvider, string organization, string repository)
    {
        var repoUrl = $"https://github.com/{organization}/{repository}";
        var token = await tokenProvider.GetTokenForRepositoryAsync(repoUrl);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"No access token available for GitHub repository '{repoUrl}'.");
        }

        return new GitHubClient(new ProductHeaderValue("BuildDuty"))
        {
            Credentials = new Credentials(token)
        };
    }
}
