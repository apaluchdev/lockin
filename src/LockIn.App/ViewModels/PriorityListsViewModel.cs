using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LockIn.Core.ChampSelect;
using LockIn.Core.Settings;

namespace LockIn.App.ViewModels;

public enum PriorityListKind
{
    Picks,
    Bans,
}

/// <summary>One champion's place in a priority list.</summary>
public sealed partial class RankedChampion(ChampionItemViewModel champion) : ObservableObject
{
    public ChampionItemViewModel Champion { get; } = champion;

    [ObservableProperty]
    public partial int Rank { get; set; }
}

/// <summary>The priority list for one role.</summary>
public sealed partial class RoleListViewModel(Role role, string title) : ObservableObject
{
    public Role Role { get; } = role;

    public string Title { get; } = title;

    public ObservableCollection<RankedChampion> Items { get; } = [];

    public int Count => Items.Count;

    internal void Renumber()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Rank = i + 1;
        }

        OnPropertyChanged(nameof(Count));
    }
}

/// <summary>The Picks or Bans tab: a priority-ordered champion list per role.</summary>
public sealed partial class PriorityListsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly ChampionDirectory _champions;

    public PriorityListsViewModel(PriorityListKind kind, SettingsStore settings, ChampionDirectory champions)
    {
        Kind = kind;
        _settings = settings;
        _champions = champions;

        var stored = Lists(settings.Snapshot());
        foreach (var role in Enum.GetValues<Role>())
        {
            var list = new RoleListViewModel(role, RoleTitle(kind, role));
            if (stored.TryGetValue(role, out var ids))
            {
                foreach (var id in ids)
                {
                    list.Items.Add(new RankedChampion(champions.Get(id)));
                }
            }

            list.Renumber();
            Roles.Add(list);
        }

        SelectedRole = Roles.First(r => r.Role == (kind == PriorityListKind.Picks ? Role.Middle : Role.Any));

        Available = new ListCollectionView(champions.All) { Filter = FilterAvailable };
        champions.Rebuilt += (_, _) => Available.Refresh();
    }

    public PriorityListKind Kind { get; }

    public bool IsPicks => Kind == PriorityListKind.Picks;

    public string Heading => Kind == PriorityListKind.Picks ? "Pick priority" : "Ban priority";

    public string Explanation => Kind == PriorityListKind.Picks
        ? "LockIn picks the highest champion on your role's list that isn't banned, taken, wanted by a teammate, or unowned. "
          + "In blind pick (no roles) and as a fallback, it uses the Any role list."
        : "LockIn bans the highest champion on your role's list (or the Default list if that's empty) that isn't already banned, "
          + "being banned by a teammate, wanted by a teammate, or your own pick.";

    public ObservableCollection<RoleListViewModel> Roles { get; } = [];

    [ObservableProperty]
    public partial RoleListViewModel SelectedRole { get; set; }

    /// <summary>Champions that can be added to the selected list, filtered by <see cref="SearchText"/>.</summary>
    public ListCollectionView Available { get; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial ChampionItemViewModel? SelectedAvailable { get; set; }

    partial void OnSelectedRoleChanged(RoleListViewModel value) => Available?.Refresh();

    partial void OnSearchTextChanged(string value)
    {
        Available.Refresh();
        if (Available.Count > 0)
        {
            SelectedAvailable = (ChampionItemViewModel)Available.GetItemAt(0);
        }
    }

    [RelayCommand]
    private void Add(ChampionItemViewModel? champion) => Insert(champion ?? SelectedAvailable, SelectedRole.Items.Count);

    /// <summary>Adds a champion at a position (used by drag and drop from the champion list).</summary>
    public void Insert(ChampionItemViewModel? champion, int index)
    {
        if (champion is null || SelectedRole.Items.Any(i => i.Champion == champion))
        {
            return;
        }

        SelectedRole.Items.Insert(Math.Clamp(index, 0, SelectedRole.Items.Count), new RankedChampion(champion));
        Changed();
        Available.Refresh();
    }

    [RelayCommand]
    private void Remove(RankedChampion? item)
    {
        if (item is not null && SelectedRole.Items.Remove(item))
        {
            Changed();
            Available.Refresh();
        }
    }

    [RelayCommand]
    private void MoveUp(RankedChampion? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(RankedChampion? item) => Move(item, +1);

    [RelayCommand]
    private void ClearList()
    {
        SelectedRole.Items.Clear();
        Changed();
        Available.Refresh();
    }

    /// <summary>Moves an entry to a new index (used by drag and drop).</summary>
    public void MoveTo(RankedChampion item, int newIndex)
    {
        var items = SelectedRole.Items;
        var oldIndex = items.IndexOf(item);
        newIndex = Math.Clamp(newIndex, 0, items.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex)
        {
            return;
        }

        items.Move(oldIndex, newIndex);
        Changed();
    }

    private void Move(RankedChampion? item, int delta)
    {
        if (item is not null)
        {
            MoveTo(item, SelectedRole.Items.IndexOf(item) + delta);
        }
    }

    private bool FilterAvailable(object obj) =>
        obj is ChampionItemViewModel champion
        && SelectedRole?.Items.All(i => i.Champion != champion) != false
        && (string.IsNullOrWhiteSpace(SearchText) || champion.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    private void Changed()
    {
        var role = SelectedRole;
        role.Renumber();
        var ids = role.Items.Select(i => i.Champion.Id).ToList();
        _settings.Update(s => Lists(s)[role.Role] = ids);
    }

    private Dictionary<Role, List<int>> Lists(AppSettings s) => Kind == PriorityListKind.Picks ? s.Picks.Lists : s.Bans.Lists;

    private static string RoleTitle(PriorityListKind kind, Role role) => role switch
    {
        Role.Any when kind == PriorityListKind.Picks => "Any role",
        Role.Any => "Default",
        _ => ChampSelectPlanner.RoleName(role),
    };
}
