using LockIn.Core.Gameflow;
using LockIn.Core.Hosting;
using LockIn.Core.Lcu;
using LockIn.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Tests.Fakes;

/// <summary>Runs LockIn's core services against a <see cref="FakeLeagueClient"/>.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly string _settingsDir;

    private TestHost(IHost host, FakeLeagueClient client, string settingsDir)
    {
        _settingsDir = settingsDir;
        Host = host;
        Client = client;
    }

    public IHost Host { get; }

    public FakeLeagueClient Client { get; }

    public ListLoggerProvider Logs { get; } = new();

    public SettingsStore Settings => Host.Services.GetRequiredService<SettingsStore>();

    public LcuConnection Connection => Host.Services.GetRequiredService<LcuConnection>();

    public static async Task<TestHost> StartAsync(Action<AppSettings>? configure = null, Action<IServiceCollection>? services = null)
    {
        var client = await FakeLeagueClient.StartAsync();
        var logs = new ListLoggerProvider();
        var settingsDir = Directory.CreateTempSubdirectory("lockin-host-").FullName;

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp =>
        {
            var store = new SettingsStore(Path.Combine(settingsDir, "settings.json"), sp.GetRequiredService<ILogger<SettingsStore>>());
            store.Update(s => configure?.Invoke(s));
            return store;
        });
        builder.Services.AddSingleton(sp => new LcuLocator(sp.GetRequiredService<ILogger<LcuLocator>>()) { InstallPathOverride = client.InstallDirectory });
        builder.Services.AddHostedSingleton<LcuConnection>();
        builder.Services.AddHostedSingleton<GameflowMonitor>();
        services?.Invoke(builder.Services);

        var host = builder.Build();
        var testHost = new TestHost(host, client, settingsDir);
        testHost.Logs.Forward(logs);
        await host.StartAsync();
        await testHost.WaitUntilAsync(() => testHost.Connection.State == LcuConnectionState.Connected
            && client.Subscriptions.Count >= LcuEndpoints.Subscribed.Count, "connect to the fake client");
        return testHost;
    }

    public async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting to {what}. Log:\n{string.Join('\n', Logs.Messages)}");
            }

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        await Client.DisposeAsync();
        Directory.Delete(_settingsDir, recursive: true);
    }
}

public sealed class ListLoggerProvider : ILoggerProvider
{
    private ListLoggerProvider? _target;
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            var source = _target ?? this;
            lock (source._messages)
            {
                return [.. source._messages];
            }
        }
    }

    internal void Forward(ListLoggerProvider target) => _target = target;

    public ILogger CreateLogger(string categoryName) => new ListLogger(this);

    public void Dispose()
    {
    }

    private sealed class ListLogger(ListLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (provider._messages)
            {
                provider._messages.Add($"[{logLevel}] {formatter(state, exception)}");
            }
        }
    }
}
