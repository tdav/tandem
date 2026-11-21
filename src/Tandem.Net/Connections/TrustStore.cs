namespace Tandem.Net.Connections;

/// <summary>Remembered SSH host keys and exceptions for TLS certificates.</summary>
public sealed class TrustStore(string directory)
{
    public const string FileName = "known-hosts.json";

    private readonly Lock _gate = new();
    private readonly ConnectionFile<Document> _file = new(directory, FileName);
    private Document? _document;

    public HostKeyInfo? HostKeyOf(string hostAndPort, string algorithm)
    {
        lock (_gate)
            return Current.Keys.FirstOrDefault(item => SameHost(item.Host, hostAndPort) &&
                item.Key.Algorithm == algorithm)?.Key;
    }

    public void TrustHostKey(string hostAndPort, HostKeyInfo key)
    {
        lock (_gate)
        {
            var next = Current with
            {
                Keys = [.. Current.Keys.Where(item => !(SameHost(item.Host, hostAndPort) &&
                    item.Key.Algorithm == key.Algorithm)), new(hostAndPort, key)],
            };
            _file.Save(next);
            _document = next;
        }
    }

    public string? CertificateOf(string hostAndPort)
    {
        lock (_gate) return Current.Certificates.FirstOrDefault(item => SameHost(item.Host, hostAndPort))?.Sha256;
    }

    public void TrustCertificate(string hostAndPort, string sha256)
    {
        lock (_gate)
        {
            var next = Current with
            {
                Certificates = [.. Current.Certificates.Where(item => !SameHost(item.Host, hostAndPort)),
                    new(hostAndPort, sha256)],
            };
            _file.Save(next);
            _document = next;
        }
    }

    private Document Current => _document ??= _file.Load(value =>
        value is { Version: 1, Keys: not null, Certificates: not null } &&
        value.Keys.All(item => item is { Host.Length: > 0, Key: { Algorithm.Length: > 0, Sha256.Length: > 0 } }) &&
        value.Certificates.All(item => item is { Host.Length: > 0, Sha256.Length: > 0 }))
        ?? new Document(1, [], []);

    private static bool SameHost(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private sealed record SavedKey(string Host, HostKeyInfo Key);
    private sealed record SavedCertificate(string Host, string Sha256);
    private sealed record Document(int Version, IReadOnlyList<SavedKey> Keys, IReadOnlyList<SavedCertificate> Certificates);
}
