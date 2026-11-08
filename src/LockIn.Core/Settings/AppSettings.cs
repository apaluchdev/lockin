namespace LockIn.Core.Settings;

/// <summary>Summoner's Rift positions as LockIn names them. <see cref="Any"/> is the fallback / no-role list.</summary>
public enum Role
{
    Any,
    Top,
    Jungle,
    Middle,
    Bottom,
    Support,
}

public enum FlashKey
{
    /// <summary>Use the spells exactly as configured per champion.</summary>
    AsConfigured,

    /// <summary>Always put Flash on D.</summary>
    D,

    /// <summary>Always put Flash on F.</summary>
    F,
}

/// <summary>Everything the user configures. Persisted as JSON by <see cref="SettingsStore"/>.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Accept the ready check when a match is found.</summary>
    public bool AutoAcceptEnabled { get; set; } = true;

    /// <summary>Master switch for champ select automation (bans, picks, runes, spells).</summary>
    public bool AutomationEnabled { get; set; } = true;

    public double AcceptDelaySeconds { get; set; }

    public BanSettings Bans { get; set; } = new();

    public PickSettings Picks { get; set; } = new();

    public LoadoutSettings Loadouts { get; set; } = new();

    public GeneralSettings General { get; set; } = new();

    public RemoteSettings Remote { get; set; } = new();
}

/// <summary>The phone page served on the local network.</summary>
public sealed class RemoteSettings
{
    public const int DefaultPort = 47800;

    public bool Enabled { get; set; }

    public int Port { get; set; } = DefaultPort;

    /// <summary>Secret key carried in the QR-code link. Generated on first use; regenerating it signs out every phone.</summary>
    public string? Key { get; set; }
}

public sealed class BanSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Wait this long after the ban turn starts before hovering, so teammates' hovers are visible.</summary>
    public double HoverDelaySeconds { get; set; } = 2;

    /// <summary>Lock the ban when this many seconds are left on the timer.</summary>
    public double LockBeforeEndSeconds { get; set; } = 3;

    /// <summary>Priority-ordered champion IDs per role. <see cref="Role.Any"/> is the default list.</summary>
    public Dictionary<Role, List<int>> Lists { get; set; } = [];
}

public sealed class PickSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Show the top pick as your intent during the planning phase (draft modes).</summary>
    public bool PreHoverInPlanning { get; set; } = true;

    /// <summary>Wait this long after the pick turn starts before hovering.</summary>
    public double HoverDelaySeconds { get; set; }

    /// <summary>Lock the pick when this many seconds are left on the timer.</summary>
    public double LockBeforeEndSeconds { get; set; } = 3;

    /// <summary>When no champion on the role's list is available, try the Any-role list.</summary>
    public bool FallBackToAnyRole { get; set; } = true;

    /// <summary>Priority-ordered champion IDs per role. <see cref="Role.Any"/> is used for blind pick / no role.</summary>
    public Dictionary<Role, List<int>> Lists { get; set; } = [];
}

public sealed class LoadoutSettings
{
    public bool Enabled { get; set; } = true;

    public FlashKey FlashKey { get; set; } = FlashKey.AsConfigured;

    /// <summary>Per-champion rune page and summoner spells, keyed by champion ID.</summary>
    public Dictionary<int, ChampionLoadout> Champions { get; set; } = [];
}

public sealed class ChampionLoadout
{
    /// <summary>The client's ID for a saved rune page.</summary>
    public long? RunePageId { get; set; }

    /// <summary>The page's name when it was chosen, used if the ID no longer exists.</summary>
    public string? RunePageName { get; set; }

    /// <summary>Summoner spell on D.</summary>
    public int? Spell1Id { get; set; }

    /// <summary>Summoner spell on F.</summary>
    public int? Spell2Id { get; set; }

    public bool IsEmpty => RunePageId is null && string.IsNullOrEmpty(RunePageName) && Spell1Id is null && Spell2Id is null;
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }

    /// <summary>The minimize button hides LockIn to the tray (closing always does).</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Run pick/ban automation in custom games too.</summary>
    public bool AutomateCustomGames { get; set; }

    /// <summary>League install folder, if not the default and the client can't be found automatically.</summary>
    public string? LeagueInstallPath { get; set; }

    public bool ShowDebugLog { get; set; }

    public bool TrayHintShown { get; set; }
}
