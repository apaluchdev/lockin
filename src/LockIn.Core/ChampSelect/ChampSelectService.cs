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
/// second, sends the resulting hovers/locks to the client, and logs every decision once.
/// </summary>
public sealed class ChampSelectService(
    LcuConnection connection,
    GameflowMonitor gameflow,
    ChampionCatalog catalog,
    SettingsStore settings,
    TimeProvider time,
    ILogger<ChampSelectService> logger) : BackgroundService
{
    private const string SessionPath = "lol-champ-select/v1/session";
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(1);

    private readonly Channel<Message> _inbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });

    // Everything below is only touched by the loop.
    private ChampSelectSession? _session;
    private ChampSelectMemory _memory = new();
    private readonly Dictionary<string, string> _lastNotes = [];
    private readonly Dictionary<int, long> _retryAt = [];
    private readonly HashSet<int> _announced = [];
    private IReadOnlySet<int>? _pickable;
    private HashSet<int>? _bannable;
    private bool _automate;
    private bool _pickableRefreshedForTurn;
    private long _gameId = -1;

    /// <summary>Raised (on a background thread) when the local player's champion is locked in. Used for runes/spells.</summary>
    public event EventHandler<ChampionLockedEventArgs>? ChampionLocked;

    private abstract record Message;

    private sealed record SessionUpdated(ChampSelectSession Session) : Message;

    private sealed record Ended : Message;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        connection.EventReceived += OnEvent;
        gameflow.PhaseChanged += OnPhaseChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        connection.EventReceived -= OnEvent;
        gameflow.PhaseChanged -= OnPhaseChanged;
        return base.StopAsync(cancellationToken);
    }

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
                logger.LogInformation("Champ select ended");
                Reset();
                break;
        }
    }

    private async Task BeginAsync(ChampSelectSession session, CancellationToken stoppingToken)
    {
        Reset();
        _gameId = session.GameId;
        var s = settings.Snapshot();

        var (queue, isCustom, gameMode) = await ReadQueueAsync(stoppingToken).ConfigureAwait(false);
        var player = session.LocalPlayer;
        var role = ChampSelectPlanner.RoleFromPosition(player?.AssignedPosition);
        var myPicks = session.AllActions.Where(a => a.Type == ChampSelectAction.Pick && a.IsAllyAction).ToList();
        var pickNumber = myPicks.FindIndex(a => a.ActorCellId == session.LocalPlayerCellId) + 1;

        logger.LogInformation("Champ select started: {Queue}. Role: {Role}{PickOrder}",
            queue, ChampSelectPlanner.RoleName(role),
            pickNumber > 0 ? $", you're pick {pickNumber} of {myPicks.Count} on your team" : "");

        _automate = gameMode is null or "CLASSIC" && (!isCustom || s.General.AutomateCustomGames);
        if (!_automate)
        {
            logger.LogInformation("{Message}", isCustom
                ? "Custom game — picks and bans are up to you (enable custom games in Settings to change this)"
                : $"LockIn only automates Summoner's Rift picks and bans; in {gameMode} they're up to you");
        }
        else if (!s.AutomationEnabled)
        {
            logger.LogInformation("Champ select automation is switched off — LockIn won't ban or pick");
        }

        // Anything already done before we joined shouldn't be announced as new.
        foreach (var action in session.AllActions.Where(a => a.ActorCellId == session.LocalPlayerCellId && a.Completed))
        {
            _announced.Add(action.Id);
        }

        await RefreshAvailabilityAsync(stoppingToken).ConfigureAwait(false);
    }

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
                _pickable?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
                _bannable?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
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
            return;
        }

        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var action in session.AllActions.Where(a => a.ActorCellId == session.LocalPlayerCellId && a.IsInProgress))
        {
            _memory.FirstSeenInProgress.TryAdd(action.Id, now);
            if (action.Type == ChampSelectAction.Pick && !_pickableRefreshedForTurn)
            {
                // Ownership/free rotation can change; refresh once when our pick turn begins.
                _pickableRefreshedForTurn = true;
                await RefreshAvailabilityAsync(stoppingToken).ConfigureAwait(false);
            }
        }

        var plan = ChampSelectPlanner.Plan(session, ChampSelectOptions.From(s), _memory, now, _pickable, _bannable, catalog.NameOf);

        foreach (var note in plan.Notes)
        {
            if (!_lastNotes.TryGetValue(note.Topic, out var last) || last != note.Message)
            {
                _lastNotes[note.Topic] = note.Message;
                logger.LogInformation("{Decision}", note.Message);
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

        logger.LogInformation("{Action}", command.Description);
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
        foreach (var action in session.AllActions.Where(a => a.ActorCellId == session.LocalPlayerCellId && a.Completed))
        {
            if (!_announced.Add(action.Id))
            {
                continue;
            }

            var byUs = _memory.LockSent.Contains(action.Id);
            var name = action.ChampionId > 0 ? catalog.NameOf(action.ChampionId) : null;
            switch (action.Type)
            {
                case ChampSelectAction.Ban when name is null:
                    logger.LogInformation("Your ban was skipped");
                    break;
                case ChampSelectAction.Ban:
                    logger.LogInformation("{Message}", byUs ? $"✓ Banned {name}" : $"You banned {name}");
                    break;
                case ChampSelectAction.Pick when name is not null:
                    logger.LogInformation("{Message}", byUs ? $"✓ Locked in {name}" : $"You locked in {name}");
                    ChampionLocked?.Invoke(this, new ChampionLockedEventArgs(action.ChampionId, _automate));
                    break;
            }
        }
    }

    private void Reset()
    {
        _session = null;
        _memory = new ChampSelectMemory();
        _lastNotes.Clear();
        _retryAt.Clear();
        _announced.Clear();
        _pickable = null;
        _bannable = null;
        _automate = false;
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
