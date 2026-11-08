using System.Windows;
using LockIn.App.ViewModels;

namespace LockIn.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
