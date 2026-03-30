using Spectre.Console.Cli;

namespace BuildDuty.Cli.Infrastructure;

/// <summary>
/// Adapts <see cref="IServiceProvider"/> for Spectre.Console.Cli dependency injection.
/// </summary>
internal sealed class TypeResolver : ITypeResolver, IDisposable
{
    private readonly IServiceProvider _provider;

    public TypeResolver(IServiceProvider provider)
    {
        _provider = provider;
    }

    public object? Resolve(Type? type)
    {
        return type is null ? null : _provider.GetService(type);
    }

    public void Dispose()
    {
        if (_provider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
