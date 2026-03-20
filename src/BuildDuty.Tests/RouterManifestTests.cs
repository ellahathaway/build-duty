using BuildDuty.AI;
using Xunit;

namespace BuildDuty.Tests;

public class RouterManifestTests
{
    private const string ValidYaml = """
        ai:
          provider: copilot-cli
          jobs:
            summarize:
              skill: summarize-failure
            cluster:
              skill: cluster-incidents
            root-cause:
              skill: diagnose-build-break
            next-actions:
              skill: suggest-next-actions
        """;

    [Fact]
    public void LoadFromYaml_ParsesAllJobs()
    {
        var router = RouterManifest.LoadFromYaml(ValidYaml);

        Assert.Equal(4, router.Jobs.Count);
        Assert.Equal("summarize-failure", router.ResolveSkill("summarize"));
        Assert.Equal("cluster-incidents", router.ResolveSkill("cluster"));
        Assert.Equal("diagnose-build-break", router.ResolveSkill("root-cause"));
        Assert.Equal("suggest-next-actions", router.ResolveSkill("next-actions"));
    }

    [Fact]
    public void ResolveSkill_UnknownJob_Throws()
    {
        var router = RouterManifest.LoadFromYaml(ValidYaml);

        var ex = Assert.Throws<InvalidOperationException>(() => router.ResolveSkill("nonexistent"));
        Assert.Contains("nonexistent", ex.Message);
    }

    [Fact]
    public void TryResolveSkill_KnownJob_ReturnsTrue()
    {
        var router = RouterManifest.LoadFromYaml(ValidYaml);

        Assert.True(router.TryResolveSkill("summarize", out var skill));
        Assert.Equal("summarize-failure", skill);
    }

    [Fact]
    public void TryResolveSkill_UnknownJob_ReturnsFalse()
    {
        var router = RouterManifest.LoadFromYaml(ValidYaml);

        Assert.False(router.TryResolveSkill("nonexistent", out _));
    }

    [Fact]
    public void ResolveSkill_CaseInsensitive()
    {
        var router = RouterManifest.LoadFromYaml(ValidYaml);

        Assert.Equal("summarize-failure", router.ResolveSkill("Summarize"));
        Assert.Equal("summarize-failure", router.ResolveSkill("SUMMARIZE"));
    }
}
