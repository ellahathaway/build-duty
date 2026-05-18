using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace BuildDuty.Tasks;

public enum LogImportance
{
    Low = MessageImportance.Low,
    Normal = MessageImportance.Normal,
    High = MessageImportance.High
}

public interface ILog
{
    void LogError(string message, params object[] messageArgs);
    void LogMessage(string message, params object[] messageArgs);
    void LogMessage(LogImportance importance, string message, params object[] messageArgs);
    void LogWarning(string message, params object[] messageArgs);
    bool HasLoggedErrors { get; }
}

internal sealed class Log : ILog, Microsoft.Extensions.Logging.ILogger
{
    private readonly TaskLoggingHelper _logger;

    public Log(TaskLoggingHelper logger)
    {
        _logger = logger;
    }

    public void LogError(string message, params object[] messageArgs)
        => _logger.LogError(message, messageArgs);

    public void LogMessage(string message, params object[] messageArgs)
        => _logger.LogMessage(message, messageArgs);

    public void LogMessage(LogImportance importance, string message, params object[] messageArgs)
        => _logger.LogMessage((MessageImportance)importance, message, messageArgs);

    public void LogWarning(string message, params object[] messageArgs)
        => _logger.LogWarning(message, messageArgs);

    public bool HasLoggedErrors => _logger.HasLoggedErrors;

    IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;

    bool Microsoft.Extensions.Logging.ILogger.IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
        => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

    void Microsoft.Extensions.Logging.ILogger.Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        switch (logLevel)
        {
            case Microsoft.Extensions.Logging.LogLevel.Error:
            case Microsoft.Extensions.Logging.LogLevel.Critical:
                _logger.LogError(message);
                break;
            case Microsoft.Extensions.Logging.LogLevel.Warning:
                _logger.LogWarning(message);
                break;
            default:
                _logger.LogMessage(MessageImportance.Normal, message);
                break;
        }
    }
}
