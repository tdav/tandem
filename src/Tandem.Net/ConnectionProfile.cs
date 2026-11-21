using System.Text.Json.Serialization;
using Tandem.Core.Vfs;

namespace Tandem.Net;

public enum NetProtocol { Ftp, FtpsExplicit, FtpsImplicit, Sftp }

public enum NetAuth { Password, PrivateKey, Anonymous }

/// <summary>
/// The saved settings of one server connection. It holds no secret: a password or key passphrase lives only in the
/// keychain and in memory (<see cref="NetCredentials"/>). Only the constructor parameters are written to
/// <c>connections.json</c>.
/// </summary>
/// <param name="PrivateKeyPath">The key file, for <see cref="NetAuth.PrivateKey"/>.</param>
/// <param name="InitialPath">The folder a new connection opens in.</param>
/// <param name="PassiveMode">FTP data connections in passive mode.</param>
/// <param name="NameEncoding">The encoding of file names on an FTP server that does not use UTF-8; null for
/// UTF-8.</param>
public sealed record ConnectionProfile(Guid Id, string Name, NetProtocol Protocol, string Host, int Port, string User,
    NetAuth Auth, string? PrivateKeyPath = null, string InitialPath = "/", bool PassiveMode = true,
    string? NameEncoding = null)
{
    public const string AnonymousUser = "anonymous";

    public static int DefaultPort(NetProtocol protocol) => protocol switch
    {
        NetProtocol.Ftp or NetProtocol.FtpsExplicit => 21,
        NetProtocol.FtpsImplicit => 990,
        NetProtocol.Sftp => 22,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };

    /// <summary>The scheme the address starts with: "ftp", "ftpes" (TLS asked for on the FTP port), "ftps" (TLS from
    /// the first byte) or "sftp".</summary>
    public static string SchemeOf(NetProtocol protocol) => protocol switch
    {
        NetProtocol.Ftp => "ftp",
        NetProtocol.FtpsExplicit => "ftpes",
        NetProtocol.FtpsImplicit => "ftps",
        NetProtocol.Sftp => "sftp",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };

    /// <summary>The server as the user reads it, e.g. "sftp://user@host" or "ftp://user@host:2121": the port only
    /// when it is not the protocol's default, the user only when there is one. Never a password: the profile has
    /// none.</summary>
    [JsonIgnore]
    public string Address
    {
        get
        {
            var host = Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;
            var user = User.Length == 0 ? "" : User + "@";
            var port = Port == DefaultPort(Protocol) ? "" : ":" + Port;
            return $"{SchemeOf(Protocol)}://{user}{host}{port}";
        }
    }

    /// <summary><see cref="Address"/> in lower case: equal for every path on the server, found without the
    /// network.</summary>
    [JsonIgnore]
    public string VolumeKey => Address.ToLowerInvariant();

    /// <summary>The profile as it is saved and used: names, host, user and paths without surrounding spaces; a port
    /// outside 1…65535 replaced by the protocol's default; an absolute, normalized initial path ("/" when it is empty
    /// or climbs above the root); the user "anonymous" for <see cref="NetAuth.Anonymous"/>; empty optional texts as
    /// null.</summary>
    public ConnectionProfile Normalized() => this with
    {
        Name = Name.Trim(),
        Host = Host.Trim(),
        Port = Port is >= 1 and <= 65535 ? Port : DefaultPort(Protocol),
        User = Auth == NetAuth.Anonymous ? AnonymousUser : User.Trim(),
        PrivateKeyPath = NullIfBlank(PrivateKeyPath),
        InitialPath = NormalizePath(InitialPath),
        NameEncoding = NullIfBlank(NameEncoding),
    };

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string NormalizePath(string? path)
    {
        var trimmed = path?.Trim() ?? "";
        if (trimmed.Length == 0) return "/";
        try
        {
            return new VfsPath("profile", trimmed.StartsWith('/') ? trimmed : "/" + trimmed).Path;
        }
        catch (ArgumentException)
        {
            return "/"; // climbs above the root
        }
    }
}
