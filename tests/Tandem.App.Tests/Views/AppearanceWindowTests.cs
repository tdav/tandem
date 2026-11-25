using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.Tests.ViewModels;
using Tandem.App.ViewModels;
using Tandem.App.Views;
using Tandem.Core.Settings;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.Views;

// The theme is process-wide, as is the language start-up applies: these tests run in the language collection and
// put the theme back when they end.
[Collection(LanguageCollection.Name)]
public class AppearanceWindowTests : LanguageTestBase, IDisposable
{
    void IDisposable.Dispose()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        Dispose();
    }

    private static ColumnSetting[] Columns(params (ColumnId Column, bool Visible, double Width)[] columns) =>
        columns.Select(c => new ColumnSetting(c.Column, c.Visible, c.Width)).ToArray();

    private static Button Header(TestApp app, PanelSide side, string name) =>
        app.PanelView(side).GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    private static Control RowOf(TestApp app, PanelSide side, string name)
    {
        var list = app.FileList(side);
        var panel = side == PanelSide.Left ? app.Left : app.Right;
        var index = panel.Rows.ToList().FindIndex(r => r.Name == name);
        return list.ContainerFromIndex(index) ?? throw new InvalidOperationException($"Row {name} is not shown.");
    }

    private static TextBlock Cell(Control row, string name) =>
        row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == name);

    private static object Resource(TestApp app, string key) =>
        app.Window.TryFindResource(key, app.Window.ActualThemeVariant, out var value)
            ? value!
            : throw new InvalidOperationException($"No resource {key}.");

    [AvaloniaFact]
    public async Task Both_panels_show_distinct_icons_for_parent_folder_and_file()
    {
        await using var app = await TestApp.StartAsync();
        await app.Right.NavigateToAsync(VfsPath.Local(TestApp.Home));
        app.Pump();

        IImage Icon(PanelSide side, string name)
        {
            var icon = Assert.Single(RowOf(app, side, name).GetVisualDescendants().OfType<Image>());
            Assert.True(icon.IsEffectivelyVisible);
            Assert.Equal(new Size(16, 16), icon.Bounds.Size);
            return Assert.IsAssignableFrom<IImage>(icon.Source);
        }

        var names = new[] { "..", "docs", "notes.txt" };
        var leftIcons = names.Select(name => Icon(PanelSide.Left, name)).ToArray();
        Assert.Equal(names.Length, leftIcons.Distinct().Count());
        Assert.Equal(names.Length, names.Select(name => Icon(PanelSide.Right, name)).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task Left_and_right_panels_use_their_own_columns()
    {
        var settings = AppSettings.Default with
        {
            RightColumns = Columns((ColumnId.Mode, true, 70), (ColumnId.Size, true, 150), (ColumnId.Extension, true, 40),
                (ColumnId.Modified, true, 120)),
        };
        await using var app = await TestApp.StartAsync(settings: settings);

        Assert.Equal(84, Header(app, PanelSide.Left, "SortBySize").Bounds.Width, 1);
        Assert.Equal(150, Header(app, PanelSide.Right, "SortBySize").Bounds.Width, 1);
        Assert.Equal(1, Grid.GetColumn(Header(app, PanelSide.Left, "SortByExtension")));
        Assert.Equal(1, Grid.GetColumn(Header(app, PanelSide.Right, "SortByMode")));
        // The size cell keeps an 8-pixel margin on its right.
        Assert.Equal(150 - 8, Cell(RowOf(app, PanelSide.Right, "notes.txt"), "RowSize").Bounds.Width, 1);
        Assert.Equal(84 - 8, Cell(RowOf(app, PanelSide.Left, "notes.txt"), "RowSize").Bounds.Width, 1);
        // The right panel's rows show its cells in its order: mode, size, extension, date.
        var rightRow = RowOf(app, PanelSide.Right, "notes.txt");
        double X(string cell) => Cell(rightRow, cell).TranslatePoint(default, rightRow)!.Value.X;
        Assert.True(X("RowMode") < X("RowSize") && X("RowSize") < X("RowExtension") &&
                    X("RowExtension") < X("RowModified"));
        var leftRow = RowOf(app, PanelSide.Left, "notes.txt");
        Assert.True(Cell(leftRow, "RowExtension").TranslatePoint(default, leftRow)!.Value.X <
                    Cell(leftRow, "RowMode").TranslatePoint(default, leftRow)!.Value.X);
        AssertHeadersLineUpWithCells(app, PanelSide.Left);
        AssertHeadersLineUpWithCells(app, PanelSide.Right);
    }

    [AvaloniaFact]
    public async Task Headers_line_up_with_cells_when_the_list_scrolls()
    {
        var fs = TestApp.DefaultFileSystem();
        for (var i = 0; i < 100; i++) fs.AddFile($"/home/many/file{i:D3}.txt", "x");
        await using var app = await TestApp.StartAsync(fs);
        await app.Left.NavigateToAsync(VfsPath.Local("/home/many"));
        app.Pump();
        var list = app.FileList(PanelSide.Left);
        var scrollBar = list.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.True(scrollBar.IsEffectivelyVisible, "The list should scroll.");

        var row = RowOf(app, PanelSide.Left, "file001.txt");
        foreach (var (header, cell) in new[] { ("SortByName", "RowNameCell"), ("SortByExtension", "RowExtension"),
                     ("SortBySize", "RowSize"), ("SortByDate", "RowModified"), ("SortByMode", "RowMode") })
        {
            var button = Header(app, PanelSide.Left, header);
            var block = row.GetVisualDescendants().OfType<Control>().Single(c => c.Name == cell);
            var headerX = button.TranslatePoint(default, app.Window)!.Value.X;
            var cellX = block.TranslatePoint(default, app.Window)!.Value.X - block.Margin.Left;
            Assert.True(Math.Abs(headerX - cellX) <= 1, $"{header}: header at {headerX}, cell at {cellX}.");
        }
        // One padding for both: the list item's and the header row's.
        var padding = Assert.IsType<Thickness>(Resource(app, "Tandem.ListItemPadding"));
        Assert.Equal(padding, ((ListBoxItem)row).Padding);
        Assert.Equal(padding, app.PanelView(PanelSide.Left).GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "HeaderRow").Padding);
        // The last column ends at the same place too.
        var mode = Cell(row, "RowMode");
        var modeRight = mode.TranslatePoint(default, app.Window)!.Value.X + mode.Bounds.Width + mode.Margin.Right;
        var headerMode = Header(app, PanelSide.Left, "SortByMode");
        var headerRight = headerMode.TranslatePoint(default, app.Window)!.Value.X + headerMode.Bounds.Width;
        Assert.True(Math.Abs(headerRight - modeRight) <= 1, $"header ends at {headerRight}, cell at {modeRight}.");
    }

    // Each visible column's header starts where its cells start (the cell's own margin left out), within a pixel.
    private static void AssertHeadersLineUpWithCells(TestApp app, PanelSide side)
    {
        var row = RowOf(app, side, "notes.txt");
        (string Header, string Cell)[] columns =
        [
            ("SortByName", "RowNameCell"), ("SortByExtension", "RowExtension"), ("SortBySize", "RowSize"),
            ("SortByDate", "RowModified"), ("SortByMode", "RowMode"),
        ];
        foreach (var (header, cell) in columns)
        {
            var button = Header(app, side, header);
            if (!button.IsEffectivelyVisible) continue;
            var block = row.GetVisualDescendants().OfType<Control>().Single(c => c.Name == cell);
            var headerX = button.TranslatePoint(default, app.Window)!.Value.X;
            var cellX = block.TranslatePoint(default, app.Window)!.Value.X - block.Margin.Left;
            Assert.True(Math.Abs(headerX - cellX) <= 1, $"{side} {header}: header at {headerX}, cell at {cellX}.");
        }
    }

    [AvaloniaFact]
    public async Task Hidden_column_is_absent_from_header_and_rows()
    {
        var settings = AppSettings.Default with
        {
            LeftColumns = Columns((ColumnId.Extension, true, 56), (ColumnId.Size, true, 84),
                (ColumnId.Modified, false, 124), (ColumnId.Mode, true, 84)),
        };
        await using var app = await TestApp.StartAsync(settings: settings);

        Assert.False(Header(app, PanelSide.Left, "SortByDate").IsEffectivelyVisible);
        Assert.True(Header(app, PanelSide.Right, "SortByDate").IsEffectivelyVisible);
        var row = RowOf(app, PanelSide.Left, "notes.txt");
        Assert.False(Cell(row, "RowModified").IsEffectivelyVisible);
        Assert.True(Cell(row, "RowSize").IsEffectivelyVisible);

        // Shown again without restart.
        app.Main.Settings.Update(app.Main.Settings.Current with { LeftColumns = AppSettings.DefaultColumns });
        app.Pump();

        Assert.True(Header(app, PanelSide.Left, "SortByDate").IsEffectivelyVisible);
        Assert.True(Cell(RowOf(app, PanelSide.Left, "notes.txt"), "RowModified").IsEffectivelyVisible);
        Assert.Equal(124, Header(app, PanelSide.Left, "SortByDate").Bounds.Width, 1);
    }

    [AvaloniaFact]
    public async Task Row_height_and_font_follow_settings_without_restart()
    {
        await using var app = await TestApp.StartAsync();
        Assert.Equal(AppSettings.DefaultRowHeight, RowOf(app, PanelSide.Left, "notes.txt").Bounds.Height, 1);

        app.Main.Settings.Update(app.Main.Settings.Current with { FontSize = 18, RowHeight = 30 });
        app.Pump();

        foreach (var side in new[] { PanelSide.Left, PanelSide.Right })
        {
            var row = RowOf(app, side, "notes.txt");
            Assert.Equal(30, row.Bounds.Height, 1);
            Assert.Equal(18, Cell(row, "RowName").FontSize);
            Assert.Equal(18, Cell(row, "RowSize").FontSize);
            foreach (var header in new[] { "SortByName", "SortBySize", "SortByMode" })
                Assert.Equal(18, Header(app, side, header).FontSize);
        }
        AssertHeadersLineUpWithCells(app, PanelSide.Left);
        var list = app.FileList(PanelSide.Left);
        Assert.Equal(Math.Max(1, (int)(list.Bounds.Height / 30) - 1), app.Main.PageSize);
    }

    [AvaloniaFact]
    public async Task Row_matching_a_rule_gets_the_palette_brush_and_marked_row_keeps_marked_colour()
    {
        var settings = AppSettings.Default with
        {
            FileColors = [new FileColorRule("*.pdf", "Red"), new FileColorRule("*", "Gray")],
        };
        await using var app = await TestApp.StartAsync(settings: settings);
        app.Main.SetTheme(ThemeChoice.Light);
        app.Pump();

        var pdf = Cell(RowOf(app, PanelSide.Left, "report.pdf"), "RowName");
        Assert.Same(Resource(app, FileColors.ResourceKey("Red")), pdf.Foreground);
        Assert.Same(Resource(app, FileColors.ResourceKey("Red")), Cell(RowOf(app, PanelSide.Left, "report.pdf"), "RowSize").Foreground);
        Assert.Same(Resource(app, FileColors.ResourceKey("Gray")), Cell(RowOf(app, PanelSide.Left, "notes.txt"), "RowName").Foreground);
        var folder = Cell(RowOf(app, PanelSide.Left, "docs"), "RowName").Foreground;
        Assert.NotSame(Resource(app, FileColors.ResourceKey("Gray")), folder);
        Assert.NotSame(Resource(app, FileColors.ResourceKey("Red")), folder);

        // The theme's own variant of the colour, without reading the folder again.
        var lightRed = ((ISolidColorBrush)pdf.Foreground!).Color;
        app.Main.SetTheme(ThemeChoice.Dark);
        app.Pump();
        pdf = Cell(RowOf(app, PanelSide.Left, "report.pdf"), "RowName");
        Assert.Same(Resource(app, FileColors.ResourceKey("Red")), pdf.Foreground);
        Assert.NotEqual(lightRed, ((ISolidColorBrush)pdf.Foreground!).Color);

        // A marked row shows the marked colour, whatever its rule.
        app.Left.Rows.Single(r => r.Name == "report.pdf").IsSelected = true;
        app.Pump();
        Assert.Same(Resource(app, "Tandem.MarkedForeground"), Cell(RowOf(app, PanelSide.Left, "report.pdf"), "RowName").Foreground);
        Assert.Same(Resource(app, "Tandem.MarkedForeground"), Cell(RowOf(app, PanelSide.Left, "report.pdf"), "RowSize").Foreground);
    }

    [AvaloniaFact]
    public async Task Hidden_entries_show_the_hidden_icon_brush_in_both_themes()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddDirectory(TestApp.Home + "/.config");
        fs.AddFile(TestApp.Home + "/.env", "x");
        var settings = AppSettings.Default with { FileColors = [new FileColorRule("*", "Gray")] };
        await using var app = await TestApp.StartAsync(fs, settings: settings);
        app.Main.Workspace.ShowHidden = true;
        app.Pump();

        Image Picture(string name) => Assert.Single(RowOf(app, PanelSide.Left, name).GetVisualDescendants().OfType<Image>());
        Avalonia.Controls.Shapes.Path Body(string name) => RowOf(app, PanelSide.Left, name).GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "HiddenIconBody");

        ISolidColorBrush? previous = null;
        foreach (var theme in new[] { ThemeChoice.Light, ThemeChoice.Dark })
        {
            app.Main.SetTheme(theme);
            app.Pump();
            var hidden = Resource(app, "Tandem.HiddenIcon");
            foreach (var name in new[] { ".config", ".env" })
            {
                Assert.False(Picture(name).IsEffectivelyVisible);
                var body = Body(name);
                Assert.True(body.IsEffectivelyVisible);
                Assert.Same(hidden, body.Fill);
                Assert.Same(Resource(app, "Tandem.HiddenIconBorder"), body.Stroke);
                Assert.Equal(new Size(16, 16), body.Bounds.Size);
            }
            // Folders and files keep different shapes when hidden.
            Assert.NotSame(Body(".config").Data, Body(".env").Data);
            foreach (var name in new[] { "docs", "notes.txt" })
            {
                Assert.True(Picture(name).IsEffectivelyVisible);
                Assert.False(Body(name).IsEffectivelyVisible);
            }
            // A mask rule colours the name; the hidden colour is the icon's only.
            Assert.Same(Resource(app, FileColors.ResourceKey("Gray")), Cell(RowOf(app, PanelSide.Left, ".env"), "RowName").Foreground);
            Assert.NotSame(hidden, Cell(RowOf(app, PanelSide.Left, ".config"), "RowName").Foreground);

            var color = Assert.IsAssignableFrom<ISolidColorBrush>(hidden);
            if (previous is not null) Assert.NotEqual(previous.Color, color.Color);
            previous = color;
        }

        // A marked hidden row: the name takes the marked colour, the icon keeps the hidden one.
        app.Left.Rows.Single(r => r.Name == ".env").IsSelected = true;
        app.Pump();
        Assert.Same(Resource(app, "Tandem.MarkedForeground"), Cell(RowOf(app, PanelSide.Left, ".env"), "RowName").Foreground);
        Assert.Same(Resource(app, "Tandem.HiddenIcon"), Body(".env").Fill);
    }

    [AvaloniaFact]
    public async Task Changing_rules_recolours_rows_without_reading_the_folder_again()
    {
        var other = new InMemoryFileSystem("observed");
        other.AddFile("/f/a.txt", "a");
        other.AddFile("/f/b.md", "b");
        var observed = new ObservedProvider(other);
        await using var app = await TestApp.StartAsync(extraProviders: [observed]);
        await app.Left.NavigateToAsync(new VfsPath("observed", "/f"));
        app.Pump();
        var reads = observed.ReadsOf("/f");
        var before = Cell(RowOf(app, PanelSide.Left, "a.txt"), "RowName").Foreground;
        Assert.NotSame(Resource(app, FileColors.ResourceKey("Blue")), before);
        var colors = app.Main.FileColors;

        app.Main.Settings.Update(app.Main.Settings.Current with { FileColors = [new FileColorRule("*.TXT", "Blue")] });
        app.Pump();

        Assert.NotSame(colors, app.Main.FileColors);
        Assert.Same(Resource(app, FileColors.ResourceKey("Blue")), Cell(RowOf(app, PanelSide.Left, "a.txt"), "RowName").Foreground);
        Assert.NotSame(Resource(app, FileColors.ResourceKey("Blue")), Cell(RowOf(app, PanelSide.Left, "b.md"), "RowName").Foreground);

        app.Main.Settings.Update(app.Main.Settings.Current with { FileColors = [] });
        app.Pump();

        Assert.NotSame(Resource(app, FileColors.ResourceKey("Blue")), Cell(RowOf(app, PanelSide.Left, "a.txt"), "RowName").Foreground);
        Assert.Equal(reads, observed.ReadsOf("/f"));
    }

    [AvaloniaFact]
    public async Task Theme_choice_sets_requested_theme_variant_and_survives_restart()
    {
        AppSettings saved;
        await using (var app = await TestApp.StartAsync())
        {
            Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
            var dark = ThemeItem(app, "Menu_ThemeDark");
            Assert.False(dark.IsChecked);
            Assert.True(ThemeItem(app, "Menu_ThemeSystem").IsChecked);

            dark.Command!.Execute(null);
            app.Pump();

            Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
            Assert.Equal(ThemeChoice.Dark, app.Main.Settings.Current.Theme);
            Assert.True(dark.IsChecked);
            Assert.False(ThemeItem(app, "Menu_ThemeSystem").IsChecked);

            app.Main.SetTheme(ThemeChoice.Light);
            app.Pump();
            Assert.Equal(ThemeVariant.Light, Application.Current.RequestedThemeVariant);
            app.Main.SetTheme(ThemeChoice.System);
            app.Pump();
            Assert.Equal(ThemeVariant.Default, Application.Current.RequestedThemeVariant);
            app.Main.SetTheme(ThemeChoice.Dark);
            saved = new JsonSettingsStore(app.StateDirectory).Load();
        }
        Assert.Equal(ThemeChoice.Dark, saved.Theme);
        Application.Current.RequestedThemeVariant = ThemeVariant.Default;

        await using var restarted = await TestApp.StartAsync(settings: saved);

        Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
        Assert.True(ThemeItem(restarted, "Menu_ThemeDark").IsChecked);
    }

    private static NativeMenuItem ThemeItem(TestApp app, string labelKey) =>
        AllItems(NativeMenu.GetMenu(app.Window)!).Single(i => i.Header == Loc.Get("Menu_Theme")).Menu!.Items
            .OfType<NativeMenuItem>().Single(i => i.Header == Loc.Get(labelKey));

    private static IEnumerable<NativeMenuItem> AllItems(NativeMenu menu)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            yield return item;
            if (item.Menu is { } sub)
                foreach (var inner in AllItems(sub))
                    yield return inner;
        }
    }

    [AvaloniaFact]
    public async Task Hundred_thousand_rows_still_virtualized_with_custom_row_height()
    {
        const int count = 100_000;
        var fs = TestApp.DefaultFileSystem();
        for (var i = 0; i < count; i++) fs.AddFile($"/big/file{i:D6}.txt");
        await using var app = await TestApp.StartAsync(fs,
            settings: AppSettings.Default with { RowHeight = 40, FileColors = [new FileColorRule("*.txt", "Teal")] });

        await app.Left.NavigateToAsync(VfsPath.Local("/big"));
        app.Pump();
        var list = app.FileList(PanelSide.Left);
        Assert.Equal(count + 1, list.ItemCount);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 199);
        Assert.Equal(40, list.ContainerFromIndex(1)!.Bounds.Height, 1);

        app.Left.CursorIndex = count;
        app.Pump();

        Assert.InRange(list.GetRealizedContainers().Count(), 1, 199);
        var last = list.ContainerFromIndex(count)!;
        Assert.Equal(40, last.Bounds.Height, 1);
        Assert.Same(Resource(app, FileColors.ResourceKey("Teal")), Cell(last, "RowName").Foreground);
    }
}
