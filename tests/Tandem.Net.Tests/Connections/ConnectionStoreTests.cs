using System.Text.Json;
using System.Runtime.Versioning;
using Tandem.Core.Tests.Vfs;
using Tandem.Net.Connections;

namespace Tandem.Net.Tests.Connections;

[SupportedOSPlatform("macos")]
public sealed class ConnectionStoreTests
{
    [Fact]
    public void Saves_only_profile_fields_and_loads_normalized_unique_profiles()
    {
        using var directory = new TempDir();
        var store = new ConnectionStore(directory.Path);
        var profile = new ConnectionProfile(Guid.NewGuid(), "  Work  ", NetProtocol.Sftp, " host ", 0, " user ", NetAuth.Password);
        store.Save([profile, profile with { Name = "Duplicate" }]);

        Assert.Equal(profile.Normalized(), Assert.Single(store.Load()));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, ConnectionStore.FileName)));
        var fields = doc.RootElement.GetProperty("Profiles")[0].EnumerateObject().Select(field => field.Name);
        Assert.Equal(new[] { "Id", "Name", "Protocol", "Host", "Port", "User", "Auth", "PrivateKeyPath",
            "InitialPath", "PassiveMode", "NameEncoding" }, fields);
    }

    [Fact]
    public void Invalid_file_is_set_aside()
    {
        using var directory = new TempDir();
        var path = Path.Combine(directory.Path, ConnectionStore.FileName);
        File.WriteAllText(path, "broken");
        Assert.Empty(new ConnectionStore(directory.Path).Load());
        Assert.False(File.Exists(path));
        Assert.Equal("broken", File.ReadAllText(path + ".bad"));
    }
}
