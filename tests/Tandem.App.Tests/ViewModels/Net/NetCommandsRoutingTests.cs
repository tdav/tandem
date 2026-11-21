using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels.Net;
using Tandem.Core.Operations;
using Tandem.Core.Settings;
using Tandem.Core.Tests.Platform;
using Tandem.Core.Vfs;
using Tandem.Net;
using Tandem.Net.Connections;
using Tandem.Net.Remote;
using Tandem.Net.Tests.Remote;

namespace Tandem.App.Tests.ViewModels.Net;

[Collection(LanguageCollection.Name)]
public sealed class NetCommandsRoutingTests : LanguageTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ConnectionProfile Profile = new(Guid.NewGuid(), "Test_Server", NetProtocol.Sftp,
        "example.invalid", 22, "tester", NetAuth.Password, InitialPath: "/share");

    [AvaloniaFact]
    public async Task Connect_and_disconnect_are_registered_but_unavailable_without_network_services()
    {
        await using var app = await TestApp.StartAsync(show: false);
        Assert.True(app.Main.Commands.IsRegistered(CommandIds.NetConnect));
        Assert.True(app.Main.Commands.IsRegistered(CommandIds.NetDisconnect));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.NetConnect));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.NetDisconnect));
        Assert.False(app.Main.Net.HasOpenConnections);
    }

    [AvaloniaFact]
    public async Task Manager_connects_the_active_panel_and_disconnect_returns_every_server_tab()
    {
        var server = Server();
        var dialogs = new RoutingDialogs(Profile.Id);
        await using var app = await StartAsync(server, dialogs);
        var origin = VfsPath.Local("/other");
        await app.Left.NavigateToAsync(origin);

        Assert.True(await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+K")));
        Assert.Equal(Profile, Assert.IsType<ConnectionListViewModel>(dialogs.Opened).Selected);
        var connected = app.Left.CurrentPath;
        Assert.Equal("/share", connected.Path);
        Assert.Equal(Profile.Address + "/share", app.Left.DisplayPath);
        Assert.Contains(app.Left.Rows, row => row.Name == "hello.txt");
        Assert.True(app.Main.Commands.CanExecute(CommandIds.NetDisconnect));
        Assert.True(app.Main.Net.HasOpenConnections);

        await app.Left.GoUpAsync();
        Assert.True(app.Left.CurrentPath.IsRoot);
        await app.Left.GoUpAsync();
        Assert.Equal(origin, app.Left.CurrentPath);
        await app.Left.NavigateToAsync(connected);
        await app.Main.Workspace.Left.NewTabAsync();
        await app.Right.NavigateToAsync(connected);
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.NetDisconnect));

        var tabs = app.Main.Workspace.Left.Tabs.Concat(app.Main.Workspace.Right.Tabs);
        Assert.All(tabs, tab => Assert.Equal(origin, tab.Panel.CurrentPath));
        Assert.False(app.Main.Commands.CanExecute(CommandIds.NetDisconnect));
        Assert.False(app.Main.Net.HasOpenConnections);
        Assert.Equal(0, server.OpenNow);
    }

    [AvaloniaFact]
    public async Task Escape_cancels_a_connection_in_progress()
    {
        var server = Server();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldConnect = gate.Task;
        await using var app = await StartAsync(server);
        var opening = app.Main.Net.ConnectAsync(Profile.Id);
        try
        {
            await TestApp.WaitUntil(() => server.Log.Any(entry => entry.EndsWith(" Connect", StringComparison.Ordinal)));
            Assert.True(app.Main.Net.IsConnecting);
            Assert.Equal(Loc.Format("Net_Connecting", Profile.Address), app.Left.Notice);

            Assert.True(await app.Main.HandleKeyAsync(KeyChord.Parse("Escape")));
            await opening.WaitAsync(Ct);

            Assert.False(app.Main.Net.IsConnecting);
            Assert.Null(app.Left.Notice);
            Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
            Assert.Equal(0, server.OpenNow);
            Assert.Empty(app.Dialogs.Messages);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [AvaloniaTheory]
    [InlineData("authentication", "Net_AuthenticationFailed")]
    [InlineData("declined", null)]
    [InlineData("trust", "Net_TrustRefused")]
    public async Task Connect_reports_a_server_refusal_but_respects_prompt_cancellation(string failure, string? key)
    {
        var server = Server();
        server.FailOn("Connect", "/", () => failure switch
        {
            "authentication" => new NetAuthenticationException("test authentication detail"),
            "declined" => new NetAuthenticationException("test credential cancellation", declined: true),
            _ => new NetTrustException("test host key detail"),
        });
        await using var app = await StartAsync(server);

        await app.Main.Net.ConnectAsync(Profile.Id);

        Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
        Assert.Null(app.Left.Notice);
        Assert.Empty(app.Failures);
        if (key is null) Assert.Empty(app.Dialogs.Messages);
        else Assert.Equal(Loc.Get(key), Assert.Single(app.Dialogs.Messages));
    }

    [AvaloniaFact]
    public async Task Disconnect_refuses_while_a_queued_operation_uses_the_server()
    {
        var server = Server();
        await using var app = await StartAsync(server);
        await app.Main.Net.ConnectAsync(Profile.Id);
        var path = app.Left.CurrentPath;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.HoldCalls = hold.Task;
        var services = app.Main.Services;
        var copy = new CopyOperation(services.Providers, [path.Combine("hello.txt")], VfsPath.Local("/other"),
            new(), app.Main.Interaction);
        var completed = services.Operations.Enqueue(copy, runImmediately: true);
        try
        {
            Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.NetDisconnect));
            Assert.Equal(Loc.Get("Net_ConnectionInUse"), Assert.Single(app.Dialogs.Messages));
            Assert.Equal(path, app.Left.CurrentPath);
            Assert.True(app.Main.Net.HasOpenConnections);
        }
        finally
        {
            hold.TrySetResult();
            await completed.WaitAsync(Ct);
        }
    }

    [AvaloniaFact]
    public async Task Closing_network_services_closes_open_server_sessions()
    {
        var server = Server();
        await using var app = await StartAsync(server);
        await app.Main.Net.ConnectAsync(Profile.Id);
        Assert.True(server.OpenNow > 0);

        Assert.True(await app.Main.ConfirmCloseAsync());

        Assert.Equal(0, server.OpenNow);
        Assert.False(app.Main.Net.HasOpenConnections);
    }

    [AvaloniaFact]
    public async Task Sidebar_updates_when_a_profile_changes_and_click_connects_the_active_panel()
    {
        var server = Server();
        await using var app = await StartAsync(server, show: true);
        app.Main.ToggleSidebar();
        app.Main.Workspace.SwitchPanel();
        var changed = Profile with { Name = "Renamed_Server" };
        await app.Main.Services.Net!.SaveAsync(changed, password: null, Ct);
        app.Pump();
        var items = app.Find<ItemsControl>("ConnectionItems");
        var button = Assert.Single(items.GetVisualDescendants().OfType<Button>());
        Assert.Equal(changed, button.CommandParameter);
        Assert.Equal(changed.Address, ToolTip.GetTip(button));
        Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == changed.Name);

        app.Click(button);

        await TestApp.WaitUntil(() => app.Right.CurrentPath.Path == "/share");
        Assert.Equal(VfsPath.Local(TestApp.Home), app.Left.CurrentPath);
        Assert.Equal(changed.Address + "/share", app.Right.DisplayPath);
    }

    private static FakeRemoteServer Server()
    {
        var server = new FakeRemoteServer();
        server.AddFile("/share/hello.txt", "hello");
        return server;
    }

    private static Task<TestApp> StartAsync(FakeRemoteServer server, RoutingDialogs? dialogs = null, bool show = false)
    {
        dialogs ??= new RoutingDialogs(null);
        return TestApp.StartAsync(show: show, configureServices: (services, directory) =>
        {
            var store = new ConnectionStore(directory);
            store.Save([Profile]);
            var secrets = new FakeSecretStore();
            var connections = new NetConnections(services.Providers, store, new TrustStore(directory), secrets,
                new DialogNetPrompts(dialogs), new Dictionary<NetProtocol, IRemoteSessionFactory>
                {
                    [NetProtocol.Sftp] = server.AsFactory(),
                }, (_, _) => throw new InvalidOperationException("No private key should be read."));
            return services with { Net = connections, NetDialogs = dialogs, Secrets = secrets };
        });
    }

    private sealed class RoutingDialogs(Guid? selected) : INetDialogs
    {
        public ConnectionListViewModel? Opened { get; private set; }

        public Task<Guid?> ShowConnectionsAsync(ConnectionListViewModel list)
        {
            Opened = list;
            return Task.FromResult(selected);
        }

        public Task<ConnectionEdit?> EditConnectionAsync(ConnectionEditViewModel edit, Window? owner = null) =>
            throw new InvalidOperationException("The connection editor was not expected.");

        public Task<SecretAnswer?> AskSecretAsync(string name, string address, SecretKind kind, bool previousWasWrong,
            CancellationToken ct) => throw new InvalidOperationException("An authentication prompt was not expected.");

        public Task<bool> ConfirmHostKeyAsync(string address, HostKeyInfo key, HostKeyInfo? previous, CancellationToken ct) =>
            throw new InvalidOperationException("A host key question was not expected.");

        public Task<bool> ConfirmCertificateAsync(string address, CertificateInfo certificate, CancellationToken ct) =>
            throw new InvalidOperationException("A certificate question was not expected.");

        public Task<bool> ConfirmDeleteConnectionAsync(string name, Window? owner = null) =>
            throw new InvalidOperationException("A deletion question was not expected.");
    }
}
