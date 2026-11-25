using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Tandem.App.Commands;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.Core.Settings;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

// Start-up applies the saved language process-wide, so these tests run in the language collection.
[Collection(LanguageCollection.Name)]
public class ListModeTests : LanguageTestBase
{
    // The running system's chords: Ctrl+F1 and Ctrl+F2 on Windows, Ctrl+Cmd+B and Ctrl+Cmd+D on macOS.
    private static KeyChord BriefChord => KeyMap.Platform.ChordsFor(CommandIds.ViewBrief)[0];
    private static KeyChord FullChord => KeyMap.Platform.ChordsFor(CommandIds.ViewFull)[0];

    /// <summary>Home holds the default entries and twenty files more, f00.txt to f19.txt.</summary>
    private static InMemoryFileSystem ManyFiles()
    {
        var fs = TestApp.DefaultFileSystem();
        for (var i = 0; i < 20; i++) fs.AddFile($"{TestApp.Home}/f{i:00}.txt");
        return fs;
    }

    private static Task<bool> Press(TestApp app, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        app.Main.HandleKeyAsync(new KeyChord(key, modifiers));

    [Fact]
    public void Windows_switches_with_Ctrl_F1_and_Ctrl_F2_and_macOS_with_Ctrl_Cmd_B_and_D()
    {
        var windows = KeyMap.ForWindows(KeyMap.Default);
        Assert.Equal([KeyChord.Parse("Ctrl+F1")], windows.ChordsFor(CommandIds.ViewBrief));
        Assert.Equal([KeyChord.Parse("Ctrl+F2")], windows.ChordsFor(CommandIds.ViewFull));
        Assert.Equal([KeyChord.Parse("Ctrl+Cmd+B")], KeyMap.Default.ChordsFor(CommandIds.ViewBrief));
        Assert.Equal([KeyChord.Parse("Ctrl+Cmd+D")], KeyMap.Default.ChordsFor(CommandIds.ViewFull));
    }

    [AvaloniaFact]
    public async Task Panels_start_in_the_full_view()
    {
        await using var app = await TestApp.StartAsync(show: false);

        Assert.Equal(ListMode.Full, app.Main.Left.ListMode);
        Assert.False(app.Main.Left.IsBrief);
        Assert.False(app.Main.Right.IsBrief);
    }

    [AvaloniaFact]
    public async Task View_chords_switch_only_the_active_panel()
    {
        await using var app = await TestApp.StartAsync(show: false);

        Assert.True(await app.Main.HandleKeyAsync(BriefChord));
        Assert.True(app.Main.Left.IsBrief);
        Assert.False(app.Main.Right.IsBrief);
        Assert.Equal(ListMode.Brief, app.Main.Workspace.View.LeftList);
        Assert.Equal(ListMode.Full, app.Main.Workspace.View.RightList);

        app.Main.Workspace.SwitchPanel();
        Assert.True(await app.Main.HandleKeyAsync(BriefChord));
        Assert.True(app.Main.Right.IsBrief);

        Assert.True(await app.Main.HandleKeyAsync(FullChord));
        Assert.False(app.Main.Right.IsBrief);
        Assert.True(app.Main.Left.IsBrief);
        Assert.Empty(app.Failures);
    }

    [AvaloniaFact]
    public async Task List_mode_raises_property_changes_for_the_view()
    {
        await using var app = await TestApp.StartAsync(show: false);
        var changed = new List<string?>();
        app.Main.Left.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await app.Main.Commands.ExecuteAsync(CommandIds.ViewBrief);

        Assert.Contains(nameof(SideViewModel.ListMode), changed);
        Assert.Contains(nameof(SideViewModel.IsBrief), changed);
    }

    [AvaloniaFact]
    public async Task Saved_list_modes_are_restored_per_side()
    {
        var state = AppState.Default(TestApp.Home) with
        {
            Language = "en", View = new ViewState(RightList: ListMode.Brief),
        };
        await using var app = await TestApp.StartAsync(state: state, show: false);

        Assert.False(app.Main.Left.IsBrief);
        Assert.True(app.Main.Right.IsBrief);
    }

    [AvaloniaFact]
    public async Task In_the_brief_view_left_and_right_move_by_a_column()
    {
        await using var app = await TestApp.StartAsync(ManyFiles(), show: false);
        await app.Main.Commands.ExecuteAsync(CommandIds.ViewBrief);
        app.Main.Left.ReportBriefLayout(rowsPerColumn: 5, visibleColumns: 2);
        app.Left.MoveCursor(1, extendSelection: false);

        Assert.True(await Press(app, Key.Right));
        Assert.Equal(6, app.Left.CursorIndex);
        Assert.True(await Press(app, Key.Down));
        Assert.Equal(7, app.Left.CursorIndex);
        Assert.True(await Press(app, Key.Left));
        Assert.Equal(2, app.Left.CursorIndex);
        // Past the first column: the first row, as Home.
        Assert.True(await Press(app, Key.Left));
        Assert.Equal(0, app.Left.CursorIndex);
    }

    [AvaloniaFact]
    public async Task In_the_brief_view_right_stops_at_the_last_row()
    {
        await using var app = await TestApp.StartAsync(ManyFiles(), show: false);
        await app.Main.Commands.ExecuteAsync(CommandIds.ViewBrief);
        app.Main.Left.ReportBriefLayout(rowsPerColumn: 10, visibleColumns: 1);
        var last = app.Left.Rows.Count - 1;
        app.Left.MoveCursor(last - 3, extendSelection: false);

        Assert.True(await Press(app, Key.Right));

        Assert.Equal(last, app.Left.CursorIndex);
    }

    [AvaloniaFact]
    public async Task In_the_brief_view_page_keys_move_by_the_columns_shown()
    {
        await using var app = await TestApp.StartAsync(ManyFiles(), show: false);
        await app.Main.Commands.ExecuteAsync(CommandIds.ViewBrief);
        app.Main.Left.ReportBriefLayout(rowsPerColumn: 4, visibleColumns: 2);

        Assert.True(await Press(app, Key.PageDown));
        Assert.Equal(8, app.Left.CursorIndex);
        Assert.True(await Press(app, Key.PageDown));
        Assert.Equal(16, app.Left.CursorIndex);
        Assert.True(await Press(app, Key.PageUp));
        Assert.Equal(8, app.Left.CursorIndex);
        Assert.True(await Press(app, Key.End));
        Assert.Equal(app.Left.Rows.Count - 1, app.Left.CursorIndex);
        Assert.True(await Press(app, Key.Home));
        Assert.Equal(0, app.Left.CursorIndex);
    }

    [AvaloniaFact]
    public async Task Shift_right_in_the_brief_view_marks_the_rows_passed_over()
    {
        await using var app = await TestApp.StartAsync(ManyFiles(), show: false);
        await app.Main.Commands.ExecuteAsync(CommandIds.ViewBrief);
        app.Main.Left.ReportBriefLayout(rowsPerColumn: 3, visibleColumns: 2);
        app.Left.MoveCursor(1, extendSelection: false);

        Assert.True(await Press(app, Key.Right, KeyModifiers.Shift));

        Assert.Equal(4, app.Left.CursorIndex);
        Assert.Equal([1, 2, 3], app.Left.Rows.Select((r, i) => (r, i)).Where(x => x.r.IsSelected).Select(x => x.i));
    }

    [AvaloniaFact]
    public async Task In_the_full_view_left_and_right_leave_the_cursor_alone()
    {
        await using var app = await TestApp.StartAsync(ManyFiles(), show: false);
        app.Main.Left.ReportBriefLayout(rowsPerColumn: 5, visibleColumns: 2);
        app.Left.MoveCursor(3, extendSelection: false);

        await Press(app, Key.Right);
        await Press(app, Key.Left);

        Assert.Equal(3, app.Left.CursorIndex);
    }

    [AvaloniaFact]
    public async Task The_other_side_keeps_its_own_brief_geometry()
    {
        await using var app = await TestApp.StartAsync(ManyFiles(), show: false);
        await app.Main.Commands.ExecuteAsync(CommandIds.ViewBrief);
        app.Main.Left.ReportBriefLayout(rowsPerColumn: 5, visibleColumns: 2);
        app.Main.Right.ReportBriefLayout(rowsPerColumn: 2, visibleColumns: 1);

        await Press(app, Key.Right);

        Assert.Equal(5, app.Left.CursorIndex);
    }
}
