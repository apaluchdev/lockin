using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Settings;

/// <summary>
/// Owns the settings: thread-safe updates, cheap snapshots for background services,
/// and debounced, atomic saves to disk.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly ILogger<SettingsStore> _logger;
    private readonly Timer _saveTimer;
    private readonly AppSettings _current;
    private AppSettings? _snapshot;
    private bool _dirty;

    public SettingsStore(string path, ILogger<SettingsStore> logger)
    {
        Path = path;
        _logger = logger;
        _current = Load();
        _saveTimer = new Timer(_ => Flush());
    }

    public string Path { get; }

    /// <summary>Raised (on the updating thread) after every <see cref="Update"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>A private copy of the current settings, safe to read from any thread.</summary>
    public AppSettings Snapshot()
    {
        lock (_gate)
        {
            return _snapshot ??= Clone(_current);
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            mutate(_current);
            _snapshot = null;
            _dirty = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes pending changes now.</summary>
    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            json = JsonSerializer.Serialize(_current, JsonOptions);
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save settings to {Path}", Path);
        }
    }

    private AppSettings Load()
    {
        if (!File.Exists(Path))
        {
            _logger.LogInformation("No settings file yet; using defaults ({Path})", Path);
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), JsonOptions) ?? new AppSettings();
            _logger.LogDebug("Loaded settings from {Path}", Path);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var backup = Path + ".bad";
            _logger.LogWarning("Settings file is unreadable ({Message}); starting from defaults and keeping the old file as {Backup}", ex.Message, backup);
            try
            {
                File.Copy(Path, backup, overwrite: true);
            }
            catch (IOException)
            {
                // Best effort.
            }

            return new AppSettings();
        }
    }

    internal static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }
}
