using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.App.ViewModels.Editing;
using Tandem.App.Views.Editing;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The macOS shell commands on the whole app over the in-memory "local disk", with recording fakes for the
/// shell, Quick Look and metadata: nothing real is launched or touched. Both panels start at /home (.., docs, src,
/// README.md, notes.txt, report.pdf).</summary>
[Collection(LanguageCollection.Name)]
public class ShellCommandsTests : LanguageTestBase
{
    private const string Remote = "remote";

    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static void Cursor(PanelViewModel panel, string name) => panel.CursorIndex = IndexOf(panel, name);

    private static InMemoryFileSystem RemoteFileSystem()
    {
        var fs = new InMemoryFileSystem(Remote);
        fs.AddFile("/shared/a.txt", "a");
        fs.AddDirectory("/shared/dir");
        return fs;
    }

    private static Task<TestApp> StartAsync(Func<TimeSpan, CancellationToken, Task>? quickLookDelay = null,
        bool withMetadata = true) =>
        TestApp.StartAsync(show: false, quickLookDelay: quickLookDelay, extraProviders: [RemoteFileSystem()],
            withMetadata: withMetadata);

    /// <summary>A delay the test completes: every call is recorded with its token.</summary>
    private sealed class ManualDelay
    {
        public List<(TaskCompletionSource Done, CancellationToken Ct)> Calls { get; } = [];

