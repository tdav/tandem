using Avalonia.Headless.XUnit;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>Drag and drop without a real drag: what may be dropped where, what a pointer position targets, what a
/// drag from a panel carries, and that a drop always ends in the ordinary copy/move dialog. The whole app runs over
/// the in-memory "local disk" with scripted dialogs; both panels start at /home (.., docs, src, README.md,
/// notes.txt, report.pdf), and /dst and /other (inside.txt) exist.</summary>
[Collection(LanguageCollection.Name)]
public class DropHandlerTests : LanguageTestBase
{
    private const string Remote = "remote";

    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static string[] Names(PanelViewModel panel) => panel.Rows.Select(r => r.Name).ToArray();

    private static VfsPath L(string path) => VfsPath.Local(path);

    private static string[] Paths(IEnumerable<FileEntry> entries) => entries.Select(e => e.Path.Path).ToArray();

    private static InMemoryFileSystem FileSystem()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddDirectory("/dst");
        fs.AddDirectory("/home/docs/sub");
        return fs;
    }

    private static InMemoryFileSystem RemoteFileSystem()
    {
        var fs = new InMemoryFileSystem(Remote);
        fs.AddDirectory("/shared/dir");
        return fs;
    }

    private static async Task<TestApp> StartAsync(string right = "/dst",
        ProviderCapabilities removed = ProviderCapabilities.None)
    {
        var app = await TestApp.StartAsync(FileSystem(), show: false, removedCapabilities: removed,
            extraProviders: [RemoteFileSystem()]);
        await app.Right.NavigateToAsync(L(right));
        return app;
    }

    private static async Task DropAndWait(TestApp app, IReadOnlyList<string> paths, PanelSide side, VfsPath target,
        bool move = false)
    {
        await app.Main.Drops.DropAsync(paths, side, target, move);
        await app.Main.Files.WhenIdleAsync();
    }

    // ---- The brief's cases -------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Drop_copies_into_panel_directory()
    {
        await using var app = await StartAsync();
        app.Dialogs.AnswerCopyMove();

        await DropAndWait(app, ["/home/notes.txt"], PanelSide.Right, L("/dst"));

        var request = Assert.Single(app.Dialogs.CopyMoveRequests);
        Assert.False(request.IsMove);
        Assert.Equal("/dst/notes.txt", request.Destination); // pre-filled with the drop target, still editable
        Assert.Equal("notes", app.Fs.ReadAllText("/dst/notes.txt"));
        Assert.True(app.Fs.Exists("/home/notes.txt"));
        Assert.Contains("notes.txt", Names(app.Right)); // the target panel shows it
        Assert.Equal(PanelSide.Right, app.Main.Workspace.ActiveSide); // the target side becomes active
    }

    [AvaloniaFact]
    public async Task Drop_on_folder_row_targets_that_folder()
    {
        await using var app = await StartAsync(right: "/home");
        app.Dialogs.AnswerCopyMove();

        var target = DropHandler.TargetAt(app.Right, IndexOf(app.Right, "docs"));
        Assert.Equal(new DropTarget(L("/home/docs"), IndexOf(app.Right, "docs")), target);
        Assert.True(app.Main.Drops.CanDrop(["/other/inside.txt"], PanelSide.Right, target!.Directory));
        await DropAndWait(app, ["/other/inside.txt"], PanelSide.Right, target.Directory);

        Assert.Equal("/home/docs/inside.txt", app.Dialogs.CopyMoveRequests.Single().Destination);
        Assert.True(app.Fs.Exists("/home/docs/inside.txt"));
    }

    [AvaloniaFact]
    public async Task Drop_with_move_flag_moves()
    {
        await using var app = await StartAsync();
        app.Dialogs.AnswerCopyMove();

        await DropAndWait(app, ["/home/notes.txt", "/home/report.pdf"], PanelSide.Right, L("/dst"), move: true);

        var request = Assert.Single(app.Dialogs.CopyMoveRequests);
        Assert.True(request.IsMove);
        Assert.Equal(2, request.ItemCount);
        Assert.Equal("/dst", request.Destination);
        Assert.True(app.Fs.Exists("/dst/notes.txt"));
        Assert.False(app.Fs.Exists("/home/notes.txt"));
        Assert.DoesNotContain("notes.txt", Names(app.Left)); // the source panel is refreshed too
    }

    [AvaloniaFact]
    public async Task Cannot_drop_into_same_directory()
    {
        await using var app = await StartAsync();

        Assert.False(app.Main.Drops.CanDrop(["/home/notes.txt", "/home/README.md"], L("/home")));
        Assert.False(app.Main.Drops.CanDrop(["/home/notes.txt"], PanelSide.Left, L("/home")));
        // Not all of them: allowed (the others are real copies).
        Assert.True(app.Main.Drops.CanDrop(["/home/notes.txt", "/other/inside.txt"], L("/home")));
        Assert.True(app.Main.Drops.CanDrop(["/home/notes.txt"], L("/dst")));
    }

    [AvaloniaFact]
    public async Task Cannot_drop_folder_into_itself()
    {
        await using var app = await StartAsync();

        Assert.False(app.Main.Drops.CanDrop(["/home/docs"], L("/home/docs"))); // onto its own row
        Assert.False(app.Main.Drops.CanDrop(["/home/docs"], L("/home/docs/sub"))); // into a folder inside it
        Assert.False(app.Main.Drops.CanDrop(["/home/notes.txt", "/home/docs"], L("/home/docs")));
        Assert.True(app.Main.Drops.CanDrop(["/home/docs"], L("/home/src")));
        Assert.True(app.Main.Drops.CanDrop(["/home/docs"], L("/home/docsx"))); // whole segments only
    }

    [AvaloniaFact]
    public async Task Cancelled_dialog_does_nothing()
    {
        await using var app = await StartAsync();
        var before = app.Fs.AllPaths.ToArray();

        await DropAndWait(app, ["/home/notes.txt"], PanelSide.Right, L("/dst"), move: true); // no answer: Cancel

        Assert.Single(app.Dialogs.CopyMoveRequests);
        Assert.Equal(before, app.Fs.AllPaths);
        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide);
        Assert.Empty(app.Main.Operations.Items);
    }

    // ---- What may be dropped -----------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("relative/notes.txt")]
    [InlineData("/../notes.txt")]
    public async Task Anything_that_is_not_a_local_path_cannot_be_dropped(string path)
    {
        await using var app = await StartAsync();

        Assert.False(app.Main.Drops.CanDrop([path], L("/dst")));
        Assert.False(app.Main.Drops.CanDrop(["/home/notes.txt", path], L("/dst")));
        Assert.False(app.Main.Drops.CanDrop([], L("/dst")));
    }

    [AvaloniaFact]
    public async Task A_drop_that_is_not_allowed_asks_nothing()
    {
        await using var app = await StartAsync();

        await DropAndWait(app, ["/home/docs"], PanelSide.Left, L("/home/docs"));

        Assert.Empty(app.Dialogs.CopyMoveRequests);
        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide);
    }

    [AvaloniaFact]
    public async Task A_panel_with_an_error_takes_no_drop()
    {
        await using var app = await StartAsync();
        await app.Right.NavigateToAsync(L("/missing"));
        Assert.NotNull(app.Right.Error);

        Assert.False(app.Main.Drops.CanDrop(["/home/notes.txt"], PanelSide.Right, app.Right.CurrentPath));
    }

    [AvaloniaFact]
    public async Task A_read_only_provider_takes_no_drop()
    {
        await using var app = await StartAsync(removed: ProviderCapabilities.Write);

        Assert.False(app.Main.Drops.CanDrop(["/home/notes.txt"], L("/dst")));
        Assert.False(app.Main.Drops.CanDrop(["/home/notes.txt"], PanelSide.Right, L("/dst")));
    }

    [AvaloniaFact]
    public async Task The_typed_destination_refers_to_the_drop_target_panels_provider()
    {
        await using var app = await StartAsync();
        await app.Right.NavigateToAsync(new VfsPath(Remote, "/shared"));
        app.Dialogs.AnswerCopyMove("/shared/dir");

        Assert.True(app.Main.Drops.CanDrop(["/home/notes.txt"], PanelSide.Right, app.Right.CurrentPath));
        await DropAndWait(app, ["/home/notes.txt"], PanelSide.Right, app.Right.CurrentPath);

        Assert.Equal("/shared/notes.txt", app.Dialogs.CopyMoveRequests.Single().Destination);
        Assert.Empty(app.Dialogs.ValidationErrors);
        var copied = await app.Main.Services.Providers.Get(Remote)
            .GetEntryAsync(new VfsPath(Remote, "/shared/dir/notes.txt"), CancellationToken.None);
        Assert.NotNull(copied);
        Assert.False(app.Fs.Exists("/shared/dir/notes.txt")); // not on the local disk
    }

    [AvaloniaFact]
    public async Task A_relative_destination_starts_at_the_dragged_items_folder()
    {
        await using var app = await StartAsync();
        app.Dialogs.AnswerCopyMove("docs");

        await DropAndWait(app, ["/home/notes.txt"], PanelSide.Right, L("/dst"));

        Assert.True(app.Fs.Exists("/home/docs/notes.txt"));
    }

    [AvaloniaFact]
    public async Task Keyboard_copy_still_goes_to_the_passive_panel()
    {
        await using var app = await StartAsync();
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Dialogs.AnswerCopyMove();

        await app.Main.Commands.ExecuteAsync(Tandem.App.Commands.CommandIds.FileCopy);
        await app.Main.Files.WhenIdleAsync();

        Assert.Equal("/dst/notes.txt", app.Dialogs.CopyMoveRequests.Single().Destination);
        Assert.True(app.Fs.Exists("/dst/notes.txt"));
        Assert.Equal(PanelSide.Left, app.Main.Workspace.ActiveSide);
    }

    // ---- What a position targets ------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_parent_row_targets_the_parent_and_a_file_row_or_empty_space_the_current_folder()
    {
        await using var app = await StartAsync(right: "/home/docs");
        var panel = app.Right;

        Assert.Equal(new DropTarget(L("/home"), 0), DropHandler.TargetAt(panel, IndexOf(panel, "..")));
        Assert.Equal(new DropTarget(L("/home/docs/sub"), IndexOf(panel, "sub")),
            DropHandler.TargetAt(panel, IndexOf(panel, "sub")));
        Assert.Equal(new DropTarget(L("/home/docs"), null), DropHandler.TargetAt(panel, -1));
        Assert.Equal(new DropTarget(L("/home/docs"), null), DropHandler.TargetAt(panel, panel.Rows.Count));

        await panel.NavigateToAsync(L("/home"));
        Assert.Equal(new DropTarget(L("/home"), null), DropHandler.TargetAt(panel, IndexOf(panel, "notes.txt")));
    }

    [AvaloniaFact]
    public async Task At_the_root_the_parent_row_targets_nothing_and_a_panel_without_a_folder_neither()
    {
        var fs = FileSystem();
        await using var app = await TestApp.StartAsync(fs, show: false);
        await app.Right.NavigateToAsync(L("/"));
        var parentIndex = IndexOf(app.Right, "..");

        Assert.True(parentIndex < 0 || DropHandler.TargetAt(app.Right, parentIndex) is null);
        Assert.Equal(new DropTarget(L("/"), null), DropHandler.TargetAt(app.Right, -1));
    }

    // ---- What a drag from a panel carries ---------------------------------------------------------------

    [AvaloniaFact]
    public async Task Dragging_a_marked_row_carries_every_marked_item_without_changing_marks()
    {
        await using var app = await StartAsync(right: "/home");
        var panel = app.Left;
        panel.Rows[IndexOf(panel, "notes.txt")].IsSelected = true;
        panel.Rows[IndexOf(panel, "docs")].IsSelected = true;
        panel.CursorIndex = IndexOf(panel, "notes.txt");

        Assert.Equal(["/home/docs", "/home/notes.txt"], Paths(DropHandler.DragItems(panel, IndexOf(panel, "notes.txt"))));

        Assert.Equal(2, panel.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Dragging_an_unmarked_row_carries_just_that_row_and_the_parent_row_nothing()
    {
        await using var app = await StartAsync();
        var panel = app.Left;
        panel.Rows[IndexOf(panel, "notes.txt")].IsSelected = true;

        Assert.Equal(["/home/report.pdf"], Paths(DropHandler.DragItems(panel, IndexOf(panel, "report.pdf"))));
        Assert.Empty(DropHandler.DragItems(panel, IndexOf(panel, "..")));
        Assert.Empty(DropHandler.DragItems(panel, -1));
        Assert.Equal(1, panel.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Nothing_is_dragged_from_a_panel_that_is_not_on_the_local_disk()
    {
        await using var app = await StartAsync();
        await app.Left.NavigateToAsync(new VfsPath(Remote, "/shared"));

        Assert.Empty(DropHandler.DragItems(app.Left, IndexOf(app.Left, "dir")));
    }
}
