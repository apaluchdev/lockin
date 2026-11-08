using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using LockIn.App.ViewModels;

namespace LockIn.App.Services;

/// <summary>The notification-area icon and its menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private TaskbarIcon? _icon;

    public void Create(MainViewModel viewModel, Action showWindow, Action exit)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Open LockIn", FontWeight = System.Windows.FontWeights.SemiBold, Command = new RelayCommand(showWindow) });
        menu.Items.Add(new Separator());
        menu.Items.Add(CheckItem("Auto-accept matches", nameof(MainViewModel.AutoAcceptEnabled), viewModel));
        menu.Items.Add(CheckItem("Champ select automation", nameof(MainViewModel.AutomationEnabled), viewModel));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Exit", Command = new RelayCommand(exit) });

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/lockin.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(showWindow),
            DataContext = viewModel,
        };
        _icon.SetBinding(TaskbarIcon.ToolTipTextProperty, new Binding(nameof(MainViewModel.TrayToolTip)));

        // Efficiency mode would throttle LockIn while hidden, which would hurt lock-in timing.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void ShowNotification(string title, string message) =>
        _icon?.ShowNotification(title, message);

    private static MenuItem CheckItem(string header, string path, object source)
    {
        var item = new MenuItem { Header = header, IsCheckable = true };
        item.SetBinding(MenuItem.IsCheckedProperty, new Binding(path) { Source = source, Mode = BindingMode.TwoWay });
        return item;
    }

    public void Dispose() => _icon?.Dispose();
}
