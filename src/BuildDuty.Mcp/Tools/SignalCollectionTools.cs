using System.ComponentModel;
using System.Text.Json;
using BuildDuty.Core;
using BuildDuty.Services.Configuration;
using BuildDuty.Signals;
using BuildDuty.Signals.Collection;
using Maestro.Common;
using Maestro.Common.AzureDevOpsTokens;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuildDuty.Mcp.Tools;

[McpServerToolType]
public class SignalCollectionTools
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [McpServerTool(Name = "build_duty_collect_signals")]
    [Description("Collect signals from Azure DevOps and GitHub based on a .build-duty.yml config file. Returns structured XML signal data including pipeline failures, GitHub issues, and PRs.")]
    public static async Task<string> CollectSignals(
        [Description("Path to the .build-duty.yml config file. If not provided, searches current directory.")] string? configPath = null)
    {
        configPath ??= FindConfigFile();

        if (configPath is null)
        {
            return "Error: No .build-duty.yml config file found. Provide a configPath parameter.";
        }

        var config = ConfigProvider.LoadFromFile(configPath);
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        var logger = loggerFactory.CreateLogger<SignalProvider>();

        var tokenProvider = new GeneralTokenProvider();
        var branchResolver = new ReleaseBranchResolver(tokenProvider, loggerFactory.CreateLogger<ReleaseBranchResolver>());
        var provider = new SignalProvider(tokenProvider, logger, branchResolver);

        var result = await provider.CollectSignalsAsync(config);

        // Use the existing XML serializer from the Signals project
        var signalsXml = SignalXmlSerializer.Serialize(result.Signals);

        var metadata = new
        {
            configName = config.Name,
            signalCount = result.Signals.Count,
            coveredScopes = result.CoveredScopes.Select(s => s.ScopeKey).ToList(),
            failures = result.Failures.Select(f => new { f.ScopeKey, f.Reason }).ToList(),
        };

        return $"""
            {JsonSerializer.Serialize(metadata, s_jsonOptions)}

            {signalsXml}
            """;
    }

    [McpServerTool(Name = "build_duty_get_config")]
    [Description("Read and parse a .build-duty.yml config file, returning the resolved configuration including pipeline definitions, GitHub repos, and filter settings.")]
    public static string GetConfig(
        [Description("Path to the .build-duty.yml config file. If not provided, searches current directory.")] string? configPath = null)
    {
        configPath ??= FindConfigFile();

        if (configPath is null)
        {
            return "Error: No .build-duty.yml config file found. Provide a configPath parameter.";
        }

        var config = ConfigProvider.LoadFromFile(configPath);

        return JsonSerializer.Serialize(config, s_jsonOptions);
    }

    private static string? FindConfigFile()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, ".build-duty.yml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }
}
