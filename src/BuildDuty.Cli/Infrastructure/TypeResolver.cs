using Spectre.Console.Cli;

namespace BuildDuty.Cli.Infrastructure;

/// <summary>
/// Adapts <see cref="IServiceProvider"/> for Spectre.Console.Cli dependency injection.
/// </summary>
internal sealed class TypeResolver : ITypeResolver, IAsyncDisposable, IDisposable
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

    public ValueTask DisposeAsync()
    {
        if (_provider is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();

        if (_provider is IDisposable disposable)
            disposable.Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_provider is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else if (_provider is IDisposable disposable)
            disposable.Dispose();
    }
}
