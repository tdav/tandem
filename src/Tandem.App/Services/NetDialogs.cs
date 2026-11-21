using Avalonia.Controls;
using Avalonia.Threading;
using Tandem.App.Resources;
using Tandem.App.ViewModels.Net;
using Tandem.App.Views.Net;
using Tandem.Net;
using Tandem.Net.Connections;

namespace Tandem.App.Services;

public sealed class NetDialogs(Func<Window?> mainWindow) : INetDialogs
{
    public async Task<Guid?> ShowConnectionsAsync(ConnectionListViewModel list) =>
        await ShowAsync(new ConnectionListDialog(list)) is Guid id ? id : null;

    public async Task<bool> OpenVaultAsync(ConnectionVaultViewModel vault, Window? owner = null) =>
        await ShowAsync(new ConnectionVaultDialog(vault), owner: owner) is true;

    public async Task<ConnectionEdit?> EditConnectionAsync(ConnectionEditViewModel edit, Window? owner = null) =>
        await ShowAsync(new ConnectionEditDialog(edit), owner: owner) as ConnectionEdit;

    public async Task<SecretAnswer?> AskSecretAsync(string connectionName, string address, SecretKind kind,
        bool previousWasWrong, CancellationToken ct) =>
        await ShowAsync(new ConnectionSecretDialog(connectionName, address, kind, previousWasWrong), ct) as SecretAnswer;

    public async Task<bool> ConfirmHostKeyAsync(string address, HostKeyInfo key, HostKeyInfo? previous,
        CancellationToken ct)
    {
        var detail = previous is null
            ? Loc.Format("Connection_HostKeyDetail", address, key.Algorithm, key.Sha256)
            : Loc.Format("Connection_HostKeyChangedDetail", address, key.Algorithm, previous.Sha256, key.Sha256);
        return await ShowAsync(new ConnectionQuestionDialog(
            Loc.Get(previous is null ? "Connection_TrustHostKey" : "Connection_HostKeyChanged"), detail,
            Loc.Get("Connection_Trust")), ct) is true;
    }

    public async Task<bool> ConfirmCertificateAsync(string address, CertificateInfo certificate, CancellationToken ct)
    {
        var detail = Loc.Format("Connection_CertificateDetail", address, certificate.Subject, certificate.Issuer,
            certificate.Sha256, certificate.NotBefore, certificate.NotAfter, certificate.Problems);
        return await ShowAsync(new ConnectionQuestionDialog(Loc.Get("Connection_TrustCertificate"), detail,
            Loc.Get("Connection_Trust")), ct) is true;
    }

    public async Task<bool> ConfirmDeleteConnectionAsync(string connectionName, Window? owner = null) =>
        await ShowAsync(new ConnectionQuestionDialog(Loc.Get("Connection_DeleteTitle"),
            Loc.Format("Connection_DeleteQuestion", connectionName), Loc.Get("Button_Delete")), owner: owner) is true;

    private async Task<object?> ShowAsync(Window dialog, CancellationToken ct = default, Window? owner = null)
    {
        if ((owner ?? mainWindow()) is not { IsVisible: true } visible || ct.IsCancellationRequested) return null;
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        }));
        return await dialog.ShowDialog<object?>(visible);
    }
}
