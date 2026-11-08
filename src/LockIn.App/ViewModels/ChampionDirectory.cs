using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LockIn.Core.Champions;

namespace LockIn.App.ViewModels;

/// <summary>A champion as shown in the UI. One shared instance per champion.</summary>
public sealed partial class ChampionItemViewModel(int id, string name) : ObservableObject
{
    public int Id { get; } = id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials))]
    public partial string Name { get; set; } = name;

    /// <summary>Local path to the icon, or null while it's loading / unavailable.</summary>
    [ObservableProperty]
    public partial string? IconPath { get; set; }

    /// <summary>False when the client is connected and says the player can't play this champion.</summary>
    [ObservableProperty]
    public partial bool IsPlayable { get; set; } = true;

    public string Initials => Name.Length >= 2 ? Name[..2] : Name;

    public override string ToString() => Name;
}

/// <summary>UI-thread mirror of <see cref="ChampionCatalog"/>: shared champion items with icons and ownership.</summary>
public sealed class ChampionDirectory
{
    private readonly ChampionCatalog _catalog;
    private readonly Dictionary<int, ChampionItemViewModel> _byId = [];

    public ChampionDirectory(ChampionCatalog catalog)
    {
        _catalog = catalog;
        catalog.ChampionsChanged += (_, _) => Ui.Post(Rebuild);
        catalog.PlayableChanged += (_, _) => Ui.Post(UpdatePlayable);
        Rebuild();
    }

    /// <summary>All champions, sorted by name.</summary>
    public ObservableCollection<ChampionItemViewModel> All { get; } = [];

    /// <summary>Raised on the UI thread after the champion list changed.</summary>
    public event EventHandler? Rebuilt;

    public ChampionItemViewModel Get(int id)
    {
        if (!_byId.TryGetValue(id, out var item))
        {
            item = new ChampionItemViewModel(id, _catalog.Find(id)?.Name ?? $"Champion #{id}");
            _byId[id] = item;
        }

        return item;
    }

    private void Rebuild()
    {
        var champions = _catalog.All;
        All.Clear();
        foreach (var champion in champions)
        {
            var item = Get(champion.Id);
            item.Name = champion.Name;
            All.Add(item);
            if (item.IconPath is null)
            {
                _ = LoadIconAsync(champion, item);
            }
        }

        UpdatePlayable();
        Rebuilt?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePlayable()
    {
        var playable = _catalog.Playable;
        foreach (var item in _byId.Values)
        {
            item.IsPlayable = playable is null || playable.Contains(item.Id);
        }
    }

    private async Task LoadIconAsync(Champion champion, ChampionItemViewModel item)
    {
        var path = await _catalog.GetIconPathAsync(champion).ConfigureAwait(false);
        if (path is not null)
        {
            Ui.Post(() => item.IconPath = path);
        }
    }
}
