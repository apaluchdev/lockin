using System.ComponentModel;
using System.Windows;
using LockIn.App.ViewModels;
using LockIn.Core.Settings;

namespace LockIn.App;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settings;

    public MainWindow(MainViewModel viewModel, SettingsStore settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Raised the first time the window is hidden to the tray.</summary>
    public event EventHandler? HiddenToTray;

    /// <summary>Set by the app when the user really wants to quit.</summary>
    public bool AllowClose { get; set; }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;  // Bring to front even if another app has focus…
        Topmost = false; // …without staying on top.
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HideToTray();
        }

        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _settings.Snapshot().General.MinimizeToTray)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }
}
