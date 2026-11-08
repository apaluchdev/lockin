using LockIn.Core.ChampSelect;
using LockIn.Core.Settings;
using static LockIn.Core.ChampSelect.ChampSelectCommandKind;

namespace LockIn.Core.Tests.ChampSelect;

public class ChampSelectPlannerTests
{
    // Champion IDs
    private const int Ahri = 103, Orianna = 61, Syndra = 134, Lux = 99, Thresh = 412, Braum = 201, Yasuo = 157, Zed = 238, Yone = 777;

    private const long Now = 1_800_000_000_000;
    private const int Me = 2;

    private static readonly Dictionary<int, string> Names = new()
    {
        [Ahri] = "Ahri", [Orianna] = "Orianna", [Syndra] = "Syndra", [Lux] = "Lux", [Thresh] = "Thresh",
        [Braum] = "Braum", [Yasuo] = "Yasuo", [Zed] = "Zed", [Yone] = "Yone",
    };

    private static ChampSelectOptions Options(
        Dictionary<Role, List<int>>? picks = null,
        Dictionary<Role, List<int>>? bans = null,
        double pickLock = 3, double pickHover = 0, double banLock = 3, double banHover = 2) => new()
    {
        PickLists = picks ?? new() { [Role.Middle] = [Ahri, Orianna, Syndra], [Role.Any] = [Lux] },
        BanLists = bans ?? new() { [Role.Any] = [Yasuo, Zed, Yone] },
        PickLockBeforeEndSeconds = pickLock,
        PickHoverDelaySeconds = pickHover,
        BanLockBeforeEndSeconds = banLock,
        BanHoverDelaySeconds = banHover,
    };

    /// <summary>A session where it's our turn, the timer snapshot taken <paramref name="snapshotAgeMs"/> ago.</summary>
    private static ChampSelectSession Session(
        string phase = ChampSelectTimer.BanPick,
        long leftMs = 25_000, long totalMs = 30_000, long snapshotAgeMs = 0, bool infinite = false,
        string position = "middle",
        ChampSelectAction[]? actions = null,
        ChampSelectPlayer[]? allies = null,
        ChampSelectPlayer[]? enemies = null) => new()
    {
        LocalPlayerCellId = Me,
        Timer = new ChampSelectTimer
        {
            Phase = phase,
            AdjustedTimeLeftInPhase = leftMs,
            TotalTimeInPhase = totalMs,
            InternalNowInEpochMs = Now - snapshotAgeMs,
            IsInfinite = infinite,
        },
        MyTeam = [new ChampSelectPlayer { CellId = Me, AssignedPosition = position }, .. allies ?? []],
        TheirTeam = [.. enemies ?? []],
        Actions = [[.. actions ?? []]],
    };

    private static ChampSelectAction MyPick(bool inProgress = true, int champion = 0, int id = 10) =>
        new() { Id = id, ActorCellId = Me, Type = ChampSelectAction.Pick, IsInProgress = inProgress, IsAllyAction = true, ChampionId = champion };

    private static ChampSelectAction MyBan(int champion = 0, int id = 1) =>
        new() { Id = id, ActorCellId = Me, Type = ChampSelectAction.Ban, IsInProgress = true, IsAllyAction = true, ChampionId = champion };

    private static ChampSelectAction Banned(int champion, int id) =>
        new() { Id = id, ActorCellId = 7, Type = ChampSelectAction.Ban, Completed = true, ChampionId = champion };

    private static ChampSelectPlan Plan(ChampSelectSession session, ChampSelectOptions? options = null, ChampSelectMemory? memory = null,
        IReadOnlySet<int>? playable = null) =>
        ChampSelectPlanner.Plan(session, options ?? Options(), memory ?? new ChampSelectMemory(), Now, playable, null, id => Names[id]);

    // ── Picking ─────────────────────────────────────────────────────────

    [Fact]
    public void Hovers_the_top_pick_for_the_assigned_role()
    {
        var plan = Plan(Session(actions: [MyPick()]));

        var command = Assert.Single(plan.Commands);
        Assert.Equal((Hover, 10, Ahri), (command.Kind, command.ActionId, command.ChampionId));
        Assert.Contains(plan.Notes, n => n.Message == "Pick: Ahri (#1 on your Mid pick list)");
    }

