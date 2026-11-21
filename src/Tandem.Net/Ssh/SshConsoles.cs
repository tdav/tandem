using Tandem.Net.Connections;

namespace Tandem.Net.Ssh;

public sealed class SshConsoles(NetConnections connections, ITerminalTransportFactory factory)
{
    public static bool Supports(ConnectionProfile profile) => profile.Protocol == NetProtocol.Sftp;
    public IReadOnlyList<ConnectionProfile> Profiles => connections.Profiles.Where(Supports).ToArray();

    public ITerminalTransport Open(Guid profileId)
    {
        var profile = connections.Profiles.FirstOrDefault(profile => profile.Id == profileId) ??
            throw new KeyNotFoundException($"Unknown connection {profileId}.");
        if (!Supports(profile)) throw new NotSupportedException("SSH requires an SFTP profile.");
        return factory.Create(profile, connections.CredentialsOf(profileId), connections.Trust);
    }
}
