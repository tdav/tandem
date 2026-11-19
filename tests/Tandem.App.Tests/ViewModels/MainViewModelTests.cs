using System.Globalization;
using System.Resources;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.ViewModels;

[Collection(LanguageCollection.Name)]
public class MainViewModelTests : LanguageTestBase
{
    [AvaloniaFact]
    public async Task Start_applies_the_saved_language_before_building()
    {
        await using var app = await TestApp.StartAsync(
            state: AppState.Default(TestApp.Home) with { Language = "ru" }, show: false);

        Assert.Equal("ru", Loc.Language);
        Assert.Equal("ru", app.Main.Workspace.Language);
        Assert.Equal("Копия", app.Main.FKeys[2].Label); // view-model text was made in the saved language
    }

    [AvaloniaFact]
    public async Task Registers_every_command()
    {
        await using var app = await TestApp.StartAsync(show: false);
        // Closing tabs is available once there are two; the file and shell commands once the cursor is on a local
        // file.
        await app.Main.Workspace.Left.NewTabAsync();
        var panel = app.Main.Workspace.ActivePanel;
        panel.CursorIndex = panel.Rows.ToList().FindIndex(r => r.Name == "notes.txt");

        Assert.All(CommandIds.All, id => Assert.True(app.Main.Commands.CanExecute(id), id));
    }

    [AvaloniaFact]
    public async Task Unbound_chords_are_not_handled()
    {
        await using var app = await TestApp.StartAsync(show: false);

        Assert.False(await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+J")));
        Assert.True(await app.Main.HandleKeyAsync(KeyChord.Parse("Tab")));
    }

    [AvaloniaFact]
    public async Task A_failing_command_is_reported_not_thrown()
    {
        await using var app = await TestApp.StartAsync(show: false);
        var failure = new InvalidOperationException("boom");
        app.Main.Commands.Register(CommandIds.PanelRefresh, () => throw failure);

        Assert.True(await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+R")));
        await app.Main.ExecuteCommandAsync(CommandIds.PanelRefresh);

        Assert.Equal([failure, failure], app.Failures);
    }

    [AvaloniaFact]
    public async Task Quick_search_ignores_blank_and_control_text()
    {
        await using var app = await TestApp.StartAsync(show: false);

        Assert.False(app.Main.HandleTextInput(" "));
        Assert.False(app.Main.HandleTextInput("\r"));
        Assert.True(app.Main.HandleTextInput("no"));
        Assert.Equal("notes.txt", app.Left.CursorRow?.Name);
    }

    [AvaloniaFact]
    public async Task Volume_changes_are_posted_to_the_ui_and_ignored_after_dispose()
    {
        await using var app = await TestApp.StartAsync(show: false);
        await TestApp.WaitUntil(() => app.Main.Volumes.Count == 1);
        Assert.Equal(1, app.Volumes.HandlerCount);
        var main = app.Main;
        var notified = 0;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Volumes)) notified++;
        };
        var lateHandlers = app.Volumes.Handlers!; // what the service may still be raising when Dispose runs

        main.Dispose();
        Assert.Equal(0, app.Volumes.HandlerCount);
        var reads = app.Volumes.Reads;
        await Task.Run(() => lateHandlers(app.Volumes, EventArgs.Empty)); // a late event, from a background thread
        await Task.Delay(100);

