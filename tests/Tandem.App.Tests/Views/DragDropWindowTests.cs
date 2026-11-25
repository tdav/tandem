using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Vfs;

namespace Tandem.App.Tests.Views;

/// <summary>Drag and drop on the headless main window. Drops are raised as the platform would raise them (enter,
/// over, leave, drop at a point) with files from "outside"; no real OS drag happens. A drag out of a panel is checked
/// up to the point where it would hand the items to the system (<see cref="FileListView.DragStarter"/>); the real
/// <see cref="DragDrop.DoDragDropAsync"/> cannot run headless.</summary>
[Collection(LanguageCollection.Name)]
public class DragDropWindowTests : LanguageTestBase
{
    /// <summary>
    /// Real (empty) files and folders in a temporary folder, because the drag data carries storage items, which only
    /// the platform can make. The same paths are added to the app's in-memory "local disk", where everything the app
    /// does happens; the real files are never changed. <c>Root</c> holds inside.txt, docs/ and other/x.txt.
    /// </summary>
    private sealed class Outside : IDisposable
    {
        private readonly string _dir;

        public Outside(TestApp app)
        {
            _dir = Path.Combine(Path.GetTempPath(), "tandem-dnd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "docs"));
            Directory.CreateDirectory(Path.Combine(_dir, "other"));
            File.WriteAllText(Path.Combine(_dir, "inside.txt"), "");
            File.WriteAllText(Path.Combine(_dir, "other", "x.txt"), "");
            App = app;
            Storage = app.Window.StorageProvider;
            // The path as the platform reports it (on macOS /var is /private/var).
            Root = Storage.TryGetFolderFromPathAsync(_dir).GetAwaiter().GetResult()?.TryGetLocalPath()?.TrimEnd('/')
                   ?? throw new InvalidOperationException("The headless platform has no storage provider.");
            app.Fs.AddFile(Root + "/inside.txt", "inside");
            app.Fs.AddDirectory(Root + "/docs");
            app.Fs.AddFile(Root + "/other/x.txt", "x");
        }

        public TestApp App { get; }
        public IStorageProvider Storage { get; }
        public string Root { get; }

        /// <summary>A drag of these items (paths below <see cref="Root"/>, e.g. "inside.txt", "docs").</summary>
        public async Task<DataTransfer> DragOf(params string[] names)
        {
            var data = new DataTransfer();
            foreach (var name in names)
            {
                var path = Path.Combine(_dir, name);
                IStorageItem? item = Directory.Exists(path)
                    ? await Storage.TryGetFolderFromPathAsync(path)
                    : await Storage.TryGetFileFromPathAsync(path);
                data.Add(DataTransferItem.CreateFile(item ?? throw new InvalidOperationException(path)));
            }
            return data;
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A drag of something that is not a file: a web link as text.</summary>
    private static DataTransfer Link()
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText("https://example.com/a"));
        return data;
    }

    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static FileListView ListView(TestApp app, PanelSide side) =>
        app.PanelView(side).GetVisualDescendants().OfType<FileListView>().Single();

    private static Control Row(TestApp app, PanelSide side, string name) =>
        app.FileList(side).ContainerFromIndex(IndexOf(side == PanelSide.Left ? app.Left : app.Right, name))
        ?? throw new InvalidOperationException("The row is not realized.");

