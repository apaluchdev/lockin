using LockIn.Core.Champions;
using LockIn.Core.ChampSelect;
using LockIn.Core.Hosting;
using LockIn.Core.Lcu;
using LockIn.Core.Settings;
using LockIn.Core.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Tests.ChampSelect;

/// <summary>End-to-end: the real connection + service against a fake League client.</summary>
public sealed class ChampSelectServiceTests : IDisposable
{
    private const int Ahri = 103, Orianna = 61, Yasuo = 157, Me = 2;
    private readonly string _cache = Directory.CreateTempSubdirectory("lockin-cs-").FullName;

    public ChampSelectServiceTests() =>
        File.WriteAllText(Path.Combine(_cache, "champions.json"),
            """{"version":"1","champions":[{"id":103,"key":"Ahri","name":"Ahri"},{"id":61,"key":"Orianna","name":"Orianna"},{"id":157,"key":"Yasuo","name":"Yasuo"}]}""");

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private async Task<TestHost> StartAsync(Action<AppSettings>? configure = null, string gameMode = "CLASSIC")
    {
        var host = await TestHost.StartAsync(
            s =>
            {
                s.Picks.Lists[Role.Middle] = [Ahri, Orianna];
                s.Bans.Lists[Role.Any] = [Yasuo];
                configure?.Invoke(s);
            },
            services =>
            {
                services.AddSingleton(sp => new ChampionCatalog(sp.GetRequiredService<LcuConnection>(),
                    sp.GetRequiredService<ILogger<ChampionCatalog>>(), new NoNetwork(), _cache));
                services.AddHostedSingleton<ChampSelectService>();
            });
        await host.Host.Services.GetRequiredService<ChampionCatalog>().LoadAsync();
        host.Client.SetGet(LcuEndpoints.GameflowSession, new
        {
            gameData = new { isCustomGame = false, queue = new { id = 400, gameMode, description = "Normal Draft" } },
        });
        host.Client.SetGet("/lol-champ-select/v1/pickable-champion-ids", new[] { Ahri, Orianna });
        host.Client.SetGet("/lol-champ-select/v1/bannable-champion-ids", new[] { Ahri, Orianna, Yasuo });
        return host;
    }

    private static object Session(long leftMs, object[] actions, string phase = "BAN_PICK", long gameId = 1) => new
    {
        gameId,
        localPlayerCellId = Me,
        myTeam = new[] { new { cellId = Me, assignedPosition = "middle", championId = 0 } },
        theirTeam = Array.Empty<object>(),
        actions = new[] { actions },
        timer = new
        {
            phase,
            adjustedTimeLeftInPhase = leftMs,
            totalTimeInPhase = 30_000,
            internalNowInEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            isInfinite = false,
        },
    };

    private static object PickAction(int championId = 0, bool completed = false, bool inProgress = true) => new
    {
        id = 10, actorCellId = Me, type = "pick", isAllyAction = true, isInProgress = inProgress, completed, championId,
    };

    [Theory]
    [InlineData("CLASSIC", false, false, true)]
    [InlineData("ARAM", false, true, false)]
    [InlineData("CLASSIC", true, false, false)]
    [InlineData("CLASSIC", true, true, true)]
    [InlineData("PRACTICETOOL", true, false, false)]
    [InlineData("PRACTICETOOL", true, true, true)]
    [InlineData("ARAM", true, true, false)]
    public void Decides_which_games_to_automate(string gameMode, bool isCustom, bool allowCustom, bool expected) =>
        Assert.Equal(expected, ChampSelectService.ShouldAutomate(gameMode, isCustom, allowCustom));

    [Fact]
    public async Task Automates_practice_tool_when_custom_games_are_enabled()
    {
        await using var host = await StartAsync(s => s.General.AutomateCustomGames = true);
        host.Client.SetGet(LcuEndpoints.GameflowSession, new { gameData = new { isCustomGame = true, queue = new { id = 0, gameMode = "PRACTICETOOL" } } });

        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(1_000, [PickAction()]));

