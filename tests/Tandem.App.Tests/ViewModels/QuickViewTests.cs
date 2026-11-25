using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.App.ViewModels.Viewing;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The quick-view panel (Ctrl+Q) on the whole app: the passive side shows the file under the active panel's
/// cursor and follows it once the cursor rests. Both panels start at /home (.., docs, src, README.md, notes.txt,
/// report.pdf); the "streams" provider hands out test streams for the cases that need slow, failing or
/// copied-to-temp files.</summary>
[Collection(LanguageCollection.Name)]
public class QuickViewTests : LanguageTestBase
{
    private static readonly KeyChord CtrlQ = KeyChord.Parse("Ctrl+Q");

    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static void Cursor(PanelViewModel panel, string name)
    {
        var index = IndexOf(panel, name);
        Assert.True(index >= 0, name);
        panel.CursorIndex = index;
    }

    /// <summary>A delay the test completes: every call is recorded with its token.</summary>
    private sealed class ManualDelay
    {
        public List<TaskCompletionSource> Calls { get; } = [];

        /// <summary>The calls neither completed nor cancelled.</summary>
        public IReadOnlyList<TaskCompletionSource> Pending => Calls.Where(c => !c.Task.IsCompleted).ToArray();

        public Task Wait(TimeSpan delay, CancellationToken ct)
        {
            Assert.Equal(QuickViewViewModel.FollowDelay, delay);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => done.TrySetCanceled(ct));
            Calls.Add(done);
            return done.Task;
        }

