namespace BuildDuty.Core;

public static class StorageProviderExtensions
{
    public static async Task<ICollection<Signal>> GetAllSignalsAsync(this IStorageProvider storageProvider)
    {
        var workItems = await storageProvider.GetWorkItemsAsync();
        var signalIds = workItems
            .SelectMany(wi => wi.LinkedAnalyses.Select(la => la.SignalId))
            .Distinct();

        var signals = new List<Signal>();
        foreach (var signalId in signalIds)
        {
            try
            {
                signals.Add(await storageProvider.GetSignalAsync(signalId));
            }
            catch (FileNotFoundException) { }
        }

        return signals;
    }

    public static async Task<ICollection<WorkItem>> GetWorkItemsForTriageRunAsync(this IStorageProvider storageProvider, string triageRunId)
    {
        var workItems = await storageProvider.GetWorkItemsAsync();
        return workItems.Where(w => w.LastTriageId == triageRunId).ToArray();
    }
}
