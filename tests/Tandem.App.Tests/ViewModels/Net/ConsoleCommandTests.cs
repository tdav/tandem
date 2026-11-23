using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Tandem.App.Commands;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.App.ViewModels.Net;
using Tandem.Core.Tests.Platform;
using Tandem.Core.Vfs;
using Tandem.Net;
using Tandem.Net.Connections;
using Tandem.Net.Remote;
using Tandem.Net.Ssh;
using Tandem.Net.Tests.Remote;

namespace Tandem.App.Tests.ViewModels.Net;

[Collection(LanguageCollection.Name)]
public sealed class ConsoleCommandTests : LanguageTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Show_open_console_brings_existing_session_forward_and_disables_after_close()
    {
        const string showOpen = CommandIds.NetShowConsole;
        await using var app = await ConsoleApp.StartAsync();
        Assert.True(app.Main.Commands.IsRegistered(showOpen));
        Assert.False(app.Main.Commands.CanExecute(showOpen));
        await app.SaveAsync("Shell", NetProtocol.Ssh);
        var terminal = await app.OpenConsoleAsync();
        Assert.True(await app.Main.Commands.ExecuteAsync(showOpen));
        Assert.Same(terminal, Assert.Single(app.Tools.Open));
        Assert.Same(terminal, app.Tools.Shown.Last());

        app.Tools.OnDialog = _ => true;
        Assert.True(await app.Tools.CloseAsync(terminal));
        Assert.False(app.Main.Commands.CanExecute(showOpen));
    }

    [AvaloniaTheory]
    [InlineData(CommandIds.NetConnectSsh)]
    [InlineData(CommandIds.NetConnect)]
    [InlineData(null)]
    public async Task A_saved_ssh_profile_opens_only_a_terminal(string? command)
    {
        await using var app = await ConsoleApp.StartAsync();
        var local = app.Main.Workspace.ActivePanel.CurrentPath;
        var sftp = await app.SaveAsync("Files");
        var profile = await app.SaveAsync("Shell", NetProtocol.Ssh);
        app.Dialogs.Selected = profile.Id;

        if (command is null) await app.Main.Net.OpenSavedConnectionCommand.ExecuteAsync(profile);
        else Assert.True(await app.Main.Commands.ExecuteAsync(command));

        if (command == CommandIds.NetConnectSsh) Assert.Equal([profile], app.Dialogs.Opened!.Profiles);
        if (command == CommandIds.NetConnect) Assert.Equal([sftp, profile], app.Dialogs.Opened!.Profiles);
        var terminal = Assert.IsType<TerminalViewModel>(Assert.Single(app.Tools.Open));
        await terminal.StartAsync(80, 24);
        Assert.Equal(profile, app.Transport.Profile);
        Assert.Equal(TerminalState.Connected, terminal.State);
        Assert.Null(app.Files.Credentials);
        Assert.Equal(local, app.Main.Workspace.ActivePanel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Ssh_menu_creates_a_saved_profile_with_the_ssh_defaults()
    {
        await using var app = await ConsoleApp.StartAsync();
        app.Dialogs.CreateProfile = true;

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.NetConnectSsh));

        var model = Assert.IsType<ConnectionEditViewModel>(app.Dialogs.Editor);
        Assert.Equal([NetProtocol.Ssh], model.AvailableProtocols);
        Assert.Equal([NetAuth.Password, NetAuth.PrivateKey], model.AuthenticationMethods);
        var profile = Assert.Single(app.Main.Net.Connections);
        Assert.Equal(NetProtocol.Ssh, profile.Protocol);
        Assert.Equal("ssh://tester@host.invalid", profile.Address);
        Assert.Equal(22, profile.Port);
        Assert.Empty(app.Tools.Open);
    }

    [AvaloniaFact]
    public async Task Console_command_needs_an_ssh_capable_profile_and_skips_the_picker_for_one_profile()
    {
        await using var app = await ConsoleApp.StartAsync();
        Assert.False(app.Main.Commands.CanExecute(CommandIds.NetConsole));
        await app.SaveAsync("FTP", NetProtocol.Ftp);
        Assert.False(app.Main.Commands.CanExecute(CommandIds.NetConsole));
        var profile = await app.SaveAsync("SSH");
        Assert.True(app.Main.Commands.CanExecute(CommandIds.NetConsole));

        var terminal = await app.OpenConsoleAsync();
        Assert.Empty(app.Tools.Dialogs);
        Assert.Equal(profile.Id, app.Transport.Profile!.Id);
        Assert.Equal(TerminalState.Connected, terminal.State);
    }

    [AvaloniaFact]
    public async Task Local_panel_picker_excludes_ftp_and_opens_the_chosen_connection()
    {
        await using var app = await ConsoleApp.StartAsync();
        await app.SaveAsync("FTP", NetProtocol.Ftp);
        var first = await app.SaveAsync("First SSH");
        var second = await app.SaveAsync("Second SSH");
        app.Tools.OnDialog = model =>
        {
            var picker = Assert.IsType<ConsolePickViewModel>(model);
            Assert.Equal(new[] { first, second }, picker.Profiles);
            return second.Id;
        };

        await app.OpenConsoleAsync();

        Assert.Single(app.Tools.Dialogs);
        Assert.Equal(second.Id, app.Transport.Profile!.Id);
    }

    [AvaloniaFact]
    public async Task Sftp_panel_bypasses_picker_and_uses_its_accepted_credentials_for_the_console()
    {
        await using var app = await ConsoleApp.StartAsync();
        var profile = await app.SaveAsync("Active SSH");
        await app.SaveAsync("Other SSH");
        var path = await app.Connections.ConnectAsync(profile.Id, VfsPath.Local(TestApp.Home), Ct);
        await app.Main.Workspace.ActivePanel.NavigateToAsync(path);
        Assert.Same(app.Connections.Trust, app.Files.Trust);
        Assert.NotNull(app.Files.Credentials);
        await CredentialLoop.RunAsync(profile, app.Files.Credentials, _ => Task.CompletedTask, Ct);
        Assert.Equal(1, app.Prompts.SecretQuestions);

        await app.OpenConsoleAsync();

        Assert.Empty(app.Tools.Dialogs);
        Assert.Equal(profile.Id, app.Transport.Profile!.Id);
        Assert.Same(app.Files.Credentials, app.Transport.Credentials);
        Assert.Same(app.Files.Trust, app.Transport.Trust);
        Assert.Equal("scripted password", app.Transport.ReceivedPassword);
        Assert.Equal(1, app.Prompts.SecretQuestions);
    }

    [AvaloniaFact]
    public async Task Closing_a_live_console_and_quitting_ask_for_confirmation()
    {
        await using var app = await ConsoleApp.StartAsync();
        await app.SaveAsync("SSH");
        var terminal = await app.OpenConsoleAsync();
        var agree = false;
        app.Tools.OnDialog = question =>
        {
            Assert.IsType<ConfirmModel>(question);
            return agree;
        };

        Assert.False(await app.Tools.CloseAsync(terminal));
        Assert.False(await app.Main.ConfirmCloseAsync());
        Assert.Single(app.Tools.Open);
        Assert.False(app.Transport.Disposed);
        agree = true;
        Assert.True(await app.Main.ConfirmCloseAsync());
        Assert.Empty(app.Tools.Open);
        Assert.True(app.Transport.Disposed);
        Assert.Equal(3, app.Tools.Dialogs.Count);
    }

    private sealed class ConsoleApp : IAsyncDisposable
    {
        private TestApp _app = null!;

        public MainViewModel Main => _app.Main;
        public NetConnections Connections => Main.Services.Net!;
        public ScriptedToolWindows Tools => _app.ScriptedToolWindows;
        public ScriptedPrompts Prompts { get; } = new();
        public ConsoleDialogs Dialogs { get; } = new();
        public RecordingFileFactory Files { get; } = new();
        public RecordingTransportFactory Transport { get; } = new();

        public static async Task<ConsoleApp> StartAsync()
        {
            var app = new ConsoleApp();
            app._app = await TestApp.StartAsync(scriptedToolWindows: true, configureServices: (services, directory) =>
            {
                var secrets = new FakeSecretStore();
                var connections = new NetConnections(services.Providers, new ConnectionStore(directory),
                    new TrustStore(directory), secrets, app.Prompts,
                    new Dictionary<NetProtocol, IRemoteSessionFactory> { [NetProtocol.Sftp] = app.Files },
                    File.ReadAllBytesAsync);
                return services with
                {
                    Net = connections,
                    NetDialogs = app.Dialogs,
                    Secrets = secrets,
                    Consoles = new SshConsoles(connections, app.Transport),
                };
            });
            return app;
        }

        public async Task<ConnectionProfile> SaveAsync(string name, NetProtocol protocol = NetProtocol.Sftp)
        {
            var profile = new ConnectionProfile(Guid.NewGuid(), name, protocol, "test.example.invalid",
                ConnectionProfile.DefaultPort(protocol), "user", NetAuth.Password);
            await Connections.SaveAsync(profile, null, Ct);
            return profile;
        }

        public async Task<TerminalViewModel> OpenConsoleAsync()
        {
            Assert.True(await Main.Commands.ExecuteAsync(CommandIds.NetConsole));
            var terminal = Assert.IsType<TerminalViewModel>(Assert.Single(Tools.Open));
            await terminal.StartAsync(80, 24);
            return terminal;
        }

        public ValueTask DisposeAsync()
        {
            Tools.OnDialog = _ => true;
            return _app.DisposeAsync();
        }
    }

    private sealed class ConsoleDialogs : INetDialogs
    {
        public Guid? Selected { get; set; }
        public bool CreateProfile { get; set; }
        public ConnectionListViewModel? Opened { get; private set; }
        public ConnectionEditViewModel? Editor { get; private set; }
        public async Task<Guid?> ShowConnectionsAsync(ConnectionListViewModel list)
        {
            Opened = list;
            if (CreateProfile) await list.EditAsync(create: true);
            return Selected;
        }

        public Task<ConnectionEdit?> EditConnectionAsync(ConnectionEditViewModel edit, Window? owner = null)
        {
            Editor = edit;
            edit.Host = "host.invalid";
            edit.User = "tester";
            return Task.FromResult(edit.Build(out _));
        }
        public Task<bool> OpenVaultAsync(ConnectionVaultViewModel vault, Window? owner = null) => throw new NotSupportedException();
        public Task<bool> ConfirmDeleteConnectionAsync(string name, Window? owner = null) => throw new NotSupportedException();
        public Task<SecretAnswer?> AskSecretAsync(string name, string address, SecretKind kind, bool previousWasWrong,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ConfirmHostKeyAsync(string address, HostKeyInfo key, HostKeyInfo? previous, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> ConfirmCertificateAsync(string address, CertificateInfo certificate, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedPrompts : INetPrompts
    {
        private int _secretQuestions;
        public int SecretQuestions => Volatile.Read(ref _secretQuestions);

        public Task<SecretAnswer?> AskSecretAsync(ConnectionProfile profile, SecretKind kind, bool previousWasWrong,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _secretQuestions);
            return Task.FromResult<SecretAnswer?>(new SecretAnswer("scripted password", false));
        }

        public Task<bool> ConfirmHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, HostKeyInfo? previous,
            CancellationToken ct) => Task.FromResult(false);

        public Task<bool> ConfirmCertificateAsync(ConnectionProfile profile, CertificateInfo certificate,
            CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class RecordingFileFactory : IRemoteSessionFactory
    {
        private readonly FakeRemoteServer _server = new();
        public ICredentialSource? Credentials { get; private set; }
        public ITrustDecisions? Trust { get; private set; }

        public IRemoteSession Create(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust)
        {
            Credentials = credentials;
            Trust = trust;
            return _server.NewSession();
        }
    }

    private sealed class RecordingTransportFactory : ITerminalTransportFactory
    {
        public ConnectionProfile? Profile { get; private set; }
        public ICredentialSource? Credentials { get; private set; }
        public ITrustDecisions? Trust { get; private set; }
        public string? ReceivedPassword { get; private set; }
        public bool Disposed { get; private set; }

        public ITerminalTransport Create(ConnectionProfile profile, ICredentialSource credentials, ITrustDecisions trust)
        {
            Profile = profile;
            Credentials = credentials;
            Trust = trust;
            return new Transport(this, profile, credentials);
        }

        private sealed class Transport(RecordingTransportFactory owner, ConnectionProfile profile,
            ICredentialSource credentials) : ITerminalTransport
        {
            public async Task ConnectAsync(int columns, int rows, CancellationToken ct)
            {
                await CredentialLoop.RunAsync(profile, credentials, accepted =>
                {
                    owner.ReceivedPassword = accepted.Password;
                    return Task.CompletedTask;
                }, ct);
            }

            public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }

            public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
            public Task ResizeAsync(int columns, int rows, CancellationToken ct) => Task.CompletedTask;
            public ValueTask DisposeAsync()
            {
                owner.Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
