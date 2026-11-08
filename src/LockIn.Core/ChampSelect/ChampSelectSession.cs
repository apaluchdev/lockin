namespace LockIn.Core.ChampSelect;

/// <summary>The parts of <c>/lol-champ-select/v1/session</c> LockIn uses.</summary>
public sealed record ChampSelectSession
{
    public long GameId { get; init; }

    public int LocalPlayerCellId { get; init; } = -1;

    public bool IsCustomGame { get; init; }

    /// <summary>Turns, in order. Each inner list is one turn (several actions when players act simultaneously).</summary>
    public List<List<ChampSelectAction>> Actions { get; init; } = [];

    public List<ChampSelectPlayer> MyTeam { get; init; } = [];

    public List<ChampSelectPlayer> TheirTeam { get; init; } = [];

    public ChampSelectTimer Timer { get; init; } = new();

    public ChampSelectBans? Bans { get; init; }

    public IEnumerable<ChampSelectAction> AllActions => Actions.SelectMany(turn => turn);

    public ChampSelectPlayer? LocalPlayer => MyTeam.FirstOrDefault(p => p.CellId == LocalPlayerCellId);
}

public sealed record ChampSelectAction
{
    public const string Ban = "ban";
    public const string Pick = "pick";

    public int Id { get; init; }

    public int ActorCellId { get; init; }

    /// <summary>The hovered (or, once completed, locked) champion; 0 for none.</summary>
    public int ChampionId { get; init; }

    public bool Completed { get; init; }

    public bool IsAllyAction { get; init; }

    public bool IsInProgress { get; init; }

    /// <summary>"ban", "pick", or bookkeeping types such as "ten_bans_reveal".</summary>
    public string Type { get; init; } = "";
}

public sealed record ChampSelectPlayer
{
    public int CellId { get; init; }

    /// <summary>Hovered or locked champion; 0 for none.</summary>
    public int ChampionId { get; init; }

    /// <summary>Champion shown as intent during planning; 0 for none.</summary>
    public int ChampionPickIntent { get; init; }

    /// <summary>"top", "jungle", "middle", "bottom", "utility" or empty when there are no roles.</summary>
    public string? AssignedPosition { get; init; }

    public int Spell1Id { get; init; }

    public int Spell2Id { get; init; }
}

public sealed record ChampSelectTimer
{
    public const string Planning = "PLANNING";
    public const string BanPick = "BAN_PICK";
    public const string Finalization = "FINALIZATION";

    /// <summary>Milliseconds left in the phase <i>at the moment the snapshot was taken</i> (<see cref="InternalNowInEpochMs"/>).</summary>
    public long AdjustedTimeLeftInPhase { get; init; }

    /// <summary>When the snapshot was taken (Unix ms). The client only refreshes it when the session changes.</summary>
    public long InternalNowInEpochMs { get; init; }

    public long TotalTimeInPhase { get; init; }

    public bool IsInfinite { get; init; }

    public string? Phase { get; init; }
}

public sealed record ChampSelectBans
{
    public List<int> MyTeamBans { get; init; } = [];

    public List<int> TheirTeamBans { get; init; } = [];
}
