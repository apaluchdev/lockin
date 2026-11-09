namespace LockIn.Core.ChampSelect;

/// <summary>A display-ready picture of champ select (for the phone page). Champions are IDs.</summary>
public sealed record ChampSelectView
{
    /// <summary>"planning", "bans", "picks", "finalization" or "starting".</summary>
    public required string Stage { get; init; }

    public required string StageLabel { get; init; }

    /// <summary>Time left in the current phase as of <see cref="AsOf"/>; null when the timer is infinite.</summary>
    public long? RemainingMs { get; init; }

    public long? TotalMs { get; init; }

    /// <summary>When <see cref="RemainingMs"/> was computed (Unix ms, PC clock).</summary>
    public long AsOf { get; init; }

    public string? Queue { get; init; }

    public required string Role { get; init; }

    /// <summary>"ban" or "pick" while it's the local player's turn; otherwise null.</summary>
    public string? MyTurn { get; init; }

    public required IReadOnlyList<PlayerView> MyTeam { get; init; }

    public required IReadOnlyList<PlayerView> TheirTeam { get; init; }

    public required IReadOnlyList<int> MyTeamBans { get; init; }

    public required IReadOnlyList<int> TheirTeamBans { get; init; }

    public PlannedChoice? PlannedPick { get; init; }

    public PlannedChoice? PlannedBan { get; init; }

    public int? PickOverride { get; init; }

    public int? BanOverride { get; init; }

    /// <summary>The local player's current summoner spell on D (0 for none).</summary>
    public int MySpell1Id { get; init; }

    /// <summary>The local player's current summoner spell on F (0 for none).</summary>
    public int MySpell2Id { get; init; }

    public bool CanLockPick { get; init; }

    public bool CanLockBan { get; init; }

    /// <summary>Whether LockIn is automating this champ select; if not, <see cref="AutomationNote"/> says why.</summary>
    public bool Automated { get; init; }

    public string? AutomationNote { get; init; }
}

/// <param name="Status">"none", "intent", "picking", "hovering" or "locked".</param>
public sealed record PlayerView(int CellId, bool IsMe, string? Position, int ChampionId, string Status, bool IsActing, bool IsBanning);

public sealed record DecisionEntry(long Time, string Message);

internal static class ChampSelectViewBuilder
{
    public static ChampSelectView Build(
        ChampSelectSession session, ChampSelectPlan? plan, long nowMs, string? queue,
        int? pickOverride, int? banOverride, bool automated, string? automationNote)
    {
        var actions = session.AllActions.ToList();
        var me = session.LocalPlayerCellId;
        var timer = session.Timer;

        var anyBanInProgress = actions.Any(a => a.Type == ChampSelectAction.Ban && a.IsInProgress && !a.Completed);
        var (stage, label) = timer.Phase switch
        {
            ChampSelectTimer.Planning => ("planning", "Planning"),
            ChampSelectTimer.BanPick when anyBanInProgress => ("bans", "Ban phase"),
            ChampSelectTimer.BanPick => ("picks", "Pick phase"),
            ChampSelectTimer.Finalization => ("finalization", "Finalizing"),
            _ => ("starting", "Game starting"),
        };

        long? remaining = null;
        if (!timer.IsInfinite && timer.TotalTimeInPhase > 0)
        {
            var age = timer.InternalNowInEpochMs > 0 ? Math.Max(0, nowMs - timer.InternalNowInEpochMs) : 0;
            remaining = Math.Max(0, timer.AdjustedTimeLeftInPhase - age);
        }

        var myTurnAction = actions.FirstOrDefault(a => a.ActorCellId == me && a.IsInProgress && !a.Completed
            && a.Type is ChampSelectAction.Ban or ChampSelectAction.Pick);

        PlayerView Player(ChampSelectPlayer p)
        {
            var pick = actions.FirstOrDefault(a => a.ActorCellId == p.CellId && a.Type == ChampSelectAction.Pick);
            var banning = actions.Any(a => a.ActorCellId == p.CellId && a.Type == ChampSelectAction.Ban && a.IsInProgress && !a.Completed);
            var (champion, status, acting) = pick switch
            {
                { Completed: true } => (pick.ChampionId > 0 ? pick.ChampionId : p.ChampionId, "locked", false),
                { IsInProgress: true } => (pick.ChampionId, pick.ChampionId > 0 ? "hovering" : "picking", true),
                _ when p.ChampionPickIntent > 0 => (p.ChampionPickIntent, "intent", false),
                _ when p.ChampionId > 0 => (p.ChampionId, timer.Phase == ChampSelectTimer.Finalization ? "locked" : "hovering", false),
                _ => (0, "none", false),
            };
            return new PlayerView(p.CellId, p.CellId == me, string.IsNullOrEmpty(p.AssignedPosition) ? null : p.AssignedPosition,
                champion, status, acting, banning);
        }

        var myBans = actions.Where(a => a.Type == ChampSelectAction.Ban && a.Completed && a.IsAllyAction && a.ChampionId > 0).Select(a => a.ChampionId).ToList();
        var theirBans = actions.Where(a => a.Type == ChampSelectAction.Ban && a.Completed && !a.IsAllyAction && a.ChampionId > 0).Select(a => a.ChampionId).ToList();
        if (session.Bans is { } bans)
        {
            myBans.AddRange(bans.MyTeamBans.Where(id => id > 0 && !myBans.Contains(id)));
            theirBans.AddRange(bans.TheirTeamBans.Where(id => id > 0 && !theirBans.Contains(id)));
        }

        var canLock = myTurnAction is not null
            && (myTurnAction.ChampionId > 0
                || (myTurnAction.Type == ChampSelectAction.Pick ? plan?.Pick : plan?.Ban)?.ChampionId is not null);

        return new ChampSelectView
        {
            Stage = stage,
            StageLabel = label,
            RemainingMs = remaining,
            TotalMs = timer.IsInfinite ? null : timer.TotalTimeInPhase,
            AsOf = nowMs,
            Queue = queue,
            Role = ChampSelectPlanner.RoleName(ChampSelectPlanner.RoleFromPosition(session.LocalPlayer?.AssignedPosition)),
            MyTurn = myTurnAction?.Type,
            MyTeam = [.. session.MyTeam.OrderBy(p => p.CellId).Select(Player)],
            TheirTeam = [.. session.TheirTeam.OrderBy(p => p.CellId).Select(Player)],
            MyTeamBans = myBans,
            TheirTeamBans = theirBans,
            PlannedPick = plan?.Pick,
            PlannedBan = plan?.Ban,
            PickOverride = pickOverride,
            BanOverride = banOverride,
            MySpell1Id = session.LocalPlayer?.Spell1Id ?? 0,
            MySpell2Id = session.LocalPlayer?.Spell2Id ?? 0,
            CanLockPick = canLock && myTurnAction!.Type == ChampSelectAction.Pick,
            CanLockBan = canLock && myTurnAction!.Type == ChampSelectAction.Ban,
            Automated = automated,
            AutomationNote = automationNote,
        };
    }
}
