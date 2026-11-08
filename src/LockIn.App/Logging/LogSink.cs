using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace LockIn.App.Logging;

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Source, string Message)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public bool IsDebug => Level <= LogLevel.Debug;
}

/// <summary>Collects log entries for the in-app activity panel. Entries are appended on the UI thread.</summary>
public sealed class LogSink
{
    private const int MaxEntries = 3000;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public void Add(LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            Append(entry);
        }
        else
        {
            dispatcher.BeginInvoke(() => Append(entry));
        }
    }

    public void Clear() => Entries.Clear();

    private void Append(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }
    }
}

public sealed class UiLoggerProvider(LogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new UiLogger(sink, ShortName(categoryName));

    public void Dispose()
    {
    }

    internal static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class UiLogger(LogSink sink, string source) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null && logLevel >= LogLevel.Error)
            {
                message += $" ({exception.GetType().Name}: {exception.Message})";
            }

            sink.Add(new LogEntry(DateTimeOffset.Now, logLevel, source, message));
        }
    }
}
