namespace BuildDuty.Signals.Collection;

/// <summary>
/// Provides authentication tokens for Azure DevOps and GitHub APIs.
/// Consumers must implement this to supply credentials.
/// </summary>
public interface ITokenProvider
{
    /// <summary>
    /// Gets an access token for the given repository or organization URI.
    /// </summary>
    Task<string> GetTokenAsync(string uri);
}
