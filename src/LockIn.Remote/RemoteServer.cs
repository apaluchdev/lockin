using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LockIn.Core.Champions;
using LockIn.Core.ChampSelect;
using LockIn.Core.Live;
using LockIn.Core.Loadouts;
using LockIn.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LockIn.Remote;

/// <summary>
/// Serves the phone page on the local network while enabled in Settings. Starts, stops and moves ports
/// as the settings change.
/// </summary>
public sealed class RemoteServer : IHostedService, IAsyncDisposable
{
    private const string BuildPlaceholder = "__LOCKIN_BUILD__";

    private static readonly Dictionary<string, (string ContentType, byte[] Bytes)> StaticFiles = LoadStaticFiles();

    /// <summary>Identifies this version of the phone page, so a page left open across an update can reload itself.</summary>
    internal static string BuildId { get; } = ComputeBuildId();

    private readonly SettingsStore _settings;
    private readonly LiveStateService _live;
    private readonly ChampSelectService _champSelect;
    private readonly LoadoutService _loadouts;
    private readonly ChampionCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly ILogger<RemoteServer> _logger;
    private readonly RemoteAuth _auth;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private RemoteHub? _hub;
    private int _configuredPort = -1;
    private int _disposed;

    public RemoteServer(SettingsStore settings, LiveStateService live, ChampSelectService champSelect, LoadoutService loadouts,
        ChampionCatalog catalog, TimeProvider time, ILogger<RemoteServer> logger)
    {
        _settings = settings;
        _live = live;
        _champSelect = champSelect;
        _loadouts = loadouts;
        _catalog = catalog;
        _time = time;
        _logger = logger;
        _auth = new RemoteAuth(time);
    }

    public bool IsRunning => _app is not null;

    /// <summary>The port actually listened on (0 when stopped).</summary>
    public int Port { get; private set; }

    /// <summary>Why the server isn't running, if it should be.</summary>
    public string? Error { get; private set; }

    public string PairingCode => _auth.PairingCode;

    public string Key => _auth.Key;

    public int ConnectedPhones => _hub?.Count ?? 0;

    /// <summary>Raised (on any thread) when running state, port, phones or codes change.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>
    /// Development/testing switch (environment variable <c>LOCKIN_REMOTE_LOOPBACK=1</c>): listen on this PC only,
    /// which avoids Windows Firewall prompts. Phones can't connect in this mode.
    /// </summary>
    public static bool LoopbackOnly { get; set; } = Environment.GetEnvironmentVariable("LOCKIN_REMOTE_LOOPBACK") == "1";

    public static IReadOnlyList<IPAddress> Addresses() =>
        LoopbackOnly ? [IPAddress.Loopback] : NetworkAddresses.LocalIPv4();

    /// <summary>The address to type on the phone.</summary>
    public string AddressFor(IPAddress ip) => $"http://{ip}:{Port}";

