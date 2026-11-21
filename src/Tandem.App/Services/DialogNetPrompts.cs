using Avalonia.Threading;
using Tandem.Net;
using Tandem.Net.Connections;

namespace Tandem.App.Services;

public sealed class DialogNetPrompts(INetDialogs dialogs) : INetPrompts
{
    public Task<SecretAnswer?> AskSecretAsync(ConnectionProfile profile, SecretKind kind, bool previousWasWrong,
        CancellationToken ct) => Dispatcher.UIThread.InvokeAsync(() =>
            dialogs.AskSecretAsync(profile.Name, profile.Address, kind, previousWasWrong, ct));

    public Task<bool> ConfirmHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, HostKeyInfo? previous,
        CancellationToken ct) => Dispatcher.UIThread.InvokeAsync(() =>
            dialogs.ConfirmHostKeyAsync(profile.Address, key, previous, ct));

    public Task<bool> ConfirmCertificateAsync(ConnectionProfile profile, CertificateInfo certificate,
        CancellationToken ct) => Dispatcher.UIThread.InvokeAsync(() =>
            dialogs.ConfirmCertificateAsync(profile.Address, certificate, ct));
}
