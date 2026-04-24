using BuildDuty.Core;
using BuildDuty.Signals.Collection;

namespace BuildDuty.Cli.Infrastructure;

/// <summary>
/// Bridges <see cref="IGeneralTokenProvider"/> from Core to <see cref="ITokenProvider"/>
/// expected by BuildDuty.Signals collectors.
/// </summary>
internal sealed class TokenProviderAdapter(IGeneralTokenProvider inner) : ITokenProvider
{
    public async Task<string> GetTokenAsync(string uri)
    {
        var token = await inner.GetTokenForRepositoryAsync(uri);
        return token ?? throw new InvalidOperationException($"No token available for '{uri}'.");
    }
}