    [Fact]
    public void Skips_banned_unowned_and_teammate_champions_and_explains_why()
    {
        var session = Session(
            actions: [MyPick(), Banned(Ahri, 50)],
            allies: [new ChampSelectPlayer { CellId = 0, ChampionPickIntent = Orianna }]);

        var plan = Plan(session, playable: new HashSet<int> { Ahri, Orianna, Lux });

        Assert.Equal(Lux, Assert.Single(plan.Commands).ChampionId);
        var note = plan.Notes.Single(n => n.Topic == "pick-choice").Message;
        Assert.Equal("Pick: Lux (#1 on your Any role pick list) — skipped Ahri (banned), Orianna (a teammate wants it), Syndra (you don't own it)", note);
    }

    [Fact]
    public void Skips_champions_the_enemy_locked()
    {
        var plan = Plan(Session(actions: [MyPick()], enemies: [new ChampSelectPlayer { CellId = 7, ChampionId = Ahri }]));

        Assert.Equal(Orianna, Assert.Single(plan.Commands).ChampionId);
    }

    [Fact]
    public void Does_not_fall_back_to_any_role_when_disabled()
    {
        var options = Options() with { FallBackToAnyRole = false };
        var plan = Plan(Session(actions: [MyPick()]), options, playable: new HashSet<int> { Lux });

        Assert.Empty(plan.Commands);
        Assert.Contains(plan.Notes, n => n.Message.StartsWith("Nothing on your pick lists is available", StringComparison.Ordinal));
    }

    [Fact]
    public void Uses_the_any_role_list_in_blind_pick()
    {
        var plan = Plan(Session(position: "", actions: [MyPick()]));

        Assert.Equal(Lux, Assert.Single(plan.Commands).ChampionId);
        Assert.Contains(plan.Notes, n => n.Message == "No assigned role — using your Any-role lists");
    }

    [Fact]
    public void Waits_for_the_hover_delay()
    {
        // 25 s left of 30 → 5 s elapsed; hover delay 8 s.
        var plan = Plan(Session(actions: [MyPick()]), Options(pickHover: 8));

        Assert.Empty(plan.Commands);
        Assert.Contains(plan.Notes, n => n.Message == "Your pick turn — hovering Ahri after 8 s");
    }

    [Fact]
    public void Does_not_re_send_a_hover_that_is_still_pending()
    {
        var memory = new ChampSelectMemory();
        memory.HoveredByUs[10] = Ahri;

        var plan = Plan(Session(actions: [MyPick(champion: 0)]), memory: memory);

        Assert.Empty(plan.Commands);
    }

    [Fact]
    public void Locks_when_the_timer_reaches_the_threshold()
    {
        var memory = new ChampSelectMemory();
        memory.HoveredByUs[10] = Ahri;

        var plan = Plan(Session(leftMs: 2_900, actions: [MyPick(champion: Ahri)]), memory: memory);

        var command = Assert.Single(plan.Commands);
        Assert.Equal((ChampSelectCommandKind.Lock, Ahri), (command.Kind, command.ChampionId));
        Assert.Equal("Locking in Ahri (2.9 s left)", command.Description);
    }

    /// <summary>
    /// The bug that cost a dodge: last pick, nobody else acts, so the client's timer snapshot never
    /// refreshes and still says 25 s left. LockIn must age the snapshot with its own clock.
    /// </summary>
    [Fact]
    public void Locks_on_time_even_when_the_client_timer_snapshot_is_stale()
    {
        var memory = new ChampSelectMemory();
        memory.HoveredByUs[10] = Thresh;
        var options = Options(picks: new() { [Role.Support] = [Thresh] });

        // Snapshot said 25 s left; 22.5 s have passed since → 2.5 s really left.
        var session = Session(position: "utility", leftMs: 25_000, snapshotAgeMs: 22_500, actions: [MyPick(champion: Thresh)]);
        var plan = Plan(session, options, memory);

        var command = Assert.Single(plan.Commands);
        Assert.Equal((ChampSelectCommandKind.Lock, Thresh), (command.Kind, command.ChampionId));

        // …and not too early: 10 s after the snapshot there are still 15 s left.
        var early = Plan(session with { Timer = session.Timer with { InternalNowInEpochMs = Now - 10_000 } }, options, memory);
        Assert.Empty(early.Commands);
    }

