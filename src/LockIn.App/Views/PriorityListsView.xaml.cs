using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LockIn.App.ViewModels;

namespace LockIn.App.Views;

/// <summary>Drag-and-drop for the priority list: reorder entries, or drag champions in from the picker.</summary>
public partial class PriorityListsView : UserControl
{
    private Point? _dragStart;
    private object? _dragItem;

    public PriorityListsView() => InitializeComponent();

    private PriorityListsViewModel ViewModel => (PriorityListsViewModel)DataContext;

    // ── Dragging out ─────────────────────────────────────────────────────

    private void OnPriorityMouseDown(object sender, MouseButtonEventArgs e) => BeginPotentialDrag(e, PriorityList);

    private void OnAvailableMouseDown(object sender, MouseButtonEventArgs e) => BeginPotentialDrag(e, AvailableList);

    private void OnPriorityMouseMove(object sender, MouseEventArgs e) => MaybeStartDrag(e, PriorityList);

    private void OnAvailableMouseMove(object sender, MouseEventArgs e) => MaybeStartDrag(e, AvailableList);

    private void BeginPotentialDrag(MouseButtonEventArgs e, ListBox list)
    {
        // Let the row buttons and scrollbar work normally.
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null
            || FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null)
        {
            _dragStart = null;
            return;
        }

        _dragStart = e.GetPosition(null);
        _dragItem = ItemUnder(list, e.OriginalSource as DependencyObject);
    }

    private void MaybeStartDrag(MouseEventArgs e, ListBox list)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragItem is null)
        {
            return;
        }

        var delta = e.GetPosition(null) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _dragItem;
        _dragStart = null;
        _dragItem = null;
        DragDrop.DoDragDrop(list, new DataObject(typeof(object), item), DragDropEffects.Move | DragDropEffects.Copy);
    }

    // ── Dropping on the priority list ────────────────────────────────────

    private void OnPriorityDragOver(object sender, DragEventArgs e)
    {
        var data = e.Data.GetData(typeof(object));
        e.Effects = data switch
        {
            RankedChampion => DragDropEffects.Move,
            ChampionItemViewModel => DragDropEffects.Copy,
            _ => DragDropEffects.None,
        };
        e.Handled = true;
    }

    private void OnPriorityDrop(object sender, DragEventArgs e)
    {
        var target = ItemUnder(PriorityList, e.OriginalSource as DependencyObject) as RankedChampion;
        var items = ViewModel.SelectedRole.Items;
        var index = target is null ? items.Count : items.IndexOf(target);

        switch (e.Data.GetData(typeof(object)))
        {
            case RankedChampion moved:
                ViewModel.MoveTo(moved, target is null ? items.Count - 1 : index);
                PriorityList.SelectedItem = moved;
                break;
            case ChampionItemViewModel champion:
                ViewModel.Insert(champion, index);
                break;
        }

        e.Handled = true;
    }

    // ── Adding from the picker ───────────────────────────────────────────

    private void OnAvailableDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(AvailableList, e.OriginalSource as DependencyObject) is ChampionItemViewModel champion)
        {
            ViewModel.AddCommand.Execute(champion);
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ViewModel.AddCommand.Execute(null);
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Down when AvailableList.Items.Count > 0:
                AvailableList.SelectedIndex = Math.Min(AvailableList.SelectedIndex + 1, AvailableList.Items.Count - 1);
                AvailableList.ScrollIntoView(AvailableList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when AvailableList.SelectedIndex > 0:
                AvailableList.SelectedIndex--;
                AvailableList.ScrollIntoView(AvailableList.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static object? ItemUnder(ItemsControl list, DependencyObject? source) =>
        FindAncestor<ListBoxItem>(source) is { } container
        && list.ItemContainerGenerator.ItemFromContainer(container) is var item
        && item != DependencyProperty.UnsetValue
            ? item
            : null;

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}
