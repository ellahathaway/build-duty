using BuildDuty.Signals.Configuration;
using Dotnet.Release;
using Microsoft.TeamFoundation.Build.WebApi;
using Xunit;

namespace BuildDuty.Signals.Tests;

public class ConfigProviderTests
{
    [Fact]
    public void LoadFromYaml_WithMinimalConfig_ReturnsConfig()
    {
        var yaml = """
            name: test-config
            """;

        var config = ConfigProvider.LoadFromYaml(yaml);

        Assert.Equal("test-config", config.Name);
        Assert.Null(config.AzureDevOps);
        Assert.Null(config.GitHub);
    }

    [Fact]
    public void LoadFromYaml_WithMissingName_Throws()
    {
        var yaml = """
            azureDevOps:
              organizations: []
            """;

        Assert.Throws<InvalidOperationException>(() => ConfigProvider.LoadFromYaml(yaml));
    }

    [Fact]
    public void LoadFromYaml_WithGitHubConfig_ParsesOrganizationsAndRepos()
    {
        var yaml = """
            name: gh-test
            gitHub:
              organizations:
                - name: dotnet
                  repositories:
                    - name: sdk
                      issues:
                        - name: ".*"
                          labels:
                            - ops-monitor
                          context: Track operational issues.
                      prs:
                        - name: "Update.*"
                          authors:
                            - dotnet-bot
                          excludeLabels:
                            - backport
            """;

        var config = ConfigProvider.LoadFromYaml(yaml);

        Assert.NotNull(config.GitHub);
        Assert.Single(config.GitHub.Organizations);
        var org = config.GitHub.Organizations[0];
        Assert.Equal("dotnet", org.Name);
        Assert.Single(org.Repositories);

        var repo = org.Repositories[0];
        Assert.Equal("sdk", repo.Name);

        Assert.NotNull(repo.Issues);
        Assert.Single(repo.Issues);
        Assert.Equal(".*", repo.Issues[0].Name);
        Assert.Single(repo.Issues[0].Labels);
        Assert.Equal("ops-monitor", repo.Issues[0].Labels[0]);
        Assert.Equal("Track operational issues.", repo.Issues[0].Context);

        Assert.NotNull(repo.PullRequests);
        Assert.Single(repo.PullRequests);
        Assert.Equal("Update.*", repo.PullRequests[0].Name);
        Assert.Single(repo.PullRequests[0].Authors);
        Assert.Equal("dotnet-bot", repo.PullRequests[0].Authors[0]);
        Assert.Single(repo.PullRequests[0].ExcludeLabels);
        Assert.Equal("backport", repo.PullRequests[0].ExcludeLabels[0]);
    }

    [Fact]
    public void LoadFromYaml_WithAzureDevOpsConfig_ParsesPipelines()
    {
        var yaml = """
            name: ado-test
            azureDevOps:
              organizations:
                - url: https://dev.azure.com/dnceng
                  projects:
                    - name: internal
                      pipelines:
                        - id: 1525
                          name: Source Build Outer Loop
                          age: "20d"
                          context: VMR source-only build outer loop.
                          branches:
                            - main
                            - release/9.0
                          status:
                            - Failed
                            - Canceled
                          timelineFilters:
                            - type: Stage
                              names:
                                - "^VMR Source.*"
                              status:
                                - Failed
            """;

        var config = ConfigProvider.LoadFromYaml(yaml);

        Assert.NotNull(config.AzureDevOps);
        Assert.Single(config.AzureDevOps.Organizations);
        var org = config.AzureDevOps.Organizations[0];
        Assert.Equal("https://dev.azure.com/dnceng", org.Url);

        var project = org.Projects[0];
        Assert.Equal("internal", project.Name);

        var pipeline = project.Pipelines[0];
        Assert.Equal(1525, pipeline.Id);
        Assert.Equal("Source Build Outer Loop", pipeline.Name);
        Assert.Equal("20d", pipeline.Age);
        Assert.Equal("VMR source-only build outer loop.", pipeline.Context);
        Assert.Equal(2, pipeline.Branches.Count);
        Assert.Equal("main", pipeline.Branches[0]);
        Assert.Equal(2, pipeline.Status.Count);
        Assert.Contains(BuildResult.Failed, pipeline.Status);
        Assert.Contains(BuildResult.Canceled, pipeline.Status);

        Assert.NotNull(pipeline.TimelineFilters);
        Assert.Single(pipeline.TimelineFilters);
        var filter = pipeline.TimelineFilters[0];
        Assert.Equal(TimelineRecordType.Stage, filter.Type);
        Assert.Single(filter.Names);
        Assert.Equal("^VMR Source.*", filter.Names[0]);
        Assert.Single(filter.Status);
        Assert.Equal(TaskResult.Failed, filter.Status[0]);
    }

    [Fact]
    public void LoadFromYaml_WithReleaseBranchConfig_Parses()
    {
        var yaml = """
            name: release-test
            azureDevOps:
              organizations:
                - url: https://dev.azure.com/dnceng
                  projects:
                    - name: internal
                      pipelines:
                        - id: 100
                          name: Test Pipeline
                          release:
                            repository: dotnet-dotnet
                            minVersion: 10
                            supportPhases:
                              - Active
                              - Maintenance
                              - Preview
            """;

        var config = ConfigProvider.LoadFromYaml(yaml);

        var pipeline = config.AzureDevOps!.Organizations[0].Projects[0].Pipelines[0];
        Assert.NotNull(pipeline.Release);
        Assert.Equal("dotnet-dotnet", pipeline.Release.Repository);
        Assert.Equal(10, pipeline.Release.MinVersion);
        Assert.Equal(3, pipeline.Release.SupportPhases.Count);
        Assert.Contains(SupportPhase.Active, pipeline.Release.SupportPhases);
        Assert.Contains(SupportPhase.Preview, pipeline.Release.SupportPhases);
    }

    [Fact]
    public void LoadFromFile_WithMissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            ConfigProvider.LoadFromFile(@"C:\nonexistent\path\.build-duty.yml"));
    }

    [Fact]
    public void LoadFromYaml_AppliesDefaults_WhenStatusEmpty()
    {
        var yaml = """
            name: defaults-test
            azureDevOps:
              organizations:
                - url: https://dev.azure.com/dnceng
                  projects:
                    - name: internal
                      pipelines:
                        - id: 1
                          name: Test
                          branches:
                            - main
            """;

        var config = ConfigProvider.LoadFromYaml(yaml);

        var pipeline = config.AzureDevOps!.Organizations[0].Projects[0].Pipelines[0];
        Assert.Equal(3, pipeline.Status.Count);
        Assert.Contains(BuildResult.Failed, pipeline.Status);
        Assert.Contains(BuildResult.PartiallySucceeded, pipeline.Status);
        Assert.Contains(BuildResult.Canceled, pipeline.Status);

        Assert.Equal(4, pipeline.TimelineResults.Count);
        Assert.Contains(TaskResult.Failed, pipeline.TimelineResults);
    }

    [Fact]
    public void LoadFromYaml_WithAiConfig_Parses()
    {
        var yaml = """
            name: ai-test
            ai:
              model: gpt-4o
            """;

        var config = ConfigProvider.LoadFromYaml(yaml);

        Assert.NotNull(config.Ai);
        Assert.Equal("gpt-4o", config.Ai.Model);
    }
}