    [Fact]
    public void Respects_a_manual_hover_and_still_locks_it_in_time()
    {
        var memory = new ChampSelectMemory();
        memory.HoveredByUs[10] = Ahri;

        var waiting = Plan(Session(actions: [MyPick(champion: Syndra)]), memory: memory);
        Assert.Empty(waiting.Commands);
        Assert.Contains(waiting.Notes, n => n.Message == "You hovered Syndra yourself — LockIn will pick it with 3 s left");

        var due = Plan(Session(leftMs: 1_000, actions: [MyPick(champion: Syndra)]), memory: memory);
        Assert.Equal((ChampSelectCommandKind.Lock, Syndra), (Assert.Single(due.Commands).Kind, due.Commands[0].ChampionId));
    }

    [Fact]
    public void Re_hovers_when_our_intent_got_banned()
    {
        var memory = new ChampSelectMemory { IntentDeclared = true };
        memory.HoveredByUs[10] = Ahri;

        // Our declared intent (Ahri) is still on the action but was banned in the ban phase.
        var plan = Plan(Session(actions: [MyPick(champion: Ahri), Banned(Ahri, 50)]), memory: memory);

        var command = Assert.Single(plan.Commands);
        Assert.Equal((Hover, Orianna), (command.Kind, command.ChampionId));
    }

    [Fact]
    public void Locks_right_after_the_hover_delay_when_the_timer_is_infinite()
    {
        var memory = new ChampSelectMemory();
        memory.FirstSeenInProgress[10] = Now - 3_000;

        var plan = Plan(Session(infinite: true, actions: [MyPick()]), Options(pickHover: 2), memory);

        Assert.Equal(ChampSelectCommandKind.Lock, Assert.Single(plan.Commands).Kind);
    }

    [Fact]
    public void Does_nothing_after_the_lock_was_sent()
    {
        var memory = new ChampSelectMemory();
        memory.LockSent.Add(10);

        Assert.Empty(Plan(Session(leftMs: 500, actions: [MyPick(champion: Ahri)]), memory: memory).Commands);
    }

    // ── Planning ────────────────────────────────────────────────────────

    [Fact]
    public void Declares_intent_during_planning()
    {
        var plan = Plan(Session(phase: ChampSelectTimer.Planning, actions: [MyPick(inProgress: false)]));

        var command = Assert.Single(plan.Commands);
        Assert.Equal((DeclareIntent, Ahri), (command.Kind, command.ChampionId));
    }

    [Fact]
    public void Keeps_an_intent_the_player_declared_themselves()
    {
        var plan = Plan(Session(phase: ChampSelectTimer.Planning, actions: [MyPick(inProgress: false, champion: Syndra)]));

        Assert.Empty(plan.Commands);
        Assert.Contains(plan.Notes, n => n.Message == "You declared Syndra yourself — keeping it");
    }

    [Fact]
    public void Declares_intent_only_once()
    {
        var plan = Plan(Session(phase: ChampSelectTimer.Planning, actions: [MyPick(inProgress: false)]),
            memory: new ChampSelectMemory { IntentDeclared = true });

        Assert.Empty(plan.Commands);
    }

    // ── Banning ─────────────────────────────────────────────────────────

    [Fact]
    public void Hovers_the_top_ban_after_the_delay_and_locks_at_the_threshold()
    {
        // 25 s left of 30 → 5 s elapsed > 2 s ban hover delay.
        var hover = Plan(Session(actions: [MyBan(), MyPick(inProgress: false)]));
        Assert.Equal((Hover, 1, Yasuo), (hover.Commands[0].Kind, hover.Commands[0].ActionId, hover.Commands[0].ChampionId));

        var memory = new ChampSelectMemory();
        memory.HoveredByUs[1] = Yasuo;
        var ban = Plan(Session(leftMs: 2_000, actions: [MyBan(champion: Yasuo), MyPick(inProgress: false)]), memory: memory);
        Assert.Equal((ChampSelectCommandKind.Lock, Yasuo), (ban.Commands[0].Kind, ban.Commands[0].ChampionId));
        Assert.Equal("Banning Yasuo (2 s left)", ban.Commands[0].Description);
    }

    [Fact]
    public void Never_bans_our_own_pick_or_what_teammates_are_banning()
    {
        var bans = new Dictionary<Role, List<int>> { [Role.Any] = [Ahri, Yasuo, Zed, Yone] };
        var allyBan = new ChampSelectAction { Id = 3, ActorCellId = 0, Type = ChampSelectAction.Ban, IsInProgress = true, IsAllyAction = true, ChampionId = Yasuo };
        var session = Session(
            actions: [MyBan(), allyBan, MyPick(inProgress: false)],
            allies: [new ChampSelectPlayer { CellId = 1, ChampionPickIntent = Zed }]);

        var plan = Plan(session, Options(bans: bans));

        Assert.Equal(Yone, plan.Commands.Single(c => c.ActionId == 1).ChampionId);
        Assert.Contains(plan.Notes, n => n.Message ==
            "Ban: Yone (#4 on your Any role ban list) — skipped Ahri (it's your own pick), Yasuo (a teammate is banning it), Zed (a teammate wants it)");
    }

