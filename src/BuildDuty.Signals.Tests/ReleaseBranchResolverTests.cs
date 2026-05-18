using System.Reflection;
using BuildDuty.Configuration.Models;
using BuildDuty.Services.Configuration;
using BuildDuty.Signals.Collection;
using Maestro.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildDuty.Signals.Tests;

public class ReleaseBranchResolverTests
{
    private static MajorReleaseIndexItem Entry(string channel, string releasesJsonUrl) => new(
        ChannelVersion: channel,
        LatestRelease: "",
        LatestReleaseDate: DateOnly.MinValue,
        Security: false,
        LatestRuntime: "",
        LatestSdk: "",
        Product: ".NET",
        SupportPhase: SupportPhase.Active,
        EolDate: DateOnly.MaxValue,
        ReleasesJson: releasesJsonUrl);

    private static ReleaseBranchConfig Config(params SupportPhase[] phases) => new()
    {
        MinVersion = 8,
        SupportPhases = phases.ToList(),
    };

    private static ReleaseBranchConfig ConfigWithRange(int min, int max, params SupportPhase[] phases) => new()
    {
        MinVersion = min,
        MaxVersion = max,
        SupportPhases = phases.ToList(),
    };

    [Fact]
    public async Task Main_Included_WhenActiveRequested()
    {
        var resolver = CreateResolver(new Dictionary<string, (SupportPhase Phase, string LatestRelease)>());

        var result = await InvokeMatchAsync(resolver, "refs/heads/main", Config(SupportPhase.Active));

        Assert.True(result);
    }

    [Fact]
    public async Task Main_Excluded_WhenActiveNotRequested()
    {
        var resolver = CreateResolver(new Dictionary<string, (SupportPhase Phase, string LatestRelease)>());

        var result = await InvokeMatchAsync(resolver, "refs/heads/main", Config(SupportPhase.Preview));

        Assert.False(result);
    }

    [Fact]
    public async Task UnknownChannel_PreviewBranch_Included_ForPreviewSupport()
    {
        var resolver = CreateResolver(new Dictionary<string, (SupportPhase Phase, string LatestRelease)>());

        var result = await InvokeMatchAsync(resolver, "refs/heads/release/12.0.1xx-preview1", Config(SupportPhase.Preview));

        Assert.True(result);
    }

    [Fact]
    public async Task UnknownChannel_NonPreviewBranch_Excluded()
    {
        var resolver = CreateResolver(new Dictionary<string, (SupportPhase Phase, string LatestRelease)>());

        var result = await InvokeMatchAsync(resolver, "refs/heads/release/12.0.1xx", Config(SupportPhase.Preview));

        Assert.False(result);
    }

