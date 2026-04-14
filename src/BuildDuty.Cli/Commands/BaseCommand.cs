using BuildDuty.Core;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

public abstract class BaseCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : BaseSettings
{
    private readonly IBuildDutyConfigProvider _configProvider;

    protected BaseCommand(IBuildDutyConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    public sealed override Task<int> ExecuteAsync(
        CommandContext context,
        TSettings settings)
    {
        _configProvider.SetConfigPath(settings.Config);

        return ExecuteCommandAsync(context, settings);
    }

    protected abstract Task<int> ExecuteCommandAsync(
        CommandContext context,
        TSettings settings);
}
