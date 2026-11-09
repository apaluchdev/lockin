using System.Globalization;
using System.Threading.Channels;
using LockIn.Core.Champions;
using LockIn.Core.Gameflow;
using LockIn.Core.Lcu;
using LockIn.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.ChampSelect;

/// <summary>
/// Runs champ select: follows the session, asks <see cref="ChampSelectPlanner"/> what to do several times a
/// second, sends the resulting hovers/locks to the client, and logs every decision once. Also publishes a
/// live <see cref="View"/> and accepts per-game overrides and lock-now requests (from the phone).
/// </summary>
public sealed class ChampSelectService(
    LcuConnection connection,
    GameflowMonitor gameflow,
    ChampionCatalog catalog,
    SettingsStore settings,
    TimeProvider time,
    ILogger<ChampSelectService> logger) : BackgroundService
{
    public const string Pick = ChampSelectAction.Pick;
    public const string Ban = ChampSelectAction.Ban;

    private const string SessionPath = "lol-champ-select/v1/session";
    private const int MaxRecentDecisions = 12;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(1);

    private readonly Channel<Message> _inbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _recentGate = new();
    private readonly List<DecisionEntry> _recent = [];
    private volatile ChampSelectView? _view;

    // Everything below is only touched by the loop.
    private ChampSelectSession? _session;
    private ChampSelectMemory _memory = new();
    private ChampSelectPlan? _lastPlan;
    private readonly Dictionary<string, string> _lastNotes = [];
    private readonly Dictionary<int, long> _retryAt = [];
    private readonly HashSet<int> _announced = [];
    private IReadOnlySet<int>? _pickable;
    private HashSet<int>? _bannable;
    private bool _automate;
    private string? _notAutomatedReason;
    private string? _queue;
    private int? _pickOverride;
    private int? _banOverride;
    private bool _pickableRefreshedForTurn;
    private long _gameId = -1;

    /// <summary>Raised (on a background thread) when the local player's champion is locked in. Used for runes/spells.</summary>
    public event EventHandler<ChampionLockedEventArgs>? ChampionLocked;

    /// <summary>Raised (on a background thread) whenever <see cref="View"/> or <see cref="RecentDecisions"/> may have changed.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>The current champ select, or null when not in one.</summary>
    public ChampSelectView? View => _view;

    /// <summary>LockIn's most recent decisions, oldest first.</summary>
    public IReadOnlyList<DecisionEntry> RecentDecisions
    {
        get
        {
            lock (_recentGate)
            {
                return [.. _recent];
            }
        }
    }

    /// <summary>Overrides the pick or ban for this champ select only. Null clears it. Returns an error message, or null on success.</summary>
    public Task<string?> SetOverrideAsync(string kind, int? championId)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Writer.TryWrite(new SetOverride(kind, championId, done));
        return done.Task;
    }

    /// <summary>Locks in the current pick or ban right away. Returns an error message, or null on success.</summary>
    public Task<string?> LockNowAsync(string kind)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Writer.TryWrite(new LockNow(kind, done));
        return done.Task;
    }

    private abstract record Message;

    private sealed record SessionUpdated(ChampSelectSession Session) : Message;

    private sealed record Ended : Message;

    private sealed record SetOverride(string Kind, int? ChampionId, TaskCompletionSource<string?> Done) : Message;

    private sealed record LockNow(string Kind, TaskCompletionSource<string?> Done) : Message;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        connection.EventReceived += OnEvent;
        gameflow.PhaseChanged += OnPhaseChanged;
        settings.Changed += OnSettingsChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        connection.EventReceived -= OnEvent;
        gameflow.PhaseChanged -= OnPhaseChanged;
        settings.Changed -= OnSettingsChanged;
        return base.StopAsync(cancellationToken);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ViewChanged?.Invoke(this, EventArgs.Empty);

    private void OnEvent(object? sender, LcuEvent e)
    {
        if (e.Uri != LcuEndpoints.ChampSelectSession)
        {
            return;
        }

        if (e.IsDelete)
        {
            _inbox.Writer.TryWrite(new Ended());
        }
        else if (e.DataAs<ChampSelectSession>() is { } session)
        {
            _inbox.Writer.TryWrite(new SessionUpdated(session));
        }
    }

    private void OnPhaseChanged(object? sender, GameflowPhaseChangedEventArgs e)
    {
        if (e.Current == GameflowMonitor.ChampSelect)
        {
            // Covers LockIn starting or reconnecting in the middle of champ select.
            _ = FetchSessionAsync();
        }
        else if (e.Previous == GameflowMonitor.ChampSelect)
        {
            _inbox.Writer.TryWrite(new Ended());
        }
    }

    private async Task FetchSessionAsync()
    {
        if (connection.Client is not { } client)
        {
            return;
        }

        try
        {
            if (await client.GetAsync<ChampSelectSession>(SessionPath).ConfigureAwait(false) is { } session)
            {
                _inbox.Writer.TryWrite(new SessionUpdated(session));
            }
        }
        catch (LcuRequestException ex)
        {
            logger.LogDebug("Could not read the champ select session: {Message}", ex.Message);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitForWorkAsync(stoppingToken).ConfigureAwait(false);
                while (_inbox.Reader.TryRead(out var message))
                {
                    await HandleAsync(message, stoppingToken).ConfigureAwait(false);
                }

                if (_session is not null)
                {
                    await EvaluateAsync(stoppingToken).ConfigureAwait(false);
                }

                PublishView();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The loop must survive anything a single evaluation throws.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "Champ select automation hit an unexpected error");
                await Task.Delay(Tick, time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Wakes on a new message, or every tick while in champ select (timers move even when the client is quiet).</summary>
    private async Task WaitForWorkAsync(CancellationToken stoppingToken)
    {
        if (_session is null)
        {
            await _inbox.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        tick.CancelAfter(Tick);
        try
        {
            await _inbox.Reader.WaitToReadAsync(tick.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Tick elapsed.
        }
    }

    private async Task HandleAsync(Message message, CancellationToken stoppingToken)
    {
        switch (message)
        {
            case SessionUpdated { Session: var session }:
                if (_session is null || session.GameId != _gameId)
                {
                    await BeginAsync(session, stoppingToken).ConfigureAwait(false);
                }

                _session = session;
                AnnounceCompletedActions(session);
                break;

            case Ended when _session is not null:
                Decide("Champ select ended");
                Reset();
                break;

            case SetOverride request:
                request.Done.TrySetResult(ApplyOverride(request.Kind, request.ChampionId));
                break;

            case LockNow request:
                request.Done.TrySetResult(await ApplyLockNowAsync(request.Kind, stoppingToken).ConfigureAwait(false));
                break;
        }
    }

    private string? ApplyOverride(string kind, int? championId)
    {
        if (_session is null)
        {
            return "You're not in champ select";
        }

        if (kind is not (Pick or Ban))
        {
            return $"Unknown action \"{kind}\"";
        }

        if (championId is { } id && catalog.Find(id) is null)
        {
            return "Unknown champion";
        }

        if (!_automate || !settings.Snapshot().AutomationEnabled)
        {
            return _notAutomatedReason ?? "Champ select automation is off";
        }

        if (kind == Pick)
        {
            _pickOverride = championId;
        }
        else
        {
            _banOverride = championId;
        }

        // Adopt whatever is hovered right now as ours, so an earlier manual hover doesn't block the override.
        foreach (var action in MyActions(_session).Where(a => a.Type == kind && a.IsInProgress && !a.Completed))
        {
            _memory.HoveredByUs[action.Id] = action.ChampionId;
        }

        // During planning, show the new pick to the team as the intent.
        if (kind == Pick && _session.Timer.Phase == ChampSelectTimer.Planning
            && MyActions(_session).FirstOrDefault(a => a.Type == Pick && !a.Completed) is { } pickAction)
        {
            _memory.HoveredByUs[pickAction.Id] = pickAction.ChampionId;
            _memory.IntentDeclared = false;
        }

        Decide(championId is { } champion
            ? $"Phone: {kind} {catalog.NameOf(champion)} this game"
            : $"Phone: cleared the {kind} override — back to your lists");
        return null;
    }

    private async Task<string?> ApplyLockNowAsync(string kind, CancellationToken stoppingToken)
    {
        if (_session is null)
        {
            return "You're not in champ select";
        }

        var action = MyActions(_session).FirstOrDefault(a => a.Type == kind && a.IsInProgress && !a.Completed);
        if (action is null)
        {
            return $"It's not your {kind} turn";
        }

        var s = settings.Snapshot();
        var kindEnabled = kind == Pick ? s.Picks.Enabled : s.Bans.Enabled;
        if (_automate && s.AutomationEnabled && kindEnabled)
        {
            // The planner locks its target (override, manual hover or list pick) on the next evaluation.
            _memory.LockNowRequested.Add(kind);
            Decide($"Phone: lock in the {kind} now");
            return null;
        }

        // Not automating: lock whatever is hovered.
        if (action.ChampionId == 0)
        {
            return "Nothing is hovered yet";
        }

        await ExecuteAsync(new ChampSelectCommand(ChampSelectCommandKind.Lock, action.Id, action.ChampionId,
            $"{(kind == Ban ? "Banning" : "Locking in")} {catalog.NameOf(action.ChampionId)} now (requested from your phone)"),
            time.GetUtcNow().ToUnixTimeMilliseconds(), stoppingToken).ConfigureAwait(false);
        return _memory.LockSent.Contains(action.Id) ? null : "The client refused the lock-in";
    }

    private async Task BeginAsync(ChampSelectSession session, CancellationToken stoppingToken)
    {
        Reset();
        _gameId = session.GameId;
        var s = settings.Snapshot();

        var (queue, isCustom, gameMode) = await ReadQueueAsync(stoppingToken).ConfigureAwait(false);
        _queue = queue;
        var role = ChampSelectPlanner.RoleFromPosition(session.LocalPlayer?.AssignedPosition);
        var myPicks = session.AllActions.Where(a => a.Type == Pick && a.IsAllyAction).ToList();
        var pickNumber = myPicks.FindIndex(a => a.ActorCellId == session.LocalPlayerCellId) + 1;

        Decide($"Champ select started: {queue}. Role: {ChampSelectPlanner.RoleName(role)}"
            + (pickNumber > 0 ? $", you're pick {pickNumber} of {myPicks.Count} on your team" : ""));

        _automate = ShouldAutomate(gameMode, isCustom, s.General.AutomateCustomGames);
        if (!_automate)
        {
            _notAutomatedReason = isCustom && !s.General.AutomateCustomGames
                ? "Custom game — picks and bans are up to you. To change this, tick \"Also automate picks and bans in custom games\" under General on the Settings tab in LockIn on your PC"
                : $"LockIn only automates Summoner's Rift picks and bans; in {gameMode} they're up to you";
            Decide(_notAutomatedReason);
        }
        else if (!s.AutomationEnabled)
        {
            Decide("Champ select automation is switched off — LockIn won't ban or pick");
        }

        // Anything already done before we joined shouldn't be announced as new.
        foreach (var action in MyActions(session).Where(a => a.Completed))
        {
            _announced.Add(action.Id);
        }

        await RefreshAvailabilityAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Summoner's Rift is automated. Custom games need the setting, and Practice Tool counts as one:
    /// the client reports it as a custom game with the mode "PRACTICETOOL".
    /// </summary>
    internal static bool ShouldAutomate(string? gameMode, bool isCustom, bool automateCustomGames) =>
        isCustom
            ? automateCustomGames && gameMode is null or "CLASSIC" or "PRACTICETOOL"
            : gameMode is null or "CLASSIC";

    private async Task<(string Queue, bool IsCustom, string? GameMode)> ReadQueueAsync(CancellationToken stoppingToken)
    {
        if (connection.Client is not { } client)
        {
            return ("unknown queue", false, null);
        }

        try
        {
            var flow = await client.GetAsync<GameflowSession>(LcuEndpoints.GameflowSession, stoppingToken).ConfigureAwait(false);
            var data = flow?.GameData;
            var queue = data?.Queue;
            var name = queue?.Description is { Length: > 0 } d ? d : queue?.GameMode ?? "unknown queue";
            return ($"{name}{(queue is { Id: > 0 } ? $" (queue {queue.Id})" : "")}", data?.IsCustomGame ?? false, queue?.GameMode);
        }
        catch (LcuRequestException ex)
        {
            logger.LogDebug("Could not read the queue: {Message}", ex.Message);
            return ("unknown queue", false, null);
        }
    }

    private async Task RefreshAvailabilityAsync(CancellationToken stoppingToken)
    {
        if (connection.Client is not { } client)
        {
            return;
        }

        try
        {
            var pickable = await client.GetAsync<int[]>("lol-champ-select/v1/pickable-champion-ids", stoppingToken).ConfigureAwait(false);
            var bannable = await client.GetAsync<int[]>("lol-champ-select/v1/bannable-champion-ids", stoppingToken).ConfigureAwait(false);
            _pickable = pickable is { Length: > 0 } ? pickable.ToHashSet() : null;
            _bannable = bannable is { Length: > 0 } ? bannable.ToHashSet() : null;
            logger.LogDebug("Pickable champions: {Pickable}, bannable: {Bannable}",
                _pickable?.Count.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                _bannable?.Count.ToString(CultureInfo.InvariantCulture) ?? "unknown");
        }
        catch (LcuRequestException ex)
        {
            logger.LogDebug("Could not read pickable champions: {Message}", ex.Message);
        }

        _pickable ??= catalog.Playable;
    }

    private async Task EvaluateAsync(CancellationToken stoppingToken)
    {
        var session = _session!;
        var s = settings.Snapshot();
        if (!_automate || !s.AutomationEnabled)
        {
            _lastPlan = null;
            return;
        }

        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var action in MyActions(session).Where(a => a.IsInProgress))
        {
            _memory.FirstSeenInProgress.TryAdd(action.Id, now);
            if (action.Type == Pick && !_pickableRefreshedForTurn)
            {
                // Ownership/free rotation can change; refresh once when our pick turn begins.
                _pickableRefreshedForTurn = true;
                await RefreshAvailabilityAsync(stoppingToken).ConfigureAwait(false);
            }
        }

        var options = ChampSelectOptions.From(s) with { PickOverride = _pickOverride, BanOverride = _banOverride };
        var plan = ChampSelectPlanner.Plan(session, options, _memory, now, _pickable, _bannable, catalog.NameOf);
        _lastPlan = plan;

        foreach (var note in plan.Notes)
        {
            if (!_lastNotes.TryGetValue(note.Topic, out var last) || last != note.Message)
            {
                _lastNotes[note.Topic] = note.Message;
                Decide(note.Message);
            }
        }

        foreach (var command in plan.Commands)
        {
            if (_retryAt.TryGetValue(command.ActionId, out var retryAt) && now < retryAt)
            {
                continue;
            }

            await ExecuteAsync(command, now, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(ChampSelectCommand command, long now, CancellationToken stoppingToken)
    {
        if (connection.Client is not { } client)
        {
            return;
        }

        Decide(command.Description);
        var path = $"lol-champ-select/v1/session/actions/{command.ActionId}";
        try
        {
            switch (command.Kind)
            {
                case ChampSelectCommandKind.DeclareIntent:
                    await client.PatchAsync(path, new { championId = command.ChampionId }, stoppingToken).ConfigureAwait(false);
                    _memory.HoveredByUs[command.ActionId] = command.ChampionId;
                    _memory.IntentDeclared = true;
                    break;

                case ChampSelectCommandKind.Hover:
                    await client.PatchAsync(path, new { championId = command.ChampionId }, stoppingToken).ConfigureAwait(false);
                    _memory.HoveredByUs[command.ActionId] = command.ChampionId;
                    break;

                case ChampSelectCommandKind.Lock:
                    await client.PatchAsync(path, new { championId = command.ChampionId, completed = true }, stoppingToken).ConfigureAwait(false);
                    _memory.HoveredByUs[command.ActionId] = command.ChampionId;
                    _memory.LockSent.Add(command.ActionId);
                    if (_session?.AllActions.FirstOrDefault(a => a.Id == command.ActionId) is { } locked)
                    {
                        _memory.LockNowRequested.Remove(locked.Type);
                    }

                    break;
            }

            _retryAt.Remove(command.ActionId);
        }
        catch (LcuRequestException ex)
        {
            logger.LogWarning("Couldn't do that: {Message}. Retrying in a moment.", ex.Message);
            _retryAt[command.ActionId] = now + (long)RetryAfterFailure.TotalMilliseconds;
        }
    }

    private void AnnounceCompletedActions(ChampSelectSession session)
    {
        foreach (var action in MyActions(session).Where(a => a.Completed))
        {
            if (!_announced.Add(action.Id))
            {
                continue;
            }

            var byUs = _memory.LockSent.Contains(action.Id);
            var name = action.ChampionId > 0 ? catalog.NameOf(action.ChampionId) : null;
            switch (action.Type)
            {
                case Ban when name is null:
                    Decide("Your ban was skipped");
                    break;
                case Ban:
                    Decide(byUs ? $"✓ Banned {name}" : $"You banned {name}");
                    break;
                case Pick when name is not null:
                    Decide(byUs ? $"✓ Locked in {name}" : $"You locked in {name}");
                    ChampionLocked?.Invoke(this, new ChampionLockedEventArgs(action.ChampionId, _automate));
                    break;
            }
        }
    }

    /// <summary>Logs a decision and keeps it for the phone's "recent decisions" list.</summary>
    private void Decide(string message)
    {
        logger.LogInformation("{Decision}", message);
        lock (_recentGate)
        {
            _recent.Add(new DecisionEntry(time.GetUtcNow().ToUnixTimeMilliseconds(), message));
            if (_recent.Count > MaxRecentDecisions)
            {
                _recent.RemoveAt(0);
            }
        }
    }

    private void PublishView()
    {
        var s = settings.Snapshot();
        _view = _session is null
            ? null
            : ChampSelectViewBuilder.Build(
                _session, _lastPlan, time.GetUtcNow().ToUnixTimeMilliseconds(), _queue, _pickOverride, _banOverride,
                automated: _automate && s.AutomationEnabled,
                automationNote: !_automate ? _notAutomatedReason : !s.AutomationEnabled ? "Champ select automation is off" : null);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<ChampSelectAction> MyActions(ChampSelectSession session) =>
        session.AllActions.Where(a => a.ActorCellId == session.LocalPlayerCellId);

    private void Reset()
    {
        _session = null;
        _memory = new ChampSelectMemory();
        _lastPlan = null;
        _lastNotes.Clear();
        _retryAt.Clear();
        _announced.Clear();
        _pickable = null;
        _bannable = null;
        _automate = false;
        _notAutomatedReason = null;
        _queue = null;
        _pickOverride = null;
        _banOverride = null;
        _pickableRefreshedForTurn = false;
        _gameId = -1;
    }

    private sealed record GameflowSession(GameflowGameData? GameData);

    private sealed record GameflowGameData(bool IsCustomGame, GameflowQueue? Queue);

    private sealed record GameflowQueue(int Id, string? GameMode, string? Description);
}

public sealed class ChampionLockedEventArgs(int championId, bool isAutomatedMode) : EventArgs
{
    public int ChampionId { get; } = championId;

    /// <summary>True when this champ select is one LockIn automates (Summoner's Rift, not custom unless enabled).</summary>
    public bool IsAutomatedMode { get; } = isAutomatedMode;
}
