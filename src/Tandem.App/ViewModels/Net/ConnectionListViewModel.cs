using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Net;
using Tandem.Net.Connections;

namespace Tandem.App.ViewModels.Net;

public sealed partial class ConnectionListViewModel(NetConnections connections, INetDialogs dialogs,
    Func<Guid, Task<bool>>? beforeDelete = null) : ObservableObject
{
    private bool _busy;

    public IReadOnlyList<ConnectionProfile> Profiles => connections.Profiles;
    [ObservableProperty] public partial ConnectionProfile? Selected { get; set; }
    [ObservableProperty] public partial string? Error { get; private set; }

    public async Task EditAsync(bool create, Window? owner = null)
    {
        if (_busy || !create && Selected is null) return;
        var profile = create ? null : Selected;
        await PerformAsync(async () =>
        {
            var remembered = profile is not null && (profile.Auth switch
            {
                NetAuth.Password => await connections.HasSavedPasswordAsync(profile.Id, CancellationToken.None),
                NetAuth.PrivateKey => await connections.HasSavedSecretAsync(profile.Id, SecretKind.Passphrase, CancellationToken.None),
                _ => false,
            });
            if (await dialogs.EditConnectionAsync(new ConnectionEditViewModel(profile, remembered), owner) is not { } edit)
                return;
            await Task.Run(() => connections.SaveAsync(edit.Profile, edit.Password, edit.Passphrase, CancellationToken.None));
            Refresh(edit.Profile.Id);
        });
    }

    public async Task DeleteAsync(Window? owner = null)
    {
        if (_busy || Selected is not { } profile) return;
        await PerformAsync(async () =>
        {
            if (!await dialogs.ConfirmDeleteConnectionAsync(profile.Name, owner)) return;
            if (beforeDelete is not null && !await beforeDelete(profile.Id)) return;
            await Task.Run(() => connections.DeleteAsync(profile.Id, CancellationToken.None));
            Refresh(null);
        });
    }

    private async Task PerformAsync(Func<Task> action)
    {
        _busy = true;
        Error = null;
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = Loc.Get("Connection_SaveFailed");
        }
        finally { _busy = false; }
    }

    private void Refresh(Guid? id)
    {
        OnPropertyChanged(nameof(Profiles));
        Selected = Profiles.FirstOrDefault(profile => profile.Id == id) ?? Profiles.FirstOrDefault();
    }
}
