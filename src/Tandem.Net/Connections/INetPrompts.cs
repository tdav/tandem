namespace Tandem.Net.Connections;

public enum SecretKind { Password, Passphrase }

public sealed record SecretAnswer(string Secret, bool Remember)
{
    public override string ToString() => $"{nameof(SecretAnswer)} {{ Secret = ***, Remember = {Remember} }}";
}

public interface INetPrompts
{
    Task<SecretAnswer?> AskSecretAsync(ConnectionProfile profile, SecretKind kind, bool previousWasWrong,
        CancellationToken ct);

    Task<bool> ConfirmHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, HostKeyInfo? previous,
        CancellationToken ct);

    Task<bool> ConfirmCertificateAsync(ConnectionProfile profile, CertificateInfo certificate, CancellationToken ct);
}
