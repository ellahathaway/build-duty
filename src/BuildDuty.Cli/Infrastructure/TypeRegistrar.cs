using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace BuildDuty.Cli.Infrastructure;

/// <summary>
/// Adapts <see cref="IServiceCollection"/> for Spectre.Console.Cli dependency injection.
/// </summary>
internal sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services;

    public TypeRegistrar(IServiceCollection services)
    {
        _services = services;
    }

    public void Register(Type service, Type implementation)
    {
        _services.AddSingleton(service, implementation);
    }

    public void RegisterInstance(Type service, object implementation)
    {
        _services.AddSingleton(service, implementation);
    }

    public void RegisterLazy(Type service, Func<object> factory)
    {
        _services.AddSingleton(service, _ => factory());
    }

    public ITypeResolver Build()
    {
        return new TypeResolver(_services.BuildServiceProvider());
    }

    public T Resolve<T>() where T : class => (T)Build().Resolve(typeof(T))!;
}
