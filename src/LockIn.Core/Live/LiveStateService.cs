using LockIn.Core.ChampSelect;
using LockIn.Core.Gameflow;
using LockIn.Core.Lcu;
using LockIn.Core.Loadouts;
using LockIn.Core.Matchmaking;
using LockIn.Core.Settings;
using Microsoft.Extensions.Hosting;

namespace LockIn.Core.Live;

/// <summary>Everything the phone page shows, in one serializable snapshot.</summary>
public sealed record LiveSnapshot
{
    /// <summary>PC clock (Unix ms) when the snapshot was taken; the phone uses it to correct for clock skew.</summary>
    public long ServerTime { get; init; }

    /// <summary>"searching", "connecting" or "connected".</summary>
    public required string Connection { get; init; }

    public string? Summoner { get; init; }

    /// <summary>The client's gameflow phase, e.g. "Lobby", "Matchmaking", "ChampSelect".</summary>
    public required string Phase { get; init; }

    public required string PhaseLabel { get; init; }

    /// <summary>Set while a match has been found and the ready check is running.</summary>
    public ReadyCheck? ReadyCheck { get; init; }

    public bool AutoAccept { get; init; }

    public bool Automation { get; init; }

    public ChampSelectView? ChampSelect { get; init; }

    /// <summary>The rune page and spells this champ select will use; null outside champ select.</summary>
    public LoadoutView? Loadout { get; init; }

    public required IReadOnlyList<DecisionEntry> Decisions { get; init; }
}

/// <summary>Combines the connection, gameflow, ready check, settings and champ select into <see cref="LiveSnapshot"/>s.</summary>
public sealed class LiveStateService(
    LcuConnection connection,
    GameflowMonitor gameflow,
    ChampSelectService champSelect,
    LoadoutService loadouts,
    SettingsStore settings,
    TimeProvider time) : IHostedService
{
    private volatile ReadyCheck? _readyCheck;

    /// <summary>Raised (on a background thread) when anything in the snapshot may have changed.</summary>
    public event EventHandler? Changed;

    public LiveSnapshot Current()
    {
        var s = settings.Snapshot();
        var phase = connection.State == LcuConnectionState.Connected ? gameflow.Phase : GameflowMonitor.None;
        var view = phase == GameflowMonitor.ChampSelect ? champSelect.View : null;
        return new LiveSnapshot
        {
            ServerTime = time.GetUtcNow().ToUnixTimeMilliseconds(),
            Connection = connection.State.ToString().ToLowerInvariant(),
            Summoner = connection.Summoner?.Label,
            Phase = phase,
            PhaseLabel = connection.State switch
            {
                LcuConnectionState.Connected => GameflowMonitor.Describe(phase),
                LcuConnectionState.Connecting => "League client loading",
                _ => "League client not running",
            },
            ReadyCheck = phase == GameflowMonitor.ReadyCheck ? _readyCheck : null,
            AutoAccept = s.AutoAcceptEnabled,
            Automation = s.AutomationEnabled,
            ChampSelect = view,
            Loadout = loadouts.ViewFor(view, s),
            Decisions = champSelect.RecentDecisions,
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        connection.StateChanged += Raise;
        connection.EventReceived += OnEvent;
        gameflow.PhaseChanged += Raise;
        champSelect.ViewChanged += Raise;
        loadouts.Changed += Raise;
        settings.Changed += Raise;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        connection.StateChanged -= Raise;
        connection.EventReceived -= OnEvent;
        gameflow.PhaseChanged -= Raise;
        champSelect.ViewChanged -= Raise;
        loadouts.Changed -= Raise;
        settings.Changed -= Raise;
        return Task.CompletedTask;
    }

    private void OnEvent(object? sender, LcuEvent e)
    {
        if (e.Uri == LcuEndpoints.ReadyCheck)
        {
            _readyCheck = e.DataAs<ReadyCheck>();
            Raise(sender, EventArgs.Empty);
        }
    }

    private void Raise(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
