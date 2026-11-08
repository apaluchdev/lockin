using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LockIn.App.Logging;
using LockIn.Core;
using LockIn.Core.Settings;

namespace LockIn.App.ViewModels;

public sealed partial class LogViewModel : ObservableObject
{
    private readonly LogSink _sink;
    private readonly SettingsStore _settings;
    private readonly bool _initializing; // Only set during construction, to skip saving the initial values.

    public LogViewModel(LogSink sink, SettingsStore settings)
    {
        _sink = sink;
        _settings = settings;
        Entries = CollectionViewSource.GetDefaultView(sink.Entries);
        Entries.Filter = item => ShowDebug || item is LogEntry { IsDebug: false };
        _initializing = true;
        ShowDebug = settings.Snapshot().General.ShowDebugLog;
        _initializing = false;
    }

    public ICollectionView Entries { get; }

    /// <summary>Also show debug-level detail (every API call, every skipped candidate).</summary>
    [ObservableProperty]
    public partial bool ShowDebug { get; set; }

    partial void OnShowDebugChanged(bool value)
    {
        Entries.Refresh();
        if (_initializing)
        {
            return;
        }

        _settings.Update(s => s.General.ShowDebugLog = value);
    }

    [RelayCommand]
    private void Clear() => _sink.Clear();

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start(new ProcessStartInfo(AppPaths.LogDirectory) { UseShellExecute = true });
    }
}
