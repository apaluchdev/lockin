using LockIn.Core.Champions;
using LockIn.Core.ChampSelect;
using LockIn.Core.Hosting;
using LockIn.Core.Lcu;
using LockIn.Core.Loadouts;
using LockIn.Core.Settings;
using LockIn.Core.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Tests.Loadouts;

public sealed class LoadoutServiceTests : IDisposable
{
    private const int Ahri = 103, Ignite = 14, Flash = 4;
    private readonly string _cache = Directory.CreateTempSubdirectory("lockin-loadout-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private async Task<(TestHost Host, LoadoutService Service)> StartAsync(Action<AppSettings> configure)
    {
        var host = await TestHost.StartAsync(configure, services =>
        {
            services.AddSingleton(sp => new ChampionCatalog(sp.GetRequiredService<LcuConnection>(),
                sp.GetRequiredService<ILogger<ChampionCatalog>>(), new NoNetworkHandler(), _cache));
            services.AddHostedSingleton<ChampSelectService>();
            services.AddHostedSingleton<LoadoutService>();
        });
        host.Client.SetGet("/" + LoadoutService.PagesPath, new object[]
        {
            new { id = 1, name = "Default", isActive = true, isEditable = true },
            new { id = 42, name = "Ahri mid", isActive = false, isEditable = true },
        });
        return (host, host.Host.Services.GetRequiredService<LoadoutService>());
    }

    private const string RunesPath = "/lol-perks/v1/currentpage";
    private const string SpellsPath = "/lol-champ-select/v1/session/my-selection";

    private static object Session(bool locked) => new
    {
        gameId = 1,
        localPlayerCellId = 0,
        myTeam = new[] { new { cellId = 0, assignedPosition = "middle", championId = locked ? Ahri : 0 } },
        actions = new[] { new[] { new { id = 5, actorCellId = 0, type = "pick", isAllyAction = true, isInProgress = !locked, completed = locked, championId = locked ? Ahri : 0 } } },
        timer = new { phase = "BAN_PICK", adjustedTimeLeftInPhase = 20_000, totalTimeInPhase = 30_000, internalNowInEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
    };

    private static async Task EnterChampSelectAsync(TestHost host)
    {
        host.Client.SetGet(LcuEndpoints.GameflowSession, new { gameData = new { queue = new { id = 400, gameMode = "CLASSIC" } } });
        host.Client.SetGet("/" + LoadoutService.PagesPath, new object[]
        {
            new { id = 1, name = "Default", isActive = true, isEditable = true },
            new { id = 42, name = "Ahri mid", isActive = false, isEditable = true },
            new { id = 43, name = "Poke", isActive = false, isEditable = true },
        });
        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(locked: false));
        var champSelect = host.Host.Services.GetRequiredService<ChampSelectService>();
        await host.WaitUntilAsync(() => champSelect.View is not null, "see champ select");
    }

    [Fact]
    public async Task A_rune_override_applies_now_and_beats_the_saved_page_at_lock_in()
    {
        var (host, service) = await StartAsync(s =>
            s.Loadouts.Champions[Ahri] = new ChampionLoadout { RunePageId = 42, RunePageName = "Ahri mid" });
        await using var _ = host;
        await EnterChampSelectAsync(host);

        Assert.Null(await service.SetRunePageOverrideAsync(43));
        Assert.Equal("43", Assert.Single(host.Client.RequestsTo("PUT", RunesPath)).Body);

        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(locked: true));

        await host.WaitUntilAsync(() => host.Client.RequestsTo("PUT", RunesPath).Count() >= 2, "re-apply at lock-in");
        Assert.All(host.Client.RequestsTo("PUT", RunesPath), r => Assert.Equal("43", r.Body));
    }

    [Fact]
    public async Task Spell_overrides_stay_on_the_chosen_keys_despite_the_flash_preference()
    {
        var (host, service) = await StartAsync(s =>
        {
            s.Loadouts.FlashKey = FlashKey.F;
            s.Loadouts.Champions[Ahri] = new ChampionLoadout { Spell1Id = 12, Spell2Id = Flash };
        });
        await using var _ = host;
        await EnterChampSelectAsync(host);

        Assert.Null(await service.SetSpellsOverrideAsync(Flash, Ignite));
        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(locked: true));

