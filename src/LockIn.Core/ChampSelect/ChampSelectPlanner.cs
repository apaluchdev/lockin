using System.Globalization;
using System.Text;
using LockIn.Core.Settings;

namespace LockIn.Core.ChampSelect;

/// <summary>The settings the planner needs, in planner-friendly form.</summary>
public sealed record ChampSelectOptions
{
    public bool BansEnabled { get; init; } = true;

    public bool PicksEnabled { get; init; } = true;

    public bool PreHoverInPlanning { get; init; } = true;

    public bool FallBackToAnyRole { get; init; } = true;

    public double BanHoverDelaySeconds { get; init; }

    public double BanLockBeforeEndSeconds { get; init; } = 3;

    public double PickHoverDelaySeconds { get; init; }

    public double PickLockBeforeEndSeconds { get; init; } = 3;

    public IReadOnlyDictionary<Role, List<int>> PickLists { get; init; } = new Dictionary<Role, List<int>>();

    public IReadOnlyDictionary<Role, List<int>> BanLists { get; init; } = new Dictionary<Role, List<int>>();

    /// <summary>Pick set from the phone for this champ select only; wins over the lists when available.</summary>
    public int? PickOverride { get; init; }

    /// <summary>Ban set from the phone for this champ select only; wins over the lists when available.</summary>
    public int? BanOverride { get; init; }

    public static ChampSelectOptions From(AppSettings s) => new()
    {
        BansEnabled = s.Bans.Enabled,
        PicksEnabled = s.Picks.Enabled,
        PreHoverInPlanning = s.Picks.PreHoverInPlanning,
        FallBackToAnyRole = s.Picks.FallBackToAnyRole,
        BanHoverDelaySeconds = s.Bans.HoverDelaySeconds,
        BanLockBeforeEndSeconds = s.Bans.LockBeforeEndSeconds,
        PickHoverDelaySeconds = s.Picks.HoverDelaySeconds,
        PickLockBeforeEndSeconds = s.Picks.LockBeforeEndSeconds,
        PickLists = s.Picks.Lists,
        BanLists = s.Bans.Lists,
    };
}

/// <summary>What LockIn has already done in this champ select. Owned and updated by the automation service.</summary>
public sealed class ChampSelectMemory
{
    /// <summary>Champion we last hovered (or declared) per action ID.</summary>
    public Dictionary<int, int> HoveredByUs { get; } = [];

    /// <summary>Actions we've sent a lock-in for.</summary>
    public HashSet<int> LockSent { get; } = [];

    /// <summary>When we first saw each action in progress (Unix ms), for timers the client reports as infinite.</summary>
    public Dictionary<int, long> FirstSeenInProgress { get; } = [];

    public bool IntentDeclared { get; set; }

    /// <summary>"pick" and/or "ban": lock in immediately (requested from the phone).</summary>
    public HashSet<string> LockNowRequested { get; } = [];
}

public enum ChampSelectCommandKind
{
    /// <summary>Show a champion as pick intent during planning.</summary>
    DeclareIntent,

    /// <summary>Hover a champion for the action in progress.</summary>
    Hover,

    /// <summary>Lock in (complete) the action with a champion.</summary>
    Lock,
}

public sealed record ChampSelectCommand(ChampSelectCommandKind Kind, int ActionId, int ChampionId, string Description);

/// <summary>A human-readable decision. The service logs it when the message for its topic changes.</summary>
public sealed record DecisionNote(string Topic, string Message);

public sealed record ChampSelectPlan(IReadOnlyList<ChampSelectCommand> Commands, IReadOnlyList<DecisionNote> Notes)
{
    /// <summary>What LockIn intends to pick, and why (null when there's no pick to make).</summary>
    public PlannedChoice? Pick { get; init; }

    /// <summary>What LockIn intends to ban, and why (null when there's no ban to make).</summary>
    public PlannedChoice? Ban { get; init; }
}

public enum ChoiceSource
{
    /// <summary>From the priority lists.</summary>
    List,

    /// <summary>Overridden from the phone for this champ select.</summary>
    Override,

    /// <summary>Hovered by the player in the client.</summary>
    Manual,
}

/// <summary>A planned ban or pick. <see cref="ChampionId"/> is null when nothing suitable was found.</summary>
public sealed record PlannedChoice(int? ChampionId, ChoiceSource Source, string Explanation);