        public Task Wait(TimeSpan delay, CancellationToken ct)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => done.TrySetCanceled(ct));
            Calls.Add((done, ct));
            return done.Task;
        }

        public void CompleteLast() => Calls[^1].Done.TrySetResult();
    }

    private static readonly string[] LocalOnly =
    [
        CommandIds.FileQuickLook, CommandIds.ShellReveal, CommandIds.ShellOpenTerminal, CommandIds.ShellOpenWith,
    ];

    // ---- Opening ----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Enter_on_file_opens_default_app()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");

        await app.Left.ActivateAsync();

        Assert.Equal([("open", "/home/notes.txt")], app.Shell.Calls);
        Assert.Empty(app.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task A_file_of_a_non_local_provider_that_cannot_be_copied_out_is_told_and_not_opened()
    {
        var remote = RemoteFileSystem();
        remote.FailOn((op, path) => op == "OpenRead" && path.Path == "/shared/a.txt", () => new IOException("gone"));
        await using var app = await TestApp.StartAsync(show: false, extraProviders: [remote]);
        await app.Left.NavigateToAsync(new VfsPath(Remote, "/shared"));
        Cursor(app.Left, "a.txt");

        await app.Left.ActivateAsync();
        await TestApp.WaitUntil(() => app.Dialogs.Messages.Count > 0);

        Assert.Empty(app.Shell.Calls);
        Assert.Equal([(Loc.Format("Shell_OpenFailed", "a.txt"), "gone")], app.Dialogs.MessagesWithDetail);
        Assert.Empty(Directory.EnumerateFileSystemEntries(app.Temp.Directory));
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task A_failing_launch_is_shown_in_words_not_thrown()
    {
        await using var app = await StartAsync();
        app.Shell.Failure = new IOException("open exited with 1");
        Cursor(app.Left, "notes.txt");

        await app.Left.ActivateAsync();
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ShellReveal));

        Assert.Equal(
        [
            (Loc.Format("Shell_OpenFailed", "notes.txt"), "open exited with 1"),
            (Loc.Format("Shell_RevealFailed", "notes.txt"), "open exited with 1"),
        ], app.Dialogs.MessagesWithDetail);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task A_launcher_that_reports_failure_later_is_shown_once_with_its_words()
    {
        await using var app = await StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Shell.ExitGate = gate;
        app.Shell.ExitFailure = new IOException("No application knows how to open this file.");
        Cursor(app.Left, "notes.txt");

        await app.Left.ActivateAsync(); // Enter: returns at once, the launcher still runs
        Assert.Empty(app.Dialogs.MessagesWithDetail);

        gate.SetResult();
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return app.Dialogs.Messages.Count > 0; });
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([(Loc.Format("Shell_OpenFailed", "notes.txt"), "No application knows how to open this file.")],
            app.Dialogs.MessagesWithDetail);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Failed_reveal_and_open_with_are_shown_too()
    {
        await using var app = await StartAsync();
        app.Shell.ExitFailure = new IOException("The file does not exist.");
        app.Shell.Apps = [new AppInfo("Broken", "/Applications/Broken.app")];
        Cursor(app.Left, "notes.txt");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ShellReveal));
        var apps = await app.Main.Shell.QueryOpenWithAsync();
        app.Main.Shell.OpenWith(apps!.Entry, apps.Apps[0]);
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return app.Dialogs.Messages.Count == 2; });

        Assert.Equal(
        [
            (Loc.Format("Shell_RevealFailed", "notes.txt"), "The file does not exist."),
            (Loc.Format("Shell_OpenFailed", "notes.txt"), "The file does not exist."),
        ], app.Dialogs.MessagesWithDetail);
    }

    [AvaloniaFact]
    public async Task A_launcher_that_succeeds_shows_nothing()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");

        await app.Left.ActivateAsync();
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ShellReveal));
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([("open", app.Left.CursorRow!.Entry!.Path.Path), ("reveal", app.Left.CursorRow.Entry.Path.Path)],
            app.Shell.Calls);
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Edit_opens_the_cursor_file_in_the_built_in_editor_and_is_off_on_a_folder()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "docs");
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileEdit));
        Cursor(app.Left, "..");
        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileEdit));

        Cursor(app.Left, "README.md");
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));

        var editor = Assert.IsType<EditorWindow>(Assert.Single(app.Windows.Open)).Editor;
        Assert.Equal(VfsPath.Local("/home/README.md"), editor.File);
        Assert.Equal("readme", editor.Document.Text);
        Assert.Empty(app.Shell.Calls);
    }

    [AvaloniaFact]
    public async Task F4_uses_external_editor_for_local_file_when_configured()
    {
        var processes = new FakeProcessRunner();
        await using var app = await TestApp.StartAsync(show: false, processes: processes,
            settings: Tandem.Core.Settings.AppSettings.Default with { ExternalEditor = "/Applications/My Editor.app" });
        await app.Left.NavigateToAsync(VfsPath.Local("/home/docs"));
        app.Main.Workspace.SwitchPanel(); // the right panel, still at /home, is active
        Cursor(app.Right, "README.md");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));

        var spec = Assert.Single(processes.Started);
        Assert.Equal("/usr/bin/open", spec.FileName);
        Assert.Equal(["-a", "/Applications/My Editor.app", "/home/README.md"], spec.Arguments);
        Assert.Equal("/home", spec.WorkingDirectory);
        Assert.Empty(app.Windows.Open);
        processes.Last.Exit(0);
        await TestApp.WaitUntil(() => processes.Last.IsDisposed);
        Assert.Equal(0, processes.Last.Kills);
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task F4_with_a_plain_external_program_passes_the_file_as_its_argument()
    {
        var processes = new FakeProcessRunner();
        await using var app = await TestApp.StartAsync(show: false, processes: processes,
            settings: Tandem.Core.Settings.AppSettings.Default with { ExternalEditor = "~/bin/ed it" });
        await app.Left.NavigateToAsync(VfsPath.Local("/home/docs"));
        app.Fs.AddFile("/home/docs/my \"file\".txt", "x");
        await app.Left.RefreshAsync();
        Cursor(app.Left, "my \"file\".txt");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));

        var spec = Assert.Single(processes.Started);
        Assert.Equal("/home/bin/ed it", spec.FileName);
        Assert.Equal(["/home/docs/my \"file\".txt"], spec.Arguments);
        Assert.Equal("/home/docs", spec.WorkingDirectory);
    }

    [AvaloniaFact]
    public async Task External_editor_application_that_open_cannot_launch_is_told()
    {
        var processes = new FakeProcessRunner();
        await using var app = await TestApp.StartAsync(show: false, processes: processes,
            settings: Tandem.Core.Settings.AppSettings.Default with { ExternalEditor = "/Applications/Gone.app" });
        Cursor(app.Left, "notes.txt");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));
        processes.Last.Write("Unable to find application named 'Gone'");
        processes.Last.Exit(1);
        await TestApp.WaitUntil(() => { Dispatcher.UIThread.RunJobs(); return app.Dialogs.Messages.Count > 0; });

        Assert.Equal([(Loc.Format("UserCommand_StartFailed", "/Applications/Gone.app"),
            "Unable to find application named 'Gone'")], app.Dialogs.MessagesWithDetail);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task F4_brings_forward_a_built_in_editor_of_the_file_instead_of_the_external_one()
    {
        var processes = new FakeProcessRunner();
        await using var app = await TestApp.StartAsync(show: false, processes: processes);
        Cursor(app.Left, "README.md");
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit)); // built-in: no external one yet
        var window = Assert.Single(app.Windows.Open);
        app.Main.Settings.Update(app.Main.Settings.Current with { ExternalEditor = "/Applications/Ed.app" });

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));

        Assert.Same(window, Assert.Single(app.Windows.Open));
        Assert.Empty(processes.Started);

        // Another file goes to the external editor.
        Cursor(app.Left, "notes.txt");
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));
        Assert.Equal(["-a", "/Applications/Ed.app", "/home/notes.txt"], Assert.Single(processes.Started).Arguments);
    }

    [AvaloniaFact]
    public async Task External_editor_that_cannot_start_is_told()
    {
        var processes = new FakeProcessRunner { StartFailure = new System.ComponentModel.Win32Exception("not found") };
        await using var app = await TestApp.StartAsync(show: false, processes: processes,
            settings: Tandem.Core.Settings.AppSettings.Default with { ExternalEditor = "/opt/missing" });
        Cursor(app.Left, "notes.txt");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));

        Assert.Equal([(Loc.Format("UserCommand_StartFailed", "/opt/missing"), "not found")],
            app.Dialogs.MessagesWithDetail);
        Assert.Empty(app.Windows.Open);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task F4_uses_built_in_editor_for_non_local_file_even_when_external_is_configured()
    {
        var processes = new FakeProcessRunner();
        await using var app = await TestApp.StartAsync(show: false, processes: processes,
            extraProviders: [RemoteFileSystem()],
            settings: Tandem.Core.Settings.AppSettings.Default with { ExternalEditor = "/Applications/Ed.app" });
        await app.Left.NavigateToAsync(new VfsPath(Remote, "/shared"));
        Cursor(app.Left, "a.txt");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));

        var editor = Assert.IsType<EditorWindow>(Assert.Single(app.Windows.Open)).Editor;
        Assert.Equal(new VfsPath(Remote, "/shared/a.txt"), editor.File);
        Assert.Empty(processes.Started);
    }

    [AvaloniaFact]
    public async Task Clearing_the_external_editor_brings_back_the_built_in_one()
    {
        var processes = new FakeProcessRunner();
        await using var app = await TestApp.StartAsync(show: false, processes: processes,
            settings: Tandem.Core.Settings.AppSettings.Default with { ExternalEditor = "/Applications/Ed.app" });
        app.Main.Settings.Update(app.Main.Settings.Current with { ExternalEditor = null });
        Cursor(app.Left, "README.md");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));

        Assert.Single(app.Windows.Open);
        Assert.Empty(processes.Started);
    }

    /// <summary>While F4 reads a file, an editor of the same file appears (another F4 that got there first): the
    /// check after the read and the showing are one step, so that editor is brought forward and no second one
    /// opens.</summary>
    [AvaloniaFact]
    public async Task Editor_of_the_same_file_opened_while_F4_reads_is_reused()
    {
        var provider = new StreamProvider();
        var gate = new TaskCompletionSource();
        TestStream? gated = null;
        var file = provider.Add("a.txt", 4, () =>
        {
            var stream = new TestStream(new MemoryStream("text"u8.ToArray()), true);
            if (gated is null) (gated, stream.Gate) = (stream, gate);
            return stream;
        });
        await using var app = await TestApp.StartAsync(show: false, extraProviders: [provider]);
        await app.Left.NavigateToAsync(new VfsPath(provider.Id, "/"));
        Cursor(app.Left, "a.txt");

        var f4 = app.Main.Commands.ExecuteAsync(CommandIds.FileEdit);
        await TestApp.WaitUntil(() => gated is not null);
        await gated!.ReadWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = await EditorViewModel.OpenAsync(app.Main.Services.Providers, file, CancellationToken.None);
        app.Windows.ShowEditor(first);
        gate.SetResult();
        Assert.True(await f4);

        Assert.Same(first, Assert.IsType<EditorWindow>(Assert.Single(app.Windows.Open)).Editor);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Viewer_still_opening_when_the_commands_are_disposed_leaves_nothing_open(bool finishesLate)
    {
        var provider = new StreamProvider();
        var gate = new TaskCompletionSource();
        var content = "some text\n"u8.ToArray();
        provider.Add("packed.txt", content.Length,
            () => new TestStream(new MemoryStream(content), false) { Gate = gate, IgnoresCancellation = finishesLate });
        await using var app = await TestApp.StartAsync(show: false, extraProviders: [provider]);
        await app.Left.NavigateToAsync(new VfsPath(provider.Id, "/"));
        Cursor(app.Left, "packed.txt");

        var f3 = app.Main.Commands.ExecuteAsync(CommandIds.FileView);
        await TestApp.WaitUntil(() => provider.Opened.Count == 1);
        await ((TestStream)provider.Opened[0]).ReadWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(Directory.EnumerateFileSystemEntries(app.Temp.Directory));
        app.Main.Shell.Dispose();
        gate.SetResult();
        await f3.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(app.Windows.Open);
        Assert.True(((TestStream)Assert.Single(provider.Opened)).IsDisposed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(app.Temp.Directory));
        Assert.Empty(app.Dialogs.Messages);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Edit_opens_a_file_of_any_provider()
    {
        await using var app = await StartAsync();
        await app.Left.NavigateToAsync(new VfsPath(Remote, "/shared"));
        Cursor(app.Left, "a.txt");

        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileEdit));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));

        var editor = Assert.IsType<EditorWindow>(Assert.Single(app.Windows.Open)).Editor;
        Assert.Equal(new VfsPath(Remote, "/shared/a.txt"), editor.File);
        Assert.Empty(app.Shell.Calls);
    }

    [AvaloniaFact]
    public async Task Edit_of_a_file_that_cannot_be_read_is_told()
    {
        var remote = RemoteFileSystem();
        remote.FailOn((op, path) => op == "OpenRead" && path.Path == "/shared/a.txt", () => new IOException("gone"));
        await using var app = await TestApp.StartAsync(show: false, extraProviders: [remote]);
        await app.Left.NavigateToAsync(new VfsPath(Remote, "/shared"));
        Cursor(app.Left, "a.txt");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileEdit));

        Assert.Equal([("Could not open “a.txt”.", "gone")], app.Dialogs.MessagesWithDetail);
        Assert.Empty(app.Windows.Open);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Reveal_and_terminal_use_the_cursor_item_and_the_current_folder()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "docs");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ShellReveal));
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.ShellOpenTerminal));

        Assert.Equal([("reveal", "/home/docs"), ("terminal", "/home")], app.Shell.Calls);
        Cursor(app.Left, "..");
        Assert.False(app.Main.Commands.CanExecute(CommandIds.ShellReveal));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.ShellOpenTerminal));
    }

    [AvaloniaFact]
    public async Task Shell_commands_disabled_for_non_local_provider()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        Assert.All(LocalOnly, id => Assert.True(app.Main.Commands.CanExecute(id), id));

        await app.Left.NavigateToAsync(new VfsPath(Remote, "/shared"));
        Cursor(app.Left, "a.txt");

        Assert.All(LocalOnly, id => Assert.False(app.Main.Commands.CanExecute(id), id));
        foreach (var id in LocalOnly) Assert.False(await app.Main.Commands.ExecuteAsync(id));
        Assert.Empty(app.Shell.Calls);
        Assert.Empty(app.QuickLook.Toggles);
        Assert.Empty(app.Shell.AppQueries);
        // Properties, the built-in viewer and the built-in editor still work, without the local-only parts.
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileProperties));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileView));
        Assert.True(app.Main.Commands.CanExecute(CommandIds.FileEdit));
    }

    [AvaloniaFact]
    public async Task Availability_is_announced_only_when_it_flips()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "README.md");
        var raised = 0;
        app.Main.Commands.CanExecuteChanged += (_, _) => raised++;

        Cursor(app.Left, "notes.txt"); // file to file: nothing flips
        Assert.Equal(0, raised);
        Cursor(app.Left, "docs"); // file.edit goes off
        Assert.True(raised >= 1);
    }

    // ---- Quick Look --------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task QuickLook_toggles_for_cursor_item()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook));
        Assert.True(app.QuickLook.IsOpen);
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook));

        Assert.Equal([["/home/notes.txt"], ["/home/notes.txt"]], app.QuickLook.Toggles);
        Assert.False(app.QuickLook.IsOpen);
    }

    [AvaloniaFact]
    public async Task QuickLook_shows_the_marked_items()
    {
        await using var app = await StartAsync();
        app.Left.Rows[IndexOf(app.Left, "notes.txt")].IsSelected = true;
        app.Left.Rows[IndexOf(app.Left, "README.md")].IsSelected = true;
        Cursor(app.Left, "docs");

        await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook);

        Assert.Equal([["/home/notes.txt", "/home/README.md"]], app.QuickLook.Toggles); // in row order
    }

    [AvaloniaFact]
    public async Task QuickLook_follows_cursor_while_open()
    {
        var delay = new ManualDelay();
        await using var app = await StartAsync(delay.Wait);
        Cursor(app.Left, "README.md");
        await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook);

        Cursor(app.Left, "notes.txt");
        Assert.Single(app.QuickLook.Toggles); // not yet: the move is debounced
        delay.CompleteLast();
        await TestApp.WaitUntil(() => app.QuickLook.Toggles.Count == 2);

        Assert.Equal(["/home/notes.txt"], app.QuickLook.Toggles[1]);
        Assert.Equal(1, app.QuickLook.Closes); // the old preview was closed first
        Assert.True(app.QuickLook.IsOpen);
    }

    [AvaloniaFact]
    public async Task Holding_an_arrow_key_retargets_once_for_the_last_row()
    {
        var delay = new ManualDelay();
        await using var app = await StartAsync(delay.Wait);
        Cursor(app.Left, "docs");
        await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook);

        foreach (var name in new[] { "src", "README.md", "notes.txt", "report.pdf" }) Cursor(app.Left, name);

        Assert.Equal(4, delay.Calls.Count);
        Assert.All(delay.Calls.Take(3), c => Assert.True(c.Ct.IsCancellationRequested)); // superseded
        delay.CompleteLast();
        await TestApp.WaitUntil(() => app.QuickLook.Toggles.Count == 2);
        await Task.Delay(50);
        Assert.Equal(2, app.QuickLook.Toggles.Count);
        Assert.Equal(["/home/report.pdf"], app.QuickLook.Toggles[1]);
    }

    [AvaloniaFact]
    public async Task Moving_the_cursor_with_QuickLook_closed_starts_nothing()
    {
        var delay = new ManualDelay();
        await using var app = await StartAsync(delay.Wait);

        Cursor(app.Left, "notes.txt");
        Cursor(app.Left, "report.pdf");

        Assert.Empty(delay.Calls);
        Assert.Empty(app.QuickLook.Toggles);
    }

    [AvaloniaFact]
    public async Task A_preview_closed_by_the_user_is_not_reopened_by_a_cursor_move()
    {
        var delay = new ManualDelay();
        await using var app = await StartAsync(delay.Wait);
        Cursor(app.Left, "README.md");
        await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook);
        Cursor(app.Left, "notes.txt");
        app.QuickLook.IsOpen = false; // closed by itself (the user pressed Esc in it)

        delay.CompleteLast();
        await Task.Delay(50);

        Assert.Single(app.QuickLook.Toggles);
    }

    [AvaloniaFact]
    public async Task QuickLook_closes_on_navigation_panel_switch_and_window_close()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook);
        await app.Left.NavigateToAsync(VfsPath.Local("/home/docs"));
        Assert.Equal(1, app.QuickLook.Closes);

        await app.Left.NavigateToAsync(VfsPath.Local("/home"));
        Cursor(app.Left, "notes.txt");
        await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook);
        app.Main.Workspace.SwitchPanel();
        Assert.Equal(2, app.QuickLook.Closes);

        Cursor(app.Right, "notes.txt");
        await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook);
        app.Main.OnWindowClosing();
        Assert.Equal(3, app.QuickLook.Closes);
    }

    [AvaloniaFact]
    public async Task A_QuickLook_failure_is_shown_in_words()
    {
        await using var app = await StartAsync();
        app.QuickLook.Failure = new IOException("qlmanage failed");
        Cursor(app.Left, "notes.txt");

        await app.Main.Commands.ExecuteAsync(CommandIds.FileQuickLook);

        Assert.Equal([(Loc.Get("Shell_QuickLookFailed"), "qlmanage failed")], app.Dialogs.MessagesWithDetail);
        Assert.Empty(app.Failures);
    }

    // ---- Open with ---------------------------------------------------------------------------------------

    private static readonly AppInfo TextEdit = new("TextEdit", "/System/Applications/TextEdit.app");
    private static readonly AppInfo Xcode = new("Xcode", "/Applications/Xcode.app");

    [AvaloniaFact]
    public async Task OpenWith_lists_apps_for_cursor_item()
    {
        await using var app = await StartAsync();
        app.Shell.Apps = [TextEdit, Xcode];
        Cursor(app.Left, "notes.txt");

        var query = await app.Main.Shell.QueryOpenWithAsync();
        Assert.Equal([TextEdit, Xcode], query!.Apps);
        Assert.Equal("notes.txt", query.Entry.Name);
        app.Main.Shell.OpenWith(query.Entry, Xcode);

        Assert.Equal(["/home/notes.txt"], app.Shell.AppQueries);
        Assert.Equal([("/home/notes.txt", Xcode)], app.Shell.OpenedWith);
        Cursor(app.Left, "..");
        Assert.Null(await app.Main.Shell.QueryOpenWithAsync());
    }

    // ---- Context menu ------------------------------------------------------------------------------------

    private static readonly string[] RowMenuKeys =
    [
        "Context_Open", "Context_OpenWith", "Context_Reveal", "Context_QuickLook", "Context_Copy", "Context_Move",
        "Context_Rename", "Context_Delete", "Context_Properties",
    ];

    [AvaloniaFact]
    public async Task Row_menu_has_the_items_with_shortcuts_and_availability()
    {
        await using var app = await StartAsync();

        var menu = app.Main.Left.ContextMenuForRow(IndexOf(app.Left, "notes.txt"));

        Assert.Equal(RowMenuKeys, menu.Select(i => i.LabelKey));
        Assert.All(menu, i => Assert.Equal(Loc.Get(i.LabelKey), i.Label));
        Assert.Equal(
            [null, CommandIds.ShellOpenWith, CommandIds.ShellReveal, CommandIds.FileQuickLook, CommandIds.FileCopy,
                CommandIds.FileMove, CommandIds.FileRename, CommandIds.FileDelete, CommandIds.FileProperties],
            menu.Select(i => i.CommandId));
        Assert.Equal(KeyChord.Parse("F5"), menu.Single(i => i.CommandId == CommandIds.FileCopy).Chord);
        Assert.Equal(KeyChord.Parse("Cmd+I"), menu.Single(i => i.CommandId == CommandIds.FileProperties).Chord);
        Assert.Null(menu.Single(i => i.CommandId == CommandIds.ShellReveal).Chord);
        Assert.All(menu, i => Assert.True(i.IsEnabled, i.LabelKey));
        Assert.True(menu.Single(i => i.LabelKey == "Context_OpenWith").HasChildren);
    }

    [AvaloniaFact]
    public async Task Row_menu_items_are_disabled_as_their_commands_are()
    {
        await using var app = await StartAsync();
        await app.Left.NavigateToAsync(new VfsPath(Remote, "/shared"));

        var menu = app.Main.Left.ContextMenuForRow(IndexOf(app.Left, "a.txt"));

        foreach (var item in menu.Where(i => i.CommandId is not null))
            Assert.Equal(app.Main.Commands.CanExecute(item.CommandId!), item.IsEnabled);
        Assert.False(menu.Single(i => i.LabelKey == "Context_Reveal").IsEnabled);
        Assert.True(menu.Single(i => i.LabelKey == "Context_Open").IsEnabled); // opened through a temporary copy
        Assert.True(menu.Single(i => i.LabelKey == "Context_Properties").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Open_with_apps_are_queried_only_when_the_submenu_opens()
    {
        await using var app = await StartAsync();
        app.Shell.Apps = [TextEdit];
        var menu = app.Main.Left.ContextMenuForRow(IndexOf(app.Left, "notes.txt"));
        var openWith = menu.Single(i => i.LabelKey == "Context_OpenWith");
        Assert.Empty(app.Shell.AppQueries);

        var apps = await openWith.GetChildrenAsync();
        _ = await openWith.GetChildrenAsync(); // asked once per menu

        Assert.Equal(["/home/notes.txt"], app.Shell.AppQueries);
        var item = Assert.Single(apps);
        Assert.Equal("TextEdit", item.Label);
        Assert.True(item.IsEnabled);
        await item.ExecuteAsync();
        Assert.Equal([("/home/notes.txt", TextEdit)], app.Shell.OpenedWith);
    }

    [AvaloniaFact]
    public async Task No_apps_shows_a_disabled_item()
    {
        await using var app = await StartAsync();
        var menu = app.Main.Left.ContextMenuForRow(IndexOf(app.Left, "notes.txt"));

        var item = Assert.Single(await menu.Single(i => i.LabelKey == "Context_OpenWith").GetChildrenAsync());

        Assert.Equal("Context_NoApps", item.LabelKey);
        Assert.False(item.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Open_with_apps_are_queried_off_the_ui_thread()
    {
        await using var app = await StartAsync();
        app.Shell.Apps = [TextEdit];
        Cursor(app.Left, "notes.txt");

        Assert.Equal([TextEdit], (await app.Main.Shell.QueryOpenWithAsync())!.Apps);

        Assert.Equal([false], app.Shell.AppQueriesOnUiThread);
    }

    [AvaloniaFact]
    public async Task An_app_query_whose_cursor_moved_meanwhile_is_dropped()
    {
        await using var app = await StartAsync();
        app.Shell.Apps = [TextEdit];
        Cursor(app.Left, "notes.txt");
        using var gate = new ManualResetEventSlim(false);
        app.Shell.AppsGate = gate;

        var query = app.Main.Shell.QueryOpenWithAsync();
        Cursor(app.Left, "report.pdf");
        gate.Set();

        Assert.Null(await query); // the apps of notes.txt are not offered for report.pdf
    }

    [AvaloniaFact]
    public async Task Open_with_acts_on_the_item_the_apps_were_asked_for()
    {
        await using var app = await StartAsync();
        app.Shell.Apps = [TextEdit];
        Cursor(app.Left, "notes.txt");
        var items = await ContextMenuModel.OpenWithItemsAsync(app.Main);

        Cursor(app.Left, "report.pdf"); // e.g. a refresh moved the cursor while the menu was open
        await Assert.Single(items!).ExecuteAsync();

        Assert.Equal([("/home/notes.txt", TextEdit)], app.Shell.OpenedWith);
    }

    [AvaloniaFact]
    public async Task A_failing_app_query_is_told_in_words_and_shows_no_apps()
    {
        await using var app = await StartAsync();
        app.Shell.AppsFailure = new IOException("LaunchServices failed");
        var menu = app.Main.Left.ContextMenuForRow(IndexOf(app.Left, "notes.txt"));

        var item = Assert.Single(await menu.Single(i => i.LabelKey == "Context_OpenWith").GetChildrenAsync());

        Assert.Equal("Context_NoApps", item.LabelKey);
        Assert.Equal([(Loc.Format("Shell_AppsFailed", "notes.txt"), "LaunchServices failed")],
            app.Dialogs.MessagesWithDetail);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task An_unexpected_app_query_failure_is_reported_and_shows_no_apps()
    {
        await using var app = await StartAsync();
        var failure = new InvalidOperationException("bug");
        app.Shell.AppsFailure = failure;
        Cursor(app.Left, "notes.txt");

        Assert.Empty((await app.Main.Shell.QueryOpenWithAsync())!.Apps);

        Assert.Equal([failure], app.Failures);
        Assert.Empty(app.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task Right_click_moves_the_cursor_and_activates_the_side()
    {
        await using var app = await StartAsync();
        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide);

        var menu = app.Main.Right.ContextMenuForRow(IndexOf(app.Right, "report.pdf"));

        Assert.Equal(PanelSide.Right, app.Main.Workspace.ActiveSide);
        Assert.Equal("report.pdf", app.Right.CursorRow?.Name);
        await menu.Single(i => i.LabelKey == "Context_Reveal").ExecuteAsync();
        Assert.Equal([("reveal", "/home/report.pdf")], app.Shell.Calls);
    }

    [AvaloniaFact]
    public async Task Open_item_opens_the_row()
    {
        await using var app = await StartAsync();
        var menu = app.Main.Left.ContextMenuForRow(IndexOf(app.Left, "notes.txt"));

        await menu.Single(i => i.LabelKey == "Context_Open").ExecuteAsync();

        Assert.Equal([("open", "/home/notes.txt")], app.Shell.Calls);
    }

    [AvaloniaFact]
    public async Task Empty_space_menu_does_not_move_the_cursor()
    {
        await using var app = await StartAsync();
        Cursor(app.Right, "notes.txt");

        var menu = app.Main.Right.ContextMenuForEmptySpace();

        Assert.Equal(["Context_NewFolder", "Context_OpenTerminal", "Context_Refresh"], menu.Select(i => i.LabelKey));
        Assert.Equal([CommandIds.FileMkdir, CommandIds.ShellOpenTerminal, CommandIds.PanelRefresh],
            menu.Select(i => i.CommandId));
        Assert.Equal("notes.txt", app.Right.CursorRow?.Name);
        Assert.Equal(PanelSide.Right, app.Main.Workspace.ActiveSide);
        Assert.Equal(KeyChord.Parse("F7"), menu[0].Chord);
        await menu[1].ExecuteAsync();
        Assert.Equal([("terminal", "/home")], app.Shell.Calls);
    }

    // ---- Access denied -----------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Access_denied_offers_the_access_settings_on_the_local_provider_only()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddDirectory("/locked");
        fs.FailOn((op, path) => op == "Enumerate" && path.Path == "/locked",
            () => new VfsAccessDeniedException("/locked"));
        var remote = RemoteFileSystem();
        remote.AddDirectory("/locked");
        remote.FailOn((op, path) => op == "Enumerate" && path.Path == "/locked",
            () => new VfsAccessDeniedException("/locked"));
        await using var app = await TestApp.StartAsync(fs, show: false, extraProviders: [remote]);

        await app.Left.NavigateToAsync(VfsPath.Local("/locked"));
        Assert.True(app.Main.Left.IsAccessDenied);
        app.Main.Left.OpenAccessSettingsCommand.Execute(null);
        Assert.Equal([("settings", "")], app.Shell.Calls);

        await app.Right.NavigateToAsync(new VfsPath(Remote, "/shared"));
        await app.Right.NavigateToAsync(new VfsPath(Remote, "/locked"));
        Assert.Equal(PanelErrorKind.AccessDenied, app.Right.ErrorKind);
        Assert.False(app.Main.Right.IsAccessDenied);
    }

    // ---- Properties --------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Properties_opens_for_the_cursor_item_and_refreshes_after_apply()
    {
        await using var app = await StartAsync();
        app.Metadata.Tags["/home/notes.txt"] = ["Work"];
        Cursor(app.Left, "notes.txt");
        app.Dialogs.OnProperties = async vm =>
        {
            vm.Octal = "600";
            return await vm.ApplyAsync();
        };

        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.FileProperties));

        var vm = Assert.Single(app.Dialogs.PropertiesRequests);
        Assert.Equal("notes.txt", vm.Name);
        Assert.Equal("Work", vm.TagsText);
        Assert.Equal("user:staff", vm.OwnerText);
        var entry = await app.Fs.GetEntryAsync(VfsPath.Local("/home/notes.txt"), CancellationToken.None);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, entry!.Mode);
        Assert.Equal("rw-------", app.Left.CursorRow?.ModeText); // refreshed, cursor kept on the item
        Assert.Equal("notes.txt", app.Left.CursorRow?.Name);
    }

    [AvaloniaFact]
    public async Task Properties_on_a_non_local_item_has_no_tags_or_owner()
    {
        await using var app = await StartAsync();
        await app.Left.NavigateToAsync(new VfsPath(Remote, "/shared"));
        Cursor(app.Left, "a.txt");

        await app.Main.Commands.ExecuteAsync(CommandIds.FileProperties);

        var vm = Assert.Single(app.Dialogs.PropertiesRequests);
        Assert.False(vm.CanEditTags);
        Assert.Equal(Loc.Get("Properties_Unavailable"), vm.OwnerText);
        Assert.Empty(app.Metadata.OwnerQueries);
        Assert.True(vm.CanEditMode);
    }

    [AvaloniaFact]
    public async Task Properties_is_off_on_the_parent_link()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "..");

        Assert.False(app.Main.Commands.CanExecute(CommandIds.FileProperties));
    }

    [AvaloniaFact]
    public async Task Properties_refreshes_when_something_was_applied_even_if_the_dialog_is_then_cancelled()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        app.Dialogs.OnProperties = async vm =>
        {
            vm.Octal = "600";
            Assert.True(await vm.ApplyAsync());
            return false; // then closed with Cancel
        };

        await app.Main.Commands.ExecuteAsync(CommandIds.FileProperties);

        Assert.Equal("rw-------", app.Left.CursorRow?.ModeText);
    }

    [AvaloniaFact]
    public async Task Properties_refreshes_when_a_later_apply_failed_after_an_earlier_one_changed_the_mode()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        app.Dialogs.OnProperties = async vm =>
        {
            vm.Octal = "600";
            Assert.True(await vm.ApplyAsync());
            app.Metadata.SetFailure = new IOException("xattr failed");
            vm.TagsText = "Red";
            Assert.False(await vm.ApplyAsync());
            return false;
        };

        await app.Main.Commands.ExecuteAsync(CommandIds.FileProperties);

        Assert.Equal("rw-------", app.Left.CursorRow?.ModeText);
    }

    [AvaloniaFact]
    public async Task A_second_properties_command_while_the_first_is_loading_opens_no_second_dialog()
    {
        await using var app = await StartAsync();
        Cursor(app.Left, "notes.txt");
        using var gate = new ManualResetEventSlim(false);
        app.Metadata.OwnerGate = gate;

        var first = app.Main.Commands.ExecuteAsync(CommandIds.FileProperties);
        Assert.False(first.IsCompleted); // still loading the owner
        Assert.False(await app.Main.Commands.ExecuteAsync(CommandIds.FileProperties));
        Assert.False(await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+I")));
        gate.Set();
        Assert.True(await first);

        Assert.Single(app.Dialogs.PropertiesRequests);
    }
}
