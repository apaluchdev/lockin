using LockIn.Core.Champions;
using LockIn.Core.ChampSelect;
using LockIn.Core.Lcu;
using LockIn.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LockIn.Core.Loadouts;

public sealed record SummonerSpell(int Id, string Name);

/// <summary>Summoner's Rift summoner spells. IDs are stable across patches.</summary>
public static class SummonerSpells
{
    public const int Flash = 4;

    public static IReadOnlyList<SummonerSpell> All { get; } =
    [
        new(1, "Cleanse"),
        new(3, "Exhaust"),
        new(Flash, "Flash"),
        new(6, "Ghost"),
        new(7, "Heal"),
        new(11, "Smite"),
        new(12, "Teleport"),
        new(14, "Ignite"),
        new(21, "Barrier"),
    ];

    public static string NameOf(int id) => All.FirstOrDefault(s => s.Id == id)?.Name ?? $"spell #{id}";

    /// <summary>Applies the Flash-key preference: swaps the two spells if Flash is on the other key.</summary>
    public static (int? D, int? F) Arrange(int? d, int? f, FlashKey flashKey) => flashKey switch
    {
        FlashKey.D when f == Flash => (Flash, d),
        FlashKey.F when d == Flash => (f, Flash),
        _ => (d, f),
    };
}

/// <summary>A saved rune page in the client (<c>/lol-perks/v1/pages</c>).</summary>
public sealed record RunePage(long Id, string Name, bool IsActive, bool IsEditable);

/// <summary>Applies the champion's saved rune page and summoner spells once it's locked in.</summary>
public sealed class LoadoutService(
    LcuConnection connection,
    ChampSelectService champSelect,
    ChampionCatalog catalog,
    SettingsStore settings,
    ILogger<LoadoutService> logger) : IHostedService
{
    public const string PagesPath = "lol-perks/v1/pages";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        champSelect.ChampionLocked += OnChampionLocked;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        champSelect.ChampionLocked -= OnChampionLocked;
        return Task.CompletedTask;
    }

    private void OnChampionLocked(object? sender, ChampionLockedEventArgs e)
    {
        var s = settings.Snapshot();
        if (!e.IsAutomatedMode || !s.AutomationEnabled || !s.Loadouts.Enabled)
        {
            return;
        }

        _ = ApplyAsync(e.ChampionId, s);
    }

    /// <summary>Applies the saved loadout for a champion. Returns when done (or failed, which is logged).</summary>
    public async Task ApplyAsync(int championId, AppSettings s)
    {
        if (connection.Client is not { } client)
        {
            return;
        }

        var name = catalog.NameOf(championId);
        if (!s.Loadouts.Champions.TryGetValue(championId, out var loadout) || loadout.IsEmpty)
        {
            logger.LogInformation("No rune page or spells saved for {Champion} — leaving them as they are", name);
            return;
        }

        await ApplySpellsAsync(client, loadout, s.Loadouts.FlashKey).ConfigureAwait(false);
        await ApplyRunesAsync(client, loadout, name).ConfigureAwait(false);
    }

    private async Task ApplySpellsAsync(LcuClient client, ChampionLoadout loadout, FlashKey flashKey)
    {
        var (d, f) = SummonerSpells.Arrange(loadout.Spell1Id, loadout.Spell2Id, flashKey);
        if (d is null && f is null)
        {
            return;
        }

        var body = new Dictionary<string, int>();
        if (d is { } spell1)
        {
            body["spell1Id"] = spell1;
        }

        if (f is { } spell2)
        {
            body["spell2Id"] = spell2;
        }

        try
        {
            await client.PatchAsync("lol-champ-select/v1/session/my-selection", body).ConfigureAwait(false);
            logger.LogInformation("Summoner spells set: {D} on D, {F} on F",
                d is { } a ? SummonerSpells.NameOf(a) : "unchanged",
                f is { } b ? SummonerSpells.NameOf(b) : "unchanged");
        }
        catch (LcuRequestException ex)
        {
            logger.LogWarning("Couldn't set summoner spells: {Message}", ex.Message);
        }
    }

    private async Task ApplyRunesAsync(LcuClient client, ChampionLoadout loadout, string championName)
    {
        if (loadout.RunePageId is null && string.IsNullOrEmpty(loadout.RunePageName))
        {
            return;
        }

        try
        {
            var pages = await client.GetAsync<List<RunePage>>(PagesPath).ConfigureAwait(false) ?? [];
            var page = pages.FirstOrDefault(p => p.Id == loadout.RunePageId)
                ?? pages.FirstOrDefault(p => string.Equals(p.Name, loadout.RunePageName, StringComparison.OrdinalIgnoreCase));
            if (page is null)
            {
                logger.LogWarning("The rune page \"{Page}\" saved for {Champion} no longer exists — pick a new one on the Runes & spells tab",
                    loadout.RunePageName, championName);
                return;
            }

            if (page.IsActive)
            {
                logger.LogInformation("Rune page \"{Page}\" is already active", page.Name);
                return;
            }

            await client.PutAsync("lol-perks/v1/currentpage", page.Id).ConfigureAwait(false);
            logger.LogInformation("Switched to rune page \"{Page}\"", page.Name);
        }
        catch (LcuRequestException ex)
        {
            logger.LogWarning("Couldn't switch rune pages: {Message}", ex.Message);
        }
    }
}
