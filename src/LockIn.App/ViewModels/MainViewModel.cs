using CommunityToolkit.Mvvm.ComponentModel;

namespace LockIn.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Status { get; set; } = "Waiting for the League client…";
}
