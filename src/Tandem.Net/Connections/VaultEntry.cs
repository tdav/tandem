namespace Tandem.Net.Connections;

/// <summary>A saved connection and its optional credentials.</summary>
public sealed record VaultEntry(ConnectionProfile Profile, string? Password, byte[]? PrivateKey, string? Passphrase)
{
    public override string ToString() => nameof(VaultEntry);
}

/// <summary>An operation failed without exposing vault contents or secrets.</summary>
public sealed class CredentialVaultException : IOException
{
    internal CredentialVaultException(string message) : base(message) { }
}
