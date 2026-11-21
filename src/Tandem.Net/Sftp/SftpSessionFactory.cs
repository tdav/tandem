using Tandem.Net.Remote;

namespace Tandem.Net.Sftp;

public sealed class SftpSessionFactory(TimeSpan? connectTimeout = null, TimeSpan? operationTimeout = null)
    : IRemoteSessionFactory
{
    public IRemoteSession Create(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust) =>
        new SftpSession(profile, credentials, trust, connectTimeout ?? TimeSpan.FromSeconds(15),
            operationTimeout ?? TimeSpan.FromSeconds(30));
}
