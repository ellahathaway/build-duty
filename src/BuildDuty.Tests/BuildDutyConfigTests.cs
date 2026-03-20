using BuildDuty.Core;
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

    private string WriteConfig(string yaml)
    {
        var path = Path.Combine(_tempDir, ".build-duty.yml");
        File.WriteAllText(path, yaml);
        return path;
    }
}
