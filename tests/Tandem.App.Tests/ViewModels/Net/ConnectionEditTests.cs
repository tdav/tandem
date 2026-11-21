using Tandem.App.Resources;
using Tandem.App.ViewModels.Net;
using Tandem.Net;

namespace Tandem.App.Tests.ViewModels.Net;

[Collection(LanguageCollection.Name)]
public sealed class ConnectionEditTests : LanguageTestBase
{
    [Fact]
    public void Protocol_defaults_follow_the_selection_until_a_custom_port_is_entered()
    {
        var edit = new ConnectionEditViewModel();
        Assert.Equal("22", edit.Port);
        edit.Protocol = NetProtocol.FtpsImplicit;
        Assert.Equal("990", edit.Port);
        edit.Port = "2121";
        edit.Protocol = NetProtocol.Ftp;
        Assert.Equal("2121", edit.Port);

        edit.Auth = NetAuth.Anonymous;
        edit.Protocol = NetProtocol.Sftp;
        Assert.Equal(NetAuth.Password, edit.Auth);
        Assert.DoesNotContain(NetAuth.Anonymous, edit.AuthenticationMethods);
    }

    [Fact]
    public void Invalid_port_and_missing_key_are_rejected()
    {
        var edit = new ConnectionEditViewModel { Host = "server.test", Port = "65536" };
        Assert.Null(edit.Build(out var error));
        Assert.Equal(Loc.Get("Connection_InvalidPort"), error);
        edit.Port = "22";
        edit.Auth = NetAuth.PrivateKey;
        Assert.Null(edit.Build(out error));
        Assert.Equal(Loc.Get("Connection_KeyRequired"), error);
    }

    [Fact]
    public void Blank_remembered_secret_preserves_it_and_unchecking_removes_it()
    {
        var profile = new ConnectionProfile(Guid.NewGuid(), "Example", NetProtocol.Sftp, "server.test", 22,
            "tester", NetAuth.Password);
        var edit = new ConnectionEditViewModel(profile, hasSavedSecret: true);

        Assert.Null(edit.Build(out _)!.Password);
        edit.Secret = "in-memory-test-token";
        var update = edit.Build(out _)!;
        Assert.Equal(edit.Secret, update.Password);
        Assert.DoesNotContain(edit.Secret, update.ToString());

        edit.Remember = false;
        Assert.Equal("", edit.Build(out _)!.Password);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Every_protocol_and_authentication_choice_has_a_label(string language)
    {
        Loc.SetLanguage(language);
        foreach (var protocol in Enum.GetValues<NetProtocol>())
            Assert.NotEqual($"NetProtocol_{protocol}", Loc.Enum(protocol));
        foreach (var authentication in Enum.GetValues<NetAuth>())
            Assert.NotEqual($"NetAuth_{authentication}", Loc.Enum(authentication));
    }
}