    [Fact]
    public async Task MaxVersion_ExcludesBranchesAboveMax()
    {
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["9.0"] = new(SupportPhase.Active, "9.0.15", [["9.0.313", "9.0.116"]]),
            ["10.0"] = new(SupportPhase.Active, "10.0.7", [["10.0.203", "10.0.107"]]),
        });

        // maxVersion: 9 → 9.0 included, 10.0 excluded
        Assert.True(await InvokeMatchAsync(resolver, "refs/heads/release/9.0.1xx", ConfigWithRange(8, 9, SupportPhase.Active)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.1xx", ConfigWithRange(8, 9, SupportPhase.Active)));

        // maxVersion: 10 → both included
        Assert.True(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.1xx", ConfigWithRange(10, 10, SupportPhase.Active)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/9.0.1xx", ConfigWithRange(10, 10, SupportPhase.Active)));
    }

    [Fact]
    public async Task Preview_FirstPreview_Included_WhenNoLatestPreviewYet()
    {
        // 11.0 is not in the index yet → unknown channel with a preview branch
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["10.0"] = new(SupportPhase.Active, "10.0.1", [["10.0.100"]]),
        });

        var result = await InvokeMatchAsync(resolver, "refs/heads/release/11.0.1xx-preview1", Config(SupportPhase.Preview));

        Assert.True(result);
    }

    [Fact]
    public async Task Preview_LatestPreview_Excluded()
    {
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["11.0"] = new(SupportPhase.Preview, "11.0.0-preview.3", [["11.0.100-preview.3.26207.106"]]),
        });

        var result = await InvokeMatchAsync(resolver, "refs/heads/release/11.0.1xx-preview3", Config(SupportPhase.Preview));

        Assert.False(result);
    }

    [Fact]
    public async Task Preview_ToRc_Excluded_WhenLatestIsRc()
    {
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["11.0"] = new(SupportPhase.Preview, "11.0.0-rc.1", [["11.0.100-rc.1.25451.107"]]),
        });

        var result = await InvokeMatchAsync(resolver, "refs/heads/release/11.0.1xx-preview7", Config(SupportPhase.Preview));

        Assert.False(result);
    }

    [Fact]
    public async Task GoLive_NewerRc_Included()
    {
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["11.0"] = new(SupportPhase.GoLive, "11.0.0-rc.1", [["11.0.100-rc.1.25451.107"]]),
        });

        var result = await InvokeMatchAsync(resolver, "refs/heads/release/11.0.1xx-rc2", Config(SupportPhase.GoLive));

        Assert.True(result);
    }

    [Fact]
    public async Task Eol_OnlyUpToLatestFeatureBand()
    {
        // EOL: latest released SDKs are in 2xx band (9.0.203) and 1xx band (9.0.106)
        // So latest feature band = 2, meaning 1xx and 2xx are included but 3xx is not
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["9.0"] = new(SupportPhase.Eol, "9.0.6", [["9.0.203", "9.0.106"], ["9.0.202", "9.0.105"]]),
        });

        var include1xx = await InvokeMatchAsync(resolver, "refs/heads/release/9.0.1xx", Config(SupportPhase.Eol));
        var include2xx = await InvokeMatchAsync(resolver, "refs/heads/release/9.0.2xx", Config(SupportPhase.Eol));
        var exclude3xx = await InvokeMatchAsync(resolver, "refs/heads/release/9.0.3xx", Config(SupportPhase.Eol));

        Assert.True(include1xx);
        Assert.True(include2xx);
        Assert.False(exclude3xx);
    }

    [Fact]
    public async Task Active_IncludesFeatureBandBranches()
    {
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["10.0"] = new(SupportPhase.Active, "10.0.7", [["10.0.203", "10.0.107"], ["10.0.202", "10.0.106"], ["10.0.100"]]),
        });

        var include3xx = await InvokeMatchAsync(resolver, "refs/heads/release/10.0.3xx", Config(SupportPhase.Active));
        var include2xx = await InvokeMatchAsync(resolver, "refs/heads/release/10.0.2xx", Config(SupportPhase.Active));
        var include1xx = await InvokeMatchAsync(resolver, "refs/heads/release/10.0.1xx", Config(SupportPhase.Active));

        Assert.True(include3xx);
        Assert.True(include2xx);
        Assert.True(include1xx);
    }

    [Fact]
    public async Task Active_ExcludesReleasedNnnBranches()
    {
        // 10.0.107 and 10.0.203 are released SDKs — their branches should be excluded
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["10.0"] = new(SupportPhase.Active, "10.0.7", [["10.0.203", "10.0.107"], ["10.0.202", "10.0.106"], ["10.0.201", "10.0.105"], ["10.0.200", "10.0.104"], ["10.0.103"], ["10.0.102"], ["10.0.101"], ["10.0.100"]]),
        });

        // Released SDK versions → excluded
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.100", Config(SupportPhase.Active)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.101", Config(SupportPhase.Active)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.107", Config(SupportPhase.Active)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.203", Config(SupportPhase.Active)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/internal/release/10.0.106", Config(SupportPhase.Active)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/internal/release/10.0.202", Config(SupportPhase.Active)));
    }

    [Fact]
    public async Task Active_IncludesUnreleasedNnnBranches()
    {
        // 10.0.108, 10.0.204, 10.0.300 are NOT released yet — should be included
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["10.0"] = new(SupportPhase.Active, "10.0.7", [["10.0.203", "10.0.107"], ["10.0.202", "10.0.106"]]),
        });

        Assert.True(await InvokeMatchAsync(resolver, "refs/heads/internal/release/10.0.108", Config(SupportPhase.Active)));
        Assert.True(await InvokeMatchAsync(resolver, "refs/heads/internal/release/10.0.204", Config(SupportPhase.Active)));
        Assert.True(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.300", Config(SupportPhase.Active)));
        Assert.True(await InvokeMatchAsync(resolver, "refs/heads/internal/release/10.0.4xx", Config(SupportPhase.Active)));
    }

    [Fact]
    public async Task Preview_NextPreviewBranch_Included()
    {
        // preview.3 is latest release → preview.4 branch should be included
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["11.0"] = new(SupportPhase.Preview, "11.0.0-preview.3", [["11.0.100-preview.3.26207.106"], ["11.0.100-preview.2.26159.112"]]),
        });

        Assert.True(await InvokeMatchAsync(resolver, "refs/heads/release/11.0.1xx-preview4", Config(SupportPhase.Preview)));
    }

    [Fact]
    public async Task Preview_AlreadyReleasedPreviewBranch_Excluded()
    {
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["11.0"] = new(SupportPhase.Preview, "11.0.0-preview.3", [["11.0.100-preview.3.26207.106"]]),
        });

        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/11.0.1xx-preview3", Config(SupportPhase.Preview)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/11.0.1xx-preview2", Config(SupportPhase.Preview)));
    }

    [Fact]
    public async Task PreviewBranch_Excluded_WhenChannelIsNotPreview()
    {
        // 10.0 is Active — preview/rc branches for it should never be included
        var resolver = CreateResolverWithSdks(new Dictionary<string, ChannelData>
        {
            ["10.0"] = new(SupportPhase.Active, "10.0.7", [["10.0.203", "10.0.107"]]),
        });

        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.1xx-preview1", Config(SupportPhase.Active)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.1xx-preview4", Config(SupportPhase.Active)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.1xx-rc1", Config(SupportPhase.Active)));
        Assert.False(await InvokeMatchAsync(resolver, "refs/heads/release/10.0.1xx-rc2", Config(SupportPhase.Active, SupportPhase.Preview)));
    }

    private record ChannelData(SupportPhase Phase, string LatestRelease, string[][] ReleaseSdks);

    private static ReleaseBranchResolver CreateResolver(Dictionary<string, (SupportPhase Phase, string LatestRelease)> channels)
    {
        // Convert to new format with single SDK per release for backward compat
        var channelData = channels.ToDictionary(
            kvp => kvp.Key,
            kvp => new ChannelData(kvp.Value.Phase, kvp.Value.LatestRelease, [[kvp.Value.LatestRelease]]));
        return CreateResolverWithSdks(channelData);
    }

    private static ReleaseBranchResolver CreateResolverWithSdks(Dictionary<string, ChannelData> channels)
    {
        var resolver = new ReleaseBranchResolver(new StubTokenProvider(), NullLogger.Instance);

        var index = channels.ToDictionary(
            kvp => kvp.Key,
            kvp => Entry(kvp.Key, $"https://example.invalid/{kvp.Key}/releases.json"));

        SetPrivateField(
            resolver,
            "_releaseIndexEntriesTask",
            new Lazy<Task<Dictionary<string, MajorReleaseIndexItem>>>(() => Task.FromResult(index)));

        PrimeReleaseCacheWithSdks(resolver, channels);
        return resolver;
    }

    private static void PrimeReleaseCacheWithSdks(
        ReleaseBranchResolver resolver,
        Dictionary<string, ChannelData> channels)
    {
        var resolverType = typeof(ReleaseBranchResolver);
        var majorReleasesType = resolverType.GetNestedType("MajorReleases", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MajorReleases type not found.");
        var releaseType = resolverType.GetNestedType("Release", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Release type not found.");
        var sdkType = resolverType.GetNestedType("Sdk", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Sdk type not found.");

        var cacheField = resolverType.GetField("_releasesCache", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_releasesCache field not found.");
        var cache = cacheField.GetValue(resolver)
            ?? throw new InvalidOperationException("_releasesCache value is null.");
        var tryAddMethod = cache.GetType().GetMethod("TryAdd")
            ?? throw new InvalidOperationException("TryAdd method not found on releases cache.");

        foreach (var (channel, data) in channels)
        {
            var releaseListType = typeof(List<>).MakeGenericType(releaseType);
            var releaseList = Activator.CreateInstance(releaseListType)
                ?? throw new InvalidOperationException("Could not create Release list.");

            // Each inner array represents one release's SDKs
            foreach (var releaseSdks in data.ReleaseSdks)
            {
                var sdkListType = typeof(List<>).MakeGenericType(sdkType);
                var sdkList = Activator.CreateInstance(sdkListType)
                    ?? throw new InvalidOperationException("Could not create SDK list.");

                foreach (var sdkVersion in releaseSdks)
                {
                    var sdk = Activator.CreateInstance(sdkType)
                        ?? throw new InvalidOperationException("Could not create Sdk instance.");
                    sdkType.GetProperty("Version")!.SetValue(sdk, sdkVersion);
                    sdkType.GetProperty("RuntimeVersion")!.SetValue(sdk, "0.0.0");
                    sdkListType.GetMethod("Add")!.Invoke(sdkList, [sdk]);
                }

                var release = Activator.CreateInstance(releaseType)
                    ?? throw new InvalidOperationException("Could not create Release instance.");
                releaseType.GetProperty("Sdks")!.SetValue(release, sdkList);
                releaseListType.GetMethod("Add")!.Invoke(releaseList, [release]);
            }

            var majorRelease = Activator.CreateInstance(majorReleasesType)
                ?? throw new InvalidOperationException("Could not create MajorReleases instance.");
            majorReleasesType.GetProperty("ChannelVersion")!.SetValue(majorRelease, channel);
            majorReleasesType.GetProperty("SupportPhase")!.SetValue(majorRelease, data.Phase);
            majorReleasesType.GetProperty("LatestRelease")!.SetValue(majorRelease, data.LatestRelease);
            majorReleasesType.GetProperty("Releases")!.SetValue(majorRelease, releaseList);

            var lazyTask = CreateLazyTask(majorReleasesType, majorRelease);
            _ = (bool)(tryAddMethod.Invoke(cache, [channel, lazyTask]) ?? false);
        }
    }

    private static object CreateLazyTask(Type valueType, object value)
    {
        var helper = typeof(ReleaseBranchResolverTests)
            .GetMethod(nameof(CreateLazyTaskGeneric), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CreateLazyTaskGeneric helper method not found.");

        return helper.MakeGenericMethod(valueType).Invoke(null, [value])
            ?? throw new InvalidOperationException("Could not create Lazy<Task<T>>.");
    }

    private static Lazy<Task<T>> CreateLazyTaskGeneric<T>(T value) where T : notnull
    {
        return new Lazy<Task<T>>(() => Task.FromResult(value));
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found.");
        field.SetValue(target, value);
    }

    private static async Task<bool> InvokeMatchAsync(ReleaseBranchResolver resolver, string branch, ReleaseBranchConfig config)
    {
        var method = typeof(ReleaseBranchResolver).GetMethod(
            "MatchesReleaseConfigFiltersAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected MatchesReleaseConfigFiltersAsync to exist.");

        var task = (Task<bool>?)method.Invoke(resolver, [branch, config])
            ?? throw new InvalidOperationException("MatchesReleaseConfigFiltersAsync invocation returned null.");

        return await task;
    }

    [Theory]
    [InlineData("refs/heads/release/10.0.2xx", "release/10.0.2xx")]
    [InlineData("refs/heads/internal/release/9.0.1xx", "internal/release/9.0.1xx")]
    [InlineData("refs/heads/main", "main")]
    [InlineData("release/10.0.3xx", "release/10.0.3xx")]
    public void StripRefsHeadsPrefix_ReturnsShortBranchName(string input, string expected)
    {
        // The resolver must strip refs/heads/ so the collector doesn't double-prefix it.
        var method = typeof(ReleaseBranchResolver).GetMethod(
            "StripRefsHeadsPrefix",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected StripRefsHeadsPrefix to exist.");

        var result = (string?)method.Invoke(null, [input]);

        Assert.Equal(expected, result);
    }

    private sealed class StubTokenProvider : IRemoteTokenProvider
    {
        public string GetTokenForRepository(string repoUri) => "test-token";
        public Task<string?> GetTokenForRepositoryAsync(string repoUri) => Task.FromResult<string?>("test-token");
    }

}