    private static Point BelowTheRows(TestApp app, PanelSide side)
    {
        var list = app.FileList(side);
        return list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height - 10), app.Window)!.Value;
    }

    /// <summary>The effects the window reports back for each drag event, after the list has handled it.</summary>
    private static List<(RoutedEvent Event, DragDropEffects Effects)> RecordEffects(TestApp app)
    {
        var effects = new List<(RoutedEvent, DragDropEffects)>();
        foreach (var routed in new[] { DragDrop.DragOverEvent, DragDrop.DropEvent })
            app.Window.AddHandler(routed, (_, e) => effects.Add((routed, e.DragEffects)), RoutingStrategies.Bubble,
                handledEventsToo: true);
        return effects;
    }

    private const DragDropEffects All = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

    /// <summary>
    /// The operations macOS offers a target, as far as Avalonia's effects go: AppKit filters the source's mask by the
    /// modifier held, and Command filters it to "generic", which Avalonia maps to nothing; so with Cmd held nothing is
    /// offered. Without a modifier, Finder and this app offer copy, move and link.
    /// </summary>
    private static DragDropEffects MacOffer(RawInputModifiers modifiers) =>
        modifiers.HasFlag(RawInputModifiers.Meta) ? DragDropEffects.None : All;

    private static void Drag(TestApp app, Point point, RawDragEventType type, IDataTransfer data,
        RawInputModifiers modifiers = RawInputModifiers.None, DragDropEffects? offered = null)
    {
        app.Window.DragDrop(point, type, data, offered ?? MacOffer(modifiers), modifiers);
        app.Pump();
    }

    private static async Task WaitForRequest(TestApp app, int count = 1)
    {
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Dialogs.CopyMoveRequests.Count == count;
        });
        await app.Main.Files.WhenIdleAsync();
        app.Pump();
    }

    private static async Task<TestApp> StartAsync()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddDirectory("/dst");
        var app = await TestApp.StartAsync(fs);
        await app.Right.NavigateToAsync(VfsPath.Local("/dst"));
        app.Pump();
        return app;
    }

    [AvaloniaFact]
    public async Task Files_dropped_from_outside_ask_the_copy_dialog_then_copy_and_activate_the_side()
    {
        await using var app = await StartAsync();
        using var outside = new Outside(app);
        var effects = RecordEffects(app);
        var data = await outside.DragOf("inside.txt");
        var point = BelowTheRows(app, PanelSide.Right);
        app.Dialogs.AnswerCopyMove();

        Drag(app, point, RawDragEventType.DragEnter, data);
        Drag(app, point, RawDragEventType.DragOver, data);
        Assert.Contains("dropTarget", ListView(app, PanelSide.Right).FindControl<ListBox>("Rows")!.Classes);
        Drag(app, point, RawDragEventType.Drop, data);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Dialogs.CopyMoveRequests.Count == 1;
        });
        await app.Main.Files.WhenIdleAsync();
        app.Pump();

        var request = app.Dialogs.CopyMoveRequests[0];
        Assert.True(request.OffersBoth); // Copy, Move and Cancel
        Assert.False(request.IsMove); // Copy pre-selected
        Assert.Equal("/dst/inside.txt", request.Destination);
        Assert.Equal("inside", app.Fs.ReadAllText("/dst/inside.txt"));
        Assert.True(app.Fs.Exists(outside.Root + "/inside.txt"));
        Assert.Equal(PanelSide.Right, app.Main.Workspace.ActiveSide);
        Assert.Equal([(DragDrop.DragOverEvent, DragDropEffects.Copy), (DragDrop.DropEvent, DragDropEffects.Copy)],
            effects);
        Assert.DoesNotContain("dropTarget", ListView(app, PanelSide.Right).FindControl<ListBox>("Rows")!.Classes);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task With_cmd_held_macos_offers_nothing_so_nothing_is_accepted_or_asked()
    {
        await using var app = await StartAsync();
        using var outside = new Outside(app);
        var effects = RecordEffects(app);
        var data = await outside.DragOf("inside.txt");
        var point = BelowTheRows(app, PanelSide.Right);

        Drag(app, point, RawDragEventType.DragEnter, data, RawInputModifiers.Meta);
        Drag(app, point, RawDragEventType.DragOver, data, RawInputModifiers.Meta);
        Drag(app, point, RawDragEventType.Drop, data, RawInputModifiers.Meta);
        await Task.Delay(20);
        app.Pump();

        Assert.All(effects, e => Assert.Equal(DragDropEffects.None, e.Effects));
        Assert.Empty(app.Dialogs.CopyMoveRequests);
        Assert.True(app.Fs.Exists(outside.Root + "/inside.txt"));
    }

    [AvaloniaTheory]
    [InlineData(DragDropEffects.Copy, DragDropEffects.Copy)]
    [InlineData(DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, DragDropEffects.Copy)]
    [InlineData(DragDropEffects.Move, DragDropEffects.Move)]
    [InlineData(DragDropEffects.Link | DragDropEffects.Move, DragDropEffects.Move)]
    [InlineData(DragDropEffects.Link, DragDropEffects.Link)]
    public async Task Any_offered_operation_is_accepted_and_copy_is_reported_whenever_offered(
        DragDropEffects offered, DragDropEffects reported)
    {
        await using var app = await StartAsync();
        using var outside = new Outside(app);
        var effects = RecordEffects(app);
        var data = await outside.DragOf("inside.txt");
        var point = BelowTheRows(app, PanelSide.Right);
        app.Dialogs.AnswerCopyMove();

        Drag(app, point, RawDragEventType.DragEnter, data, offered: offered);
        Drag(app, point, RawDragEventType.DragOver, data, offered: offered);
        Drag(app, point, RawDragEventType.Drop, data, offered: offered);
        await WaitForRequest(app);

        Assert.Equal([reported, reported], effects.Select(e => e.Effects));
        Assert.True(app.Dialogs.CopyMoveRequests[0].OffersBoth);
        Assert.False(app.Dialogs.CopyMoveRequests[0].IsMove); // whatever was offered, Copy is pre-selected
        Assert.True(app.Fs.Exists("/dst/inside.txt"));
    }

    [AvaloniaFact]
    public async Task Cmd_reported_at_the_drop_only_preselects_move_and_copy_is_still_reported()
    {
        await using var app = await StartAsync();
        using var outside = new Outside(app);
        var effects = RecordEffects(app);
        var data = await outside.DragOf("inside.txt");
        var point = BelowTheRows(app, PanelSide.Right);
        app.Dialogs.AnswerCopyMove(); // Enter: the pre-selected action

        // A platform that still offers everything with Cmd held (not what macOS does, see MacOffer). On Windows the
        // move key is Shift, as in Explorer.
        var move = OperatingSystem.IsWindows() ? RawInputModifiers.Shift : RawInputModifiers.Meta;
        Drag(app, point, RawDragEventType.DragEnter, data, offered: All);
        Drag(app, point, RawDragEventType.DragOver, data, move, offered: All);
        Drag(app, point, RawDragEventType.Drop, data, move, offered: All);
        await WaitForRequest(app);

        Assert.Equal([DragDropEffects.Copy, DragDropEffects.Copy], effects.Select(e => e.Effects));
        Assert.True(app.Dialogs.CopyMoveRequests[0].IsMove);
        Assert.True(app.Fs.Exists("/dst/inside.txt"));
        Assert.False(app.Fs.Exists(outside.Root + "/inside.txt")); // moved on the in-memory disk only
    }

    [AvaloniaFact]
    public async Task Nothing_is_accepted_while_a_dialog_of_the_app_is_open()
    {
        await using var app = await StartAsync();
        using var outside = new Outside(app);
        var effects = RecordEffects(app);
        var data = await outside.DragOf("inside.txt");
        var point = BelowTheRows(app, PanelSide.Right);
        var message = MessageDialog.ShowAsync(app.Window, "Tandem", "busy");
        app.Pump();

        Drag(app, point, RawDragEventType.DragEnter, data);
        Drag(app, point, RawDragEventType.DragOver, data);
        Drag(app, point, RawDragEventType.Drop, data);
        await Task.Delay(20);
        app.Pump();
        Assert.All(effects, e => Assert.Equal(DragDropEffects.None, e.Effects));
        Assert.Empty(app.Dialogs.CopyMoveRequests);

        foreach (var owned in app.Window.OwnedWindows.ToArray()) owned.Close();
        await TestApp.WaitUntil(() => message.IsCompleted);
        effects.Clear();
        Drag(app, point, RawDragEventType.DragEnter, data);
        Drag(app, point, RawDragEventType.DragOver, data);
        Assert.Equal([DragDropEffects.Copy], effects.Select(e => e.Effects));
        Drag(app, point, RawDragEventType.DragLeave, data);
    }

    [AvaloniaFact]
    public async Task A_recycled_row_does_not_keep_the_highlight()
    {
        await using var app = await StartAsync();
        using var outside = new Outside(app);
        var data = await outside.DragOf("inside.txt");
        var docs = Row(app, PanelSide.Left, "docs");
        Drag(app, app.CenterOf(docs), RawDragEventType.DragEnter, data);
        Drag(app, app.CenterOf(docs), RawDragEventType.DragOver, data);
        Assert.Contains("dropTarget", docs.Classes);

        // The rows change under the pointer (here: another folder; scrolling recycles rows the same way).
        await app.Left.NavigateToAsync(VfsPath.Local("/other"));
        app.Pump();

        Assert.DoesNotContain(app.FileList(PanelSide.Left).GetRealizedContainers(), c => c.Classes.Contains("dropTarget"));
        Assert.DoesNotContain("dropTarget", docs.Classes);
    }

    [AvaloniaFact]
    public async Task A_folder_row_under_the_pointer_is_highlighted_until_the_drag_leaves()
    {
        await using var app = await StartAsync();
        using var outside = new Outside(app);
        var data = await outside.DragOf("inside.txt");
        var docs = Row(app, PanelSide.Left, "docs");
        var notes = Row(app, PanelSide.Left, "notes.txt");
        var rows = ListView(app, PanelSide.Left).FindControl<ListBox>("Rows")!;

        Drag(app, app.CenterOf(docs), RawDragEventType.DragEnter, data);
        Drag(app, app.CenterOf(docs), RawDragEventType.DragOver, data);
        Assert.Contains("dropTarget", docs.Classes);
        Assert.DoesNotContain("dropTarget", rows.Classes);

        // Over a file the target is the folder shown: the whole list is highlighted, the row no longer.
        Drag(app, app.CenterOf(notes), RawDragEventType.DragOver, data);
        Assert.DoesNotContain("dropTarget", docs.Classes);
        Assert.DoesNotContain("dropTarget", notes.Classes);
        Assert.Contains("dropTarget", rows.Classes);

        Drag(app, app.CenterOf(notes), RawDragEventType.DragLeave, data);
        Assert.DoesNotContain("dropTarget", rows.Classes);
        Assert.Empty(app.Dialogs.CopyMoveRequests);
    }

    [AvaloniaFact]
    public async Task What_cannot_be_dropped_shows_no_effect_and_no_highlight_and_asks_nothing()
    {
        await using var app = await StartAsync();
        using var outside = new Outside(app);
        await app.Left.NavigateToAsync(VfsPath.Local(outside.Root));
        app.Pump();
        var effects = RecordEffects(app);
        var rows = ListView(app, PanelSide.Left).FindControl<ListBox>("Rows")!;
        var below = BelowTheRows(app, PanelSide.Left);

        void DragAndDrop(Point point, IDataTransfer data)
        {
            Drag(app, point, RawDragEventType.DragEnter, data);
            Drag(app, point, RawDragEventType.DragOver, data);
            Assert.DoesNotContain("dropTarget", rows.Classes);
            Drag(app, point, RawDragEventType.Drop, data);
        }

        DragAndDrop(below, await outside.DragOf("inside.txt")); // into the folder it is already in
        DragAndDrop(below, Link()); // not a file
        var docsRow = Row(app, PanelSide.Left, "docs");
        DragAndDrop(app.CenterOf(docsRow), await outside.DragOf("docs")); // a folder onto its own row
        Assert.DoesNotContain("dropTarget", docsRow.Classes);
        app.Pump();
        await app.Main.Files.WhenIdleAsync();

        Assert.Equal(6, effects.Count);
        Assert.All(effects, e => Assert.Equal(DragDropEffects.None, e.Effects));
        Assert.Empty(app.Dialogs.CopyMoveRequests);
    }

    [AvaloniaFact]
    public async Task A_panel_with_an_error_takes_no_drop()
    {
        await using var app = await StartAsync();
        using var outside = new Outside(app);
        await app.Right.NavigateToAsync(VfsPath.Local("/missing"));
        app.Pump();
        var effects = RecordEffects(app);
        var data = await outside.DragOf("inside.txt");
        var point = BelowTheRows(app, PanelSide.Right);

        Drag(app, point, RawDragEventType.DragEnter, data);
        Drag(app, point, RawDragEventType.DragOver, data);
        Drag(app, point, RawDragEventType.Drop, data);
        app.Pump();

        Assert.All(effects, e => Assert.Equal(DragDropEffects.None, e.Effects));
        Assert.Empty(app.Dialogs.CopyMoveRequests);
    }

    [AvaloniaFact]
    public async Task Dropping_on_the_parent_row_targets_the_parent_folder()
    {
        await using var app = await StartAsync();
        using var outside = new Outside(app);
        await app.Left.NavigateToAsync(VfsPath.Local(outside.Root + "/docs"));
        app.Pump();
        var data = await outside.DragOf("other/x.txt");
        var parent = app.CenterOf(Row(app, PanelSide.Left, ".."));

        Drag(app, parent, RawDragEventType.DragEnter, data);
        Drag(app, parent, RawDragEventType.DragOver, data);
        Drag(app, parent, RawDragEventType.Drop, data);
        await TestApp.WaitUntil(() =>
        {
            app.Pump();
            return app.Dialogs.CopyMoveRequests.Count == 1;
        });

        Assert.Equal(Path.Join(outside.Root, "x.txt"), app.Dialogs.CopyMoveRequests[0].Destination);
    }

    // ---- Dragging out of a panel -------------------------------------------------------------------------

    private static List<string[]> RecordDragStarts(FileListView list)
    {
        var starts = new List<string[]>();
        list.DragStarter = (_, items) =>
        {
            starts.Add(items.Select(i => i.Path.Path).ToArray());
            return Task.CompletedTask;
        };
        return starts;
    }

    [AvaloniaFact]
    public async Task A_drag_starts_only_past_the_threshold_and_carries_the_marked_items_without_changing_marks()
    {
        await using var app = await StartAsync();
        app.Left.Rows[IndexOf(app.Left, "notes.txt")].IsSelected = true;
        app.Left.Rows[IndexOf(app.Left, "docs")].IsSelected = true;
        app.Pump();
        var starts = RecordDragStarts(ListView(app, PanelSide.Left));
        var start = app.CenterOf(Row(app, PanelSide.Left, "notes.txt"));

        app.Window.MouseDown(start, MouseButton.Left);
        app.Window.MouseMove(start + new Point(2, 1), RawInputModifiers.LeftMouseButton);
        app.Pump();
        Assert.Empty(starts);
        app.Window.MouseMove(start + new Point(12, 2), RawInputModifiers.LeftMouseButton);
        app.Window.MouseMove(start + new Point(30, 2), RawInputModifiers.LeftMouseButton);
        app.Pump();
        app.Window.MouseUp(start + new Point(30, 2), MouseButton.Left);
        app.Pump();

        Assert.Equal([["/home/docs", "/home/notes.txt"]], starts);
        Assert.Equal(2, app.Left.SelectedCount);
        Assert.Equal("notes.txt", app.Left.CursorRow?.Name);
    }

    [AvaloniaFact]
    public async Task A_click_a_cmd_click_and_a_drag_from_the_parent_row_start_no_drag()
    {
        await using var app = await StartAsync();
        var starts = RecordDragStarts(ListView(app, PanelSide.Left));

        var report = app.CenterOf(Row(app, PanelSide.Left, "report.pdf"));
        app.Window.MouseDown(report, MouseButton.Left);
        app.Window.MouseUp(report, MouseButton.Left);
        app.Pump();
        Assert.Equal("report.pdf", app.Left.CursorRow?.Name);

        var notes = app.CenterOf(Row(app, PanelSide.Left, "notes.txt"));
        app.Window.MouseDown(notes, MouseButton.Left, TestKeys.Platform(RawInputModifiers.Meta));
        app.Window.MouseMove(notes + new Point(20, 0), TestKeys.Platform(RawInputModifiers.LeftMouseButton | RawInputModifiers.Meta));
        app.Window.MouseUp(notes + new Point(20, 0), MouseButton.Left, TestKeys.Platform(RawInputModifiers.Meta));
        app.Pump();
        Assert.True(app.Left.Rows[IndexOf(app.Left, "notes.txt")].IsSelected); // Cmd+click marked, as before

        var parent = app.CenterOf(Row(app, PanelSide.Left, ".."));
        app.Window.MouseDown(parent, MouseButton.Left);
        app.Window.MouseMove(parent + new Point(20, 0), RawInputModifiers.LeftMouseButton);
        app.Window.MouseUp(parent + new Point(20, 0), MouseButton.Left);
        app.Pump();

        Assert.Empty(starts);
        Assert.Equal(1, app.Left.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Moving_with_the_button_up_after_a_release_starts_no_drag()
    {
        await using var app = await StartAsync();
        var starts = RecordDragStarts(ListView(app, PanelSide.Left));
        var notes = app.CenterOf(Row(app, PanelSide.Left, "notes.txt"));

        app.Window.MouseDown(notes, MouseButton.Left);
        app.Window.MouseUp(notes, MouseButton.Left);
        app.Window.MouseMove(notes + new Point(40, 0));
        app.Pump();

        Assert.Empty(starts);
    }
}
