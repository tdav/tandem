using System.Diagnostics;
using System.Text;
using Avalonia.Headless.XUnit;
using Tandem.App.ViewModels;
using Tandem.Core.Listing;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

[Collection(LanguageCollection.Name)]
public class PanelQuickSearchTests : IDisposable
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly ProviderRegistry _providers = new();
    private PanelViewModel? _panel;

    public PanelQuickSearchTests()
    {
        _providers.Register(new ObservedProvider(_fs));
    }

    public void Dispose()
    {
        _panel?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static VfsPath Mem(string path) => new("mem", path);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>/d with rows [.., a.txt, README.md, report.pdf, zeta].</summary>
    private async Task<PanelViewModel> OpenSampleAsync()
    {
        _fs.AddFile("/d/a.txt");
        _fs.AddFile("/d/README.md");
        _fs.AddFile("/d/report.pdf");
        _fs.AddFile("/d/zeta");
        var panel = _panel = new PanelViewModel(_providers);
        await panel.NavigateToAsync(Mem("/d"));
        Assert.Equal(["..", "a.txt", "README.md", "report.pdf", "zeta"], panel.Rows.Select(r => r.Name));
        return panel;
    }

    [AvaloniaFact]
    public async Task QuickSearch_moves_to_first_prefix_match()
    {
        var panel = await OpenSampleAsync();
        panel.CursorIndex = 4;

        Assert.True(panel.QuickSearchInput("r"));
        Assert.Equal("README.md", panel.CursorRow!.Name);
        Assert.True(panel.QuickSearchInput("e"));
        Assert.Equal("re", panel.QuickSearchText);
        Assert.Equal("README.md", panel.CursorRow!.Name);
        Assert.True(panel.QuickSearchInput("p"));
        Assert.Equal("rep", panel.QuickSearchText);
        Assert.Equal("report.pdf", panel.CursorRow!.Name);

        panel.QuickSearchBackspace();
        Assert.Equal("re", panel.QuickSearchText);
        Assert.Equal("README.md", panel.CursorRow!.Name);

        panel.QuickSearchCancel();
        Assert.Equal("", panel.QuickSearchText);
        Assert.Equal("README.md", panel.CursorRow!.Name);

        Assert.True(panel.QuickSearchInput("REP")); // several characters at once, any case
        Assert.Equal("REP", panel.QuickSearchText);
        Assert.Equal("report.pdf", panel.CursorRow!.Name);
    }

    [AvaloniaFact]
    public async Task QuickSearch_rejects_char_without_match()
    {
        var panel = await OpenSampleAsync();
        Assert.True(panel.QuickSearchInput("re"));
        var cursor = panel.CursorIndex;

        Assert.False(panel.QuickSearchInput("x"));
        Assert.Equal("re", panel.QuickSearchText);
        Assert.Equal(cursor, panel.CursorIndex);

        Assert.False(panel.QuickSearchInput("px")); // "rep" would match, "repx" does not: nothing is accepted
        Assert.Equal("re", panel.QuickSearchText);
        Assert.Equal(cursor, panel.CursorIndex);

        Assert.False(panel.QuickSearchInput(""));
        Assert.Equal("re", panel.QuickSearchText);
    }

    [AvaloniaFact]
    public async Task QuickSearch_never_matches_parent_link()
    {
        var panel = await OpenSampleAsync();
        panel.CursorIndex = 3;

        Assert.False(panel.QuickSearchInput("."));
        Assert.Equal("", panel.QuickSearchText);
        Assert.Equal(3, panel.CursorIndex);
    }

    [AvaloniaFact]
    public async Task QuickSearch_ends_without_moving_when_backspaced_to_empty()
    {
        var panel = await OpenSampleAsync();
        Assert.True(panel.QuickSearchInput("z"));
        Assert.Equal("zeta", panel.CursorRow!.Name);

        panel.QuickSearchBackspace();
        Assert.Equal("", panel.QuickSearchText);
        Assert.Equal("zeta", panel.CursorRow!.Name);

        panel.QuickSearchBackspace(); // nothing to remove
        Assert.Equal("", panel.QuickSearchText);
        Assert.Equal("zeta", panel.CursorRow!.Name);
    }

    [AvaloniaFact]
    public async Task QuickSearch_ends_when_the_cursor_moves()
    {
        var panel = await OpenSampleAsync();

        Assert.True(panel.QuickSearchInput("re"));
        panel.MoveCursor(1, extendSelection: false);
        Assert.Equal("", panel.QuickSearchText);

        Assert.True(panel.QuickSearchInput("re"));
        panel.CursorIndex = 1; // e.g. a click on a row
        Assert.Equal("", panel.QuickSearchText);

        Assert.True(panel.QuickSearchInput("re"));
        panel.ToggleSelectAndMoveDown();
        Assert.Equal("", panel.QuickSearchText);
    }

    [AvaloniaFact]
    public async Task QuickSearch_matches_nfd_name_with_nfc_input()
    {
        var nfd = "й.txt".Normalize(NormalizationForm.FormD);
        Assert.NotEqual("й.txt", nfd);
        _fs.AddFile("/d/a.txt");
        _fs.AddFile("/d/" + nfd);
        var nfc = "ё.txt".Normalize(NormalizationForm.FormC);
        _fs.AddFile("/d/" + nfc);
        var panel = _panel = new PanelViewModel(_providers);
        await panel.NavigateToAsync(Mem("/d"));
        Assert.Contains(panel.Rows, r => r.Name == nfd); // the name is kept as stored

        Assert.True(panel.QuickSearchInput("й")); // "й" in NFC
        Assert.Equal(nfd, panel.CursorRow!.Name);
        Assert.True(panel.QuickSearchInput("."));
        Assert.Equal(nfd, panel.CursorRow!.Name);

        panel.QuickSearchCancel();
        Assert.True(panel.QuickSearchInput("Ё".Normalize(NormalizationForm.FormD))); // NFD input, upper case
        Assert.Equal(nfc, panel.CursorRow!.Name);
    }

    [AvaloniaFact]
    public async Task Odd_names_do_not_break_listing()
    {
        const string emoji = "😀 smile.txt";
        const string newline = "line\nbreak.txt";
        var longName = new string('a', 251) + ".txt";
        Assert.Equal(255, longName.Length);
        _fs.AddFile("/d/" + emoji, "12");
        _fs.AddFile("/d/" + newline, "123");
        _fs.AddFile("/d/" + longName, "1234");
        _fs.AddFile("/d/b.dat", "1");
        var panel = _panel = new PanelViewModel(_providers);

        await panel.NavigateToAsync(Mem("/d"));
        Assert.Equal(5, panel.Rows.Count);
        Assert.Equal(4, panel.TotalCount);
        Assert.Equal(10, panel.TotalBytes);
        Assert.Equivalent(new[] { emoji, newline, longName, "b.dat" }, panel.Rows.Skip(1).Select(r => r.Name));
        Assert.All(panel.Rows, r => _ = (r.SizeText, r.DateText, r.ModeText, r.Extension));

        panel.Sort = new SortSpec(SortColumn.Size, Descending: true);
        await WaitUntil(() => panel.Rows[^1].Name == "b.dat"); // by name it is second
        Assert.Equal([longName, newline, emoji, "b.dat"], panel.Rows.Skip(1).Select(r => r.Name));

        panel.SelectAll();
        Assert.Equal(4, panel.SelectedCount);
        Assert.Equal(10, panel.SelectedBytes);
        Assert.Equal(4, panel.GetOperationTargets().Count);
        panel.UnselectByMask("*\n*");
        Assert.Equal(3, panel.SelectedCount);
        panel.ClearSelection();
        panel.SelectByMask("😀*;a*");
        Assert.Equal(2, panel.SelectedCount);
        Assert.Equal(6, panel.SelectedBytes);
        Assert.Equal("Selected 2 of 4, 6 B", panel.StatusText);

        Assert.True(panel.QuickSearchInput("😀"));
        Assert.Equal(emoji, panel.CursorRow!.Name);
        Assert.True(panel.QuickSearchInput(" "));
        panel.QuickSearchBackspace();
        Assert.Equal("😀", panel.QuickSearchText);
        panel.QuickSearchBackspace(); // the whole emoji, never half of a surrogate pair
        Assert.Equal("", panel.QuickSearchText);

        Assert.True(panel.QuickSearchInput("line\nb"));
        Assert.Equal(newline, panel.CursorRow!.Name);
        panel.QuickSearchCancel();
        Assert.True(panel.QuickSearchInput(new string('A', 251) + ".TXT"));
        Assert.Equal(longName, panel.CursorRow!.Name);
        Assert.False(panel.QuickSearchInput("x"));
        Assert.Equal(2, panel.SelectedCount);
    }

    [AvaloniaFact]
    public async Task QuickSearch_resets_on_navigation()
    {
        var panel = await OpenSampleAsync();
        _fs.AddFile("/other/report.txt");
        Assert.True(panel.QuickSearchInput("re"));

        await panel.NavigateToAsync(Mem("/other"));
        Assert.Equal("", panel.QuickSearchText);

        Assert.True(panel.QuickSearchInput("re"));
        await panel.GoUpAsync();
        Assert.Equal("", panel.QuickSearchText);
        Assert.Equal(Mem("/"), panel.CurrentPath);
    }
}
