using BuildDuty.Core;
using Dotnet.Release;
using Dotnet.Release.Releases;
using Xunit;

namespace BuildDuty.Tests;

public class ReleaseBranchResolverTests
{
    private static MajorReleaseIndexItem Entry(string channel, SupportPhase phase, string? latestSdk = null) => new(
        ChannelVersion: channel,
        LatestRelease: "",
        LatestReleaseDate: DateOnly.MinValue,
        Security: false,
        LatestRuntime: "",
        LatestSdk: latestSdk ?? "",
        Product: ".NET",
        SupportPhase: phase,
        EolDate: DateOnly.MaxValue,
        ReleaseType: ReleaseType.LTS,
        ReleasesJson: "",
        PatchReleasesInfoUri: "");

    /// <summary>
    /// Runs the full filtering pipeline: index entries → supported channels → released branches → filter.
    /// channelSdks maps channel → list of SDK versions from the per-channel releases.json.
    /// </summary>
    private static List<string> FilterFromIndex(
        List<string> rawBranches,
        IList<MajorReleaseIndexItem> entries,
        Dictionary<string, List<string>>? channelSdks = null,
        IReadOnlyCollection<SupportPhase>? supportPhases = null,
        int? minVersion = null)
    {
        var supported = ReleaseBranchResolver.GetSupportedChannels(entries, supportPhases, minVersion);
        if (supported is null)
        {
            return ["main"];
        }

        var released = ReleaseBranchResolver.GetReleasedBranches(entries, supported, channelSdks);
        return ReleaseBranchResolver.FilterBranches(rawBranches, supported, released);
    }

    #region CompareSuffix

    [Theory]
    [InlineData(null, "-preview3", 1)]        // GA beats preview
    [InlineData("-rc1", "-preview7", 1)]       // RC beats preview
    [InlineData("-preview3", "-preview2", 1)]  // higher N wins
    [InlineData("-preview3", "-preview3", 0)]  // equal
    [InlineData(null, null, 0)]                // both GA
    public void CompareSuffix_OrdersCorrectly(string? a, string? b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(ReleaseBranchResolver.CompareSuffix(a, b)));
    }

    #endregion

    #region ParseSdkVersion

    [Theory]
    [InlineData("11.0.100-preview.3.26207.106", "11.0", "11.0.1xx", "-preview3")]
    [InlineData("11.0.100-rc.1.26207.106", "11.0", "11.0.1xx", "-rc1")]
    [InlineData("10.0.203", "10.0", "10.0.203", null)]  // GA: specific version, not band
    public void ParseSdkVersion_ParsesCorrectly(string sdk, string expectedChannel, string expectedVersionBase, string? expectedSuffix)
    {
        var result = ReleaseBranchResolver.ParseSdkVersion(sdk);

        Assert.NotNull(result);
        Assert.Equal(expectedChannel, result.Value.Channel);
        Assert.Equal(expectedVersionBase, result.Value.VersionBase);
        Assert.Equal(expectedSuffix, result.Value.Suffix);
    }

    #endregion

    #region FilterBranches (full pipeline from index entries)

    [Fact]
    public void FilterBranches_SelectsLatestSuffix_ExcludesReleased_FiltersUnsupported()
    {
        // Covers: channel filtering, latest suffix selection, released preview
        // exclusion, internal/public separation, no fallback to older previews,
        // GA multi-band exclusion (releases.json has multiple SDKs per release),
        // and GA specific-version excluded while band branch kept.
        var entries = new List<MajorReleaseIndexItem>
        {
            Entry("11.0", SupportPhase.Preview, "11.0.100-preview.3.26207.106"),
            Entry("10.0", SupportPhase.Active, "10.0.203"),
        };
        // Per-channel releases.json: 10.0's latest release shipped SDKs for both bands
        var channelSdks = new Dictionary<string, List<string>>
        {
            ["10.0"] = ["10.0.203", "10.0.107"],
        };
        var branches = new List<string>
        {
            // 11.0 preview: preview3 released, preview4 is next
            "refs/heads/release/11.0.1xx-preview2",            // older — not selected
            "refs/heads/release/11.0.1xx-preview3",            // released — exclude
            "refs/heads/release/11.0.1xx-preview4",            // next — keep
            "refs/heads/internal/release/11.0.1xx-preview3",   // internal released — exclude
            "refs/heads/internal/release/11.0.1xx-preview4",   // internal next — keep
            // 10.0 GA: both released versions (203 and 107) excluded, band branches kept
            "refs/heads/release/10.0.1xx",                     // band — keep
            "refs/heads/release/10.0.107",                     // released (from releases.json) — exclude
            "refs/heads/release/10.0.108",                     // different version — keep
            "refs/heads/release/10.0.2xx",                     // band — keep
            "refs/heads/release/10.0.203",                     // released — exclude
            "refs/heads/release/10.0.3xx",                     // band — keep (no SDKs in this band)
            // Unsupported / non-release
            "refs/heads/release/8.0.1xx",                      // unsupported channel
            "refs/heads/feature/something",                     // non-release ref
        };

        var result = FilterFromIndex(branches, entries, channelSdks);

        Assert.Equal("main", result[0]);
        var releaseBranches = result.Skip(1).ToHashSet();
        Assert.Equal(new HashSet<string>
        {
            "release/11.0.1xx-preview4",
            "internal/release/11.0.1xx-preview4",
            "release/10.0.1xx",
            "release/10.0.108",
            "release/10.0.2xx",
            "release/10.0.3xx",
        }, releaseBranches);
    }

    #endregion
}
