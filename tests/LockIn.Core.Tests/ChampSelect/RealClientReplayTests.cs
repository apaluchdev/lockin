using System.Text.Json;
using LockIn.Core.ChampSelect;
using LockIn.Core.Lcu;
using LockIn.Core.Settings;

namespace LockIn.Core.Tests.ChampSelect;

/// <summary>
/// Snapshots captured from the real League client (a ranked draft, playing support from cell 8), trimmed to the
/// fields LockIn reads. They show things a hand-written fake didn't: the ban action is "in progress" already
/// during planning, ban action IDs start at 0, and skipped bans are reported with champion ID -1.
/// </summary>
public class RealClientReplayTests
{
    private const int Yasuo = 157, Zed = 238, Malphite = 54, MyBanAction = 8;

    private static readonly Dictionary<int, string> Names = new() { [Yasuo] = "Yasuo", [Zed] = "Zed", [Malphite] = "Malphite" };

    private static ChampSelectSession Load(string name) =>
        JsonSerializer.Deserialize<ChampSelectSession>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{name}.json")), LcuClient.JsonOptions)!;

    private static ChampSelectOptions Options(params int[] bans) => new()
    {
        BanLists = new Dictionary<Role, List<int>> { [Role.Any] = [.. bans] },
        PickLists = new Dictionary<Role, List<int>>(),
        BanHoverDelaySeconds = 2,
        BanLockBeforeEndSeconds = 3,
    };

    private static ChampSelectPlan Plan(ChampSelectSession session, ChampSelectOptions options, long msIntoSnapshot, ChampSelectMemory? memory = null) =>
        ChampSelectPlanner.Plan(session, options, memory ?? new ChampSelectMemory(),
            session.Timer.InternalNowInEpochMs + msIntoSnapshot, null, id => Names.GetValueOrDefault(id, $"#{id}"));

    [Fact]
    public void The_fixtures_look_like_what_we_expect_from_the_real_client()
    {
        var session = Load("ranked-planning");

        Assert.Equal("PLANNING", session.Timer.Phase);
        Assert.Equal("utility", session.LocalPlayer!.AssignedPosition);
        var myBan = session.AllActions.Single(a => a.ActorCellId == session.LocalPlayerCellId && a.Type == ChampSelectAction.Ban);
        Assert.True(myBan.IsInProgress); // The client already says "in progress" during planning.
        Assert.Equal(MyBanAction, myBan.Id);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(6_000)]
    [InlineData(10_500)] // 1.2 s left: a ban would have been locked here
    public void Planning_shows_the_planned_ban_but_does_not_act_on_it(long msIntoPlanning)
    {
        var plan = Plan(Load("ranked-planning"), Options(Yasuo, Zed), msIntoPlanning);

        Assert.Equal(Yasuo, plan.Ban!.ChampionId);
        Assert.DoesNotContain(plan.Commands, c => c.ActionId == MyBanAction);
        Assert.Contains(plan.Notes, n => n.Message == "Planning: will ban Yasuo when the ban phase starts");
    }

    [Fact]
    public void Waits_out_the_hover_delay_in_the_ban_phase_then_hovers_the_first_ban()
    {
        var session = Load("ranked-ban-phase");

        Assert.Empty(Plan(session, Options(Yasuo, Zed), 500).Commands);

        var hover = Assert.Single(Plan(session, Options(Yasuo, Zed), 3_000).Commands);
        Assert.Equal((ChampSelectCommandKind.Hover, MyBanAction, Yasuo), (hover.Kind, hover.ActionId, hover.ChampionId));
    }

    [Fact]
    public void Locks_the_ban_with_three_seconds_left_even_though_the_client_timer_is_stale()
    {
        var session = Load("ranked-ban-phase"); // Snapshot said 25 s left.
        var memory = new ChampSelectMemory();
        memory.HoveredByUs[MyBanAction] = Yasuo;

        Assert.Empty(Plan(session, Options(Yasuo, Zed), 15_000, memory).Commands);
        var command = Assert.Single(Plan(session, Options(Yasuo, Zed), 22_500, memory).Commands);

        Assert.Equal((ChampSelectCommandKind.Lock, MyBanAction, Yasuo), (command.Kind, command.ActionId, command.ChampionId));
    }

    [Fact]
    public void Skips_champions_other_players_already_banned()
    {
        var session = Load("ranked-ban-phase-late"); // Malphite was banned by then.

        var plan = Plan(session, Options(Malphite, Yasuo), 1_000);

        Assert.Equal(Yasuo, plan.Ban!.ChampionId);
        Assert.Contains("Malphite (already banned)", plan.Ban.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Moves_on_to_the_next_ban_once_the_client_has_refused_one()
    {
        var memory = new ChampSelectMemory();
        memory.RefusedBans.Add(Yasuo);

        var plan = Plan(Load("ranked-ban-phase"), Options(Yasuo, Zed), 3_000, memory);

        Assert.Equal(Zed, Assert.Single(plan.Commands).ChampionId);
        Assert.Contains("Yasuo (the League client refused it)", plan.Ban!.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_phone_override_for_the_ban_is_used()
    {
        var options = Options(Yasuo) with { BanOverride = Zed };

        var plan = Plan(Load("ranked-ban-phase"), options, 3_000);

        Assert.Equal(ChoiceSource.Override, plan.Ban!.Source);
        Assert.Equal(Zed, Assert.Single(plan.Commands).ChampionId);
    }
}
