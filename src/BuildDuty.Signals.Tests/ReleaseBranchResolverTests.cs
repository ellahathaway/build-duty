using BuildDuty.Signals.Collection;
using Dotnet.Release;
using Dotnet.Release.Releases;
using Xunit;

namespace BuildDuty.Signals.Tests;

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
    [InlineData(null, "-preview3", 1)]
    [InlineData("-rc1", "-preview7", 1)]
    [InlineData("-preview3", "-preview2", 1)]
    [InlineData("-preview3", "-preview3", 0)]
    [InlineData(null, null, 0)]
    public void CompareSuffix_OrdersCorrectly(string? a, string? b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(ReleaseBranchResolver.CompareSuffix(a, b)));
    }

    #endregion

    #region ParseSdkVersion

    [Theory]
    [InlineData("11.0.100-preview.3.26207.106", "11.0", "11.0.1xx", "-preview3")]
    [InlineData("11.0.100-rc.1.26207.106", "11.0", "11.0.1xx", "-rc1")]
    [InlineData("10.0.203", "10.0", "10.0.203", null)]
    public void ParseSdkVersion_ParsesCorrectly(string sdk, string expectedChannel, string expectedVersionBase, string? expectedSuffix)
    {
        var result = ReleaseBranchResolver.ParseSdkVersion(sdk);

        Assert.NotNull(result);
        Assert.Equal(expectedChannel, result.Value.Channel);
        Assert.Equal(expectedVersionBase, result.Value.VersionBase);
        Assert.Equal(expectedSuffix, result.Value.Suffix);
    }

    #endregion

    #region FilterBranches

    [Fact]
    public void FilterBranches_SelectsLatestSuffix_ExcludesReleased_FiltersUnsupported()
    {
        var entries = new List<MajorReleaseIndexItem>
        {
            Entry("11.0", SupportPhase.Preview, "11.0.100-preview.3.26207.106"),
            Entry("10.0", SupportPhase.Active, "10.0.203"),
        };
        var channelSdks = new Dictionary<string, List<string>>
        {
            ["10.0"] = ["10.0.203", "10.0.107"],
        };
        var branches = new List<string>
        {
            "refs/heads/release/11.0.1xx-preview2",
            "refs/heads/release/11.0.1xx-preview3",
            "refs/heads/release/11.0.1xx-preview4",
            "refs/heads/internal/release/11.0.1xx-preview3",
            "refs/heads/internal/release/11.0.1xx-preview4",
            "refs/heads/release/10.0.1xx",
            "refs/heads/release/10.0.107",
            "refs/heads/release/10.0.108",
            "refs/heads/release/10.0.2xx",
            "refs/heads/release/10.0.203",
            "refs/heads/release/10.0.3xx",
            "refs/heads/release/8.0.1xx",
            "refs/heads/feature/something",
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