/// <summary>
/// Decides what to do in champ select. Pure: given the session, settings, memory and the current time it
/// returns commands plus explanations, and never talks to the client itself.
/// </summary>
public static class ChampSelectPlanner
{
    public static ChampSelectPlan Plan(
        ChampSelectSession session,
        ChampSelectOptions options,
        ChampSelectMemory memory,
        long nowMs,
        IReadOnlySet<int>? playable = null,
        IReadOnlySet<int>? bannable = null,
        Func<int, string>? nameOf = null)
    {
        var ctx = new Context(session, options, memory, nowMs, playable, bannable, nameOf ?? (id => $"champion #{id}"));
        var commands = new List<ChampSelectCommand>();
        var notes = new List<DecisionNote>();

        notes.Add(new("role", ctx.Role == Role.Any
            ? "No assigned role — using your Any-role lists"
            : $"Your role: {RoleName(ctx.Role)}"));

        var myPick = ctx.Actions.FirstOrDefault(a => a.ActorCellId == session.LocalPlayerCellId && a.Type == ChampSelectAction.Pick && !a.Completed);
        var myUpcomingBan = ctx.Actions.FirstOrDefault(a => a.ActorCellId == session.LocalPlayerCellId && a.Type == ChampSelectAction.Ban && !a.Completed);

        // What we'd pick right now. Also used to avoid banning our own pick.
        var pick = options.PicksEnabled && myPick is not null ? ctx.ChoosePick() : null;
        if (pick is not null)
        {
            notes.Add(new("pick-choice", pick.Explanation));
        }

        // What we'd ban (computed ahead of the ban turn too, so the phone can show it).
        var ban = options.BansEnabled && myUpcomingBan is not null ? ctx.ChooseBan(pick?.ChampionId) : null;
        var plannedPick = pick?.ToPlanned();
        var plannedBan = ban?.ToPlanned();

        if (myUpcomingBan is { IsInProgress: true } myBan)
        {
            plannedBan = PlanTurn(ctx, myBan, isBan: true, ban, commands, notes);
        }

        if (myPick is not null && options.PicksEnabled)
        {
            if (myPick.IsInProgress)
            {
                plannedPick = PlanTurn(ctx, myPick, isBan: false, pick, commands, notes);
            }
            else if (session.Timer.Phase == ChampSelectTimer.Planning && options.PreHoverInPlanning && !memory.IntentDeclared)
            {
                PlanIntent(ctx, myPick, pick?.ChampionId, commands, notes);
            }
        }
        else if (myPick is { IsInProgress: true })
        {
            notes.Add(new("pick-turn", "Your pick turn — pick automation is off"));
        }

        return new ChampSelectPlan(commands, notes) { Pick = plannedPick, Ban = plannedBan };
    }

    private static void PlanIntent(Context ctx, ChampSelectAction myPick, int? choice, List<ChampSelectCommand> commands, List<DecisionNote> notes)
    {
        var current = myPick.ChampionId;
        if (current != 0 && ctx.Memory.HoveredByUs.GetValueOrDefault(myPick.Id) != current)
        {
            notes.Add(new("intent", $"You declared {ctx.Name(current)} yourself — keeping it"));
            return;
        }

        if (choice is { } champion && current != champion)
        {
            commands.Add(new(ChampSelectCommandKind.DeclareIntent, myPick.Id, champion, $"Showing {ctx.Name(champion)} as your pick intent"));
        }
    }

