using BuildDuty.Cli;
using BuildDuty.Cli.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

var services = new ServiceCollection();

services.AddLogging();
services.AddSignalCollectionServices();
services.AddAiServices();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("build-duty");
});

return await app.RunAsync(args);