using Tandem.Core.Vfs;
using Tandem.Net.Ftp;
using Tandem.Net.Remote;
using Tandem.Net.Sftp;

namespace Tandem.Net.Tests.Integration;

internal sealed record LiveServer(ConnectionProfile Profile, ICredentialSource Credentials, ITrustDecisions Trust)
{
    public const string SftpVariable = "TANDEM_TEST_SFTP_URL";
    public const string FtpVariable = "TANDEM_TEST_FTP_URL";
    public const string FtpsVariable = "TANDEM_TEST_FTPS_URL";

    public static LiveServer? Read(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Contains(':') ||
            string.IsNullOrEmpty(uri.Host) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException($"{variable} must be a URL with the user name and test directory only.");

        var protocol = (variable, uri.Scheme) switch
        {
            (SftpVariable, "sftp") => NetProtocol.Sftp,
            (FtpVariable, "ftp") => NetProtocol.Ftp,
            (FtpsVariable, "ftpes") => NetProtocol.FtpsExplicit,
            (FtpsVariable, "ftps") => NetProtocol.FtpsImplicit,
            _ => throw new InvalidOperationException($"Unexpected protocol in {variable}."),
        };
        var prefix = variable[..^4];
        var password = Environment.GetEnvironmentVariable(prefix + "_PASSWORD");
        var keyPath = protocol == NetProtocol.Sftp ? Environment.GetEnvironmentVariable(prefix + "_KEY") : null;
        var passphrase = keyPath is null ? null : Environment.GetEnvironmentVariable(prefix + "_PASSPHRASE");
        if (password is null && keyPath is null) return null;
        var user = Uri.UnescapeDataString(uri.UserInfo);
        if (user.Length == 0 || user.Contains(':'))
            throw new InvalidOperationException($"{variable} must include a user name without a password.");
        var profile = new ConnectionProfile(Guid.NewGuid(), "Integration", protocol, uri.IdnHost,
            uri.IsDefaultPort ? ConnectionProfile.DefaultPort(protocol) : uri.Port,
            user, keyPath is null ? NetAuth.Password : NetAuth.PrivateKey, keyPath,
            Uri.UnescapeDataString(uri.AbsolutePath)).Normalized();
        return new LiveServer(profile,
            new TestCredentials(password, keyPath is null ? null : File.ReadAllBytes(keyPath), passphrase),
            new TestTrust(Environment.GetEnvironmentVariable("TANDEM_TEST_FTPS_TRUST") == "1"));
    }

    public RemoteProvider Create()
    {
        IRemoteSessionFactory factory = Profile.Protocol == NetProtocol.Sftp ? new SftpSessionFactory() : new FtpSessionFactory();
        return new("integration:" + Guid.NewGuid().ToString("N"), Profile,
            () => factory.Create(Profile, Credentials, Trust), new HostLocation(VfsPath.Local("/"), null));
    }

    public static async Task<(RemoteProvider Provider, VfsPath Root)> OpenDirectoryAsync(LiveServer server,
        CancellationToken ct)
    {
        var provider = server.Create();
        try
        {
            await provider.ConnectAsync(ct);
            var root = new VfsPath(provider.Id, server.Profile.InitialPath).Combine("tandem-test-" + Guid.NewGuid().ToString("N"));
            await provider.CreateDirectoryAsync(root, ct);
            return (provider, root);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    internal sealed class TestCredentials(string? password, byte[]? key, string? passphrase) : ICredentialSource
    {
        public Task<NetCredentials?> GetAsync(ConnectionProfile profile, bool previousWasWrong, CancellationToken ct) =>
            Task.FromResult<NetCredentials?>(new(password, key, passphrase));

        public void Accepted(ConnectionProfile profile, NetCredentials credentials) { }
    }

    internal sealed class TestTrust(bool certificateOverride) : ITrustDecisions
    {
        private HostKeyInfo? _knownHost;
        private readonly object _gate = new();

        public Task<bool> TrustHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, CancellationToken ct)
        {
            lock (_gate)
            {
                _knownHost ??= key;
                return Task.FromResult(_knownHost == key);
            }
        }

        public Task<bool> TrustCertificateAsync(ConnectionProfile profile, CertificateInfo certificate, CancellationToken ct)
            => Task.FromResult(certificateOverride);
    }
}
