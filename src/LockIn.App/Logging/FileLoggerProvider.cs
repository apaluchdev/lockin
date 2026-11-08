using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LockIn.App.Logging;

/// <summary>
/// Writes every log entry to a daily file (lockin-yyyy-MM-dd.log) on a background task, and
/// deletes files older than <see cref="RetentionDays"/>.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 14;

    private readonly string _directory;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        DeleteOldFiles();
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, UiLoggerProvider.ShortName(categoryName));

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        var builder = new StringBuilder();
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            builder.Clear();
            while (_queue.Reader.TryRead(out var line))
            {
                builder.AppendLine(line);
            }

            var path = Path.Combine(_directory, $"lockin-{DateTime.Now:yyyy-MM-dd}.log");
            try
            {
                await File.AppendAllTextAsync(path, builder.ToString()).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Logging must never take the app down; drop this batch.
            }
        }
    }

    private void DeleteOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "lockin-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Ignore files in use.
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string source) : ILogger
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

            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Level(logLevel)}] {source}: {formatter(state, exception)}");
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Enqueue(line);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
