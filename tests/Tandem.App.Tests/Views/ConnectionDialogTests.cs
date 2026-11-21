using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels.Net;
using Tandem.App.Views.Net;
using Tandem.Core.Tests.Platform;
using Tandem.Core.Vfs;
using Tandem.Net;
using Tandem.Net.Connections;
using Tandem.Net.Remote;

namespace Tandem.App.Tests.Views;

[Collection(LanguageCollection.Name)]
public sealed class ConnectionDialogTests : LanguageTestBase
{
    [AvaloniaFact]
    public async Task Real_manager_creates_and_edits_a_profile_with_a_masked_password()
    {
        await using var app = await TestApp.StartAsync();
        var dialogs = new NetDialogs(() => app.Window);
        var secretStore = new FakeSecretStore();
        await using var connections = new NetConnections(new ProviderRegistry(),
            new ConnectionStore(app.StateDirectory), new TrustStore(app.StateDirectory), secretStore,
            new DialogNetPrompts(dialogs), new Dictionary<NetProtocol, IRemoteSessionFactory>(), File.ReadAllBytesAsync);
        var model = new ConnectionListViewModel(connections, dialogs);

        var selection = dialogs.ShowConnectionsAsync(model);
        var manager = Assert.Single(app.Window.OwnedWindows.OfType<ConnectionListDialog>());
        Assert.False(Named<Button>(manager, "Connect").IsEnabled);
        try
        {
            Click(manager, "Create");
            var editor = Assert.Single(manager.OwnedWindows.OfType<ConnectionEditDialog>());
            Assert.Equal("22", Named<TextBox>(editor, "Port").Text);
            Click(editor, "Save");
            Assert.Equal(Loc.Get("Connection_HostRequired"), Named<TextBlock>(editor, "Error").Text);

            SelectTag(editor, "Protocol", NetProtocol.FtpsImplicit);
            Assert.Equal("990", Named<TextBox>(editor, "Port").Text);
            Assert.False(Named<TextBlock>(editor, "PlainFtpWarning").IsVisible);
            SelectTag(editor, "Protocol", NetProtocol.Ftp);
            Assert.Equal("21", Named<TextBox>(editor, "Port").Text);
            Assert.True(Named<TextBlock>(editor, "PlainFtpWarning").IsVisible);
            Assert.Equal(new object?[] { NetAuth.Password, NetAuth.Anonymous },
                Named<ComboBox>(editor, "Auth").Items.OfType<ComboBoxItem>().Select(item => item.Tag));

            Named<TextBox>(editor, "Host").Text = "server.example.test";
            Named<TextBox>(editor, "Name").Text = "Work files";
            Named<TextBox>(editor, "User").Text = "tandem";
            var passwordBox = Named<TextBox>(editor, "Secret");
            Assert.NotEqual('\0', passwordBox.PasswordChar);
            passwordBox.Text = "remember this password";
            Named<CheckBox>(editor, "Remember").IsChecked = true;
            Click(editor, "Save");
            await TestApp.WaitUntil(() => model.Selected is not null);

            var profile = Assert.Single(connections.Profiles);
            Assert.Equal("Work files", profile.Name);
            Assert.Equal("ftp://tandem@server.example.test", profile.Address);
            var passwordKey = NetConnections.SecretKey(profile.Id, SecretKind.Password);
            Assert.Equal("remember this password", secretStore.Secrets[passwordKey]);

            Click(manager, "Edit");
            await TestApp.WaitUntil(() => manager.OwnedWindows.OfType<ConnectionEditDialog>().Any());
            editor = Assert.Single(manager.OwnedWindows.OfType<ConnectionEditDialog>());
            Assert.True(string.IsNullOrEmpty(Named<TextBox>(editor, "Secret").Text));
            Assert.True(Named<CheckBox>(editor, "Remember").IsChecked);
            Named<TextBox>(editor, "Name").Text = "Renamed";
            Click(editor, "Save");
            await TestApp.WaitUntil(() => model.Selected?.Name == "Renamed");
            Assert.Equal("remember this password", secretStore.Secrets[passwordKey]);
            Click(manager, "Connect");
            Assert.Equal(profile.Id, await selection);
        }
        finally
        {
            foreach (var owned in manager.OwnedWindows.ToArray()) owned.Close();
            if (manager.IsVisible) manager.Close();
        }
    }

    [AvaloniaFact]
    public async Task Background_secret_prompt_masks_input_and_says_when_previous_one_was_wrong()
    {
        await using var app = await TestApp.StartAsync();
        var prompts = new DialogNetPrompts(new NetDialogs(() =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            return app.Window;
        }));
        var ct = TestContext.Current.CancellationToken;
        var profile = new ConnectionProfile(Guid.NewGuid(), "Shell", NetProtocol.Sftp, "host.test", 22,
            "user", NetAuth.Password);
        var pending = Task.Run(() => prompts.AskSecretAsync(profile, SecretKind.Password, true, ct), ct);
        await TestApp.WaitUntil(() => app.Window.OwnedWindows.Count != 0);
        var dialog = Assert.Single(app.Window.OwnedWindows);
        try
        {
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == Loc.Get("Connection_SecretRefused"));
            var password = Named<TextBox>(dialog, "Secret");
            Assert.NotEqual('\0', password.PasswordChar);
            password.Text = "new secret";
            Named<CheckBox>(dialog, "Remember").IsChecked = true;
            Click(dialog, "Confirm");
            Assert.Equal(new SecretAnswer("new secret", true), await pending);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task Changed_host_key_shows_both_fingerprints_and_defaults_to_cancel()
    {
        await using var app = await TestApp.StartAsync();
        var dialogs = new NetDialogs(() => app.Window);
        var pending = dialogs.ConfirmHostKeyAsync("sftp://host.test", new HostKeyInfo("ssh-ed25519", "SHA256:new"),
            new HostKeyInfo("ssh-ed25519", "SHA256:old"), TestContext.Current.CancellationToken);
        var dialog = Assert.Single(app.Window.OwnedWindows);
        try
        {
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text?.Contains("SHA256:old", StringComparison.Ordinal) == true &&
                text.Text.Contains("SHA256:new", StringComparison.Ordinal));
            Assert.True(Named<Button>(dialog, "Cancel").IsDefault);
            Assert.False(Named<Button>(dialog, "Confirm").IsDefault);
            Click(dialog, "Cancel");
            Assert.False(await pending);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
        }
    }

    private static void SelectTag(Window window, string control, object value)
    {
        var box = Named<ComboBox>(window, control);
        box.SelectedItem = Assert.Single(box.Items.OfType<ComboBoxItem>(), item => Equals(item.Tag, value));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window dialog, string button)
    {
        Named<Button>(dialog, button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Window dialog, string name) where T : Control =>
        Assert.Single(dialog.GetVisualDescendants().OfType<T>(), child => child.Name == name);
}
