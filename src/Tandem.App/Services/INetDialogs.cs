using Avalonia.Controls;
using Tandem.App.ViewModels.Net;
using Tandem.Net;
using Tandem.Net.Connections;

namespace Tandem.App.Services;

public sealed record ConnectionEdit(ConnectionProfile Profile, string? Password)
{
    public string? Passphrase { get; init; }
    public override string ToString() => $"{nameof(ConnectionEdit)} {{ Profile = {Profile} }}";
}

public interface INetDialogs
{
    Task<Guid?> ShowConnectionsAsync(ConnectionListViewModel list);
    Task<bool> OpenVaultAsync(ConnectionVaultViewModel vault, Window? owner = null);
    Task<ConnectionEdit?> EditConnectionAsync(ConnectionEditViewModel edit, Window? owner = null);
    Task<SecretAnswer?> AskSecretAsync(string connectionName, string address, SecretKind kind, bool previousWasWrong,
        CancellationToken ct);
    Task<bool> ConfirmHostKeyAsync(string address, HostKeyInfo key, HostKeyInfo? previous, CancellationToken ct);
    Task<bool> ConfirmCertificateAsync(string address, CertificateInfo certificate, CancellationToken ct);
    Task<bool> ConfirmDeleteConnectionAsync(string connectionName, Window? owner = null);
}