        Assert.Single(main.Volumes);
        Assert.Equal(0, notified);
        Assert.Equal(reads, app.Volumes.Reads);
    }

    [AvaloniaFact]
    public async Task A_late_event_posted_before_dispose_reads_nothing_afterwards()
    {
        await using var app = await TestApp.StartAsync(show: false);
        await TestApp.WaitUntil(() => app.Main.Volumes.Count == 1);
        var reads = app.Volumes.Reads;

        app.Volumes.Handlers!(app.Volumes, EventArgs.Empty); // posts a read to the UI thread...
        app.Main.Dispose(); // ...which runs only after the model is gone
        await Task.Delay(100);

        Assert.Equal(reads, app.Volumes.Reads);
    }

    [AvaloniaFact]
    public async Task A_failing_volume_read_is_logged_and_keeps_the_known_volumes()
    {
        await using var app = await TestApp.StartAsync(show: false);
        await TestApp.WaitUntil(() => app.Main.Volumes.Count == 1);

        app.Volumes.Failure = new IOException("volumes unreadable");
        await app.Volumes.ChangeAsync([]);
        await TestApp.WaitUntil(() => File.Exists(app.LogFile));

        Assert.Contains("volumes unreadable", File.ReadAllText(app.LogFile));
        Assert.Single(app.Main.Volumes);
        Assert.Empty(app.Failures); // logged, not shown as a command failure
    }

    [AvaloniaFact]
    public async Task A_failing_path_commit_is_reported_not_thrown()
    {
        await using var app = await TestApp.StartAsync(show: false);
        var failure = new InvalidOperationException("navigated handler broke");
        app.Left.Navigated += (_, _) => throw failure;
        app.Main.Left.BeginPathEdit();
        app.Main.Left.PathText = "/other";

        Assert.False(await app.Main.Left.CommitPathEditAsync());

        Assert.Equal([failure], app.Failures);
        Assert.True(app.Main.Left.IsEditingPath);
    }

    [AvaloniaFact]
    public async Task Free_space_of_the_volume_holding_the_folder_is_shown()
    {
        await using var app = await TestApp.StartAsync(show: false);
        await app.Volumes.ChangeAsync(
        [
            new VolumeInfo("Macintosh HD", "/", 2048L * 1024 * 1024, 1024L * 1024 * 1024, false),
            new VolumeInfo("Other", "/other", 100, 40, true),
        ]);
        await TestApp.WaitUntil(() => app.Main.Volumes.Count == 2);

        Assert.Equal("1.0 GB free of 2.0 GB", app.Main.Left.VolumeText);
        await app.Left.NavigateToAsync(VfsPath.Local("/other"));
        Assert.Equal("40 B free of 100 B", app.Main.Left.VolumeText);
    }

    [AvaloniaFact]
    public async Task Language_choice_is_saved_and_needs_a_restart()
    {
        await using var app = await TestApp.StartAsync(show: false);
        var notices = new List<string>();
        app.Main.NoticeRequested += (_, text) => notices.Add(text);

        app.Main.SetLanguage("ru");

        Assert.Equal("ru", app.Main.Workspace.Language);
        Assert.Equal("en", Loc.Language); // the running app keeps its language
        Assert.Equal([Loc.Get("Menu_LanguageRestartNotice")], notices);
        app.Main.Workspace.Save();
        Assert.Equal("ru", new JsonStateStore(app.StateDirectory).Load(TestApp.Home).Language);

        app.Main.SetLanguage(null);
        Assert.Null(app.Main.Workspace.Language);
    }

    [AvaloniaFact]
    public async Task Favorites_add_and_remove_the_current_folder()
    {
        await using var app = await TestApp.StartAsync(show: false);

        app.Main.AddCurrentToFavorites();
        Assert.Equal(["/home"], app.Main.Workspace.Favorites.Items);
        Assert.True(app.Main.IsCurrentFavorite);

        await app.Main.OpenFavoriteAsync("/other");
        Assert.Equal("/other", app.Left.CurrentPath.Path);
        Assert.False(app.Main.IsCurrentFavorite);
        await app.Main.OpenFavoriteAsync("/home");
        app.Main.RemoveCurrentFromFavorites();
        Assert.Empty(app.Main.Workspace.Favorites.Items);
    }

    [AvaloniaFact]
    public async Task Tab_and_view_commands_act_on_the_workspace()
    {
        await using var app = await TestApp.StartAsync(show: false);

        await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+T"));
        Assert.Equal(2, app.Main.Workspace.Left.Tabs.Count);
        await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+W"));
        Assert.Single(app.Main.Workspace.Left.Tabs);

        await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+Shift+OemPeriod"));
        Assert.True(app.Main.Workspace.ShowHidden);

        await app.Main.HandleKeyAsync(KeyChord.Parse("Cmd+A"));
        Assert.Equal(app.Left.TotalCount, app.Left.SelectedCount);
        await app.Main.HandleKeyAsync(KeyChord.Parse("Shift+Cmd+A"));
        Assert.Equal(0, app.Left.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Choosing_a_tab_activates_it_and_its_side()
    {
        await using var app = await TestApp.StartAsync(show: false);
        await app.Main.Workspace.Right.NewTabAsync();
        var first = app.Main.Workspace.Right.Tabs[0];

        app.Main.Right.SelectedTab = first;

        Assert.Same(first, app.Main.Workspace.Right.Active);
        Assert.Equal(PanelSide.Right, app.Main.Workspace.ActiveSide);
        Assert.Same(first.Panel, app.Main.Right.Panel);

        app.Main.Right.SelectedTab = null; // what the tab strip pushes while its items are replaced
        Assert.Same(first, app.Main.Workspace.Right.Active);
    }

    [AvaloniaFact]
    public async Task Escape_and_backspace_edit_a_running_quick_search()
    {
        await using var app = await TestApp.StartAsync(show: false);
        app.Main.HandleTextInput("rep");

        Assert.True(await app.Main.HandleKeyAsync(new KeyChord(Key.Back, KeyModifiers.None)));
        Assert.Equal("re", app.Left.QuickSearchText);
        Assert.Equal("/home", app.Left.CurrentPath.Path); // Backspace edited the search, it did not go up
        Assert.True(await app.Main.HandleKeyAsync(new KeyChord(Key.Escape, KeyModifiers.None)));
        Assert.Equal("", app.Left.QuickSearchText);
    }

    [Fact]
    public void Every_command_has_a_menu_label_in_both_languages()
    {
        var strings = new ResourceManager("Tandem.App.Resources.Strings", typeof(Loc).Assembly);
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("ru") })
        {
            // Without falling back to the parent culture, so a missing Russian label is caught.
            var set = strings.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
            foreach (var id in CommandIds.All)
                Assert.False(string.IsNullOrEmpty(set.GetString(Tandem.App.Views.MainMenu.LabelKey(id))),
                    $"No {culture.Name} label for {id}.");
        }
    }

    [Fact]
    public void Error_log_never_throws()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tandem-log-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new ErrorLog(Path.Combine(directory, "logs", "tandem.log"));
            log.Write(new InvalidOperationException("first failure"));
            var text = File.ReadAllText(Path.Combine(directory, "logs", "tandem.log"));
            Assert.Contains("first failure", text);
            Assert.Contains(nameof(InvalidOperationException), text);

            // A path that cannot be a file: writing fails inside, silently.
            Directory.CreateDirectory(Path.Combine(directory, "dir.log"));
            new ErrorLog(Path.Combine(directory, "dir.log")).Write(new InvalidOperationException("lost"));
            new ErrorLog("\0bad").Write(new InvalidOperationException("lost"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Free_space_is_read_again_when_an_operation_ends()
    {
        await using var app = await TestApp.StartAsync(show: false);
        await TestApp.WaitUntil(() => app.Main.Volumes.Count == 1);
        var op = new GateOperation();
        _ = app.Main.Services.Operations.Enqueue(op, runImmediately: true);
        await op.Started;
        await Task.Delay(50);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var readsWhileRunning = Volatile.Read(ref app.Volumes.Reads);
        app.Volumes.ChangeVolumesSilently(
            [new VolumeInfo("Macintosh HD", "/", 1000L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024, false)]);

        op.Release();
        await op.Completion;

        await TestApp.WaitUntil(() =>
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            return app.Main.Volumes[0].FreeBytes == 100L * 1024 * 1024 * 1024;
        });
        Assert.True(Volatile.Read(ref app.Volumes.Reads) > readsWhileRunning);
    }

    [AvaloniaFact]
    public async Task Closing_without_operations_asks_nothing()
    {
        await using var app = await TestApp.StartAsync(show: false);

        Assert.True(await app.Main.ConfirmCloseAsync());
        Assert.Empty(app.Dialogs.QuitRequests);
    }

    [AvaloniaFact]
    public async Task Closing_with_a_running_operation_asks_and_staying_keeps_it_running()
    {
        await using var app = await TestApp.StartAsync(show: false);
        var op = new GateOperation();
        _ = app.Main.Services.Operations.Enqueue(op, runImmediately: true);
        await op.Started;
        app.Dialogs.QuitAnswers.Enqueue(false);

        Assert.False(await app.Main.ConfirmCloseAsync());

        Assert.Equal([1], app.Dialogs.QuitRequests);
        Assert.Equal(OperationState.Running, op.State);
        op.Release();
        Assert.Equal(OperationState.Completed, (await op.Completion).State);
    }

    [AvaloniaFact]
    public async Task Closing_and_stopping_cancels_running_and_queued_operations_and_waits_for_them()
    {
        await using var app = await TestApp.StartAsync(show: false);
        var running = new GateOperation("v", "a.txt");
        var queued = new GateOperation("v", "b.txt"); // same volume: waits for the first
        _ = app.Main.Services.Operations.Enqueue(running);
        _ = app.Main.Services.Operations.Enqueue(queued);
        await running.Started;
        app.Dialogs.QuitAnswers.Enqueue(true);

        Assert.True(await app.Main.ConfirmCloseAsync());

        Assert.Equal([2], app.Dialogs.QuitRequests);
        Assert.True(running.Completion.IsCompleted); // awaited, not just asked to stop
        Assert.Equal(OperationState.Cancelled, (await running.Completion).State);
        Assert.Equal(OperationState.Cancelled, (await queued.Completion).State);
    }

    [AvaloniaFact]
    public async Task Closing_waits_for_stopped_operations_only_so_long()
    {
        await using var app = await TestApp.StartAsync(show: false);
        var stubborn = new StubbornOperation();
        _ = app.Main.Services.Operations.Enqueue(stubborn, runImmediately: true);
        await stubborn.Started;
        app.Dialogs.QuitAnswers.Enqueue(true);

        Assert.True(await app.Main.ConfirmCloseAsync(TimeSpan.FromMilliseconds(100)));

        Assert.False(stubborn.Completion.IsCompleted);
        Assert.Contains("did not stop in time", File.ReadAllText(app.LogFile));
        stubborn.Release();
        await stubborn.Completion;
    }

    /// <summary>An operation that does not react to cancellation until released.</summary>
    private sealed class StubbornOperation : FileOperation
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override OperationKind Kind => OperationKind.Copy;
        public override int ItemCount => 1;
        public override IReadOnlyList<VfsPath> Sources { get; } = [VfsPath.Local("/src/a.txt")];
        public override VfsPath? Destination => VfsPath.Local("/dst");
        public Task Started => _started.Task;
        public void Release() => _release.TrySetResult();

        public override Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<string>>(["stubborn"]);

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            SetState(OperationState.Running);
            _started.TrySetResult();
            await _release.Task;
        }
    }
}
