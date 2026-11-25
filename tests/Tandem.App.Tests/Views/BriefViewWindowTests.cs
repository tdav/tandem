using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Tandem.App.Commands;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.Views;

// Start-up applies the saved language process-wide, so these tests run in the language collection.
[Collection(LanguageCollection.Name)]
public class BriefViewWindowTests : LanguageTestBase
{
    /// <summary>Home holds the default entries and <paramref name="count"/> files more, f000.txt onwards.</summary>
    private static InMemoryFileSystem ManyFiles(int count)
    {
        var fs = TestApp.DefaultFileSystem();
        for (var i = 0; i < count; i++) fs.AddFile($"{TestApp.Home}/f{i:000}.txt");
        return fs;
    }

    private static async Task<TestApp> StartBriefAsync(int files = 200)
    {
        var app = await TestApp.StartAsync(ManyFiles(files));
        await app.Main.Commands.ExecuteAsync(CommandIds.ViewBrief);
        app.Pump();
        app.Pump();
        return app;
    }

    private static BriefColumnsPanel Panel(TestApp app, PanelSide side = PanelSide.Left) =>
        Assert.IsType<BriefColumnsPanel>(app.FileList(side).ItemsPanelRoot);

    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static Control Row(TestApp app, int index) =>
        app.FileList(PanelSide.Left).ContainerFromIndex(index) ?? throw new InvalidOperationException($"Row {index} is not shown.");

