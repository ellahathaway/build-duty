using BuildDuty.Core;
using BuildDuty.Services.Configuration;
using BuildDuty.Signals;
using BuildDuty.Signals.Collection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace BuildDuty.Tasks;

/// <summary>
/// MSBuild task that collects signals from Azure DevOps and GitHub
/// based on a .build-duty.yml config and writes XML output.
/// </summary>
public sealed class CollectSignals : Task
{
    /// <summary>
    /// Path to the .build-duty.yml config file.
    /// </summary>
    [Required]
    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to write the collected signals XML output.
    /// </summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Number of signals collected (output parameter).
    /// </summary>
    [Output]
    public int SignalCount { get; set; }

    public override bool Execute()
    {
        try
        {
            return ExecuteAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.LogError("BuildDuty signal collection failed: {0}", ex.Message);
            return false;
        }
    }

    private async Task<bool> ExecuteAsync()
    {
        var log = new Log(Log);

        if (!File.Exists(ConfigPath))
        {
            log.LogError("Config file not found: {0}", ConfigPath);
            return false;
        }

        var config = ConfigProvider.LoadFromFile(ConfigPath);
        log.LogMessage("Collecting signals for '{0}'...", config.Name);

        var tokenProvider = new GeneralTokenProvider();
        var branchResolver = new ReleaseBranchResolver(tokenProvider, log);
        var provider = new SignalProvider(tokenProvider, log, branchResolver);

        var result = await provider.CollectSignalsAsync(config);

        if (result.Failures.Count > 0)
        {
            foreach (var failure in result.Failures)
            {
                log.LogWarning("Collection failure [{0}]: {1}", failure.ScopeKey, failure.Reason);
            }
        }

        var outputDir = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        SignalXmlSerializer.SerializeToFile(result.Signals, OutputPath);

        SignalCount = result.Signals.Count;
        log.LogMessage("Collected {0} signals, written to {1}", result.Signals.Count, OutputPath);

        return true;
    }
}
