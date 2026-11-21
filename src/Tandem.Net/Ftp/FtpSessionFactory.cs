using Tandem.Net.Remote;

namespace Tandem.Net.Ftp;

public sealed class FtpSessionFactory(TimeSpan? connectTimeout = null, TimeSpan? readTimeout = null)
    : IRemoteSessionFactory
{
    public IRemoteSession Create(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust) =>
        new FtpSession(profile, credentials, trust, connectTimeout ?? TimeSpan.FromSeconds(15),
            readTimeout ?? TimeSpan.FromSeconds(30));
}
