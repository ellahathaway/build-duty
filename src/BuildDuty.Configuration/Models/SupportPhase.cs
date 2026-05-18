namespace BuildDuty.Configuration.Models;

/// <summary>
/// Represents the support lifecycle phase of a .NET release.
/// Values align with the "support-phase" field in the dotnet/core releases-index.json.
/// </summary>
public enum SupportPhase
{
    Active,
    Preview,
    GoLive,
    Eol,
    Maintenance
}
