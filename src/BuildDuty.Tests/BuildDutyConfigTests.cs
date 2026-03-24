using BuildDuty.Core.Models;
using Xunit;

namespace BuildDuty.Tests;

public class BuildDutyConfigTests : IDisposable
{
    private readonly string _tempDir;

    public BuildDutyConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"buildduty_cfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void LoadFromFile_ParsesName()
    {
        var path = WriteConfig("name: my-project\nAzureDevOps:\n  organizations: []");
        var config = BuildDutyConfig.LoadFromFile(path);
        Assert.Equal("my-project", config.Name);
    }

    [Fact]
    public void LoadFromFile_MissingName_Throws()
    {
        var path = WriteConfig("AzureDevOps:\n  organizations: []");
        var ex = Assert.Throws<InvalidOperationException>(() => BuildDutyConfig.LoadFromFile(path));
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromFile_EmptyName_Throws()
    {
        var path = WriteConfig("name: \"\"\nAzureDevOps:\n  organizations: []");
        var ex = Assert.Throws<InvalidOperationException>(() => BuildDutyConfig.LoadFromFile(path));
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromFile_ParsesAdoPipelines()
    {
        var yaml = """
            name: test-project
            azureDevOps:
              organizations:
                - url: https://dev.azure.com/myorg
                  projects:
                    - name: myproject
                      pipelines:
                        - id: 42
                          name: ci-pipeline
                          branches:
                            - main
                            - release/*
                          status:
                            - failed
            """;
        var path = WriteConfig(yaml);
        var config = BuildDutyConfig.LoadFromFile(path);

        Assert.NotNull(config.AzureDevOps);
        var org = Assert.Single(config.AzureDevOps.Organizations);
        Assert.Equal("https://dev.azure.com/myorg", org.Url);
        var project = Assert.Single(org.Projects);
        Assert.Equal("myproject", project.Name);
        var pipeline = Assert.Single(project.Pipelines);
        Assert.Equal(42, pipeline.Id);
        Assert.Equal("ci-pipeline", pipeline.Name);
        Assert.Equal(["main", "release/*"], pipeline.Branches);
        Assert.Equal(["failed"], pipeline.EffectiveStatus);
    }

    [Fact]
    public void PipelineStatus_DefaultsToFailedPartiallySucceededAndCanceled_WhenOmitted()
    {
        var pipeline = new AzureDevOpsPipelineConfig { Id = 1, Name = "test" };
        Assert.Equal(["failed", "partiallySucceeded", "canceled"], pipeline.EffectiveStatus);
    }

    [Theory]
    [InlineData("7d", 7 * 24 * 60)]
    [InlineData("24h", 24 * 60)]
    [InlineData("2d12h", 2 * 24 * 60 + 12 * 60)]
    [InlineData("30m", 30)]
    [InlineData("1d6h30m", 1 * 24 * 60 + 6 * 60 + 30)]
    public void ParsedAge_ParsesDurationString(string age, int expectedMinutes)
    {
        var pipeline = new AzureDevOpsPipelineConfig { Id = 1, Name = "test", Age = age };
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), pipeline.ParsedAge);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ParsedAge_ReturnsNull_WhenEmpty(string? age)
    {
        var pipeline = new AzureDevOpsPipelineConfig { Id = 1, Name = "test", Age = age };
        Assert.Null(pipeline.ParsedAge);
    }

    private string WriteConfig(string yaml)
    {
        var path = Path.Combine(_tempDir, ".build-duty.yml");
        File.WriteAllText(path, yaml);
        return path;
    }
}
