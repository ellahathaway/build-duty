namespace BuildDuty.Core;

public static class StorageProviderExtensions
{
    public static async Task<IEnumerable<Signal>> GetSignalsFromWorkItemsAsync(this IStorageProvider storageProvider)
    {
        var workItems = await storageProvider.GetWorkItemsAsync();
        var signalTasks = workItems
            .SelectMany(wi => wi.SignalIds)
            .Distinct()
            .Select(storageProvider.GetSignalAsync);

        return await Task.WhenAll(signalTasks);
    }
}
