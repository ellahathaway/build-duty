using System.Diagnostics;
using Maestro.Common;

namespace BuildDuty.Core;

public interface ISignalCollector
{
    Task<List<ISignal>> CollectAsync(CancellationToken ct = default);
}

public abstract class SignalCollector<TConfig>(TConfig config, IRemoteTokenProvider tokenProvider, IWorkItemsProvider workItemsProvider) : ISignalCollector
{
    protected TConfig Config { get; } = config;

    protected IRemoteTokenProvider TokenProvider { get; } = tokenProvider;

    protected IWorkItemsProvider WorkItemsProvider { get; } = workItemsProvider;

    public async Task<List<ISignal>> CollectAsync(CancellationToken ct = default)
    {
        try
        {
            return await CollectCoreAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SignalCollector] {GetType().Name}: {ex}");
            throw;
        }
    }

    protected abstract Task<List<ISignal>> CollectCoreAsync(CancellationToken ct = default);
}
