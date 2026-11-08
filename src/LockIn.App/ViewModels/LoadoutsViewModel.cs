using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LockIn.Core.Lcu;
using LockIn.Core.Loadouts;
using LockIn.Core.Settings;
using Microsoft.Extensions.Logging;

namespace LockIn.App.ViewModels;

/// <summary>A rune page choice. <see cref="Id"/> null with no name means "don't change".</summary>
public sealed record RunePageOption(long? Id, string Name, bool Missing = false)
{
    public static RunePageOption DontChange { get; } = new(null, "(don't change)");

    public string Display => Missing ? $"{Name} (not found)" : Name;
}

public sealed record SpellOption(int? Id, string Name)
{
    public static SpellOption DontChange { get; } = new(null, "(don't change)");

    public static IReadOnlyList<SpellOption> All { get; } =
        [DontChange, .. SummonerSpells.All.Select(s => new SpellOption(s.Id, s.Name))];

    public static SpellOption For(int? id) => All.FirstOrDefault(o => o.Id == id) ?? DontChange;
}

/// <summary>One champion's rune page and spells.</summary>
public sealed partial class LoadoutRowViewModel : ObservableObject
{
    private readonly Action<LoadoutRowViewModel> _save;
    private readonly bool _loading;

    public LoadoutRowViewModel(ChampionItemViewModel champion, ChampionLoadout? loadout, RunePageOption page, Action<LoadoutRowViewModel> save)
    {
        Champion = champion;
        _save = save;
        _loading = true;
        RunePage = page;
        Spell1 = SpellOption.For(loadout?.Spell1Id);
        Spell2 = SpellOption.For(loadout?.Spell2Id);
        _loading = false;
    }

    public ChampionItemViewModel Champion { get; }

    public override string ToString() => Champion.Name;

    [ObservableProperty]
    public partial RunePageOption? RunePage { get; set; }

    [ObservableProperty]
    public partial SpellOption Spell1 { get; set; }

    [ObservableProperty]
    public partial SpellOption Spell2 { get; set; }

    partial void OnRunePageChanged(RunePageOption? value) => Save();

    partial void OnSpell1Changed(SpellOption value) => Save();

    partial void OnSpell2Changed(SpellOption value) => Save();

    private void Save()
    {
        if (!_loading)
        {
            _save(this);
        }
    }

    public ChampionLoadout ToLoadout() => new()
    {
        RunePageId = RunePage?.Id,
        RunePageName = RunePage?.Id is null ? null : RunePage.Name,
        Spell1Id = Spell1?.Id,
        Spell2Id = Spell2?.Id,
    };
}

