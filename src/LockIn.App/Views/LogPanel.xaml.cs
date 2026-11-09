using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LockIn.App.Logging;

namespace LockIn.App.Views;

/// <summary>The activity log. Follows new entries while scrolled to the bottom.</summary>
public partial class LogPanel : UserControl
{
    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _followTail = true;

    public LogPanel()
    {
        InitializeComponent();
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            CopiedNote.Visibility = Visibility.Collapsed;
        };
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

    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(EntriesList, source) is not ListBoxItem { DataContext: LogEntry entry })
        {
            return; // The scrollbar or empty space, not a log line.
        }

        try
        {
            Clipboard.SetText($"{entry.TimeText}  {entry.Message}");
        }
        catch (ExternalException)
        {
            return; // Another app is holding the clipboard; a second double-click will work.
        }

        CopiedNote.Visibility = Visibility.Visible;
        _copiedTimer.Stop();
        _copiedTimer.Start();
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
