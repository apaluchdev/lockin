using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Lcu;

public enum LcuConnectionState
{
    /// <summary>No League client found yet.</summary>
    Searching,

    /// <summary>Client found; waiting for its API to come up.</summary>
    Connecting,

    /// <summary>API reachable and event feed subscribed.</summary>
    Connected,
}

public sealed record CurrentSummoner(long SummonerId, string? Puuid, string? GameName, string? TagLine, string? DisplayName)
{
    public string Label =>
        !string.IsNullOrEmpty(GameName)
            ? string.IsNullOrEmpty(TagLine) ? GameName : $"{GameName}#{TagLine}"
            : DisplayName ?? $"summoner {SummonerId}";
}

/// <summary>
/// Keeps a connection to the League client: finds it, waits for its API, subscribes to the
/// WebSocket event feed, and starts over whenever the client closes or restarts.
/// </summary>
public sealed class LcuConnection(LcuLocator locator, ILogger<LcuConnection> logger) : BackgroundService
{
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromSeconds(1);

    private volatile LcuClient? _client;

    public LcuConnectionState State { get; private set; } = LcuConnectionState.Searching;

    /// <summary>The API client while <see cref="State"/> is Connected, otherwise null.</summary>
    public LcuClient? Client => State == LcuConnectionState.Connected ? _client : null;

    public CurrentSummoner? Summoner { get; private set; }

    /// <summary>Raised on a background thread whenever <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised on a background thread for each subscribed client event. Handlers must be quick.</summary>
    public event EventHandler<LcuEvent>? EventReceived;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Looking for the League client…");
        while (!stoppingToken.IsCancellationRequested)
        {
            var lockfile = locator.TryLocate();
            if (lockfile is null)
            {
                SetState(LcuConnectionState.Searching);
                await DelayAsync(SearchInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            logger.LogInformation("Found the League client: {Lockfile}", lockfile);
            try
            {
                await RunSessionAsync(lockfile, stoppingToken).ConfigureAwait(false);
                logger.LogInformation("The League client closed");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or LcuRequestException or IOException or OperationCanceledException)
            {
                logger.LogWarning("Lost the connection to the League client: {Message}", ex.Message);
            }
            finally
            {
                var client = _client;
                _client = null;
                Summoner = null;
                SetState(LcuConnectionState.Searching);
                client?.Dispose();
            }

            await DelayAsync(SearchInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunSessionAsync(LcuLockfile lockfile, CancellationToken stoppingToken)
    {
        SetState(LcuConnectionState.Connecting);
        var client = new LcuClient(lockfile);
        _client = client;

        var summoner = await WaitForApiAsync(client, lockfile, stoppingToken).ConfigureAwait(false);
        if (summoner is null)
        {
            return; // Client exited while starting up.
        }

        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = LcuCertificate.Callback;
        socket.Options.SetRequestHeader("Authorization", LcuClient.CreateAuthHeader(lockfile).ToString());
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        var scheme = lockfile.Protocol == "https" ? "wss" : "ws";
        await socket.ConnectAsync(new Uri($"{scheme}://127.0.0.1:{lockfile.Port}/"), stoppingToken).ConfigureAwait(false);

        foreach (var uri in LcuEndpoints.Subscribed)
        {
            var frame = Encoding.UTF8.GetBytes($"[5,\"{LcuEndpoints.EventName(uri)}\"]");
            await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, stoppingToken).ConfigureAwait(false);
            logger.LogDebug("Subscribed to {Uri}", uri);
        }

        Summoner = summoner;
        logger.LogInformation("Connected to the League client as {Summoner}", summoner.Label);
        SetState(LcuConnectionState.Connected);

        await ReceiveLoopAsync(socket, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>The client's API answers before the player is fully loaded; wait until we can read who is logged in.</summary>
    private async Task<CurrentSummoner?> WaitForApiAsync(LcuClient client, LcuLockfile lockfile, CancellationToken stoppingToken)
    {
        var loggedWaiting = false;
        while (true)
        {
            try
            {
                var summoner = await client.GetAsync<CurrentSummoner>("lol-summoner/v1/current-summoner", stoppingToken).ConfigureAwait(false);
                if (summoner is not null && summoner.SummonerId != 0)
                {
                    return summoner;
                }
            }
            catch (LcuRequestException ex)
            {
                logger.LogDebug("Client API not ready yet: {Message}", ex.Message);
            }

            if (!loggedWaiting)
            {
                logger.LogInformation("Waiting for the League client to finish loading…");
                loggedWaiting = true;
            }

            if (!IsAlive(lockfile.ProcessId))
            {
                return null;
            }

            await DelayAsync(ReadyPollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken stoppingToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, stoppingToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (LcuEventParser.TryParse(message.GetBuffer().AsSpan(0, (int)message.Length), out var lcuEvent))
                {
                    Raise(lcuEvent!);
                }

                message.SetLength(0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Raise(LcuEvent lcuEvent)
    {
        logger.LogTrace("Event {EventType} {Uri}", lcuEvent.EventType, lcuEvent.Uri);
        var handlers = EventReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<LcuEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, lcuEvent);
            }
#pragma warning disable CA1031 // One faulty subscriber must not break the event feed.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "Error handling client event {Uri}", lcuEvent.Uri);
            }
        }
    }

    private void SetState(LcuConnectionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // A faulty subscriber must not break the connection loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Error handling connection state change");
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the caller's loop checks the token.
        }
    }
}
