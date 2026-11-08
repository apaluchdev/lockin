using System.Diagnostics;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LockIn.App.Services;
using LockIn.Core;
using LockIn.Core.Lcu;
using LockIn.Core.Settings;

namespace LockIn.App.ViewModels;

/// <summary>The Settings tab: timings and general options. Every change is saved immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private const double MaxSeconds = 60;

    private readonly SettingsStore _store;
    private readonly StartupRegistration _startup;
    private readonly LcuLocator _locator;
    private AppSettings _s;

    public SettingsViewModel(SettingsStore store, StartupRegistration startup, LcuLocator locator)
    {
        _store = store;
        _startup = startup;
        _locator = locator;
        _s = store.Snapshot();
        _locator.InstallPathOverride = _s.General.LeagueInstallPath;
    }

    public IReadOnlyList<FlashKey> FlashKeys { get; } = Enum.GetValues<FlashKey>();

    // ── Accept ───────────────────────────────────────────────────────────

    public double AcceptDelaySeconds
    {
        get => _s.AcceptDelaySeconds;
        set => Set(Clamp(value), (s, v) => s.AcceptDelaySeconds = v);
    }

    // ── Bans ─────────────────────────────────────────────────────────────

    public bool BansEnabled
    {
        get => _s.Bans.Enabled;
        set => Set(value, (s, v) => s.Bans.Enabled = v);
    }

    public double BanHoverDelaySeconds
    {
        get => _s.Bans.HoverDelaySeconds;
        set => Set(Clamp(value), (s, v) => s.Bans.HoverDelaySeconds = v);
    }

    public double BanLockBeforeEndSeconds
    {
        get => _s.Bans.LockBeforeEndSeconds;
        set => Set(Clamp(value), (s, v) => s.Bans.LockBeforeEndSeconds = v);
    }

    // ── Picks ────────────────────────────────────────────────────────────

    public bool PicksEnabled
    {
        get => _s.Picks.Enabled;
        set => Set(value, (s, v) => s.Picks.Enabled = v);
    }

    public bool PreHoverInPlanning
    {
        get => _s.Picks.PreHoverInPlanning;
        set => Set(value, (s, v) => s.Picks.PreHoverInPlanning = v);
    }

    public double PickHoverDelaySeconds
    {
        get => _s.Picks.HoverDelaySeconds;
        set => Set(Clamp(value), (s, v) => s.Picks.HoverDelaySeconds = v);
    }

    public double PickLockBeforeEndSeconds
    {
        get => _s.Picks.LockBeforeEndSeconds;
        set => Set(Clamp(value), (s, v) => s.Picks.LockBeforeEndSeconds = v);
    }

    public bool FallBackToAnyRole
    {
        get => _s.Picks.FallBackToAnyRole;
        set => Set(value, (s, v) => s.Picks.FallBackToAnyRole = v);
    }

    // ── Runes & spells ───────────────────────────────────────────────────

    public bool LoadoutsEnabled
    {
        get => _s.Loadouts.Enabled;
        set => Set(value, (s, v) => s.Loadouts.Enabled = v);
    }

    public FlashKey FlashKey
    {
        get => _s.Loadouts.FlashKey;
        set => Set(value, (s, v) => s.Loadouts.FlashKey = v);
    }

    // ── General ──────────────────────────────────────────────────────────

    public bool StartWithWindows
    {
        get => _s.General.StartWithWindows;
        set
        {
            Set(value, (s, v) => s.General.StartWithWindows = v);
            _startup.Apply(value);
        }
    }

    public bool MinimizeToTray
    {
        get => _s.General.MinimizeToTray;
        set => Set(value, (s, v) => s.General.MinimizeToTray = v);
    }

    public bool AutomateCustomGames
    {
        get => _s.General.AutomateCustomGames;
        set => Set(value, (s, v) => s.General.AutomateCustomGames = v);
    }

    public string LeagueInstallPath
    {
        get => _s.General.LeagueInstallPath ?? "";
        set
        {
            var path = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Set(path, (s, v) => s.General.LeagueInstallPath = v);
            _locator.InstallPathOverride = path;
        }
    }

    public string DefaultInstallPath => LcuLocator.DefaultInstallPath;

    public string SettingsFile => AppPaths.SettingsFile;

    public string Version =>
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev";

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        Directory.CreateDirectory(AppPaths.RoamingDirectory);
        Process.Start(new ProcessStartInfo(AppPaths.RoamingDirectory) { UseShellExecute = true });
    }

    private static double Clamp(double seconds) =>
        double.IsFinite(seconds) ? Math.Round(Math.Clamp(seconds, 0, MaxSeconds), 2) : 0;

    private void Set<T>(T value, Action<AppSettings, T> apply, [CallerMemberName] string? property = null)
    {
        _store.Update(s => apply(s, value));
        _s = _store.Snapshot();
        OnPropertyChanged(property);
    }
}
