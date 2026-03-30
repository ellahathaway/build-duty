using System.ComponentModel;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

public class BaseSettings : CommandSettings
{
    [CommandOption("--config")]
    [Description("Path to .build-duty.yml config file")]
    public string? Config { get; set; }
}
