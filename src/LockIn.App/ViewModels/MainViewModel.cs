using CommunityToolkit.Mvvm.ComponentModel;
using LockIn.Core.Gameflow;
using LockIn.Core.Lcu;
using LockIn.Core.Settings;

namespace LockIn.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly LcuConnection _connection;
    private readonly GameflowMonitor _gameflow;
    private readonly SettingsStore _settings;
    private readonly bool _initializing; // Only set during construction, to skip saving the initial values.

    public MainViewModel(
        LcuConnection connection,
        GameflowMonitor gameflow,
        SettingsStore settings,
        SettingsViewModel settingsPage,
        LogViewModel log,
        ChampionDirectory champions,
        LoadoutsViewModel loadoutsPage,
        PhoneViewModel phonePage)
    {
        _connection = connection;
        _gameflow = gameflow;
        _settings = settings;
        SettingsPage = settingsPage;
        PicksPage = new PriorityListsViewModel(PriorityListKind.Picks, settings, champions);
        BansPage = new PriorityListsViewModel(PriorityListKind.Bans, settings, champions);
        LoadoutsPage = loadoutsPage;
        PhonePage = phonePage;
        Log = log;

        var snapshot = settings.Snapshot();
        _initializing = true;
        AutoAcceptEnabled = snapshot.AutoAcceptEnabled;
        AutomationEnabled = snapshot.AutomationEnabled;
        _initializing = false;

        connection.StateChanged += (_, _) => Ui.Post(RefreshStatus);
        gameflow.PhaseChanged += (_, _) => Ui.Post(RefreshStatus);
        RefreshStatus();
    }

    public PriorityListsViewModel PicksPage { get; }

    public PriorityListsViewModel BansPage { get; }

    public LoadoutsViewModel LoadoutsPage { get; }

    public PhoneViewModel PhonePage { get; }

    public SettingsViewModel SettingsPage { get; }

    public LogViewModel Log { get; }

    [ObservableProperty]
    public partial LcuConnectionState ConnectionState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayToolTip))]
    public partial string ConnectionText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayToolTip))]
    public partial string PhaseText { get; private set; } = "";

    public string TrayToolTip => ConnectionState == LcuConnectionState.Connected
        ? $"LockIn — {PhaseText}"
        : $"LockIn — {ConnectionText}";

    [ObservableProperty]
    public partial bool AutoAcceptEnabled { get; set; }

    [ObservableProperty]
    public partial bool AutomationEnabled { get; set; }

    partial void OnAutoAcceptEnabledChanged(bool value) => SaveUnlessInitializing(s => s.AutoAcceptEnabled = value);

    partial void OnAutomationEnabledChanged(bool value) => SaveUnlessInitializing(s => s.AutomationEnabled = value);

    private void SaveUnlessInitializing(Action<AppSettings> apply)
    {
        if (!_initializing)
        {
            _settings.Update(apply);
        }
    }

    private void RefreshStatus()
    {
        ConnectionState = _connection.State;
        ConnectionText = _connection.State switch
        {
            LcuConnectionState.Connected => $"Connected as {_connection.Summoner?.Label}",
            LcuConnectionState.Connecting => "League client found, waiting for it to load…",
            _ => "Waiting for the League client…",
        };
        PhaseText = _connection.State == LcuConnectionState.Connected ? GameflowMonitor.Describe(_gameflow.Phase) : "";
        OnPropertyChanged(nameof(TrayToolTip));
    }
}
