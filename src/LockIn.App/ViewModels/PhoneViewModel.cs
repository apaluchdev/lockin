using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LockIn.Core.Settings;
using LockIn.Remote;
using QRCoder;

namespace LockIn.App.ViewModels;

/// <summary>The Phone tab: turn the phone page on/off and show how to connect (QR code, address, pairing code).</summary>
public sealed partial class PhoneViewModel : ObservableObject
{
    private readonly RemoteServer _server;
    private readonly SettingsStore _settings;
    private readonly bool _loading;

    public PhoneViewModel(RemoteServer server, SettingsStore settings)
    {
        _server = server;
        _settings = settings;
        var remote = settings.Snapshot().Remote;
        _loading = true;
        Enabled = remote.Enabled;
        Port = remote.Port;
        _loading = false;

        RefreshAddresses();
        server.StatusChanged += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    /// <summary>This PC's addresses on the local network, best first.</summary>
    public ObservableCollection<string> Addresses { get; } = [];

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial int Port { get; set; }

    [ObservableProperty]
    public partial string? SelectedAddress { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    [ObservableProperty]
    public partial ImageSource? QrCode { get; private set; }

    [ObservableProperty]
    public partial string Address { get; private set; } = "";

    [ObservableProperty]
    public partial string PairingCode { get; private set; } = "";

    public bool HasSeveralAddresses => Addresses.Count > 1;

    partial void OnEnabledChanged(bool value)
    {
        if (!_loading)
        {
            _settings.Update(s => s.Remote.Enabled = value);
        }
    }

    partial void OnPortChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        var port = value is >= 1024 and <= 65535 ? value : RemoteSettings.DefaultPort;
        if (port != value)
        {
            Port = port;
            return;
        }

        _settings.Update(s => s.Remote.Port = port);
    }

    partial void OnSelectedAddressChanged(string? value) => Refresh();

    [RelayCommand]
    private void RefreshAddresses()
    {
        var previous = SelectedAddress;
        Addresses.Clear();
        foreach (var address in RemoteServer.Addresses())
        {
            Addresses.Add(address.ToString());
        }

        SelectedAddress = previous is not null && Addresses.Contains(previous) ? previous : Addresses.FirstOrDefault();
        OnPropertyChanged(nameof(HasSeveralAddresses));
    }

    [RelayCommand]
    private void CopyLink()
    {
        if (CurrentIp() is { } ip && _server.IsRunning)
        {
            Clipboard.SetText(_server.LinkFor(ip));
        }
    }

    [RelayCommand]
    private void SignOutPhones()
    {
        var answer = MessageBox.Show(
            "Every connected phone will be signed out and the QR code and pairing code will change. Continue?",
            "Sign out all phones", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK)
        {
            _server.RegenerateKey();
        }
    }

    private IPAddress? CurrentIp() => SelectedAddress is { } text && IPAddress.TryParse(text, out var ip) ? ip : null;

    private void Refresh()
    {
        IsRunning = _server.IsRunning;
        HasError = _server.Error is not null;
        PairingCode = _server.PairingCode.Length == 6 ? $"{_server.PairingCode[..3]} {_server.PairingCode[3..]}" : _server.PairingCode;

        var ip = CurrentIp();
        if (_server.IsRunning && ip is not null)
        {
            Address = _server.AddressFor(ip);
            QrCode = RenderQr(_server.LinkFor(ip));
        }
        else
        {
            Address = "";
            QrCode = null;
        }

        var phones = _server.ConnectedPhones;
        StatusText = _server.Error
            ?? (!Enabled ? "Off — turn it on to connect your phone."
                : !_server.IsRunning ? "Starting…"
                : ip is null ? "Running, but this PC doesn't seem to be on a network."
                : phones switch
                {
                    0 => $"Running on port {_server.Port}. No phone connected yet.",
                    1 => $"Running on port {_server.Port}. 1 phone connected.",
                    _ => $"Running on port {_server.Port}. {phones} phones connected.",
                });
    }

    private static BitmapImage RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
