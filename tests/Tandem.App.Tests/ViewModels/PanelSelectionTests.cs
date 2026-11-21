using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Tandem.App.ViewModels;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

// In the language collection: the status line is compared with English resources, and the 100k timing test should
// not share the machine with other tests.
[Collection(LanguageCollection.Name)]
public class PanelSelectionTests : IDisposable
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly ProviderRegistry _providers = new();
    private PanelViewModel? _panel;

    public PanelSelectionTests()
    {
        _providers.Register(new ObservedProvider(_fs));
    }

    public void Dispose()
    {
        _panel?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static VfsPath Mem(string path) => new("mem", path);

    private static string[] SelectedNames(PanelViewModel panel) =>
        panel.Rows.Where(r => r.IsSelected).Select(r => r.Name).ToArray();

    private static int IndexOf(PanelViewModel panel, string name) => panel.Rows.ToList().FindIndex(r => r.Name == name);

    private static EntryRowViewModel Row(PanelViewModel panel, string name) => panel.Rows.Single(r => r.Name == name);

    /// <summary>/d with rows [.., sub, a.txt, b.txt, c.md, notes] — sizes 1000, 1048, 300, 0.</summary>
    private async Task<PanelViewModel> OpenSampleAsync()
    {
        _fs.AddDirectory("/d/sub");
        _fs.AddFile("/d/a.txt", new byte[1000]);
        _fs.AddFile("/d/b.txt", new byte[1048]);
        _fs.AddFile("/d/c.md", new byte[300]);
        _fs.AddFile("/d/notes");
        var panel = _panel = new PanelViewModel(_providers);
        await panel.NavigateToAsync(Mem("/d"));
        Assert.Equal(["..", "sub", "a.txt", "b.txt", "c.md", "notes"], panel.Rows.Select(r => r.Name));
        return panel;
    }

    [AvaloniaFact]
    public async Task SelectWhere_marks_matching_rows_and_unmarks_the_rest()
    {
        var panel = await OpenSampleAsync();
        Row(panel, "b.txt").IsSelected = true;
        Row(panel, "notes").IsSelected = true;

        panel.SelectWhere(e => e.Name.EndsWith(".txt", StringComparison.Ordinal) || e.Name == "sub");

        Assert.Equal(["sub", "a.txt", "b.txt"], SelectedNames(panel));
        Assert.Equal(3, panel.SelectedCount);
        Assert.Equal(2048, panel.SelectedBytes);

        panel.SelectWhere(_ => false);
        Assert.Empty(SelectedNames(panel));
        Assert.Equal(0, panel.SelectedCount);
    }

    [AvaloniaFact]
    public async Task SelectWhere_never_marks_the_parent_link_and_updates_the_status()
    {
        var panel = await OpenSampleAsync();
        var changed = new List<string?>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        panel.SelectWhere(_ => true);

        Assert.False(Row(panel, "..").IsSelected);
        Assert.Equal(5, panel.SelectedCount);
        Assert.Equal("Selected 5 of 5, 2.3 KB", panel.StatusText);
        Assert.Contains(nameof(PanelViewModel.SelectedCount), changed);
        Assert.Contains(nameof(PanelViewModel.StatusText), changed);
    }

    [AvaloniaFact]
    public async Task Toggle_selects_and_moves_down()
    {
        var panel = await OpenSampleAsync();
        panel.CursorIndex = 2;

        panel.ToggleSelectAndMoveDown();
        Assert.Equal(["a.txt"], SelectedNames(panel));
        Assert.Equal(3, panel.CursorIndex);

        panel.ToggleSelectAndMoveDown();
        Assert.Equal(["a.txt", "b.txt"], SelectedNames(panel));
        Assert.Equal(4, panel.CursorIndex);

        panel.CursorIndex = 2;
        panel.ToggleSelectAndMoveDown(); // toggles back off
        Assert.Equal(["b.txt"], SelectedNames(panel));

        panel.CursorIndex = 5;
        panel.ToggleSelectAndMoveDown(); // the last row: selected, the cursor stays
        Assert.Equal(["b.txt", "notes"], SelectedNames(panel));
        Assert.Equal(5, panel.CursorIndex);
        Assert.Equal(2, panel.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Parent_link_is_never_selected()
    {
        var panel = await OpenSampleAsync();
        var parent = panel.Rows[0];

        panel.CursorIndex = 0;
        panel.ToggleSelectAndMoveDown();
        Assert.False(parent.IsSelected);
        Assert.Equal(1, panel.CursorIndex);
        Assert.Equal(0, panel.SelectedCount);

        panel.SelectAll();
        Assert.False(parent.IsSelected);
        Assert.Equal(5, panel.SelectedCount);

        panel.InvertSelection();
        Assert.False(parent.IsSelected);
        Assert.Equal(0, panel.SelectedCount);

        panel.InvertSelection();
        Assert.False(parent.IsSelected);
        Assert.Equal(5, panel.SelectedCount);

        panel.ClearSelection();
        parent.IsSelected = true; // as a binding would
        Assert.False(parent.IsSelected);
        Assert.Equal(0, panel.SelectedCount);

        panel.SelectByMask("*");
        panel.CursorIndex = 0;
        panel.MoveCursor(2, extendSelection: true);
        Assert.False(parent.IsSelected);
        Assert.Equal(5, panel.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Shift_move_selects_range()
    {
        var panel = await OpenSampleAsync();
        panel.CursorIndex = 2;

        panel.MoveCursor(1, extendSelection: true); // Shift+Down: the row left is selected, the row reached is not
        Assert.Equal(["a.txt"], SelectedNames(panel));
        Assert.Equal(3, panel.CursorIndex);

        panel.MoveCursor(1, extendSelection: true);
        Assert.Equal(["a.txt", "b.txt"], SelectedNames(panel));
        Assert.Equal(4, panel.CursorIndex);
        Assert.Equal(2, panel.SelectedCount);
        Assert.Equal(2048, panel.SelectedBytes);
    }

    [AvaloniaFact]
    public async Task Shift_move_up_selects_rows_left_behind()
    {
        var panel = await OpenSampleAsync();
        panel.CursorIndex = 5;

        panel.MoveCursor(-1, extendSelection: true);
        Assert.Equal(["notes"], SelectedNames(panel));
        Assert.Equal(4, panel.CursorIndex);

        panel.MoveCursor(-2, extendSelection: true);
        Assert.Equal(["b.txt", "c.md", "notes"], SelectedNames(panel));
        Assert.Equal(2, panel.CursorIndex);
    }

    [AvaloniaFact]
    public async Task Shift_page_move_selects_every_row_passed()
    {
        var panel = await OpenSampleAsync();
        panel.CursorIndex = 1;

        panel.MoveCursor(3, extendSelection: true); // Shift+PageDown with a page of three rows
        Assert.Equal(["sub", "a.txt", "b.txt"], SelectedNames(panel));
        Assert.Equal(4, panel.CursorIndex);
        Assert.Equal(3, panel.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Shift_move_past_the_end_selects_through_the_last_row()
    {
        var panel = await OpenSampleAsync();
        panel.CursorIndex = 4;

        panel.MoveCursor(10, extendSelection: true); // clamped at the last row, which the move passed over too
        Assert.Equal(["c.md", "notes"], SelectedNames(panel));
        Assert.Equal(5, panel.CursorIndex);

        panel.ClearSelection();
        panel.MoveCursor(1, extendSelection: true); // Shift+Down on the last row selects it
        Assert.Equal(["notes"], SelectedNames(panel));
        Assert.Equal(5, panel.CursorIndex);

        panel.ClearSelection();
        panel.CursorIndex = 2;
        panel.MoveCursor(-10, extendSelection: true); // up to the first row, ".." skipped
        Assert.Equal(["sub", "a.txt"], SelectedNames(panel));
        Assert.Equal(0, panel.CursorIndex);
    }

    [AvaloniaFact]
    public async Task MoveCursor_clamps_at_the_ends_and_selects_nothing_without_shift()
    {
        var panel = await OpenSampleAsync();
        panel.CursorIndex = 2;

        panel.MoveCursor(100, extendSelection: false);
        Assert.Equal(5, panel.CursorIndex);
        panel.MoveCursor(-100, extendSelection: false);
        Assert.Equal(0, panel.CursorIndex);
        panel.MoveCursor(2, extendSelection: false);
        Assert.Equal(2, panel.CursorIndex);
        Assert.Empty(SelectedNames(panel));
    }

    [AvaloniaFact]
    public async Task Select_by_mask_and_unselect()
    {
        var panel = await OpenSampleAsync();

        panel.SelectByMask("*.txt");
        Assert.Equal(["a.txt", "b.txt"], SelectedNames(panel));
        Assert.Equal(2, panel.SelectedCount);

        panel.UnselectByMask("a*");
        Assert.Equal(["b.txt"], SelectedNames(panel));
        Assert.Equal(1, panel.SelectedCount);

        panel.SelectByMask("S*;*.MD"); // folders too; case-insensitive; several parts
        Assert.Equal(["sub", "b.txt", "c.md"], SelectedNames(panel));

        panel.UnselectByMask("*.*"); // everything, names without a dot included
        Assert.Empty(SelectedNames(panel));

        panel.SelectByMask("");
        Assert.Equal(5, panel.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Invert_selection()
    {
        var panel = await OpenSampleAsync();
        Row(panel, "a.txt").IsSelected = true;

        panel.InvertSelection();

        Assert.Equal(["sub", "b.txt", "c.md", "notes"], SelectedNames(panel));
        Assert.Equal(4, panel.SelectedCount);
        Assert.Equal(1048 + 300, panel.SelectedBytes);
    }

    [AvaloniaFact]
    public async Task Status_counts_bytes_of_selected_files()
    {
        var panel = await OpenSampleAsync();
        Assert.Equal(5, panel.TotalCount);
        Assert.Equal(1000 + 1048 + 300, panel.TotalBytes);
        Assert.Equal("Selected 0 of 5, 0 B", panel.StatusText);

        var changed = new List<string?>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // through the row, as the view's binding does; a folder counts as 0 bytes
        Row(panel, "a.txt").IsSelected = true;
        Row(panel, "b.txt").IsSelected = true;
        Row(panel, "sub").IsSelected = true;
        Assert.Equal(3, panel.SelectedCount);
        Assert.Equal(2048, panel.SelectedBytes);
        Assert.Equal("Selected 3 of 5, 2.0 KB", panel.StatusText);
        Assert.Contains(nameof(PanelViewModel.StatusText), changed);

        Row(panel, "a.txt").IsSelected = false;
        Row(panel, "b.txt").IsSelected = true; // no change
        Assert.Equal(2, panel.SelectedCount);
        Assert.Equal(1048, panel.SelectedBytes);
        Assert.Equal("Selected 2 of 5, 1.0 KB", panel.StatusText);

        // the listing changes: the totals follow
        _fs.AddFile("/d/new.bin", new byte[10]);
        await panel.RefreshAsync();
        Assert.Equal(6, panel.TotalCount);
        Assert.Equal(2358, panel.TotalBytes);
        Assert.Equal("Selected 2 of 6, 1.0 KB", panel.StatusText);
    }

    [AvaloniaFact]
    public async Task Selection_survives_refresh_and_is_cleared_by_navigation()
    {
        var panel = await OpenSampleAsync();
        panel.SelectByMask("b.txt;c.md;notes");
        await _fs.DeleteAsync(Mem("/d/notes"), recursive: false, CancellationToken.None);

        await panel.RefreshAsync();
        Assert.Equal(["b.txt", "c.md"], SelectedNames(panel));
        Assert.Equal(2, panel.SelectedCount);
        Assert.Equal(1348, panel.SelectedBytes);

        // a row of the restored selection still updates the totals
        Row(panel, "c.md").IsSelected = false;
        Assert.Equal(1, panel.SelectedCount);
        Assert.Equal(1048, panel.SelectedBytes);

        await panel.NavigateToAsync(Mem("/d/sub"));
        await panel.GoUpAsync();
        Assert.Empty(SelectedNames(panel));
        Assert.Equal(0, panel.SelectedCount);
        Assert.Equal(0, panel.SelectedBytes);
    }

    [AvaloniaFact]
    public async Task Rows_no_longer_listed_do_not_change_the_totals()
    {
        var panel = await OpenSampleAsync();
        var oldRow = Row(panel, "a.txt");
        await panel.RefreshAsync(); // new row objects

        oldRow.IsSelected = true;

        Assert.Equal(0, panel.SelectedCount);
        Assert.Equal(0, panel.SelectedBytes);
    }

    [AvaloniaFact]
    public async Task Targets_are_selection_or_cursor_item()
    {
        var panel = await OpenSampleAsync();
        panel.CursorIndex = IndexOf(panel, "a.txt");
        Assert.Equal(["a.txt"], panel.GetOperationTargets().Select(e => e.Name));

        Row(panel, "c.md").IsSelected = true;
        Row(panel, "sub").IsSelected = true;
        Assert.Equal(["sub", "c.md"], panel.GetOperationTargets().Select(e => e.Name)); // in row order

        panel.CursorIndex = 0;
        Assert.Equal(["sub", "c.md"], panel.GetOperationTargets().Select(e => e.Name));
    }

    [AvaloniaFact]
    public async Task Targets_empty_when_cursor_on_parent_link()
    {
        var panel = await OpenSampleAsync();
        panel.CursorIndex = 0;

        Assert.Empty(panel.GetOperationTargets());
    }

    [AvaloniaFact]
    public async Task SelectAll_on_100k_rows_is_fast_and_notifies_once()
    {
        const int count = 100_000;
        for (var i = 0; i < count; i++) _fs.AddFile($"/big/f{i}.txt", "x");
        _fs.AddFile("/warm/a.txt");
        var panel = _panel = new PanelViewModel(_providers);
        await panel.NavigateToAsync(Mem("/warm"));
        panel.SelectAll(); // warm-up: JIT stays out of the timing
        await panel.NavigateToAsync(Mem("/big"));

        var collectionChanges = 0;
        ((INotifyCollectionChanged)panel.Rows).CollectionChanged += (_, _) => collectionChanges++;
        var countChanges = 0;
        var statusChanges = 0;
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelViewModel.SelectedCount)) countChanges++;
            if (e.PropertyName == nameof(PanelViewModel.StatusText)) statusChanges++;
        };

        var clock = Stopwatch.StartNew();
        panel.SelectAll();
        var selectAllMs = clock.ElapsedMilliseconds;

        Assert.InRange(selectAllMs, 0, 199);
        Assert.Equal(count, panel.SelectedCount);
        Assert.Equal(count, panel.SelectedBytes);
        Assert.Equal(1, countChanges);
        Assert.Equal(1, statusChanges);
        Assert.Equal(0, collectionChanges);

        panel.InvertSelection();
        Assert.Equal(0, panel.SelectedCount);
        panel.SelectByMask("f1*");
        Assert.Equal(11_111, panel.SelectedCount);
        Assert.Equal(3, countChanges);
        Assert.Equal(0, collectionChanges);
    }

    [AvaloniaFact]
    public async Task Empty_folder_has_no_targets_and_a_zero_status()
    {
        _fs.AddDirectory("/empty");
        var panel = _panel = new PanelViewModel(_providers);
        await panel.NavigateToAsync(Mem("/"));
        await panel.NavigateToAsync(Mem("/empty"));

        panel.SelectAll();
        panel.ToggleSelectAndMoveDown();
        panel.MoveCursor(1, extendSelection: true);

        Assert.Empty(panel.GetOperationTargets());
        Assert.Equal("Selected 0 of 0, 0 B", panel.StatusText);
    }
}
