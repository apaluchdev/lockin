using System.Collections.Frozen;
using System.Text.Json;
using LockIn.Core.Lcu;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Champions;

/// <param name="Id">Riot's numeric champion ID (what the client API uses).</param>
/// <param name="Key">Data Dragon's string ID, e.g. "MonkeyKing" for Wukong (used for icon file names).</param>
/// <param name="Name">Display name.</param>
public sealed record Champion(int Id, string Key, string Name);

/// <summary>
/// The list of all champions (from Riot's public Data Dragon CDN, cached on disk so it works offline),
/// their icons, and which ones the logged-in player can play.
/// </summary>
public sealed class ChampionCatalog : IHostedService, IDisposable
{
    private const string DataDragon = "https://ddragon.leagueoflegends.com";

    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly HttpClient _http;
    private readonly LcuConnection _connection;
    private readonly ILogger<ChampionCatalog> _logger;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _downloadGate = new(6);
    private readonly Dictionary<string, Task<string?>> _iconTasks = [];
    private readonly Lock _gate = new();
    private CancellationTokenSource? _stopping;

    public ChampionCatalog(LcuConnection connection, ILogger<ChampionCatalog> logger, HttpMessageHandler? handler = null, string? cacheDirectory = null)
    {
        _connection = connection;
        _logger = logger;
        _cacheDirectory = cacheDirectory ?? AppPaths.CacheDirectory;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LockIn/1.0");
    }

    /// <summary>All champions, sorted by name. Empty until loaded.</summary>
    public IReadOnlyList<Champion> All { get; private set; } = [];

    public string? Version { get; private set; }

    /// <summary>Champion IDs the player owns or can play for free right now; null when not known (client not connected).</summary>
    public IReadOnlySet<int>? Playable { get; private set; }

    private FrozenDictionary<int, Champion> ById { get; set; } = FrozenDictionary<int, Champion>.Empty;

    /// <summary>Raised (on a background thread) when <see cref="All"/> changes.</summary>
    public event EventHandler? ChampionsChanged;

    /// <summary>Raised (on a background thread) when <see cref="Playable"/> changes.</summary>
    public event EventHandler? PlayableChanged;

    private string ChampionCacheFile => Path.Combine(_cacheDirectory, "champions.json");

    private string IconDirectory => Path.Combine(_cacheDirectory, "icons");

    public Champion? Find(int id) => ById.GetValueOrDefault(id);

    public string NameOf(int id) => Find(id)?.Name ?? $"champion #{id}";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _connection.StateChanged += OnConnectionStateChanged;
        _ = LoadAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _connection.StateChanged -= OnConnectionStateChanged;
        _stopping?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Loads the cached list, then refreshes it from Data Dragon if there's a newer patch.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var cached = ReadCache();
        if (cached is not null)
        {
            Publish(cached.Version, cached.Champions);
            _logger.LogDebug("Loaded {Count} champions from cache (patch {Version})", cached.Champions.Count, cached.Version);
        }

        try
        {
            var versions = await GetJsonAsync<string[]>($"{DataDragon}/api/versions.json", cancellationToken).ConfigureAwait(false);
            var latest = versions?.FirstOrDefault();
            if (latest is null || latest == cached?.Version)
            {
                return;
            }

            using var doc = await GetJsonAsync<JsonDocument>($"{DataDragon}/cdn/{latest}/data/en_US/champion.json", cancellationToken).ConfigureAwait(false);
            var champions = ParseDataDragon(doc!);
            WriteCache(new ChampionCache(latest, champions));
            Publish(latest, champions);
            _logger.LogInformation("Champion list updated to patch {Version} ({Count} champions)", latest, champions.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or IOException or KeyNotFoundException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (cached is null)
            {
                _logger.LogWarning("Could not download the champion list ({Message}). It will be loaded from the League client once it's running.", ex.Message);
            }
            else
            {
                _logger.LogDebug("Could not check for a newer champion list: {Message}", ex.Message);
            }
        }
    }

    /// <summary>Path to the champion's square icon, downloading it on first use. Null if unavailable.</summary>
    public Task<string?> GetIconPathAsync(Champion champion)
    {
        lock (_gate)
        {
            if (!_iconTasks.TryGetValue(champion.Key, out var task) || (task.IsCompleted && task.Result is null))
            {
                task = DownloadIconAsync(champion);
                _iconTasks[champion.Key] = task;
            }

            return task;
        }
    }

