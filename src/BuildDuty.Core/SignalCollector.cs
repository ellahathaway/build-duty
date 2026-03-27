using BuildDuty.Core.Models;
using Maestro.Common;

namespace BuildDuty.Core;

public interface ISignalCollector
{
    Task<List<string>> CollectAsync();
}

public abstract class SignalCollector<TConfig>(
    IBuildDutyConfigProvider configProvider,
    IRemoteTokenProvider tokenProvider,
    IStorageProvider storageProvider) : ISignalCollector
    where TConfig : class
{
    private TConfig? _config;

    protected TConfig Config => _config ??= ResolveConfig(configProvider.GetConfig());

    protected IRemoteTokenProvider TokenProvider { get; } = tokenProvider;

    protected IStorageProvider StorageProvider { get; } = storageProvider;

    protected abstract TConfig ResolveConfig(BuildDutyConfig config);

    public async Task<List<string>> CollectAsync()
    {
        try
        {
            var signals = await CollectCoreAsync();
            foreach (var signal in signals)
            {
                await StorageProvider.SaveSignalAsync(signal);
            }

            return signals.Select(s => s.Id).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SignalCollector] {GetType().Name}: {ex}");
            throw;
        }
    }

    protected abstract Task<List<ISignal>> CollectCoreAsync();
}
