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
    public IReadOnlyList<ConnectionProfile> Profiles => connections.Profiles;
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial ConnectionProfile? Selected { get; set; }
    [ObservableProperty] public partial string? Error { get; private set; }

    public bool CanManageProfiles => !connections.VaultConfigured || connections.VaultUnlocked;

    public string? VaultAction => connections.VaultPath is null || connections.VaultUnlocked ? null :
        Loc.Get(connections.VaultConfigured ? "Vault_Unlock" : "Vault_Setup");

    public string? VaultStatus => connections.VaultPath is not { } path ? null :
        Loc.Format(connections.VaultUnlocked ? "Vault_ActivePath" : connections.VaultConfigured ? "Vault_LockedPath" :
            "Vault_SetupPath", path);

    public async Task OpenVaultAsync(Window? owner = null)
    {
        if (IsBusy || VaultAction is null) return;
        await PerformAsync(async () =>
        {
            await dialogs.OpenVaultAsync(new ConnectionVaultViewModel(connections, create: !connections.VaultConfigured), owner);
            OnPropertyChanged(nameof(CanManageProfiles));
            OnPropertyChanged(nameof(VaultAction));
            OnPropertyChanged(nameof(VaultStatus));
            Refresh(Selected?.Id);
        });
    }

    public async Task EditAsync(bool create, Window? owner = null)
    {
        if (IsBusy || !CanManageProfiles || !create && Selected is null) return;
        var profile = create ? null : Selected;
        await PerformAsync(async () =>
        {
            var remembered = profile is not null && (profile.Auth switch
            {
                NetAuth.Password => await connections.HasSavedPasswordAsync(profile.Id, CancellationToken.None),
                NetAuth.PrivateKey => await connections.HasSavedSecretAsync(profile.Id, SecretKind.Passphrase, CancellationToken.None),
                _ => false,
            });
            var hasStoredKey = profile?.Auth == NetAuth.PrivateKey &&
                await connections.HasStoredPrivateKeyAsync(profile.Id, CancellationToken.None);
            if (await dialogs.EditConnectionAsync(new ConnectionEditViewModel(profile, remembered, hasStoredKey), owner) is not { } edit)
                return;
            await Task.Run(() => connections.SaveAsync(edit.Profile, edit.Password, edit.Passphrase, CancellationToken.None));
            Refresh(edit.Profile.Id);
        });
    }

    public async Task DeleteAsync(Window? owner = null)
    {
        if (IsBusy || !CanManageProfiles || Selected is not { } profile) return;
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
        IsBusy = true;
        Error = null;
        try { await action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = Loc.Get("Connection_SaveFailed");
        }
        finally { IsBusy = false; }
    }

    private void Refresh(Guid? id)
    {
        OnPropertyChanged(nameof(Profiles));
        Selected = Profiles.FirstOrDefault(profile => profile.Id == id) ?? Profiles.FirstOrDefault();
    }
}
