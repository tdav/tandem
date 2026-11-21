using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.Net.Connections;

namespace Tandem.App.ViewModels.Net;

public sealed partial class ConnectionVaultViewModel(NetConnections connections, bool create) : ObservableObject
{
    public bool IsCreating => create;
    public string Path { get; } = connections.VaultPath ?? throw new ArgumentException("A vault is required.", nameof(connections));

    [ObservableProperty] public partial string? Error { get; private set; }

    public bool Validate(string password, string confirmation)
    {
        Error = password.Length == 0 ? Loc.Get("Vault_PasswordRequired") :
            create && password.Length < 12 ? Loc.Get("Vault_PasswordTooShort") :
            create && password != confirmation ? Loc.Get("Vault_PasswordMismatch") : null;
        return Error is null;
    }

    public async Task<bool> SubmitAsync(string password, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => create ? connections.EnableVaultAsync(password, ct) :
                connections.UnlockVaultAsync(password, ct), ct);
            return connections.VaultUnlocked;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = Loc.Get(create ? "Vault_CreateFailed" : "Vault_UnlockFailed");
            return false;
        }
    }
}