    /// <summary>Plans our ban or pick turn. Returns what will be banned/picked, for display.</summary>
    private static PlannedChoice? PlanTurn(Context ctx, ChampSelectAction action, bool isBan, Choice? choice, List<ChampSelectCommand> commands, List<DecisionNote> notes)
    {
        var verb = isBan ? "ban" : "pick";
        var topic = $"{verb}-turn";
        if (isBan && !ctx.Options.BansEnabled)
        {
            notes.Add(new(topic, "Your ban turn — ban automation is off"));
            return null;
        }

        if (ctx.Memory.LockSent.Contains(action.Id))
        {
            return choice?.ToPlanned(); // Waiting for the client to confirm.
        }

        var hoverDelay = isBan ? ctx.Options.BanHoverDelaySeconds : ctx.Options.PickHoverDelaySeconds;
        var lockBefore = isBan ? ctx.Options.BanLockBeforeEndSeconds : ctx.Options.PickLockBeforeEndSeconds;
        var (remaining, elapsed) = ctx.Timing(action);

        var current = action.ChampionId;
        var requested = ctx.Memory.HoveredByUs.GetValueOrDefault(action.Id);
        var manual = current != 0 && current != requested;

        int? target;
        PlannedChoice? planned;
        if (manual)
        {
            target = current;
            planned = new PlannedChoice(current, ChoiceSource.Manual, $"You hovered {ctx.Name(current)} yourself");
            notes.Add(new(topic, $"You hovered {ctx.Name(current)} yourself — LockIn will {verb} it with {Seconds(lockBefore)} left"));
        }
        else
        {
            target = choice?.ChampionId;
            planned = choice?.ToPlanned();
            if (isBan && choice is not null)
            {
                notes.Add(new("ban-choice", choice.Explanation));
            }
        }

        if (target is not { } champion)
        {
            notes.Add(new(topic, isBan
                ? "Nothing on your ban lists can be banned — leaving the ban to you"
                : "Nothing on your pick lists is available — leaving the pick to you"));
            return planned;
        }

        // An infinite timer (custom games) has no deadline: lock as soon as the hover delay has passed.
        var lockNow = ctx.Memory.LockNowRequested.Contains(verb);
        var lockDue = lockNow || (remaining is { } left ? left <= lockBefore : elapsed >= hoverDelay);
        var hoverDue = elapsed >= hoverDelay;

        if (lockDue)
        {
            var timeLeft = lockNow ? " now (requested from your phone)" : remaining is { } r ? $" ({Seconds(r)} left)" : "";
            commands.Add(new(ChampSelectCommandKind.Lock, action.Id, champion,
                isBan ? $"Banning {ctx.Name(champion)}{timeLeft}" : $"Locking in {ctx.Name(champion)}{timeLeft}"));
        }
        else if (!manual && current != champion && requested != champion)
        {
            if (hoverDue)
            {
                var when = remaining is null ? "right away (no timer)" : $"with {Seconds(lockBefore)} left";
                commands.Add(new(ChampSelectCommandKind.Hover, action.Id, champion,
                    $"Hovering {ctx.Name(champion)} to {verb} — will lock in {when}"));
            }
            else
            {
                notes.Add(new(topic, $"Your {verb} turn — hovering {ctx.Name(champion)} after {Seconds(hoverDelay)}"));
            }
        }
        else if (!manual)
        {
            notes.Add(new(topic, $"{(isBan ? "Ban" : "Pick")}: {ctx.Name(champion)} hovered — locking in with {Seconds(lockBefore)} left"));
        }

        return planned;
    }

    internal static Role RoleFromPosition(string? position) => position?.ToLowerInvariant() switch
    {
        "top" => Role.Top,
        "jungle" => Role.Jungle,
        "middle" or "mid" => Role.Middle,
        "bottom" or "bot" => Role.Bottom,
        "utility" or "support" => Role.Support,
        _ => Role.Any,
    };

    public static string RoleName(Role role) => role switch
    {
        Role.Any => "Any role",
        Role.Middle => "Mid",
        Role.Bottom => "Bot",
        _ => role.ToString(),
    };

    private static string Seconds(double seconds) => seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";

    private sealed record Choice(int? ChampionId, string Explanation, ChoiceSource Source = ChoiceSource.List)
    {
        public PlannedChoice ToPlanned() => new(ChampionId, Source, Explanation);
    }

    private sealed class Context
    {
        public Context(ChampSelectSession session, ChampSelectOptions options, ChampSelectMemory memory, long nowMs,
            IReadOnlySet<int>? playable, IReadOnlySet<int>? bannable, Func<int, string> nameOf)
        {
            Session = session;
            Options = options;
            Memory = memory;
            NowMs = nowMs;
            Playable = playable;
            Bannable = bannable;
            Name = nameOf;
            Actions = [.. session.AllActions];
            Role = RoleFromPosition(session.LocalPlayer?.AssignedPosition);

            var me = session.LocalPlayerCellId;
            Banned = [.. Actions.Where(a => a.Type == ChampSelectAction.Ban && a.Completed && a.ChampionId > 0).Select(a => a.ChampionId)];
            if (session.Bans is { } bans)
            {
                Banned.UnionWith(bans.MyTeamBans.Where(id => id > 0));
                Banned.UnionWith(bans.TheirTeamBans.Where(id => id > 0));
            }

            Picked = [.. Actions.Where(a => a.Type == ChampSelectAction.Pick && a.Completed && a.ChampionId > 0).Select(a => a.ChampionId)];
            Picked.UnionWith(session.TheirTeam.Where(p => p.ChampionId > 0).Select(p => p.ChampionId));

            AllyWants = [.. session.MyTeam.Where(p => p.CellId != me)
                .SelectMany(p => new[] { p.ChampionId, p.ChampionPickIntent })
                .Where(id => id > 0)];
            AllyBanHovers = [.. Actions.Where(a => a.Type == ChampSelectAction.Ban && a.IsAllyAction && !a.Completed && a.ActorCellId != me && a.ChampionId > 0)
                .Select(a => a.ChampionId)];
        }

        public ChampSelectSession Session { get; }

        public ChampSelectOptions Options { get; }

        public ChampSelectMemory Memory { get; }

        public long NowMs { get; }

        public IReadOnlySet<int>? Playable { get; }

        public IReadOnlySet<int>? Bannable { get; }

        public Func<int, string> Name { get; }

        public List<ChampSelectAction> Actions { get; }

        public Role Role { get; }

        public HashSet<int> Banned { get; }

