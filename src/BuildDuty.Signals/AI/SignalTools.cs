using Microsoft.Extensions.AI;
using System.ComponentModel;

namespace BuildDuty.Signals.AI;

public static class SignalTools
{
    public static ICollection<AIFunction> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(
                (Func<string, Task<object>>)(async ([Description("The path to the signals xml file")] string filePath) =>
                {
                    try
                    {
                        var signals = SignalXmlSerializer.DeserializeFromFile(filePath);
                        return new { SignalsCount = signals.Count, Signals = signals, Error = (string?)null };
                    }
                    catch(Exception ex)
                    {
                        return new { SignalsCount = 0, Signals = (object?)null, Error = $"Failed to deserialize signals from file '{filePath}': {ex.Message}" };
                    }
                }),
                "deserialize_signals_from_file",
                "Deserialize signals from an XML file.")
        ];
    }
};
