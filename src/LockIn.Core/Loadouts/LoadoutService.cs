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

/// <summary>The rune page and spells this champ select will use, for the phone page.</summary>
public sealed record LoadoutView
{
    /// <summary>The page being used; null when LockIn is leaving the current page alone.</summary>
    public string? RunePage { get; init; }

    public bool RuneOverride { get; init; }

    public int? Spell1Id { get; init; }

    public string? Spell1 { get; init; }

    public int? Spell2Id { get; init; }

    public string? Spell2 { get; init; }

    public bool SpellOverride { get; init; }
}

/// <summary>
/// Applies the champion's saved rune page and summoner spells once it's locked in, and holds rune/spell
/// overrides set from the phone. Overrides apply immediately and last for the current champ select only.
/// </summary>
public sealed class LoadoutService(
    LcuConnection connection,
    ChampSelectService champSelect,
    ChampionCatalog catalog,
    SettingsStore settings,
    ILogger<LoadoutService> logger) : IHostedService
{
    public const string PagesPath = "lol-perks/v1/pages";
    private const string NotInChampSelect = "You're not in champ select";
    private const string NoClient = "The League client isn't connected";

    private readonly Lock _gate = new();
    private RunePage? _runeOverride;
    private (int D, int F)? _spellOverride;
    private (int ChampionId, bool Automated)? _locked;

    /// <summary>Raised (on any thread) when an override is set or cleared.</summary>
    public event EventHandler? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        champSelect.ChampionLocked += OnChampionLocked;
        champSelect.ViewChanged += OnViewChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        champSelect.ChampionLocked -= OnChampionLocked;
        champSelect.ViewChanged -= OnViewChanged;
        return Task.CompletedTask;
    }

    /// <summary>Overrides last for one champ select.</summary>
    private void OnViewChanged(object? sender, EventArgs e)
    {
        if (champSelect.View is not null)
        {
            return;
        }

        bool cleared;
        lock (_gate)
        {
            cleared = _runeOverride is not null || _spellOverride is not null || _locked is not null;
            _runeOverride = null;
            _spellOverride = null;
            _locked = null;
        }

        if (cleared)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnChampionLocked(object? sender, ChampionLockedEventArgs e)
    {
        lock (_gate)
        {
            _locked = (e.ChampionId, e.IsAutomatedMode);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        var s = settings.Snapshot();
        if (ShouldApply(e.IsAutomatedMode, s))
        {
            _ = ApplyAsync(e.ChampionId, s);
        }
    }

    private static bool ShouldApply(bool automatedMode, AppSettings s) =>
        automatedMode && s.AutomationEnabled && s.Loadouts.Enabled;

    /// <summary>The client's saved rune pages, or null when they can't be read.</summary>
    public async Task<IReadOnlyList<RunePage>?> GetRunePagesAsync()
    {
        if (connection.Client is not { } client)
        {
            return null;
        }

        try
        {
            return await client.GetAsync<List<RunePage>>(PagesPath).ConfigureAwait(false);
        }
        catch (LcuRequestException ex)
        {
            logger.LogWarning("Couldn't read rune pages: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Switches rune page for this champ select only (null clears the override). Returns an error, or null on success.</summary>
    public async Task<string?> SetRunePageOverrideAsync(long? pageId)
    {
        if (champSelect.View is null)
        {
            return NotInChampSelect;
        }

        if (connection.Client is not { } client)
        {
            return NoClient;
        }

        if (pageId is null)
        {
            lock (_gate)
            {
                _runeOverride = null;
            }

            logger.LogInformation("Phone: cleared the rune page override");
            Changed?.Invoke(this, EventArgs.Empty);
            await ReapplyIfLockedAsync().ConfigureAwait(false);
            return null;
        }

        try
        {
            var pages = await client.GetAsync<List<RunePage>>(PagesPath).ConfigureAwait(false) ?? [];
            if (pages.FirstOrDefault(p => p.Id == pageId) is not { } page)
            {
                return "That rune page no longer exists";
            }

            if (!page.IsActive)
            {
                await client.PutAsync("lol-perks/v1/currentpage", page.Id).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _runeOverride = page;
            }

            logger.LogInformation("Phone: rune page \"{Page}\" this game", page.Name);
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (LcuRequestException ex)
        {
            logger.LogWarning("Couldn't switch rune pages: {Message}", ex.Message);
            return "The client refused the rune page change";
        }
    }

    /// <summary>
    /// Sets the summoner spells for this champ select only, exactly on the keys given (both null clears the
    /// override). Returns an error, or null on success.
    /// </summary>
    public async Task<string?> SetSpellsOverrideAsync(int? spell1Id, int? spell2Id)
    {
        if (champSelect.View is null)
        {
            return NotInChampSelect;
        }

        if (connection.Client is not { } client)
        {
            return NoClient;
        }

        if (spell1Id is null && spell2Id is null)
        {
            lock (_gate)
            {
                _spellOverride = null;
            }

            logger.LogInformation("Phone: cleared the summoner spell override");
            Changed?.Invoke(this, EventArgs.Empty);
            await ReapplyIfLockedAsync().ConfigureAwait(false);
            return null;
        }

        if (spell1Id is not { } d || spell2Id is not { } f)
        {
            return "Choose a spell for both D and F";
        }

        if (d == f)
        {
            return "Choose two different spells";
        }

        if (SummonerSpells.All.All(s => s.Id != d) || SummonerSpells.All.All(s => s.Id != f))
        {
            return "Unknown summoner spell";
        }

        try
        {
            await client.PatchAsync("lol-champ-select/v1/session/my-selection", new { spell1Id = d, spell2Id = f }).ConfigureAwait(false);
        }
        catch (LcuRequestException ex)
        {
            logger.LogWarning("Couldn't set summoner spells: {Message}", ex.Message);
            return "The client refused those summoner spells";
        }

        lock (_gate)
        {
            _spellOverride = (d, f);
        }

        logger.LogInformation("Phone: {D} on D, {F} on F this game", SummonerSpells.NameOf(d), SummonerSpells.NameOf(f));
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>After clearing an override post lock-in, go back to the champion's saved setup.</summary>
    private async Task ReapplyIfLockedAsync()
    {
        (int ChampionId, bool Automated)? locked;
        lock (_gate)
        {
            locked = _locked;
        }

        var s = settings.Snapshot();
        if (locked is { } l && ShouldApply(l.Automated, s))
        {
            await ApplyAsync(l.ChampionId, s).ConfigureAwait(false);
        }
    }

    /// <summary>What this champ select will use, or null when not in one.</summary>
    public LoadoutView? ViewFor(ChampSelectView? view, AppSettings s)
    {
        if (view is null)
        {
            return null;
        }

        var me = view.MyTeam.FirstOrDefault(p => p.IsMe);
        var champion = me is { Status: "locked", ChampionId: > 0 }
            ? me.ChampionId
            : view.PlannedPick?.ChampionId ?? (me is { ChampionId: > 0 } ? me.ChampionId : null);
        var saved = view.Automated && s.Loadouts.Enabled && champion is { } id ? s.Loadouts.Champions.GetValueOrDefault(id) : null;
        var (savedD, savedF) = SummonerSpells.Arrange(saved?.Spell1Id, saved?.Spell2Id, s.Loadouts.FlashKey);

        RunePage? rune;
        (int D, int F)? spells;
        lock (_gate)
        {
            rune = _runeOverride;
            spells = _spellOverride;
        }

        var d = spells?.D ?? savedD ?? (view.MySpell1Id > 0 ? view.MySpell1Id : null);
        var f = spells?.F ?? savedF ?? (view.MySpell2Id > 0 ? view.MySpell2Id : null);
        return new LoadoutView
        {
            RunePage = rune?.Name ?? saved?.RunePageName,
            RuneOverride = rune is not null,
            Spell1Id = d,
            Spell1 = d is { } a ? SummonerSpells.NameOf(a) : null,
            Spell2Id = f,
            Spell2 = f is { } b ? SummonerSpells.NameOf(b) : null,
            SpellOverride = spells is not null,
        };
    }

    /// <summary>Applies the saved loadout for a champion, with any phone overrides on top. Returns when done (or failed, which is logged).</summary>
    public async Task ApplyAsync(int championId, AppSettings s)
    {
        if (connection.Client is not { } client)
        {
            return;
        }

        RunePage? runeOverride;
        (int D, int F)? spellOverride;
        lock (_gate)
        {
            runeOverride = _runeOverride;
            spellOverride = _spellOverride;
        }

        var name = catalog.NameOf(championId);
        var hasSaved = s.Loadouts.Champions.TryGetValue(championId, out var saved) && !saved!.IsEmpty;
        if (!hasSaved && runeOverride is null && spellOverride is null)
        {
            logger.LogInformation("No rune page or spells saved for {Champion} — leaving them as they are", name);
            return;
        }

        // Overridden spells stay exactly where the phone put them; only saved ones follow the Flash preference.
        var (d, f) = SummonerSpells.Arrange(saved?.Spell1Id, saved?.Spell2Id, s.Loadouts.FlashKey);
        await ApplySpellsAsync(client, spellOverride?.D ?? d, spellOverride?.F ?? f).ConfigureAwait(false);
        await ApplyRunesAsync(client, runeOverride?.Id ?? saved?.RunePageId, runeOverride?.Name ?? saved?.RunePageName, name).ConfigureAwait(false);
    }

    private async Task ApplySpellsAsync(LcuClient client, int? d, int? f)
    {
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

    private async Task ApplyRunesAsync(LcuClient client, long? pageId, string? pageName, string championName)
    {
        if (pageId is null && string.IsNullOrEmpty(pageName))
        {
            return;
        }

        try
        {
            var pages = await client.GetAsync<List<RunePage>>(PagesPath).ConfigureAwait(false) ?? [];
            var page = pages.FirstOrDefault(p => p.Id == pageId)
                ?? pages.FirstOrDefault(p => string.Equals(p.Name, pageName, StringComparison.OrdinalIgnoreCase));
            if (page is null)
            {
                logger.LogWarning("The rune page \"{Page}\" saved for {Champion} no longer exists — pick a new one on the Runes & spells tab",
                    pageName, championName);
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
