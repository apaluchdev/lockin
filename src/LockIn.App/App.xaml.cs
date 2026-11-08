using System.Windows;
using System.Windows.Threading;
using LockIn.App.Logging;
using LockIn.App.Services;
using LockIn.App.ViewModels;
using LockIn.Core;
using LockIn.Core.Gameflow;
using LockIn.Core.Hosting;
using LockIn.Core.Lcu;
using LockIn.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LockIn.App;

#pragma warning disable CA1001 // Disposables are released in OnExit, the WPF app lifetime hook.
public partial class App : Application
#pragma warning restore CA1001
{
    private SingleInstance? _singleInstance;
    private IHost? _host;
    private ILogger<App>? _logger;
    private MainWindow? _window;
    private TrayIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirstInstance)
        {
            _singleInstance.SignalFirstInstance();
            Shutdown();
            return;
        }

        _host = BuildHost(e.Args);
        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved background error");
            args.SetObserved();
        };

        _logger.LogInformation("LockIn {Version} starting", typeof(App).Assembly.GetName().Version?.ToString(3));

        var settings = _host.Services.GetRequiredService<SettingsStore>();
        _host.Services.GetRequiredService<StartupRegistration>().Apply(settings.Snapshot().General.StartWithWindows);

        _window = _host.Services.GetRequiredService<MainWindow>();
        _window.HiddenToTray += OnHiddenToTray;
        MainWindow = _window;

        _tray = new TrayIcon();
        _tray.Create(_host.Services.GetRequiredService<MainViewModel>(), ShowMainWindow, ExitApplication);

        if (!e.Args.Contains(StartupRegistration.TrayArgument, StringComparer.OrdinalIgnoreCase))
        {
            _window.Show();
        }

        _singleInstance.ListenForShowRequests(() => Ui.Post(ShowMainWindow));

        await _host.StartAsync();
    }

    private static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        var logSink = new LogSink();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddProvider(new UiLoggerProvider(logSink));
        builder.Logging.AddProvider(new FileLoggerProvider(AppPaths.LogDirectory));

        var services = builder.Services;
        services.AddSingleton(logSink);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new SettingsStore(AppPaths.SettingsFile, sp.GetRequiredService<ILogger<SettingsStore>>()));

        // League client
        services.AddSingleton<LcuLocator>();
        services.AddHostedSingleton<LcuConnection>();
        services.AddHostedSingleton<GameflowMonitor>();

        // App services
        services.AddSingleton<StartupRegistration>();

        // UI
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return builder.Build();
    }

    private void ShowMainWindow() => _window?.ShowFromTray();

    private void OnHiddenToTray(object? sender, EventArgs e)
    {
        var settings = _host!.Services.GetRequiredService<SettingsStore>();
        if (!settings.Snapshot().General.TrayHintShown)
        {
            _tray?.ShowNotification("LockIn is still running", "LockIn keeps working from the tray. Right-click the icon to exit.");
            settings.Update(s => s.General.TrayHintShown = true);
        }
    }

    private void ExitApplication()
    {
        _logger?.LogInformation("Exiting");
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }

        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unexpected error");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_host is not null)
        {
            // Stop on a pool thread so nothing waits on the (now blocked) UI thread.
            Task.Run(() => _host.StopAsync(TimeSpan.FromSeconds(3))).Wait(TimeSpan.FromSeconds(4));
            _host.Services.GetService<SettingsStore>()?.Flush();
            _host.Dispose();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