        public HashSet<int> Picked { get; }

        public HashSet<int> AllyWants { get; }

        public HashSet<int> AllyBanHovers { get; }

        /// <summary>
        /// Seconds left on the timer (null when infinite) and seconds since the turn started.
        /// The client's timer is a snapshot that goes stale when nothing changes, so we age it ourselves.
        /// </summary>
        public (double? Remaining, double Elapsed) Timing(ChampSelectAction action)
        {
            var timer = Session.Timer;
            if (timer.IsInfinite || timer.TotalTimeInPhase <= 0)
            {
                var firstSeen = Memory.FirstSeenInProgress.GetValueOrDefault(action.Id, NowMs);
                return (null, (NowMs - firstSeen) / 1000.0);
            }

            var age = timer.InternalNowInEpochMs > 0 ? Math.Max(0, NowMs - timer.InternalNowInEpochMs) : 0;
            var remainingMs = Math.Max(0, timer.AdjustedTimeLeftInPhase - age);
            var elapsedMs = Math.Max(0, timer.TotalTimeInPhase - remainingMs);
            return (remainingMs / 1000.0, elapsedMs / 1000.0);
        }

        public Choice ChoosePick()
        {
            var lists = new List<Role>();
            if (Role != Role.Any)
            {
                lists.Add(Role);
            }

            if (Role == Role.Any || Options.FallBackToAnyRole)
            {
                lists.Add(Role.Any);
            }

            string? WhyNot(int id) =>
                Banned.Contains(id) ? "banned"
                : Picked.Contains(id) ? "already picked"
                : AllyWants.Contains(id) ? "a teammate wants it"
                : Playable is not null && !Playable.Contains(id) ? "you don't own it"
                : null;

            return WithOverride(Options.PickOverride, "pick", WhyNot, () => Choose(lists, Options.PickLists, "pick", WhyNot));
        }

        public Choice ChooseBan(int? ownPick)
        {
            var list = Role != Role.Any && Options.BanLists.TryGetValue(Role, out var roleBans) && roleBans.Count > 0
                ? Role
                : Role.Any;

            string? WhyNot(int id) =>
                id == ownPick ? "it's your own pick"
                : Banned.Contains(id) ? "already banned"
                : AllyBanHovers.Contains(id) ? "a teammate is banning it"
                : AllyWants.Contains(id) ? "a teammate wants it"
                : Picked.Contains(id) ? "already picked"
                : Bannable is not null && !Bannable.Contains(id) ? "can't be banned"
                : null;

            return WithOverride(Options.BanOverride, "ban", WhyNot, () => Choose([list], Options.BanLists, "ban", WhyNot));
        }

        /// <summary>A phone override wins when it's available; otherwise say why and fall back to the lists.</summary>
        private Choice WithOverride(int? overrideId, string kind, Func<int, string?> whyNot, Func<Choice> fromLists)
        {
            if (overrideId is not { } id)
            {
                return fromLists();
            }

            if (whyNot(id) is not { } reason)
            {
                return new Choice(id, $"{Capitalize(kind)}: {Name(id)} (your override from the phone)", ChoiceSource.Override);
            }

            var fallback = fromLists();
            return fallback with { Explanation = $"Your override {Name(id)} can't be used ({reason}) — {char.ToLowerInvariant(fallback.Explanation[0])}{fallback.Explanation[1..]}" };
        }

        private Choice Choose(List<Role> roles, IReadOnlyDictionary<Role, List<int>> lists, string kind, Func<int, string?> whyNot)
        {
            var skipped = new List<string>();
            var listNames = new List<string>();
            foreach (var role in roles)
            {
                if (!lists.TryGetValue(role, out var list) || list.Count == 0)
                {
                    continue;
                }

                listNames.Add(RoleName(role));
                for (var i = 0; i < list.Count; i++)
                {
                    var id = list[i];
                    if (whyNot(id) is { } reason)
                    {
                        skipped.Add($"{Name(id)} ({reason})");
                        continue;
                    }

                    var text = new StringBuilder();
                    text.Append(CultureInfo.InvariantCulture, $"{Capitalize(kind)}: {Name(id)} (#{i + 1} on your {RoleName(role)} {kind} list)");
                    if (skipped.Count > 0)
                    {
                        text.Append(CultureInfo.InvariantCulture, $" — skipped {string.Join(", ", skipped)}");
                    }

                    return new Choice(id, text.ToString());
                }
            }

            if (listNames.Count == 0)
            {
                var names = string.Join(" / ", roles.Select(RoleName));
                return new Choice(null, $"Your {names} {kind} list is empty");
            }

            return new Choice(null, $"No {kind} available on your {string.Join(" / ", listNames)} list — skipped {string.Join(", ", skipped)}");
        }

        private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
    }
}