        await host.WaitUntilAsync(
            () => host.Client.RequestsTo("PATCH", "/lol-champ-select/v1/session/actions/10").Any(r => r.Body.Contains("\"completed\":true", StringComparison.Ordinal)),
            "lock in during Practice Tool");
    }

    [Fact]
    public async Task Says_where_the_custom_games_setting_is()
    {
        await using var host = await StartAsync();
        host.Client.SetGet(LcuEndpoints.GameflowSession, new { gameData = new { isCustomGame = true, queue = new { id = 0, gameMode = "PRACTICETOOL" } } });

        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(1_000, [PickAction()]));

        await host.WaitUntilAsync(() => host.Logs.Messages.Any(m => m.Contains("Settings tab in LockIn on your PC", StringComparison.Ordinal)), "name the setting");
        Assert.Empty(host.Client.RequestsTo("PATCH", "/lol-champ-select"));
    }

    [Fact]
    public async Task Hovers_then_locks_when_the_stale_timer_runs_down_with_no_client_updates()
    {
        await using var host = await StartAsync(s => s.Picks.LockBeforeEndSeconds = 3);

        // 3.6 s left, then the client goes quiet: no more session updates at all.
        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(3_600, [PickAction()]));

        await host.WaitUntilAsync(() => host.Client.RequestsTo("PATCH", "/lol-champ-select/v1/session/actions/10").Any(), "hover Ahri");
        var hover = host.Client.RequestsTo("PATCH", "/lol-champ-select/v1/session/actions/10").First();
        Assert.Equal(Ahri, hover.Json.GetProperty("championId").GetInt32());
        Assert.False(hover.Json.TryGetProperty("completed", out _));

        await host.WaitUntilAsync(
            () => host.Client.RequestsTo("PATCH", "/lol-champ-select/v1/session/actions/10").Any(r => r.Body.Contains("\"completed\":true", StringComparison.Ordinal)),
            "lock in after the timer runs down", timeoutMs: 3000);
        Assert.Contains(host.Logs.Messages, m => m.Contains("Locking in Ahri", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bans_and_announces_the_confirmed_ban()
    {
        await using var host = await StartAsync(s => s.Bans.HoverDelaySeconds = 0);
        static object Ban(bool completed, int champion) => new
        {
            id = 1, actorCellId = Me, type = "ban", isAllyAction = true, isInProgress = !completed, completed, championId = champion,
        };

        // When LockIn locks the ban, the fake client confirms it like the real one would.
        host.Client.OnRequest = async request =>
        {
            if (request.Path.EndsWith("/actions/1", StringComparison.Ordinal) && request.Body.Contains("\"completed\":true", StringComparison.Ordinal))
            {
                await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(1_000, [Ban(true, Yasuo), PickAction(inProgress: false)]));
            }
        };

        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(2_000, [Ban(false, 0), PickAction(inProgress: false)]));

        await host.WaitUntilAsync(() => host.Logs.Messages.Any(m => m.Contains("✓ Banned Yasuo", StringComparison.Ordinal)), "confirm the ban");
    }

    [Fact]
    public async Task Leaves_aram_alone()
    {
        await using var host = await StartAsync(gameMode: "ARAM");

        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(1_000, [PickAction()]));

        await host.WaitUntilAsync(() => host.Logs.Messages.Any(m => m.Contains("in ARAM they're up to you", StringComparison.Ordinal)), "skip ARAM");
        await Task.Delay(600);
        Assert.Empty(host.Client.RequestsTo("PATCH", "/lol-champ-select"));
    }

    [Fact]
    public async Task Does_nothing_when_automation_is_off()
    {
        await using var host = await StartAsync(s => s.AutomationEnabled = false);

        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(1_000, [PickAction()]));

        await host.WaitUntilAsync(() => host.Logs.Messages.Any(m => m.Contains("automation is switched off", StringComparison.Ordinal)), "log that it's off");
        await Task.Delay(600);
        Assert.Empty(host.Client.RequestsTo("PATCH", "/lol-champ-select"));
    }

    [Fact]
    public async Task Picks_up_a_champ_select_already_in_progress_when_connecting()
    {
        await using var host = await StartAsync(s => s.Picks.LockBeforeEndSeconds = 29);
        host.Client.SetGet(LcuEndpoints.ChampSelectSession, Session(25_000, [PickAction()]));

        await host.Client.PushAsync(LcuEndpoints.GameflowPhase, "ChampSelect");

        await host.WaitUntilAsync(
            () => host.Client.RequestsTo("PATCH", "/lol-champ-select/v1/session/actions/10").Any(r => r.Body.Contains("\"completed\":true", StringComparison.Ordinal)),
            "lock in from a fetched session");
    }

    [Fact]
    public async Task Respects_a_manual_lock_and_reports_it()
    {
        await using var host = await StartAsync(s => s.Picks.HoverDelaySeconds = 20);
        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(25_000, [PickAction()]));
        await host.WaitUntilAsync(() => host.Logs.Messages.Any(m => m.Contains("hovering Ahri after 20 s", StringComparison.Ordinal)), "wait for hover delay");

        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(20_000, [PickAction(Orianna, completed: true, inProgress: false)]));

        await host.WaitUntilAsync(() => host.Logs.Messages.Any(m => m.Contains("You locked in", StringComparison.Ordinal)), "report the manual lock");
        Assert.Empty(host.Client.RequestsTo("PATCH", "/lol-champ-select"));
    }

    [Fact]
    public async Task A_pick_override_during_planning_redeclares_the_intent()
    {
        await using var host = await StartAsync();
        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(25_000, [PickAction(inProgress: false)], phase: "PLANNING"));
        await host.WaitUntilAsync(() => host.Client.RequestsTo("PATCH", "/lol-champ-select/v1/session/actions/10").Any(), "declare Ahri as intent");

        var service = host.Host.Services.GetRequiredService<ChampSelectService>();
        Assert.Null(await service.SetOverrideAsync(ChampSelectService.Pick, Orianna));

        await host.WaitUntilAsync(
            () => host.Client.RequestsTo("PATCH", "/lol-champ-select/v1/session/actions/10").Any(r => r.Json.GetProperty("championId").GetInt32() == Orianna),
            "re-declare Orianna as intent");
        Assert.Equal(ChoiceSource.Override, service.View!.PlannedPick!.Source);
    }

    [Fact]
    public async Task Lock_now_is_refused_outside_our_turn()
    {
        await using var host = await StartAsync();
        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(25_000, [PickAction(inProgress: false)], phase: "PLANNING"));
        var service = host.Host.Services.GetRequiredService<ChampSelectService>();
        await host.WaitUntilAsync(() => service.View is not null, "see champ select");

        Assert.Equal("It's not your pick turn", await service.LockNowAsync(ChampSelectService.Pick));
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network in tests");
    }
}
