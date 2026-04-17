using BuildDuty.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

internal abstract class BaseCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : BaseSettings
{
    private readonly IBuildDutyConfigProvider _configProvider;
    public BuildDutyFailures Failures = new();

    protected BaseCommand(IBuildDutyConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    public sealed override async Task<int> ExecuteAsync(
        CommandContext context,
        TSettings settings)
    {
        int result;
        try
        {
            _configProvider.SetConfigPath(settings.Config);
            result = await ExecuteCommandAsync(context, settings);
        }
        catch (Exception ex)
        {
            Failures.Add(GetType().Name, ex.Message);
            result = 1;
        }

        if (Failures.HasFailures)
        {
            AnsiConsole.MarkupLine($"\n[red]BuildDuty failed — {Failures.GetFailures().Count} issue(s):[/]");
            foreach (var (_, message) in Failures.GetFailures())
            {
                AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(message)}");
            }
        }

        return result;
    }

    protected abstract Task<int> ExecuteCommandAsync(
        CommandContext context,
        TSettings settings);
}
