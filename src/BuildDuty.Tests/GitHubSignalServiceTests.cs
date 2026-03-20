using BuildDuty.Core;
using BuildDuty.Core.Models;
using Octokit;
using Xunit;

namespace BuildDuty.Tests;

public class GitHubSignalServiceTests
{
    [Fact]
    public void IssueConfig_DefaultsToOpen()
    {
        var config = new GitHubIssueConfig();
        Assert.Equal("open", config.EffectiveState);
    }

    [Fact]
    public void IssueConfig_RespectsExplicitState()
    {
        var config = new GitHubIssueConfig { State = "closed" };
        Assert.Equal("closed", config.EffectiveState);
    }

    [Fact]
    public void PullRequestConfig_DefaultsToOpen()
    {
        var config = new GitHubPullRequestConfig();
        Assert.Equal("open", config.EffectiveState);
    }

    [Fact]
    public void PullRequestConfig_RespectsExplicitState()
    {
        var config = new GitHubPullRequestConfig { State = "all" };
        Assert.Equal("all", config.EffectiveState);
    }

    [Fact]
    public async Task CollectAsync_ReturnsEmpty_WhenNoRepositories()
    {
        var config = new GitHubConfig { Repositories = [] };
        var client = new GitHubClient(new Octokit.ProductHeaderValue("test"));
        var svc = new GitHubSignalService(config, client);

        var items = await svc.CollectAsync();

        Assert.Empty(items);
    }

    [Fact]
    public void GitHubCredentialProvider_Create_DoesNotThrow()
    {
        // Should resolve without throwing even if gh CLI is not installed
        var provider = GitHubCredentialProvider.Create();
        var creds = provider.GetCredentials();
        Assert.NotNull(creds);
    }
}
