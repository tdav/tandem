namespace Tandem.Core.Platform;

/// <summary>A store of secrets (on macOS, the keychain). A key is any non-empty string.</summary>
public interface ISecretStore
{
    /// <summary>The secret stored under <paramref name="key"/>; null when there is none.</summary>
    /// <exception cref="SecretStoreException">The store is unavailable or access was refused.</exception>
    Task<string?> GetAsync(string key, CancellationToken ct);

    /// <summary>Stores <paramref name="secret"/> under <paramref name="key"/>, creating or replacing it.</summary>
    /// <exception cref="SecretStoreException">The store is unavailable or access was refused.</exception>
    Task SetAsync(string key, string secret, CancellationToken ct);

    /// <summary>Removes the secret under <paramref name="key"/>; a missing one is not an error.</summary>
    /// <exception cref="SecretStoreException">The store is unavailable or access was refused.</exception>
    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>The secret store is unavailable or the user refused access to it.</summary>
public class SecretStoreException : IOException
{
    public SecretStoreException(string message, Exception? inner = null) : base(message, inner) { }
}
