using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels.Net;
using Tandem.App.Views.Net;
using Tandem.Core.Tests.Platform;
using Tandem.Core.Vfs;
using Tandem.Net;
using Tandem.Net.Connections;
using Tandem.Net.Remote;
using Tandem.Net.Ssh;

namespace Tandem.App.Tests.Views;

[Collection(LanguageCollection.Name)]
public sealed class ConnectionDialogTests : LanguageTestBase
{
    private const string TestMasterPassword = "synthetic vault password";
    private static readonly ConnectionProfile VaultProfile = new(Guid.NewGuid(), "Vault SFTP", NetProtocol.Sftp,
        "vault.test", 22, "test", NetAuth.PrivateKey);

    [AvaloniaFact]
    public async Task Manager_command_prompts_before_showing_profiles_and_unlock_refreshes_available_connections()
    {
        await using var app = await StartVaultAppAsync(configured: true);
        var commands = app.Main.Commands;
        Assert.True(commands.CanExecute(CommandIds.NetConnect));
        Assert.False(commands.CanExecute(CommandIds.NetConsole));
        Assert.Empty(app.Main.Net.Connections);

        var opening = commands.ExecuteAsync(CommandIds.NetConnect);
        var prompt = Assert.Single(app.Window.OwnedWindows);
        Assert.IsNotType<ConnectionListDialog>(prompt);
        try
        {
            var password = Named<TextBox>(prompt, "MasterPassword");
            Assert.NotEqual('\0', password.PasswordChar);
            Assert.Contains(prompt.GetVisualDescendants().OfType<TextBlock>(), block =>
                block.Text?.Contains(Path.Combine(app.StateDirectory, CredentialVault.FileName), StringComparison.Ordinal) == true);
            password.Text = "wrong synthetic password";
            Click(prompt, "Confirm");
            await TestApp.WaitUntil(() => Named<TextBlock>(prompt, "Error").IsVisible);
            Assert.Equal(Loc.Get("Vault_UnlockFailed"), Named<TextBlock>(prompt, "Error").Text);
            Assert.True(string.IsNullOrEmpty(password.Text));
            Assert.Empty(app.Main.Net.Connections);
            Assert.False(commands.CanExecute(CommandIds.NetConsole));
            Click(prompt, "Cancel");
            Assert.True(await opening);
            Assert.Empty(app.Window.OwnedWindows);

            opening = commands.ExecuteAsync(CommandIds.NetConnect);
            prompt = Assert.Single(app.Window.OwnedWindows);
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = app.Main.Services.Net!;
            service.Changed += HoldCommittedUnlock;
            try
            {
                Named<TextBox>(prompt, "MasterPassword").Text = TestMasterPassword;
                Click(prompt, "Confirm");
                await committed.Task.WaitAsync(TestContext.Current.CancellationToken);
                Click(prompt, "Cancel");
                Assert.True(prompt.IsVisible);
            }
            finally
            {
                service.Changed -= HoldCommittedUnlock;
                release.TrySetResult();
            }
            await TestApp.WaitUntil(() => app.Window.OwnedWindows.OfType<ConnectionListDialog>().Any());
            var manager = Assert.Single(app.Window.OwnedWindows.OfType<ConnectionListDialog>());
            Assert.Equal(VaultProfile, Assert.Single(app.Main.Net.Connections));
            Assert.True(commands.CanExecute(CommandIds.NetConsole));
            Assert.True(Named<Button>(manager, "Connect").IsEnabled);
            Assert.False(Named<Button>(manager, "VaultAction").IsVisible);

            Click(manager, "Edit");
            await TestApp.WaitUntil(() => manager.OwnedWindows.OfType<ConnectionEditDialog>().Any());
            var editor = Assert.Single(manager.OwnedWindows.OfType<ConnectionEditDialog>());
            Assert.True(string.IsNullOrEmpty(Named<TextBox>(editor, "Secret").Text));
            Assert.True(string.IsNullOrEmpty(Named<TextBox>(editor, "PrivateKeyPath").Text));
            Named<TextBox>(editor, "Name").Text = "Renamed vault SFTP";
            Click(editor, "Save");
            await TestApp.WaitUntil(() => app.Main.Net.Connections.Single().Name == "Renamed vault SFTP");
            Click(manager, "Close");
            Assert.True(await opening);

            void HoldCommittedUnlock(object? sender, EventArgs args)
            {
                committed.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        }
        finally
        {
            foreach (var window in app.Window.OwnedWindows.ToArray()) window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Manager_setup_requires_matching_masked_passwords_and_migrates_only_when_confirmed()
    {
        await using var app = await StartVaultAppAsync(configured: false);
        var opening = app.Main.Commands.ExecuteAsync(CommandIds.NetConnect);
        var manager = Assert.Single(app.Window.OwnedWindows.OfType<ConnectionListDialog>());
        var path = Path.Combine(app.StateDirectory, CredentialVault.FileName);
        try
        {
            Click(manager, "VaultAction");
            var prompt = Assert.Single(manager.OwnedWindows);
            var password = Named<TextBox>(prompt, "MasterPassword");
            var confirmation = Named<TextBox>(prompt, "MasterPasswordConfirmation");
            Assert.NotEqual('\0', password.PasswordChar);
            Assert.NotEqual('\0', confirmation.PasswordChar);
            password.Text = confirmation.Text = "test";
            Click(prompt, "Confirm");
            Assert.Equal(Loc.Get("Vault_PasswordTooShort"), Named<TextBlock>(prompt, "Error").Text);
            Assert.False(File.Exists(path));
            password.Text = TestMasterPassword;
            confirmation.Text = "different synthetic password";
            Click(prompt, "Confirm");
            Assert.Equal(Loc.Get("Vault_PasswordMismatch"), Named<TextBlock>(prompt, "Error").Text);
            Assert.False(File.Exists(path));
            Click(prompt, "Cancel");
            Assert.False(File.Exists(path));
            Assert.Empty(manager.OwnedWindows);

            Click(manager, "VaultAction");
            prompt = Assert.Single(manager.OwnedWindows);
            Named<TextBox>(prompt, "MasterPassword").Text = TestMasterPassword;
            Named<TextBox>(prompt, "MasterPasswordConfirmation").Text = TestMasterPassword;
            Click(prompt, "Confirm");
            await TestApp.WaitUntil(() => !Named<Button>(manager, "VaultAction").IsVisible);
            Assert.True(File.Exists(path));
            Assert.Equal("Legacy FTP", Assert.Single(app.Main.Net.Connections).Name);
            Click(manager, "Close");
            Assert.True(await opening);
        }
        finally
        {
            foreach (var window in manager.OwnedWindows.ToArray()) window.Close();
            if (manager.IsVisible) manager.Close();
        }
    }

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

    private static async Task<TestApp> StartVaultAppAsync(bool configured)
    {
        TestApp? app = null;
        app = await TestApp.StartAsync(configureServices: (services, directory) =>
        {
            var store = new ConnectionStore(directory);
            store.Save([new ConnectionProfile(Guid.NewGuid(), "Legacy FTP", NetProtocol.Ftp, "legacy.test", 21,
                "test", NetAuth.Anonymous)]);
            if (configured)
            {
                using var seed = new CredentialVault(directory);
                seed.CreateAsync(TestMasterPassword, [new VaultEntry(VaultProfile, null, [1, 2, 3], null)],
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            var dialogs = new NetDialogs(() => app?.Window);
            var secrets = new FakeSecretStore();
            var connections = new NetConnections(services.Providers, store, new TrustStore(directory), secrets,
                new DialogNetPrompts(dialogs), new Dictionary<NetProtocol, IRemoteSessionFactory>(),
                (_, _) => throw new InvalidOperationException("The stored key must be used."), new CredentialVault(directory));
            return services with { Net = connections, NetDialogs = dialogs, Secrets = secrets,
                Consoles = new SshConsoles(connections, new SshShellTransportFactory()) };
        });
        return app;
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
