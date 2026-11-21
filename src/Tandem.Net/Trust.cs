namespace Tandem.Net;

/// <summary>What a connection logs in with. Lives only in memory (and, through <see cref="ICredentialSource"/>, in
/// the keychain); <see cref="ToString"/> prints no secret.</summary>
public sealed record NetCredentials(string? Password, byte[]? PrivateKey, string? Passphrase)
{
    public override string ToString() =>
        $"{nameof(NetCredentials)} {{ Password = {Mask(Password)}, PrivateKey = {(PrivateKey is null ? "null" : "***")}, " +
        $"Passphrase = {Mask(Passphrase)} }}";

    private static string Mask(string? secret) => secret is null ? "null" : "***";
}

/// <summary>An SSH host key; <paramref name="Sha256"/> as "SHA256:…".</summary>
public sealed record HostKeyInfo(string Algorithm, string Sha256);

/// <summary>A TLS certificate that did not pass the system's check; <paramref name="Problems"/> says why.</summary>
public sealed record CertificateInfo(string Subject, string Issuer, string Sha256, DateTimeOffset NotBefore,
    DateTimeOffset NotAfter, string Problems);

/// <summary>Asks the user whether to trust a host key or certificate that is not known yet.</summary>
public interface ITrustDecisions
{
    Task<bool> TrustHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, CancellationToken ct);

    Task<bool> TrustCertificateAsync(ConnectionProfile profile, CertificateInfo certificate, CancellationToken ct);
}

/// <summary>Where a connection gets its credentials: the keychain, or the user.</summary>
public interface ICredentialSource
{
    /// <summary>null — the user declined. <paramref name="previousWasWrong"/>: the server did not accept the previous
    /// credentials.</summary>
    Task<NetCredentials?> GetAsync(ConnectionProfile profile, bool previousWasWrong, CancellationToken ct);

    /// <summary>The server accepted <paramref name="credentials"/>: they may be remembered.</summary>
    void Accepted(ConnectionProfile profile, NetCredentials credentials);
}