    private async Task<string?> DownloadIconAsync(Champion champion)
    {
        var path = Path.Combine(IconDirectory, champion.Key + ".png");
        if (File.Exists(path))
        {
            return path;
        }

        if (Version is null)
        {
            return null;
        }

        await _downloadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var bytes = await _http.GetByteArrayAsync(new Uri($"{DataDragon}/cdn/{Version}/img/champion/{champion.Key}.png")).ConfigureAwait(false);
            Directory.CreateDirectory(IconDirectory);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogDebug("Could not download the icon for {Champion}: {Message}", champion.Name, ex.Message);
            return null;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>Reloads which champions the player can play (owned, free rotation, rentals).</summary>
    public async Task RefreshPlayableAsync(CancellationToken cancellationToken = default)
    {
        if (_connection.Client is not { } client || _connection.Summoner is not { } summoner)
        {
            return;
        }

        try
        {
            var inventory = await client.GetAsync<List<InventoryChampion>>(
                $"lol-champions/v1/inventories/{summoner.SummonerId}/champions-minimal", cancellationToken).ConfigureAwait(false);
            if (inventory is null)
            {
                return;
            }

            var playable = inventory.Where(c => c.Id > 0 && c.IsPlayable).Select(c => c.Id).ToFrozenSet();
            Playable = playable;
            _logger.LogDebug("You can play {Count} champions right now", playable.Count);
            PlayableChanged?.Invoke(this, EventArgs.Empty);

            if (All.Count == 0)
            {
                await LoadFromClientAsync(client, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (LcuRequestException ex)
        {
            _logger.LogDebug("Could not read owned champions: {Message}", ex.Message);
        }
    }

    private async Task LoadFromClientAsync(LcuClient client, CancellationToken cancellationToken)
    {
        var summary = await client.GetAsync<List<ClientChampionSummary>>("lol-game-data/assets/v1/champion-summary.json", cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return;
        }

        var champions = summary.Where(c => c.Id > 0)
            .Select(c => new Champion(c.Id, c.Alias, c.Name))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Publish(Version, champions);
        _logger.LogInformation("Loaded {Count} champions from the League client", champions.Count);
    }

    private void OnConnectionStateChanged(object? sender, EventArgs e)
    {
        if (_connection.State == LcuConnectionState.Connected)
        {
            _ = RefreshPlayableAsync(_stopping?.Token ?? default);
        }
        else if (Playable is not null)
        {
            Playable = null;
            PlayableChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static List<Champion> ParseDataDragon(JsonDocument doc) =>
        doc.RootElement.GetProperty("data").EnumerateObject()
            .Select(p => new Champion(
                int.Parse(p.Value.GetProperty("key").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                p.Value.GetProperty("id").GetString()!,
                p.Value.GetProperty("name").GetString()!))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void Publish(string? version, IReadOnlyList<Champion> champions)
    {
        Version = version ?? Version;
        All = champions;
        ById = champions.ToFrozenDictionary(c => c.Id);
        ChampionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        await using var stream = await _http.GetStreamAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private ChampionCache? ReadCache()
    {
        try
        {
            return File.Exists(ChampionCacheFile)
                ? JsonSerializer.Deserialize<ChampionCache>(File.ReadAllText(ChampionCacheFile), CacheJson)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogDebug("Ignoring unreadable champion cache: {Message}", ex.Message);
            return null;
        }
    }

    private void WriteCache(ChampionCache cache)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllText(ChampionCacheFile, JsonSerializer.Serialize(cache, CacheJson));
        }
        catch (IOException ex)
        {
            _logger.LogDebug("Could not write the champion cache: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _downloadGate.Dispose();
        _stopping?.Dispose();
    }

    private sealed record ChampionCache(string Version, List<Champion> Champions);

    private sealed record ClientChampionSummary(int Id, string Name, string Alias);

    private sealed record Ownership(bool Owned, Rental? Rental);

    private sealed record Rental(bool Rented);

    private sealed record InventoryChampion(int Id, bool FreeToPlay, Ownership? Ownership)
    {
        public bool IsPlayable => FreeToPlay || Ownership?.Owned == true || Ownership?.Rental?.Rented == true;
    }
}
