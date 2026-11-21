using System.Text.Json;

namespace Tandem.Net.Tests.Remote;

public sealed class ConnectionProfileTests
{
    private static ConnectionProfile Profile(NetProtocol protocol, int port, string host = "host", string user = "user",
        NetAuth auth = NetAuth.Password) =>
        new(Guid.NewGuid(), "name", protocol, host, port, user, auth);

    [Theory]
    [InlineData(NetProtocol.Sftp, 22, "sftp://user@host")]
    [InlineData(NetProtocol.Sftp, 2222, "sftp://user@host:2222")]
    [InlineData(NetProtocol.Ftp, 21, "ftp://user@host")]
    [InlineData(NetProtocol.Ftp, 2121, "ftp://user@host:2121")]
    [InlineData(NetProtocol.FtpsExplicit, 21, "ftpes://user@host")]
    [InlineData(NetProtocol.FtpsImplicit, 990, "ftps://user@host")]
    [InlineData(NetProtocol.FtpsImplicit, 21, "ftps://user@host:21")]
    public void Address_shows_the_port_only_when_it_is_not_the_default(NetProtocol protocol, int port, string address)
    {
        var profile = Profile(protocol, port);

        Assert.Equal(address, profile.Address);
        Assert.Equal(address, profile.VolumeKey);
    }

    [Theory]
    [InlineData(NetProtocol.Ftp, 21)]
    [InlineData(NetProtocol.FtpsExplicit, 21)]
    [InlineData(NetProtocol.FtpsImplicit, 990)]
    [InlineData(NetProtocol.Sftp, 22)]
    public void Default_ports(NetProtocol protocol, int port) =>
        Assert.Equal(port, ConnectionProfile.DefaultPort(protocol));

    [Fact]
    public void Volume_key_is_the_address_in_lower_case_and_an_ipv6_host_is_bracketed()
    {
        Assert.Equal("sftp://user@my.host", Profile(NetProtocol.Sftp, 22, "My.Host", "User").VolumeKey);
        Assert.Equal("sftp://User@My.Host", Profile(NetProtocol.Sftp, 22, "My.Host", "User").Address);
        Assert.Equal("ftp://u@[::1]:2121", Profile(NetProtocol.Ftp, 2121, "::1", "u").Address);
        Assert.Equal("ftp://host", Profile(NetProtocol.Ftp, 21, user: "").Address);
    }

    [Fact]
    public void Normalized_fixes_port_path_and_anonymous_user()
    {
        var profile = new ConnectionProfile(Guid.NewGuid(), "  My server ", NetProtocol.Ftp, "  ftp.test ", 0, " bob ",
            NetAuth.Anonymous, PrivateKeyPath: "  ", InitialPath: " pub/./files/ ", NameEncoding: " ");

        var normalized = profile.Normalized();

        Assert.Equal("My server", normalized.Name);
        Assert.Equal("ftp.test", normalized.Host);
        Assert.Equal(21, normalized.Port);
        Assert.Equal("anonymous", normalized.User);
        Assert.Null(normalized.PrivateKeyPath);
        Assert.Equal("/pub/files", normalized.InitialPath);
        Assert.Null(normalized.NameEncoding);
        Assert.Equal(profile.Id, normalized.Id);

        Assert.Equal(22, (Profile(NetProtocol.Sftp, 70000) with { InitialPath = "" }).Normalized().Port);
        Assert.Equal("/", (Profile(NetProtocol.Sftp, 22) with { InitialPath = "" }).Normalized().InitialPath);
        Assert.Equal("/", (Profile(NetProtocol.Sftp, 22) with { InitialPath = "/a/../.." }).Normalized().InitialPath);
        Assert.Equal("user", Profile(NetProtocol.Sftp, 22, user: " user ").Normalized().User);
        Assert.Equal(2222, Profile(NetProtocol.Sftp, 2222).Normalized().Port);
    }

    [Fact]
    public void Json_holds_only_the_constructor_parameters()
    {
        var profile = Profile(NetProtocol.Sftp, 2222) with { PrivateKeyPath = "/keys/id", NameEncoding = "cp1251" };

        var json = JsonSerializer.Serialize(profile);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["Id", "Name", "Protocol", "Host", "Port", "User", "Auth", "PrivateKeyPath", "InitialPath", "PassiveMode",
                "NameEncoding"],
            document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(profile, JsonSerializer.Deserialize<ConnectionProfile>(json));
    }

    [Fact]
    public void Credentials_do_not_print_their_secrets()
    {
        var text = new NetCredentials("hunter2", [1, 2, 3], "open sesame").ToString();

        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("open sesame", text);
        Assert.Contains("***", text);
    }
}
