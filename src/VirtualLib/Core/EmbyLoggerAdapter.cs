using Microsoft.Extensions.Logging;
using EmbyILogger = MediaBrowser.Model.Logging.ILogger;

namespace VirtualLib.Core;

/// <summary>
/// Adapts Emby's own <see cref="MediaBrowser.Model.Logging.ILogger"/> to the
/// <see cref="Microsoft.Extensions.Logging.ILogger{T}"/> abstraction used throughout this
/// plugin's services (<c>SyncService</c>, <c>LibraryCleanupService</c>, <c>ConnectorFactory</c>…).
/// Without this bridge those services were constructed with <c>NullLogger&lt;T&gt;</c> and every
/// log line — including the orphan-cleanup dry-run output — was silently dropped (code-reviewer
/// report on #44, same root cause as #26).
///
/// Accepts a <see cref="Func{TResult}"/> rather than a bare <see cref="EmbyILogger"/> because
/// <c>BaseApiService.Logger</c> is a settable property populated by the ServiceStack host
/// <b>after</b> construction (property injection) — reading it eagerly inside a constructor would
/// capture a stale/default value. The accessor is invoked once per log call, always after the
/// consuming service's constructor has returned and the owning request is actually being handled.
/// </summary>
public sealed class EmbyLoggerAdapter<T> : ILogger<T>
{
    private readonly Func<EmbyILogger?> _loggerAccessor;

    /// <summary>For contexts where a real <see cref="EmbyILogger"/> is already available at construction time (e.g. constructor-injected <c>ILogManager</c> in a scheduled task).</summary>
    public EmbyLoggerAdapter(EmbyILogger logger) : this(() => logger) { }

    /// <summary>For contexts where the underlying logger is only populated after construction (e.g. <c>BaseApiService.Logger</c>).</summary>
    public EmbyLoggerAdapter(Func<EmbyILogger?> loggerAccessor)
    {
        _loggerAccessor = loggerAccessor;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var embyLogger = _loggerAccessor();
        if (embyLogger is null) return; // Not wired yet — fail silent rather than throw from a log call.

        var message = formatter(state, exception);
        if (exception is not null)
            message = $"{message} — {exception}";

        switch (logLevel)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
                embyLogger.Debug(message);
                break;
            case LogLevel.Information:
                embyLogger.Info(message);
                break;
            case LogLevel.Warning:
                embyLogger.Warn(message);
                break;
            case LogLevel.Error:
                embyLogger.Error(message);
                break;
            case LogLevel.Critical:
                embyLogger.Fatal(message);
                break;
            case LogLevel.None:
            default:
                break;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
