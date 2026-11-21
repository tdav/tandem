using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Viewing;
using Tandem.Net;

namespace Tandem.App.ViewModels.Net;

public sealed partial class ConnectionEditViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly ConnectionProfile? _original;
    private bool _defaultPort;
    private bool _settingProtocol;

    public ConnectionEditViewModel(ConnectionProfile? profile = null, bool hasSavedSecret = false)
    {
        _original = profile;
        _id = profile?.Id ?? Guid.NewGuid();
        Protocol = profile?.Protocol ?? NetProtocol.Sftp;
        Name = profile?.Name ?? "";
        Host = profile?.Host ?? "";
        Port = (profile?.Port ?? ConnectionProfile.DefaultPort(Protocol)).ToString(CultureInfo.InvariantCulture);
        _defaultPort = profile is null || profile.Port == ConnectionProfile.DefaultPort(Protocol);
        User = profile?.User ?? "";
        Auth = profile?.Auth ?? NetAuth.Password;
        PrivateKeyPath = profile?.PrivateKeyPath ?? "";
        InitialPath = profile?.InitialPath ?? "/";
        PassiveMode = profile?.PassiveMode ?? true;
        NameEncoding = profile?.NameEncoding ?? TextEncodings.Utf8.Id;
        Remember = hasSavedSecret;
    }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial NetProtocol Protocol { get; set; } = NetProtocol.Sftp;
    [ObservableProperty] public partial string Host { get; set; } = "";
    [ObservableProperty] public partial string Port { get; set; } = "22";
    [ObservableProperty] public partial string User { get; set; } = "";
    [ObservableProperty] public partial NetAuth Auth { get; set; }
    [ObservableProperty] public partial string PrivateKeyPath { get; set; } = "";
    [ObservableProperty] public partial string Secret { get; set; } = "";
    [ObservableProperty] public partial bool Remember { get; set; }
    [ObservableProperty] public partial string InitialPath { get; set; } = "/";
    [ObservableProperty] public partial bool PassiveMode { get; set; } = true;
    [ObservableProperty] public partial string NameEncoding { get; set; } = TextEncodings.Utf8.Id;

    public bool IsFtp => Protocol != NetProtocol.Sftp;
    public IReadOnlyList<NetAuth> AuthenticationMethods => IsFtp ? [NetAuth.Password, NetAuth.Anonymous] :
        [NetAuth.Password, NetAuth.PrivateKey];

    partial void OnProtocolChanged(NetProtocol value)
    {
        if (_defaultPort)
        {
            _settingProtocol = true;
            Port = ConnectionProfile.DefaultPort(value).ToString(CultureInfo.InvariantCulture);
            _settingProtocol = false;
        }
        if (!AuthenticationMethods.Contains(Auth)) Auth = NetAuth.Password;
        OnPropertyChanged(nameof(IsFtp));
        OnPropertyChanged(nameof(AuthenticationMethods));
    }

    partial void OnPortChanged(string value)
    {
        if (!_settingProtocol) _defaultPort = value == ConnectionProfile.DefaultPort(Protocol)
            .ToString(CultureInfo.InvariantCulture);
    }

    public ConnectionEdit? Build(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Host)) error = Loc.Get("Connection_HostRequired");
        else if (!int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                 || port is < 1 or > 65535)
            error = Loc.Get("Connection_InvalidPort");
        else if (Auth == NetAuth.PrivateKey && string.IsNullOrWhiteSpace(PrivateKeyPath))
            error = Loc.Get("Connection_KeyRequired");
        else if (!AuthenticationMethods.Contains(Auth))
            error = Loc.Get("Connection_InvalidAuth");
        else
        {
            var profile = new ConnectionProfile(_id, string.IsNullOrWhiteSpace(Name) ? Host : Name, Protocol, Host,
                port, User, Auth, Auth == NetAuth.PrivateKey ? PrivateKeyPath : null, InitialPath, PassiveMode,
                IsFtp && NameEncoding != TextEncodings.Utf8.Id ? NameEncoding : null).Normalized();
            var secret = !Remember ? "" : Secret.Length == 0 && _original?.Auth == Auth ? null :
                Secret.Length == 0 ? null : Secret;
            return new ConnectionEdit(profile, Auth == NetAuth.Password ? secret : "")
            { Passphrase = Auth == NetAuth.PrivateKey ? secret : "" };
        }
        return null;
    }
}