        await host.WaitUntilAsync(() => host.Client.RequestsTo("PATCH", SpellsPath).Count() >= 2, "re-apply at lock-in");
        Assert.All(host.Client.RequestsTo("PATCH", SpellsPath), r =>
            Assert.Equal((Flash, Ignite), (r.Json.GetProperty("spell1Id").GetInt32(), r.Json.GetProperty("spell2Id").GetInt32())));
    }

    [Fact]
    public async Task Clearing_an_override_after_lock_in_restores_the_saved_setup()
    {
        var (host, service) = await StartAsync(s =>
            s.Loadouts.Champions[Ahri] = new ChampionLoadout { RunePageId = 42, RunePageName = "Ahri mid" });
        await using var _ = host;
        await EnterChampSelectAsync(host);
        Assert.Null(await service.SetRunePageOverrideAsync(43));
        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(locked: true));
        await host.WaitUntilAsync(() => host.Client.RequestsTo("PUT", RunesPath).Count() >= 2, "re-apply at lock-in");

        Assert.Null(await service.SetRunePageOverrideAsync(null));

        Assert.Equal("42", host.Client.RequestsTo("PUT", RunesPath).Last().Body);
    }

    [Fact]
    public async Task Overrides_end_with_the_champ_select()
    {
        var (host, service) = await StartAsync(s =>
            s.Loadouts.Champions[Ahri] = new ChampionLoadout { RunePageId = 42, RunePageName = "Ahri mid" });
        await using var _ = host;
        await EnterChampSelectAsync(host);
        Assert.Null(await service.SetRunePageOverrideAsync(43));
        var champSelect = host.Host.Services.GetRequiredService<ChampSelectService>();

        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, null, "Delete");
        await host.WaitUntilAsync(() => champSelect.View is null, "leave champ select");
        await service.ApplyAsync(Ahri, host.Settings.Snapshot());

        Assert.Equal("42", host.Client.RequestsTo("PUT", RunesPath).Last().Body);
    }

    [Theory]
    [InlineData(4, 4, "Choose two different spells")]
    [InlineData(4, null, "Choose a spell for both D and F")]
    [InlineData(4, 999, "Unknown summoner spell")]
    public async Task Rejects_invalid_spell_choices(int? d, int? f, string error)
    {
        var (host, service) = await StartAsync(_ => { });
        await using var __ = host;
        await EnterChampSelectAsync(host);

        Assert.Equal(error, await service.SetSpellsOverrideAsync(d, f));
        Assert.Empty(host.Client.RequestsTo("PATCH", SpellsPath));
    }

    [Fact]
    public async Task Refuses_overrides_outside_champ_select()
    {
        var (host, service) = await StartAsync(_ => { });
        await using var __ = host;

        Assert.Equal("You're not in champ select", await service.SetRunePageOverrideAsync(42));
        Assert.Equal("You're not in champ select", await service.SetSpellsOverrideAsync(Flash, Ignite));
    }

    [Fact]
    public async Task Applies_spells_with_the_flash_preference_and_the_saved_rune_page()
    {
        var (host, service) = await StartAsync(s =>
        {
            s.Loadouts.FlashKey = FlashKey.F;
            s.Loadouts.Champions[Ahri] = new ChampionLoadout { RunePageId = 42, RunePageName = "Ahri mid", Spell1Id = Flash, Spell2Id = Ignite };
        });
        await using var _ = host;

        await service.ApplyAsync(Ahri, host.Settings.Snapshot());

        var spells = Assert.Single(host.Client.RequestsTo("PATCH", "/lol-champ-select/v1/session/my-selection"));
        Assert.Equal(Ignite, spells.Json.GetProperty("spell1Id").GetInt32());
        Assert.Equal(Flash, spells.Json.GetProperty("spell2Id").GetInt32());
        var runes = Assert.Single(host.Client.RequestsTo("PUT", "/lol-perks/v1/currentpage"));
        Assert.Equal("42", runes.Body);
    }

    [Fact]
    public async Task Finds_a_rune_page_by_name_when_its_id_changed()
    {
        var (host, service) = await StartAsync(s =>
            s.Loadouts.Champions[Ahri] = new ChampionLoadout { RunePageId = 999, RunePageName = "ahri MID" });
        await using var _ = host;

        await service.ApplyAsync(Ahri, host.Settings.Snapshot());

        Assert.Equal("42", Assert.Single(host.Client.RequestsTo("PUT", "/lol-perks/v1/currentpage")).Body);
        Assert.Empty(host.Client.RequestsTo("PATCH", "/lol-champ-select/v1/session/my-selection"));
    }

    [Fact]
    public async Task Warns_when_the_saved_page_is_gone()
    {
        var (host, service) = await StartAsync(s =>
            s.Loadouts.Champions[Ahri] = new ChampionLoadout { RunePageId = 7, RunePageName = "Deleted page" });
        await using var _ = host;

        await service.ApplyAsync(Ahri, host.Settings.Snapshot());

        Assert.Empty(host.Client.RequestsTo("PUT", "/lol-perks/v1/currentpage"));
        Assert.Contains(host.Logs.Messages, m => m.Contains("no longer exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Is_triggered_by_a_lock_in()
    {
        var (host, _) = await StartAsync(s =>
        {
            s.Picks.Lists[Role.Middle] = [Ahri];
            s.Loadouts.Champions[Ahri] = new ChampionLoadout { RunePageId = 42 };
        });
        await using var __ = host;
        host.Client.SetGet(LcuEndpoints.GameflowSession, new { gameData = new { queue = new { id = 400, gameMode = "CLASSIC" } } });
        static object Session(bool locked) => new
        {
            gameId = 1,
            localPlayerCellId = 0,
            myTeam = new[] { new { cellId = 0, assignedPosition = "middle", championId = locked ? Ahri : 0 } },
            actions = new[] { new[] { new { id = 5, actorCellId = 0, type = "pick", isAllyAction = true, isInProgress = !locked, completed = locked, championId = locked ? Ahri : 0 } } },
            timer = new { phase = "BAN_PICK", adjustedTimeLeftInPhase = 20_000, totalTimeInPhase = 30_000, internalNowInEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        };

        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(locked: false));
        await host.Client.PushAsync(LcuEndpoints.ChampSelectSession, Session(locked: true));

        await host.WaitUntilAsync(() => host.Client.RequestsTo("PUT", "/lol-perks/v1/currentpage").Any(), "switch rune page after the lock-in");
    }

    [Theory]
    [InlineData(FlashKey.D, Ignite, Flash, Flash, Ignite)]
    [InlineData(FlashKey.F, Flash, Ignite, Ignite, Flash)]
    [InlineData(FlashKey.AsConfigured, Ignite, Flash, Ignite, Flash)]
    [InlineData(FlashKey.D, Ignite, 12, Ignite, 12)]
    public void Arranges_flash_on_the_preferred_key(FlashKey key, int d, int f, int expectedD, int expectedF) =>
        Assert.Equal((expectedD, expectedF), SummonerSpells.Arrange(d, f, key));
}