    [Fact]
    public void Prefers_the_role_ban_list_over_the_default()
    {
        var bans = new Dictionary<Role, List<int>> { [Role.Any] = [Yasuo], [Role.Middle] = [Zed] };

        var plan = Plan(Session(actions: [MyBan()]), Options(bans: bans));

        Assert.Equal(Zed, Assert.Single(plan.Commands).ChampionId);
    }

    [Fact]
    public void Leaves_the_ban_alone_when_bans_are_off()
    {
        var plan = Plan(Session(actions: [MyBan()]), Options() with { BansEnabled = false });

        Assert.Empty(plan.Commands);
        Assert.Contains(plan.Notes, n => n.Message == "Your ban turn — ban automation is off");
    }

    [Fact]
    public void Explains_an_empty_list()
    {
        var plan = Plan(Session(actions: [MyBan()]), Options(bans: []));

        Assert.Empty(plan.Commands);
        Assert.Contains(plan.Notes, n => n.Message == "Your Any role ban list is empty");
    }

    // ── Phone overrides and lock-now ─────────────────────────────────────

    [Fact]
    public void A_pick_override_wins_over_the_list()
    {
        var plan = Plan(Session(actions: [MyPick()]), Options() with { PickOverride = Syndra });

        Assert.Equal(Syndra, Assert.Single(plan.Commands).ChampionId);
        Assert.Equal(new PlannedChoice(Syndra, ChoiceSource.Override, "Pick: Syndra (your override from the phone)"), plan.Pick);
    }

    [Fact]
    public void An_unavailable_override_falls_back_to_the_list_and_says_why()
    {
        var plan = Plan(Session(actions: [MyPick(), Banned(Syndra, 50)]), Options() with { PickOverride = Syndra });

        Assert.Equal(Ahri, Assert.Single(plan.Commands).ChampionId);
        Assert.Equal(ChoiceSource.List, plan.Pick!.Source);
        Assert.Equal("Your override Syndra can't be used (banned) — pick: Ahri (#1 on your Mid pick list)", plan.Pick.Explanation);
    }

    [Fact]
    public void A_ban_override_is_planned_before_the_ban_turn()
    {
        var upcomingBan = MyBan() with { IsInProgress = false };
        var plan = Plan(Session(phase: ChampSelectTimer.Planning, actions: [upcomingBan, MyPick(inProgress: false)]), Options() with { BanOverride = Zed });

        Assert.Equal(new PlannedChoice(Zed, ChoiceSource.Override, "Ban: Zed (your override from the phone)"), plan.Ban);
        Assert.DoesNotContain(plan.Commands, c => c.ActionId == 1);
    }

    [Fact]
    public void Lock_now_locks_immediately()
    {
        var memory = new ChampSelectMemory();
        memory.HoveredByUs[10] = Ahri;
        memory.LockNowRequested.Add("pick");

        var plan = Plan(Session(leftMs: 25_000, actions: [MyPick(champion: Ahri)]), memory: memory);

        var command = Assert.Single(plan.Commands);
        Assert.Equal((ChampSelectCommandKind.Lock, Ahri), (command.Kind, command.ChampionId));
        Assert.Equal("Locking in Ahri now (requested from your phone)", command.Description);
    }

    [Fact]
    public void Reports_a_manual_hover_as_the_planned_pick()
    {
        var plan = Plan(Session(actions: [MyPick(champion: Syndra)]));

        Assert.Equal(ChoiceSource.Manual, plan.Pick!.Source);
        Assert.Equal(Syndra, plan.Pick.ChampionId);
    }

    [Theory]
    [InlineData("top", Role.Top)]
    [InlineData("jungle", Role.Jungle)]
    [InlineData("middle", Role.Middle)]
    [InlineData("bottom", Role.Bottom)]
    [InlineData("utility", Role.Support)]
    [InlineData("", Role.Any)]
    [InlineData(null, Role.Any)]
    public void Maps_client_positions_to_roles(string? position, Role role) =>
        Assert.Equal(role, ChampSelectPlanner.RoleFromPosition(position));
}
