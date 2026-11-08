using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LockIn.App.Views;

/// <summary>The activity log. Follows new entries while scrolled to the bottom.</summary>
public partial class LogPanel : UserControl
{
    private bool _followTail = true;

    public LogPanel()
    {
        InitializeComponent();
        ((INotifyCollectionChanged)EntriesList.Items).CollectionChanged += OnItemsChanged;
        EntriesList.Loaded += (_, _) =>
        {
            if (FindScrollViewer(EntriesList) is { } viewer)
            {
                viewer.ScrollChanged += (_, e) =>
                {
                    // Only user scrolling changes whether we follow; content growth keeps the current mode.
                    if (e.ExtentHeightChange == 0)
                    {
                        _followTail = viewer.VerticalOffset >= viewer.ScrollableHeight - 4;
                    }
                };
            }

            ScrollToEnd();
        };
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_followTail && e.Action == NotifyCollectionChangedAction.Add)
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        if (EntriesList.Items.Count > 0)
        {
            EntriesList.ScrollIntoView(EntriesList.Items[^1]);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
