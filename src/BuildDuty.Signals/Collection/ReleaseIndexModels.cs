using BuildDuty.Configuration.Models;

namespace BuildDuty.Signals.Collection;

/// <summary>
/// Top-level model for the dotnet/core releases-index.json file.
/// </summary>
internal sealed class MajorReleasesIndex
{
    public required List<MajorReleaseIndexItem> ReleasesIndex { get; set; }
}

/// <summary>
/// A single entry in the releases index, representing one major .NET version channel.
/// </summary>
internal sealed record MajorReleaseIndexItem(
    string ChannelVersion,
    string LatestRelease,
    DateOnly LatestReleaseDate,
    bool Security,
    string LatestRuntime,
    string LatestSdk,
    string Product,
    SupportPhase SupportPhase,
    DateOnly? EolDate,
    string ReleasesJson);
