using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LockIn.Core.ChampSelect;
using LockIn.Core.Live;
using LockIn.Core.Loadouts;
using Microsoft.Extensions.Logging;

namespace LockIn.Remote;

/// <summary>
/// The phones' WebSocket connections: pushes live state when it changes plus a heartbeat every second,
/// and carries the phone's commands (override, lock now) to the champ select service.
/// </summary>
internal sealed class RemoteHub : IDisposable
{
    public const int MaxPhones = 8;
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly LiveStateService _live;
    private readonly ChampSelectService _champSelect;
    private readonly LoadoutService _loadouts;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, Phone> _phones = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _broadcaster;
    private int _dirty = 1;

    public RemoteHub(LiveStateService live, ChampSelectService champSelect, LoadoutService loadouts, TimeProvider time, ILogger logger)
    {
        _live = live;
        _champSelect = champSelect;
        _loadouts = loadouts;
        _time = time;
        _logger = logger;
        _live.Changed += OnLiveChanged;
        _broadcaster = Task.Run(BroadcastLoopAsync);
    }

    public int Count => _phones.Count;

    /// <summary>Raised when a phone connects or disconnects.</summary>
    public event EventHandler? CountChanged;

    private void OnLiveChanged(object? sender, EventArgs e) => Volatile.Write(ref _dirty, 1);

    public async Task RunAsync(WebSocket socket, IPAddress? address, CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _stopping.Token);
        var phone = new Phone(socket);
        _phones[phone.Id] = phone;
        _logger.LogInformation("Phone connected ({Address})", address);
        CountChanged?.Invoke(this, EventArgs.Empty);

        var sender = phone.SendLoopAsync(linked.Token);
        try
        {
            phone.Enqueue(StateMessage());
            await ReceiveLoopAsync(phone, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Phone went away (screen locked, Wi-Fi dropped…). It reconnects by itself.
        }
        finally
        {
            _phones.TryRemove(phone.Id, out _);
            phone.Complete();
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await sender.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Already closing.
            }

            _logger.LogInformation("Phone disconnected ({Address})", address);
            CountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ReceiveLoopAsync(Phone phone, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (phone.Socket.State == WebSocketState.Open)
        {
            var result = await phone.Socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await phone.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > 64 * 1024)
            {
                return; // Nothing legitimate is this big.
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            phone.Enqueue(await HandleCommandAsync(text).ConfigureAwait(false));
        }
    }

    internal async Task<string> HandleCommandAsync(string text)
    {
        string? requestId = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            requestId = root.TryGetProperty("id", out var id) ? id.ToString() : null;
            var type = root.GetProperty("type").GetString();
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;

            string? error = type switch
            {
                "override" when kind is ChampSelectService.Pick or ChampSelectService.Ban =>
                    await _champSelect.SetOverrideAsync(kind,
                        root.TryGetProperty("championId", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null).ConfigureAwait(false),
                "lockNow" when kind is ChampSelectService.Pick or ChampSelectService.Ban =>
                    await _champSelect.LockNowAsync(kind).ConfigureAwait(false),
                "runeOverride" =>
                    await _loadouts.SetRunePageOverrideAsync(
                        root.TryGetProperty("pageId", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : null).ConfigureAwait(false),
                "spellOverride" =>
                    await _loadouts.SetSpellsOverrideAsync(Spell(root, "spell1Id"), Spell(root, "spell2Id")).ConfigureAwait(false),
                "ping" => null,
                _ => "Unknown command",
            };

            Volatile.Write(ref _dirty, 1); // Show the effect right away.
            return JsonSerializer.Serialize(new { type = type == "ping" ? "pong" : "result", id = requestId, ok = error is null, message = error }, Json);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return JsonSerializer.Serialize(new { type = "result", id = requestId, ok = false, message = "Malformed command" }, Json);
        }
    }

    private static int? Spell(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private string StateMessage() => JsonSerializer.Serialize(new { type = "state", build = RemoteServer.BuildId, state = _live.Current() }, Json);

    private async Task BroadcastLoopAsync()
    {
        var lastHeartbeat = _time.GetTimestamp();
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(BroadcastInterval, _time, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_phones.IsEmpty)
            {
                continue;
            }

            if (Interlocked.Exchange(ref _dirty, 0) == 1)
            {
                Broadcast(StateMessage());
            }

            if (_time.GetElapsedTime(lastHeartbeat) >= HeartbeatInterval)
            {
                lastHeartbeat = _time.GetTimestamp();
                Broadcast(JsonSerializer.Serialize(new { type = "heartbeat", serverTime = _time.GetUtcNow().ToUnixTimeMilliseconds() }, Json));
            }
        }
    }

    private void Broadcast(string message)
    {
        foreach (var phone in _phones.Values)
        {
            phone.Enqueue(message);
        }
    }

    /// <summary>Closes every connection (e.g. after the key changed).</summary>
    public void DisconnectAll()
    {
        foreach (var phone in _phones.Values)
        {
            phone.Socket.Abort();
        }
    }

    public void Dispose()
    {
        _live.Changed -= OnLiveChanged;
        _stopping.Cancel();
        DisconnectAll();
        try
        {
            _broadcaster.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Stopping.
        }

        _stopping.Dispose();
    }

    private sealed class Phone(WebSocket socket)
    {
        // Slow phones only ever get the latest few messages.
        private readonly Channel<string> _outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        public Guid Id { get; } = Guid.NewGuid();

        public WebSocket Socket { get; } = socket;

        public void Enqueue(string message) => _outbox.Writer.TryWrite(message);

        public void Complete() => _outbox.Writer.TryComplete();

        public async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Socket.State != WebSocketState.Open)
                {
                    return;
                }

                await Socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
