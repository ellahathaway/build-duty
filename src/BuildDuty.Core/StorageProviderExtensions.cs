namespace BuildDuty.Core;

public static class StorageProviderExtensions
{
    public static async Task<ICollection<Signal>> GetUnresolvedSignalsAsync(this IStorageProvider storageProvider)
    {
        var workItems = await storageProvider.GetWorkItemsAsync();
        var signalIds = workItems
            .Where(wi => !wi.Resolved)
            .SelectMany(wi => wi.LinkedAnalyses.Select(la => la.SignalId))
            .Distinct();

        var signals = new List<Signal>();
        foreach (var signalId in signalIds)
        {
            try
            {
                var signal = await storageProvider.GetSignalAsync(signalId);
                if (!signal.IsResolvedCollectionReason)
                {
                    signals.Add(signal);
                }
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
