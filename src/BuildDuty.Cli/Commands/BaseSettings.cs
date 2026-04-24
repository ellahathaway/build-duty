using System.ComponentModel;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Commands;

public class BaseSettings : CommandSettings
{
    [CommandOption("--config")]
    [Description("Path to the config file")]
    public required string Config { get; set; }
}
