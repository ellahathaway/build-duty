using BuildDuty.Core;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Infrastructure;

/// <summary>
/// Wires the --config option from any command's settings into the shared <see cref="IBuildDutyConfigProvider"/>.
/// Runs before command execution so the provider has the path before anything calls <c>GetConfig()</c>.
/// </summary>
internal sealed class ConfigInterceptor(IServiceProvider serviceProvider) : ICommandInterceptor
{
    public void Intercept(CommandContext context, CommandSettings settings)
    {
        var configProp = settings.GetType().GetProperty("Config");
        if (configProp?.GetValue(settings) is string path)
        {
            var configProvider = (IBuildDutyConfigProvider)serviceProvider.GetService(typeof(IBuildDutyConfigProvider))!;
            configProvider.ConfigPath = path;
        }
    }
}