/// <summary>The Runes &amp; spells tab.</summary>
public sealed partial class LoadoutsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly ChampionDirectory _champions;
    private readonly LcuConnection _connection;
    private readonly ILogger<LoadoutsViewModel> _logger;
    private readonly HashSet<int> _extraChampions = [];

    public LoadoutsViewModel(SettingsStore settings, ChampionDirectory champions, LcuConnection connection, ILogger<LoadoutsViewModel> logger)
    {
        _settings = settings;
        _champions = champions;
        _connection = connection;
        _logger = logger;

        RunePages.Add(RunePageOption.DontChange);
        foreach (var loadout in settings.Snapshot().Loadouts.Champions.Values.Where(l => l.RunePageId is not null))
        {
            AddPageOption(new RunePageOption(loadout.RunePageId, loadout.RunePageName ?? $"Page {loadout.RunePageId}", Missing: true));
        }

        RebuildRows();
        settings.Changed += (_, _) => Ui.Post(RebuildRows);
        champions.Rebuilt += (_, _) => RebuildRows();
        connection.StateChanged += (_, _) => Ui.Post(OnConnectionChanged);
        OnConnectionChanged();
    }

    public ObservableCollection<LoadoutRowViewModel> Rows { get; } = [];

    /// <summary>"(don't change)", the client's pages, and any saved page the client doesn't have (marked missing).</summary>
    public ObservableCollection<RunePageOption> RunePages { get; } = [];

    public IReadOnlyList<SpellOption> Spells => SpellOption.All;

    public ObservableCollection<ChampionItemViewModel> AllChampions => _champions.All;

    [ObservableProperty]
    public partial bool IsClientConnected { get; private set; }

    [ObservableProperty]
    public partial string RunePageStatus { get; private set; } = "";

    [ObservableProperty]
    public partial ChampionItemViewModel? ChampionToAdd { get; set; }

    [RelayCommand]
    private async Task RefreshRunePagesAsync()
    {
        if (_connection.Client is not { } client)
        {
            return;
        }

        try
        {
            var pages = await client.GetAsync<List<RunePage>>(LoadoutService.PagesPath).ConfigureAwait(true) ?? [];
            ApplyClientPages(pages);
            RunePageStatus = $"{pages.Count} rune pages loaded from the client";
        }
        catch (LcuRequestException ex)
        {
            RunePageStatus = "Couldn't load rune pages from the client";
            _logger.LogWarning("Couldn't load rune pages: {Message}", ex.Message);
        }
    }

    [RelayCommand]
    private void AddChampion()
    {
        if (ChampionToAdd is { } champion)
        {
            _extraChampions.Add(champion.Id);
            RebuildRows();
            ChampionToAdd = null;
        }
    }

    [RelayCommand]
    private void ClearRow(LoadoutRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _extraChampions.Remove(row.Champion.Id);
        _settings.Update(s => s.Loadouts.Champions.Remove(row.Champion.Id));
        Rows.Remove(row);
        RebuildRows();
    }

    private void OnConnectionChanged()
    {
        IsClientConnected = _connection.State == LcuConnectionState.Connected;
        if (IsClientConnected)
        {
            _ = RefreshRunePagesAsync();
        }
        else
        {
            RunePageStatus = "Start the League client to choose from your saved rune pages.";
        }
    }

    private void ApplyClientPages(List<RunePage> pages)
    {
        var selected = Rows.ToDictionary(r => r.Champion.Id, r => r.RunePage);
        RunePages.Clear();
        RunePages.Add(RunePageOption.DontChange);
        foreach (var page in pages)
        {
            RunePages.Add(new RunePageOption(page.Id, page.Name));
        }

        // Keep saved choices visible (and re-match pages whose ID changed but name didn't).
        foreach (var row in Rows)
        {
            row.RunePage = ResolvePage(selected.GetValueOrDefault(row.Champion.Id));
        }
    }

    private RunePageOption ResolvePage(RunePageOption? saved)
    {
        if (saved?.Id is null)
        {
            return RunePageOption.DontChange;
        }

        var match = RunePages.FirstOrDefault(p => p.Id == saved.Id && !p.Missing)
            ?? RunePages.FirstOrDefault(p => !p.Missing && p.Id is not null && string.Equals(p.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
        return match ?? AddPageOption(saved with { Missing = true });
    }

    private RunePageOption AddPageOption(RunePageOption option)
    {
        var existing = RunePages.FirstOrDefault(p => p.Id == option.Id && p.Name == option.Name);
        if (existing is not null)
        {
            return existing;
        }

        RunePages.Add(option);
        return option;
    }

    /// <summary>Rows: every champion on a pick list (in list order), then others with a saved loadout or added here.</summary>
    private void RebuildRows()
    {
        var s = _settings.Snapshot();
        var ids = new List<int>();
        foreach (var role in new[] { Role.Top, Role.Jungle, Role.Middle, Role.Bottom, Role.Support, Role.Any })
        {
            if (s.Picks.Lists.TryGetValue(role, out var list))
            {
                ids.AddRange(list);
            }
        }

        ids.AddRange(s.Loadouts.Champions.Keys.Concat(_extraChampions)
            .Select(_champions.Get).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => c.Id));
        ids = [.. ids.Distinct()];

        if (ids.SequenceEqual(Rows.Select(r => r.Champion.Id)))
        {
            return;
        }

        var existing = Rows.ToDictionary(r => r.Champion.Id);
        Rows.Clear();
        foreach (var id in ids)
        {
            if (!existing.TryGetValue(id, out var row))
            {
                var loadout = s.Loadouts.Champions.GetValueOrDefault(id);
                var page = loadout?.RunePageId is { } pageId
                    ? ResolvePage(new RunePageOption(pageId, loadout.RunePageName ?? $"Page {pageId}"))
                    : RunePageOption.DontChange;
                row = new LoadoutRowViewModel(_champions.Get(id), loadout, page, Save);
            }

            Rows.Add(row);
        }
    }

    private void Save(LoadoutRowViewModel row)
    {
        var loadout = row.ToLoadout();
        _settings.Update(s =>
        {
            if (loadout.IsEmpty)
            {
                s.Loadouts.Champions.Remove(row.Champion.Id);
            }
            else
            {
                s.Loadouts.Champions[row.Champion.Id] = loadout;
            }
        });
    }
}
