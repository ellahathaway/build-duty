namespace BuildDuty.Core;

public interface ISignalCollector
{
    Task<List<string>> CollectAsync();
}

public abstract class SignalCollector<TConfig> : ISignalCollector
    where TConfig : class
{
    protected SignalCollector(
        TConfig config,
        IGeneralTokenProvider tokenProvider,
        IStorageProvider storageProvider)
    {
        Config = config;
        TokenProvider = tokenProvider;
        StorageProvider = storageProvider;
    }

    protected TConfig Config { get; }

    protected IGeneralTokenProvider TokenProvider { get; }

    protected IStorageProvider StorageProvider { get; }

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

    protected abstract Task<List<Signal>> CollectCoreAsync();
}
