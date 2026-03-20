using BuildDuty.Core;
using Xunit;

namespace BuildDuty.Tests;

public class ReleaseBranchResolverTests
{
    [Fact]
    public void CollectReleaseBranchLabels_CategorizesBranches()
    {
        var branches = new[]
        {
            "main",
            "release/9.0.1xx",
            "release/8.0.4xx",
            "internal/release/8.0.4xx",
            "release/8.0.401",
            "users/someone/feature",
        };

        var result = ReleaseBranchResolver.CollectReleaseBranchLabels(branches);

        Assert.True(result.HasMain);
        Assert.Equal(3, result.PublicBranches.Count);
        Assert.Contains("9.0.1xx", result.PublicBranches.Keys);
        Assert.Contains("8.0.4xx", result.PublicBranches.Keys);
        Assert.Contains("8.0.401", result.PublicBranches.Keys);
        Assert.Single(result.InternalBranches);
        Assert.Contains("8.0.4xx", result.InternalBranches.Keys);
    }

    [Fact]
    public void FilterLabels_KeepsOnlySupportedChannels()
    {
        var labels = new[] { "9.0.1xx", "8.0.4xx", "7.0.3xx", "8.0.401" };
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "8.0", "9.0" };

        var result = ReleaseBranchResolver.FilterLabels(labels, supported);

        Assert.Contains("9.0.1xx", result);
        Assert.Contains("8.0.4xx", result);
        Assert.Contains("8.0.401", result);
        Assert.DoesNotContain("7.0.3xx", result);
    }

    [Fact]
    public void FilterByPreviewAndLatestSpecific_KeepsLatestPreviewAndSpecific()
    {
        var labels = new[]
        {
            "9.0.1xx",           // feature band, non-preview → keep
            "9.0.100-preview1",  // preview → only if latest
            "9.0.100-preview3",  // preview → latest → keep
            "9.0.100-rc1",       // rc > preview → keep (rc beats preview)
            "9.0.101",           // specific → only if latest
            "9.0.103",           // specific → latest → keep
        };

        var result = ReleaseBranchResolver.FilterByPreviewAndLatestSpecific(labels);

        Assert.Contains("9.0.1xx", result);    // feature band kept
        Assert.Contains("9.0.100-rc1", result); // rc beats preview
        Assert.Contains("9.0.103", result);     // latest specific
        Assert.DoesNotContain("9.0.100-preview1", result); // not latest preview
        Assert.DoesNotContain("9.0.101", result);          // not latest specific
    }

    [Fact]
    public void SortLabels_SortsByVersionDescending()
    {
        var labels = new[] { "8.0.4xx", "9.0.1xx", "8.0.401", "10.0.1xx" };

        var result = ReleaseBranchResolver.SortLabels(labels);

        Assert.Equal("10.0.1xx", result[0]);
        Assert.Equal("9.0.1xx", result[1]);
    }

    [Fact]
    public void CollectReleaseBranchLabels_NoMain_ReturnsFalse()
    {
        var branches = new[] { "release/9.0.1xx", "release/8.0.4xx" };

        var result = ReleaseBranchResolver.CollectReleaseBranchLabels(branches);

        Assert.False(result.HasMain);
        Assert.Equal(2, result.PublicBranches.Count);
    }

    [Fact]
    public void FilterByPreviewAndLatestSpecific_WithReleasedSdks_FiltersStaleSpecificVersions()
    {
        var labels = new[]
        {
            "10.0.1xx",   // feature band → always keep
            "10.0.100",   // specific → stale (10.0.105 released)
            "10.0.101",   // specific → stale
            "10.0.104",   // specific → stale
            "10.0.2xx",   // feature band → always keep
            "10.0.200",   // specific → stale (10.0.201 released)
        };

        // Simulate released SDK versions from releases.json
        var releaseData = new ReleaseBranchResolver.ReleaseData(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "10.0.100", "10.0.101", "10.0.102", "10.0.103", "10.0.104", "10.0.105",
                "10.0.200", "10.0.201",
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var result = ReleaseBranchResolver.FilterByPreviewAndLatestSpecific(labels, releaseData);

        Assert.Contains("10.0.1xx", result);   // feature band kept
        Assert.Contains("10.0.2xx", result);   // feature band kept
        Assert.DoesNotContain("10.0.100", result); // superseded by 10.0.105
        Assert.DoesNotContain("10.0.101", result); // superseded by 10.0.105
        Assert.DoesNotContain("10.0.104", result); // superseded by 10.0.105
        Assert.DoesNotContain("10.0.200", result); // superseded by 10.0.201
    }

    [Fact]
    public void FilterByPreviewAndLatestSpecific_KeepsLatestSpecificWhenNoNewerRelease()
    {
        var labels = new[]
        {
            "10.0.1xx",
            "10.0.104",   // latest specific, no 10.0.105 released
        };

        // Only versions up to 10.0.104 have been released
        var releaseData = new ReleaseBranchResolver.ReleaseData(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "10.0.100", "10.0.101", "10.0.102", "10.0.103", "10.0.104",
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var result = ReleaseBranchResolver.FilterByPreviewAndLatestSpecific(labels, releaseData);

        Assert.Contains("10.0.1xx", result);
        Assert.Contains("10.0.104", result); // kept — no newer release
    }

    [Fact]
    public void FilterByPreviewAndLatestSpecific_FiltersReleasedPreviews()
    {
        var labels = new[]
        {
            "11.0.1xx-preview1",
            "11.0.1xx-preview2",
        };

        // preview1 and preview2 have both been released
        var releaseData = new ReleaseBranchResolver.ReleaseData(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "11.0-preview-1",
                "11.0-preview-2",
            });

        var result = ReleaseBranchResolver.FilterByPreviewAndLatestSpecific(labels, releaseData);

        Assert.DoesNotContain("11.0.1xx-preview1", result);
        Assert.DoesNotContain("11.0.1xx-preview2", result);
    }

    [Fact]
    public void FilterByPreviewAndLatestSpecific_KeepsUnreleasedPreview()
    {
        var labels = new[]
        {
            "11.0.1xx-preview1",
            "11.0.1xx-preview2",
            "11.0.1xx-preview3",
        };

        // Only preview1 and preview2 have been released; preview3 hasn't
        var releaseData = new ReleaseBranchResolver.ReleaseData(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "11.0-preview-1",
                "11.0-preview-2",
            });

        var result = ReleaseBranchResolver.FilterByPreviewAndLatestSpecific(labels, releaseData);

        Assert.DoesNotContain("11.0.1xx-preview1", result);
        Assert.DoesNotContain("11.0.1xx-preview2", result);
        Assert.Contains("11.0.1xx-preview3", result); // kept — not yet released
    }
}
