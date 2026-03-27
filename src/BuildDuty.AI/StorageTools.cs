using BuildDuty.Core;
using Microsoft.Extensions.AI;

namespace BuildDuty.AI;

public class StorageTools
{
    private readonly IStorageProvider _storageProvider;

    public StorageTools(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    public ICollection<AIFunction> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(
                async (string signalId) =>
                {
                    return await _storageProvider.GetSignalAsync(signalId);
                },
                "get_signal",
                "Get full signal details by ID as JSON."),

            AIFunctionFactory.Create(
                async (string signalId, string summary) =>
                {
                    var signal = await _storageProvider.GetSignalAsync(signalId);

                    signal.Summary = summary;
                    await _storageProvider.SaveSignalAsync(signal);

                    return;
                },
                "update_signal_summary",
                "Update a signal summary by signal ID."),
        ];
    }
}