        public void CompleteAll()
        {
            foreach (var call in Calls) call.TrySetResult();
        }
    }

    private static Task<TestApp> StartAsync(Func<TimeSpan, CancellationToken, Task>? delay = null,
        StreamProvider? streams = null) =>
        TestApp.StartAsync(show: false, quickLookDelay: delay,
            extraProviders: streams is null ? null : [streams]);

    /// <summary>Opens the quick view with the cursor of the active (left) panel on
    /// <paramref name="name"/>, and waits until it shows.</summary>
    private static async Task<QuickViewViewModel> OpenAsync(TestApp app, string name)
    {
        Cursor(app.Left, name);
        Assert.True(await app.Main.HandleKeyAsync(CtrlQ));
        await app.Main.QuickView.IdleAsync();
        return app.Main.QuickView;
    }

    /// <summary>A stream provider of <paramref name="count"/> text files "f00.txt"…, each "file {i}"; each open makes
    /// a <see cref="TestStream"/> (non-seekable ones are copied to the temporary files first).</summary>
    private static (StreamProvider Provider, List<TestStream> Streams) Streams(int count, bool seekable = true,
        Func<int, TestStream, TestStream>? configure = null)
    {
        var provider = new StreamProvider();
        var streams = new List<TestStream>();
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var content = System.Text.Encoding.ASCII.GetBytes($"file {i}\n");
            provider.Add($"f{i:D2}.txt", content.Length, () =>
            {
                var stream = new TestStream(new MemoryStream(content), seekable);
                stream = configure?.Invoke(index, stream) ?? stream;
                lock (streams) streams.Add(stream);
                return stream;
            });
        }
        return (provider, streams);
    }

    private static async Task ShowStreamsAsync(TestApp app)
    {
        await app.Left.NavigateToAsync(new VfsPath("streams", "/"));
        Assert.Null(app.Left.Error);
    }

    private static IReadOnlyList<string> TempEntries(TestApp app) =>
        Directory.Exists(app.Temp.Directory) ? Directory.EnumerateFileSystemEntries(app.Temp.Directory).ToArray() : [];

    [AvaloniaFact]
    public async Task Ctrl_Q_shows_cursor_file_in_the_passive_side()
    {
        await using var app = await StartAsync();

        var quick = await OpenAsync(app, "notes.txt");

        Assert.True(quick.IsOpen);
        Assert.Equal(PanelSide.Right, quick.Side);
        Assert.NotNull(quick.Viewer);
        Assert.Equal(["notes"], quick.Viewer.Lines);
        Assert.Null(quick.Message);
        Assert.True(app.Main.Right.ShowsQuickView);
        Assert.False(app.Main.Left.ShowsQuickView);
        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide); // the list keeps the keyboard
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task Follows_cursor_after_the_delay()
    {
        var delay = new ManualDelay();
        await using var app = await StartAsync(delay.Wait);
        var quick = await OpenAsync(app, "notes.txt");
        Assert.Empty(delay.Pending); // opening shows the file at once

        Cursor(app.Left, "README.md");
        Dispatcher.UIThread.RunJobs();
        Assert.Single(delay.Pending);
        Assert.Equal(["notes"], quick.Viewer!.Lines); // not yet: the cursor has not rested

        delay.CompleteAll();
        await quick.IdleAsync();

        Assert.Equal(["readme"], quick.Viewer!.Lines);
        Assert.Equal(PanelSide.Right, quick.Side);
    }

    [AvaloniaFact]
    public async Task Fast_cursor_moves_open_only_the_last_file()
    {
        var delay = new ManualDelay();
        var (provider, _) = Streams(10);
        await using var app = await StartAsync(delay.Wait, provider);
        await ShowStreamsAsync(app);
        var quick = await OpenAsync(app, "f00.txt");
        Assert.Single(provider.Opened);

        for (var i = 1; i < 10; i++) Cursor(app.Left, $"f{i:D2}.txt");
        delay.CompleteAll();
        await quick.IdleAsync();

        Assert.Equal(2, provider.Opened.Count); // f00 on opening, then only f09
        Assert.Equal(["file 9"], quick.Viewer!.Lines);
    }

    [AvaloniaFact]
    public async Task Previous_viewer_is_disposed_when_following()
    {
        // f00 and f01 are read from their streams; f02 cannot seek and is read from a temporary copy.
        var (provider, streams) = Streams(3, configure: (i, s) => i == 2 ? new TestStream(s, seekable: false) : s);
        await using var app = await StartAsync(streams: provider);
        await ShowStreamsAsync(app);
        var quick = await OpenAsync(app, "f00.txt");
        var first = quick.Viewer!;

        Cursor(app.Left, "f01.txt");
        await quick.IdleAsync();

        Assert.NotSame(first, quick.Viewer);
        Assert.Equal(["file 1"], quick.Viewer!.Lines);
        Assert.True(streams[0].IsDisposed);
        Assert.False(streams[1].IsDisposed);

        Cursor(app.Left, "f02.txt");
        await quick.IdleAsync();
        var copy = Assert.Single(TempEntries(app));

        Cursor(app.Left, "f00.txt");
        await quick.IdleAsync();

        Assert.True(streams[1].IsDisposed);
        Assert.False(File.Exists(copy)); // the copy is released with its viewer
        Assert.Empty(TempEntries(app));
        Assert.Equal(["file 0"], quick.Viewer!.Lines);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rapid_moves_while_files_open_leave_one_viewer_and_one_temporary_copy(bool seekable)
    {
        // Every open after the first is held (in its first read, or in its copy to the temporary files) and ignores
        // cancellation, so each finishes late, after the cursor has moved on.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (provider, _) = Streams(12, seekable,
            (i, s) => i == 0 ? s : new TestStream(s, seekable) { Gate = gate, IgnoresCancellation = true });
        await using var app = await StartAsync(streams: provider);
        await ShowStreamsAsync(app);
        var quick = await OpenAsync(app, "f00.txt");

        for (var i = 1; i < 12; i++)
        {
            Cursor(app.Left, $"f{i:D2}.txt");
            await TestApp.WaitUntil(() => provider.Opened.Count == i + 1); // the open for this position has started
        }
        gate.SetResult();
        await quick.IdleAsync();

        Assert.Equal(["file 11"], quick.Viewer!.Lines);
        Assert.Empty(app.Failures);
        var opened = provider.Opened.Cast<TestStream>().ToArray();
        if (seekable)
        {
            // Only the viewer shown still holds its stream.
            Assert.Single(opened, s => !s.IsDisposed);
            Assert.False(opened[^1].IsDisposed);
            Assert.Empty(TempEntries(app));
        }
        else
        {
            // Each source is closed once copied; only the copy of the viewer shown is left.
            Assert.All(opened, s => Assert.True(s.IsDisposed));
            Assert.Single(TempEntries(app));
        }

        quick.Close();
        await quick.IdleAsync();
        Assert.All(opened, s => Assert.True(s.IsDisposed));
        Assert.Empty(TempEntries(app));
    }

    [AvaloniaFact]
    public async Task Open_finishing_late_for_a_previous_cursor_position_is_discarded()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (provider, _) = Streams(3,
            configure: (i, s) => i == 1 ? new TestStream(s, seekable: true) { Gate = gate, IgnoresCancellation = true } : s);
        await using var app = await StartAsync(streams: provider);
        await ShowStreamsAsync(app);
        var quick = await OpenAsync(app, "f00.txt");

        Cursor(app.Left, "f01.txt"); // slow
        var slow = (TestStream)await WaitForOpenAsync(provider, 2);
        await slow.ReadWaiting.Task;
        Cursor(app.Left, "f02.txt");
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return quick.Viewer?.Lines is ["file 2"];
        });
        gate.SetResult();
        await quick.IdleAsync();

        Assert.Equal(["file 2"], quick.Viewer!.Lines);
        Assert.True(slow.IsDisposed);
    }

    private static async Task<Stream> WaitForOpenAsync(StreamProvider provider, int count)
    {
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return provider.Opened.Count >= count;
        });
        return provider.Opened[count - 1];
    }

    [AvaloniaFact]
    public async Task Folder_under_cursor_shows_a_message_not_a_viewer()
    {
        await using var app = await StartAsync();
        var quick = await OpenAsync(app, "notes.txt");
        var file = quick.Viewer!;

        Cursor(app.Left, "docs");
        await quick.IdleAsync();

        Assert.Null(quick.Viewer);
        Assert.Equal(Loc.Format("QuickView_Folder", "docs"), quick.Message);
        Assert.Contains("docs", quick.Message);
        Assert.Contains(Loc.Get("Panel_Folder"), quick.Message);
        Assert.True(file.Lines.Count > 0);

        Cursor(app.Left, "..");
        await quick.IdleAsync();
        Assert.Null(quick.Viewer);
        Assert.Null(quick.Message);
        Assert.True(quick.IsOpen);
    }

    [AvaloniaFact]
    public async Task Switching_panel_closes_quick_view()
    {
        var (provider, streams) = Streams(1, seekable: false);
        await using var app = await StartAsync(streams: provider);
        await ShowStreamsAsync(app);
        var quick = await OpenAsync(app, "f00.txt");

        Assert.True(await app.Main.HandleKeyAsync(KeyChord.Parse("Tab")));
        await quick.IdleAsync();

        Assert.False(quick.IsOpen);
        Assert.Null(quick.Side);
        Assert.Null(quick.Viewer);
        Assert.False(app.Main.Right.ShowsQuickView);
        Assert.True(streams[0].IsDisposed);
        Assert.Empty(TempEntries(app));
    }

    [AvaloniaFact]
    public async Task Another_tab_on_the_active_side_closes_quick_view()
    {
        await using var app = await StartAsync();
        var quick = await OpenAsync(app, "notes.txt");

        await app.Main.Workspace.Left.NewTabAsync();

        Assert.False(quick.IsOpen);
    }

    [AvaloniaFact]
    public async Task Closing_the_window_closes_quick_view()
    {
        await using var app = await StartAsync();
        var quick = await OpenAsync(app, "notes.txt");

        app.Main.OnWindowClosing();

        Assert.False(quick.IsOpen);
        Assert.Null(quick.Viewer);
    }

    [AvaloniaFact]
    public async Task The_replaced_side_keeps_its_folder_cursor_and_selection()
    {
        await using var app = await StartAsync();
        Cursor(app.Right, "report.pdf");
        app.Right.Rows[IndexOf(app.Right, "src")].IsSelected = true;
        var tabs = app.Main.Workspace.Right.Tabs.ToArray();
        var panel = app.Right;

        var quick = await OpenAsync(app, "notes.txt");
        Cursor(app.Left, "README.md");
        await quick.IdleAsync();
        Assert.True(await app.Main.HandleKeyAsync(CtrlQ));
        await quick.IdleAsync();

        Assert.False(quick.IsOpen);
        Assert.Same(panel, app.Right);
        Assert.Equal(tabs, app.Main.Workspace.Right.Tabs);
        Assert.Equal(VfsPath.Local(TestApp.Home), app.Right.CurrentPath);
        Assert.Equal("report.pdf", app.Right.CursorRow!.Name);
        Assert.Equal(["src"], app.Right.Rows.Where(r => r.IsSelected).Select(r => r.Name));
    }

    [AvaloniaFact]
    public async Task Unreadable_file_shows_error_message_and_keeps_following()
    {
        var (provider, _) = Streams(2,
            configure: (i, s) => i == 0 ? new TestStream(s, seekable: true) { Failure = new IOException("bad sector") } : s);
        await using var app = await StartAsync(streams: provider);
        await ShowStreamsAsync(app);

        var quick = await OpenAsync(app, "f00.txt");

        Assert.True(quick.IsOpen);
        Assert.Null(quick.Viewer);
        Assert.NotNull(quick.Message);
        Assert.Contains("f00.txt", quick.Message);
        Assert.Empty(app.Failures);

        Cursor(app.Left, "f01.txt");
        await quick.IdleAsync();

        Assert.Equal(["file 1"], quick.Viewer!.Lines);
        Assert.Null(quick.Message);
    }

    [AvaloniaFact]
    public async Task Quick_view_command_is_bound_to_Ctrl_Q_and_toggles()
    {
        await using var app = await StartAsync();

        Assert.Equal(CommandIds.ViewQuickView, KeyMap.Default.Find(CtrlQ));
        var quick = await OpenAsync(app, "notes.txt");
        Assert.True(quick.IsOpen);
        await app.Main.ExecuteCommandAsync(CommandIds.ViewQuickView);
        Assert.False(quick.IsOpen);
    }

    [AvaloniaFact]
    public async Task Only_the_side_showing_quick_view_hosts_its_viewer()
    {
        await using var app = await StartAsync();

        var quick = await OpenAsync(app, "notes.txt");

        Assert.Same(quick.Viewer, app.Main.Right.QuickViewer);
        Assert.Null(app.Main.Left.QuickViewer);

        Cursor(app.Left, "README.md");
        await quick.IdleAsync();
        Assert.Same(quick.Viewer, app.Main.Right.QuickViewer);
        Assert.Null(app.Main.Left.QuickViewer);

        quick.Close();
        Assert.Null(app.Main.Right.QuickViewer);
    }

    [AvaloniaFact]
    public async Task Refresh_of_an_unchanged_file_keeps_its_viewer()
    {
        await using var app = await StartAsync();
        var quick = await OpenAsync(app, "notes.txt");
        var viewer = quick.Viewer;

        await app.Left.RefreshAsync();
        await quick.IdleAsync();

        Assert.Same(viewer, quick.Viewer);
    }

    [AvaloniaFact]
    public async Task Refresh_of_a_changed_file_opens_it_again()
    {
        await using var app = await StartAsync();
        var quick = await OpenAsync(app, "notes.txt");
        var viewer = quick.Viewer;

        app.Fs.AddFile(TestApp.Home + "/notes.txt", "notes, longer now", DateTimeOffset.UtcNow.AddMinutes(1));
        await app.Left.RefreshAsync();
        await quick.IdleAsync();

        Assert.NotSame(viewer, quick.Viewer);
        Assert.Equal(["notes, longer now"], quick.Viewer!.Lines);
    }

    [AvaloniaFact]
    public async Task Same_size_but_newer_file_opens_again()
    {
        await using var app = await StartAsync();
        var quick = await OpenAsync(app, "notes.txt");
        var viewer = quick.Viewer;

        app.Fs.AddFile(TestApp.Home + "/notes.txt", "NOTES", DateTimeOffset.UtcNow.AddMinutes(1));
        await app.Left.RefreshAsync();
        await quick.IdleAsync();

        Assert.NotSame(viewer, quick.Viewer);
        Assert.Equal(["NOTES"], quick.Viewer!.Lines);
    }

    [AvaloniaFact]
    public async Task Refresh_after_an_error_tries_the_file_again()
    {
        var failures = 1;
        var (provider, _) = Streams(1, configure: (_, s) =>
            failures-- > 0 ? new TestStream(s, seekable: true) { Failure = new IOException("busy") } : s);
        await using var app = await StartAsync(streams: provider);
        await ShowStreamsAsync(app);
        var quick = await OpenAsync(app, "f00.txt");
        Assert.Null(quick.Viewer);
        Assert.NotNull(quick.Message);

        await app.Left.RefreshAsync();
        await quick.IdleAsync();

        Assert.Equal(["file 0"], quick.Viewer!.Lines);
        Assert.Null(quick.Message);
    }

    [AvaloniaFact]
    public async Task Slow_open_shows_a_loading_cue_instead_of_the_previous_file()
    {
        var delay = new ManualDelay();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (provider, streams) = Streams(2,
            configure: (i, s) => i == 1 ? new TestStream(s, seekable: true) { Gate = gate } : s);
        await using var app = await StartAsync(delay.Wait, provider);
        await ShowStreamsAsync(app);
        var quick = await OpenAsync(app, "f00.txt");
        Assert.Equal(["file 0"], quick.Viewer!.Lines);
        Assert.Null(quick.Message);
        Assert.Empty(delay.Pending);

        Cursor(app.Left, "f01.txt");
        Dispatcher.UIThread.RunJobs();
        delay.CompleteAll(); // the cursor has rested: f01 starts opening and is held by the gate
        await WaitForOpenAsync(provider, 2);
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return delay.Pending.Count == 1;
        });
        Assert.Equal(["file 0"], quick.Viewer!.Lines); // not yet: the open is still within the delay

        delay.CompleteAll(); // it takes longer than the delay
        await TestApp.WaitUntil(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return quick.Message is not null;
        });

        Assert.Null(quick.Viewer);
        Assert.Equal(Loc.Format("QuickView_Loading", "f01.txt"), quick.Message);
        await TestApp.WaitUntil(() => streams[0].IsDisposed); // the previous file is let go (disposed off the UI thread)

        gate.SetResult();
        await quick.IdleAsync();
        Assert.Equal(["file 1"], quick.Viewer!.Lines);
        Assert.Null(quick.Message);
    }

    [AvaloniaFact]
    public async Task Fast_open_shows_no_loading_cue()
    {
        var delay = new ManualDelay();
        await using var app = await StartAsync(delay.Wait);
        var quick = await OpenAsync(app, "notes.txt");

        Assert.Equal(["notes"], quick.Viewer!.Lines);
        Assert.Null(quick.Message);
        Assert.Empty(delay.Pending); // the cue's wait was cancelled when the file opened
        delay.CompleteAll();
        await quick.IdleAsync();
        Assert.Null(quick.Message);
    }

    [AvaloniaFact]
    public async Task Folder_change_of_the_active_panel_follows_to_the_new_cursor_file()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile("/other/inside.txt", "inside");
        await using var app = await TestApp.StartAsync(fs, show: false);
        var quick = await OpenAsync(app, "notes.txt");

        await app.Left.NavigateToAsync(VfsPath.Local("/other"), "inside.txt");
        await quick.IdleAsync();

        Assert.True(quick.IsOpen);
        Assert.Equal(PanelSide.Right, quick.Side);
        Assert.Equal(["inside"], quick.Viewer!.Lines);
    }

    [AvaloniaFact]
    public async Task Swapping_panels_closes_quick_view()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS chords (Cmd); KeyMapWindowsTests and WindowsStartTests cover Windows.");
        await using var app = await StartAsync();
        var quick = await OpenAsync(app, "notes.txt");

        Assert.True(await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+U")));
        await quick.IdleAsync();

        Assert.False(quick.IsOpen);
        Assert.Null(quick.Viewer);
        Assert.False(app.Main.Left.ShowsQuickView);
        Assert.False(app.Main.Right.ShowsQuickView);
    }

    [AvaloniaFact]
    public async Task Declined_archive_password_is_not_asked_again_until_the_folder_changes()
    {
        await using var app = await ArchiveApp.StartAsync();
        await ArchiveApp.EnterAsync(app, "encrypted.zip");
        app.Dialogs.PasswordAnswers.Enqueue(null);

        var quickView = await OpenAsync(app, "a.txt");
        Assert.Equal([("encrypted.zip", false)], app.Dialogs.PasswordRequests);
        Cursor(app.Left, "привет.txt");
        await quickView.IdleAsync();

        Assert.Single(app.Dialogs.PasswordRequests); // the decline holds for this folder
        Assert.Null(quickView.Viewer);
        Assert.Equal(Loc.Format("QuickView_OpenFailed", "привет.txt",
            Loc.Get("OperationFailed_PasswordRequired")), quickView.Message);

        await app.Left.NavigateToAsync(app.Left.CurrentPath.Combine("docs"));
        app.Dialogs.PasswordAnswers.Enqueue("secret");
        Cursor(app.Left, "readme.txt");
        await quickView.IdleAsync();

        Assert.Equal(2, app.Dialogs.PasswordRequests.Count); // another folder: asked again
        Assert.Equal(["hello"], quickView.Viewer?.Lines);
    }
}