    private static TextBlock Cell(Control row, string name) =>
        row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == name);

    private static ScrollViewer Scroller(TestApp app) =>
        app.FileList(PanelSide.Left).GetVisualDescendants().OfType<ScrollViewer>().First();

    private static object Resource(TestApp app, string key) =>
        app.Window.TryFindResource(key, app.Window.ActualThemeVariant, out var value)
            ? value!
            : throw new InvalidOperationException($"No resource {key}.");

    [AvaloniaFact]
    public async Task Brief_view_lays_names_out_in_columns_top_to_bottom()
    {
        await using var app = await StartBriefAsync();
        var panel = Panel(app);
        var rows = panel.RowsPerColumn;
        var list = app.FileList(PanelSide.Left);

        Assert.True(rows > 3, $"{rows} rows per column");
        Assert.Equal(rows, app.Main.Left.BriefRows);
        // Columns of equal width share the visible width, none narrower than the minimum.
        Assert.True(panel.ColumnWidth >= panel.MinColumnWidth);
        Assert.Equal(Scroller(app).Viewport.Width, panel.ColumnWidth * panel.VisibleColumns, 1);
        Assert.Equal(rows * panel.VisibleColumns, app.Main.Left.BriefPageSize);

        var first = Row(app, 0);
        var below = Row(app, 1);
        var nextColumn = Row(app, rows);
        Assert.Equal(first.Bounds.X, below.Bounds.X, 1);
        Assert.Equal(first.Bounds.Bottom, below.Bounds.Y, 1);
        Assert.Equal(first.Bounds.Y, nextColumn.Bounds.Y, 1);
        Assert.Equal(first.Bounds.X + panel.ColumnWidth, nextColumn.Bounds.X, 1);
        Assert.Equal(panel.ColumnWidth, first.Bounds.Width, 1);
        // Scrolls sideways, never up and down: the columns are wider than the list.
        Assert.True(Scroller(app).Extent.Width > Scroller(app).Viewport.Width);
        Assert.Equal(Scroller(app).Viewport.Height, Scroller(app).Extent.Height, 1);
        // Only the columns in view have rows.
        Assert.Null(list.ContainerFromIndex(app.Left.Rows.Count - 1));
        // No column headers: the brief view has no columns to head.
        Assert.False(app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "HeaderRow").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Brief_row_shows_the_name_and_the_extension_at_the_right_edge()
    {
        await using var app = await StartBriefAsync(files: 0);
        var row = Row(app, IndexOf(app.Left, "notes.txt"));

        Assert.Equal("notes", Cell(row, "RowName").Text);
        var extension = Cell(row, "RowExtension");
        Assert.Equal("txt", extension.Text);
        var right = extension.TranslatePoint(new Point(extension.Bounds.Width, 0), row)!.Value.X;
        Assert.True(row.Bounds.Width - right < 12, $"extension ends at {right} of {row.Bounds.Width}");
        Assert.DoesNotContain(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Name is "RowSize" or "RowModified");

        var folder = Row(app, IndexOf(app.Left, "docs"));
        Assert.Equal("docs", Cell(folder, "RowName").Text);
        Assert.Equal("", Cell(folder, "RowExtension").Text);
        Assert.Equal("..", Cell(Row(app, 0), "RowName").Text);
    }

    [AvaloniaFact]
    public async Task Marked_rows_keep_their_colour_in_the_brief_view()
    {
        await using var app = await StartBriefAsync(files: 0);
        app.Left.Rows.Single(r => r.Name == "notes.txt").IsSelected = true;
        app.Pump();

        var row = Row(app, IndexOf(app.Left, "notes.txt"));
        Assert.Same(Resource(app, "Tandem.MarkedForeground"), Cell(row, "RowName").Foreground);
        Assert.Same(Resource(app, "Tandem.MarkedForeground"), Cell(row, "RowExtension").Foreground);
    }

    [AvaloniaFact]
    public async Task Right_and_left_keys_move_the_cursor_by_a_column_and_bring_it_into_view()
    {
        await using var app = await StartBriefAsync();
        var rows = Panel(app).RowsPerColumn;

        app.Press(Key.Down);
        app.Press(Key.Right);
        Assert.Equal(1 + rows, app.Left.CursorIndex);
        app.Press(Key.Left);
        Assert.Equal(1, app.Left.CursorIndex);

        app.Press(Key.End);
        var last = app.Left.Rows.Count - 1;
        Assert.Equal(last, app.Left.CursorIndex);
        var row = Row(app, last);
        var scroller = Scroller(app);
        Assert.True(scroller.Offset.X > 0);
        var x = row.TranslatePoint(default, scroller)!.Value.X;
        Assert.True(x >= -1 && x + row.Bounds.Width <= scroller.Viewport.Width + 1, $"row at {x}");
        Assert.True(row.IsKeyboardFocusWithin);

        app.Press(Key.Home);
        Assert.Equal(0, scroller.Offset.X, 1);
        Assert.True(Row(app, 0).IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task A_click_in_another_column_moves_the_cursor_there()
    {
        await using var app = await StartBriefAsync();
        var target = Panel(app).RowsPerColumn + 2;

        app.Click(Row(app, target));

        Assert.Equal(target, app.Left.CursorIndex);
        app.Click(Row(app, target - 1), RawInputModifiers.Meta);
        Assert.True(app.Left.Rows[target - 1].IsSelected);
    }


    [AvaloniaFact]
    public async Task Brief_and_full_views_switch_back_and_forth_keeping_the_cursor()
    {
        await using var app = await StartBriefAsync();
        app.Left.CursorIndex = 150;
        app.Pump();

        await app.Main.Commands.ExecuteAsync(CommandIds.ViewFull);
        app.Pump();
        app.Pump();

        var list = app.FileList(PanelSide.Left);
        Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
        Assert.Equal(150, app.Left.CursorIndex);
        var row = list.ContainerFromIndex(150)!;
        Assert.NotNull(row);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "RowSize");
        Assert.True(app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "HeaderRow").IsEffectivelyVisible);
        Assert.True(row.IsKeyboardFocusWithin);

        await app.Main.Commands.ExecuteAsync(CommandIds.ViewBrief);
        app.Pump();
        app.Pump();
        Assert.IsType<BriefColumnsPanel>(list.ItemsPanelRoot);
        Assert.True(list.ContainerFromIndex(150)!.IsKeyboardFocusWithin);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task The_other_panel_keeps_the_full_view()
    {
        await using var app = await StartBriefAsync();

        Assert.IsType<VirtualizingStackPanel>(app.FileList(PanelSide.Right).ItemsPanelRoot);
    }

    [AvaloniaFact]
    public async Task Rename_in_place_works_in_the_brief_view()
    {
        await using var app = await StartBriefAsync(files: 0);
        app.Left.CursorIndex = IndexOf(app.Left, "notes.txt");
        app.Pump();

        app.Press(Key.F2, physical: PhysicalKey.F2);
        var editor = app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<InlineRenameEditor>().Single();
        Assert.Equal("notes.txt", editor.Text);
        app.Type("x");
        app.Press(Key.Enter, physical: PhysicalKey.Enter);
        await TestApp.WaitUntil(() => app.Fs.Exists("/home/x.txt"));
        Assert.False(app.Fs.Exists("/home/notes.txt"));
    }

    [AvaloniaFact]
    public async Task Mouse_wheel_scrolls_the_brief_view_sideways()
    {
        await using var app = await StartBriefAsync();
        var scroller = Scroller(app);
        var list = app.FileList(PanelSide.Left);

        app.Window.MouseWheel(app.CenterOf(list), new Vector(0, -1));
        app.Pump();

        Assert.Equal(Panel(app).ColumnWidth, scroller.Offset.X, 1);
        Assert.Equal(0, scroller.Offset.Y);
    }

    [AvaloniaFact]
    public async Task Taller_rows_give_fewer_rows_per_column()
    {
        await using var app = await StartBriefAsync();
        var before = Panel(app).RowsPerColumn;

        app.Main.Left.Layout.Apply(app.Main.Left.Layout.Columns, 13, 40);
        app.Pump();
        app.Pump();

        Assert.True(Panel(app).RowsPerColumn < before);
        Assert.Equal(Panel(app).RowsPerColumn, app.Main.Left.BriefRows);
        Assert.Equal(40, Row(app, 1).Bounds.Y - Row(app, 0).Bounds.Y, 1);
    }

    [AvaloniaFact]
    public async Task View_menu_checks_the_active_panels_list_mode()
    {
        await using var app = await StartBriefAsync(files: 0);
        static IEnumerable<NativeMenuItem> All(NativeMenu menu) => menu.Items.OfType<NativeMenuItem>()
            .SelectMany(i => i.Menu is { } sub ? All(sub).Prepend(i) : [i]);
        NativeMenuItem Item(string id) =>
            All(NativeMenu.GetMenu(app.Window)!).Single(i => i.CommandParameter as string == id);

        Assert.True(Item(CommandIds.ViewBrief).IsChecked);
        Assert.False(Item(CommandIds.ViewFull).IsChecked);
        Assert.Equal("Brief", Item(CommandIds.ViewBrief).Header);
        Assert.Equal("Full", Item(CommandIds.ViewFull).Header);

        app.Main.Workspace.SwitchPanel(); // the right panel shows the full view
        Assert.False(Item(CommandIds.ViewBrief).IsChecked);
        Assert.True(Item(CommandIds.ViewFull).IsChecked);
    }
}
