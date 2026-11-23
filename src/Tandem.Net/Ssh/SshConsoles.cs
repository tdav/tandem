using Tandem.Net.Connections;

namespace Tandem.Net.Ssh;

public sealed class SshConsoles(NetConnections connections, ITerminalTransportFactory factory)
{
    public static bool Supports(ConnectionProfile profile) => profile.Protocol is NetProtocol.Sftp or NetProtocol.Ssh;
    public IReadOnlyList<ConnectionProfile> Profiles => connections.Profiles.Where(Supports).ToArray();

    public ITerminalTransport Open(Guid profileId)
    {
        var profile = connections.Profiles.FirstOrDefault(profile => profile.Id == profileId) ??
            throw new KeyNotFoundException($"Unknown connection {profileId}.");
        if (!Supports(profile)) throw new NotSupportedException("The console requires an SSH or SFTP profile.");
        return factory.Create(profile, connections.CredentialsOf(profileId), connections.Trust);
    }
}