    /// <summary>The link for the QR code: includes the key after '#', so it never leaves the phone in a request.</summary>
    public string LinkFor(IPAddress ip) => $"{AddressFor(ip)}/#k={Key}";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_settings.Snapshot().Remote.Key))
        {
            _settings.Update(s => s.Remote.Key = RemoteAuth.NewKey());
        }

        _settings.Changed += OnSettingsChanged;
        await ReconcileAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.Changed -= OnSettingsChanged;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopServerAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Signs out every phone: new key (QR link) and new pairing code.</summary>
    public void RegenerateKey()
    {
        _settings.Update(s => s.Remote.Key = RemoteAuth.NewKey());
        _auth.NewCode();
        _hub?.DisconnectAll();
        _logger.LogInformation("Phone access key changed — phones need to scan the new QR code or enter the new pairing code");
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _ = ReconcileAsync();

    /// <summary>Brings the server in line with the settings (on/off, port, key).</summary>
    private async Task ReconcileAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var remote = _settings.Snapshot().Remote;
            if (remote.Key is { } key && key != _auth.Key)
            {
                _auth.SetKey(key);
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }

            if (_app is not null && (!remote.Enabled || remote.Port != _configuredPort))
            {
                await StopServerAsync().ConfigureAwait(false);
            }

            if (_app is null && remote.Enabled)
            {
                await StartServerAsync(remote.Port).ConfigureAwait(false);
            }
            else if (!remote.Enabled && Error is not null)
            {
                Error = null;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartServerAsync(int port)
    {
        _configuredPort = port;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            if (LoopbackOnly)
            {
                options.Listen(IPAddress.Loopback, port);
            }
            else
            {
                options.ListenAnyIP(port);
            }
        });

        var app = builder.Build();
        var hub = new RemoteHub(_live, _champSelect, _loadouts, _time, _logger);
        hub.CountChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
        Map(app, hub);

        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            hub.Dispose();
            await app.DisposeAsync().ConfigureAwait(false);
            Error = $"Port {port} is already in use — pick another one";
            _logger.LogWarning("Phone page couldn't start: {Message}", ex.Message);
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _app = app;
        _hub = hub;
        Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("[::]", "localhost", StringComparison.Ordinal)).Port;
        Error = null;
        var where = Addresses() is [var first, ..] ? AddressFor(first) : $"port {Port}";
        _logger.LogInformation("Phone page running at {Address} (pairing code {Code})", where, _auth.PairingCode);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task StopServerAsync()
    {
        if (_app is null)
        {
            return;
        }

        _hub?.Dispose();
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
        _hub = null;
        Port = 0;
        _logger.LogInformation("Phone page stopped");
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Map(WebApplication app, RemoteHub hub)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy =
                "default-src 'self'; connect-src 'self' ws: wss:; img-src 'self' data:; style-src 'self'; script-src 'self'; base-uri 'none'; frame-ancestors 'none'";
            await next(context).ConfigureAwait(false);
        });

        foreach (var (path, file) in StaticFiles)
        {
            var bytes = path == "index.html"
                ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file.Bytes).Replace(BuildPlaceholder, BuildId, StringComparison.Ordinal))
                : file.Bytes;
            app.MapGet(path == "index.html" ? "/" : "/" + path, (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-cache";
                return Results.Bytes(bytes, file.ContentType);
            });
        }

        app.MapGet("/icons/{id:int}.png", async (int id, HttpContext context) =>
        {
            if (_catalog.Find(id) is not { } champion || await _catalog.GetIconPathAsync(champion).ConfigureAwait(false) is not { } path)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.File(path, "image/png");
        });

        app.MapPost("/api/pair", async (HttpContext context) =>
        {
            string? code = null;
            try
            {
                using var doc = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
                code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
            }
            catch (JsonException)
            {
                // Treated as a wrong code.
            }

            var address = context.Connection.RemoteIpAddress ?? IPAddress.None;
            switch (_auth.TryPair(address, code))
            {
                case RemoteAuth.PairResult.Ok:
                    _logger.LogInformation("Phone paired with the code ({Address})", address);
                    return Results.Json(new { key = _auth.Key });
                case RemoteAuth.PairResult.TooManyAttempts:
                    _logger.LogWarning("Too many wrong pairing codes from {Address}; blocked for a few minutes", address);
                    return Results.Json(new { message = "Too many wrong codes. Try again in a few minutes." }, statusCode: 429);
                default:
                    return Results.Json(new { message = "That code isn't right. Check the Phone tab on your PC." }, statusCode: 401);
            }
        });

        app.MapGet("/api/champions", (HttpContext context) =>
        {
            if (!IsAuthorized(context))
            {
                return Results.Unauthorized();
            }

            var s = _settings.Snapshot();
            var playable = _catalog.Playable;
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                champions = _catalog.All.Select(c => new { c.Id, c.Name, playable = playable?.Contains(c.Id) ?? true }),
                picks = s.Picks.Lists.ToDictionary(l => l.Key.ToString(), l => l.Value),
                bans = s.Bans.Lists.ToDictionary(l => l.Key.ToString(), l => l.Value),
            }, RemoteHub.Json);
        });

        app.MapGet("/api/loadout-options", async (HttpContext context) =>
        {
            if (!IsAuthorized(context))
            {
                return Results.Unauthorized();
            }

            var pages = await _loadouts.GetRunePagesAsync().ConfigureAwait(false) ?? [];
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                pages = pages.Select(p => new { p.Id, p.Name, p.IsActive }),
                spells = SummonerSpells.All,
            }, RemoteHub.Json);
        });

        app.Map("/ws", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!IsAuthorized(context) || !IsSameOrigin(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (hub.Count >= RemoteHub.MaxPhones)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await hub.RunAsync(socket, context.Connection.RemoteIpAddress, context.RequestAborted).ConfigureAwait(false);
        });
    }

    /// <summary>The key comes as "Authorization: Bearer …" or, for WebSockets (which can't set headers), "?k=…".</summary>
    private bool IsAuthorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var key = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : context.Request.Query["k"].ToString();
        return _auth.IsValidKey(key);
    }

    /// <summary>Blocks other websites from opening the socket from a browser on the PC (cross-site WebSocket hijacking).</summary>
    private static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin)
            || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static string ComputeBuildId()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, file) in StaticFiles.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData(file.Bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..16];
    }

    private static Dictionary<string, (string, byte[])> LoadStaticFiles()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, (string, byte[])>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("wwwroot/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var path = name["wwwroot/".Length..];
            var type = Path.GetExtension(path) switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".png" => "image/png",
                ".webmanifest" => "application/manifest+json",
                _ => "application/octet-stream",
            };
            files[path] = (type, copy.ToArray());
        }

        return files;
    }

    public async ValueTask DisposeAsync()
    {
        // The container may dispose us twice (singleton + hosted-service registration).
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }
}
