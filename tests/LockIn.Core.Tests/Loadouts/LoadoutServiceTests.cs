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
                sp.GetRequiredService<ILogger<ChampionCatalog>>(), new HttpClientHandler(), _cache));
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
