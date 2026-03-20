using Microsoft.TeamFoundation.Build.WebApi;
using BuildDuty.Core;
using BuildDuty.Core.Models;
using Xunit;

namespace BuildDuty.Tests;

public class AzureDevOpsSignalServiceTests
{
    [Fact]
    public async Task CollectAsync_ReturnsEmpty_WhenNoOrganizations()
    {
        var config = new AzureDevOpsConfig { Organizations = [] };
        var factory = new FakeBuildHttpClientFactory();
        var svc = new AzureDevOpsSignalService(config, factory);

        var items = await svc.CollectAsync();

        Assert.Empty(items);
    }

    [Fact]
    public void PipelineStatus_DefaultsToFailedAndPartiallySucceeded()
    {
        var pipeline = new AzureDevOpsPipelineConfig { Id = 1, Name = "test" };
        Assert.Equal(["failed", "partiallySucceeded"], pipeline.EffectiveStatus);
    }

    [Theory]
    [InlineData("https://dev.azure.com/dnceng", "dnceng")]
    [InlineData("https://dev.azure.com/dnceng/", "dnceng")]
    [InlineData("https://dev.azure.com/myorg/myproject", "myorg")]
    public void ExtractAccountName_ParsesOrgFromUrl(string url, string expected)
    {
        Assert.Equal(expected, AzureDevOpsCredentialProvider.ExtractAccountName(url));
    }

    private sealed class FakeBuildHttpClientFactory : IBuildHttpClientFactory
    {
        public Task<BuildHttpClient> CreateAsync(string organizationUrl, CancellationToken ct = default)
            => throw new InvalidOperationException("Should not be called when there are no organizations");
    }
}
