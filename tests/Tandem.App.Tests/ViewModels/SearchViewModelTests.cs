using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels;
using Tandem.App.ViewModels.Search;
using Tandem.App.Views.Search;
using Tandem.Core.Search;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Core.Viewing;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The file search model over an in-memory "local disk" (no wall-clock waits: the flush pause is
/// immediate), and its "go to" and "show in panel" through the whole app.</summary>
[Collection(LanguageCollection.Name)]
public class SearchViewModelTests : LanguageTestBase
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Immediate = (_, _) => Task.CompletedTask;

    /// <summary>/r/a/1.txt, /r/a/2.txt, /r/b/3.txt and /r/b/note.md.</summary>
    private static InMemoryFileSystem FileSystem()
    {
        var fs = new InMemoryFileSystem(VfsPath.LocalProviderId);
        fs.AddFile("/r/a/1.txt", "one");
        fs.AddFile("/r/a/2.txt", "two");
        fs.AddFile("/r/b/3.txt", "three");
        fs.AddFile("/r/b/note.md", "note");
        return fs;
    }

    private static SearchViewModel New(InMemoryFileSystem fs, string root = "/r",
        Func<FileEntry, Task>? goTo = null, Func<IReadOnlyList<VfsPath>, string, Task>? showInPanel = null)
    {
        var providers = new ProviderRegistry();
        providers.Register(fs);
        return new SearchViewModel(providers, VfsPath.Local(root), goTo ?? (_ => Task.CompletedTask),
            showInPanel ?? ((_, _) => Task.CompletedTask), Immediate);
    }

    /// <summary>Holds the walk when it starts reading <paramref name="folder"/>, until the returned gate is set.
    /// The walk runs off the UI thread, so only it waits.</summary>
    private static Gate HoldAt(InMemoryFileSystem fs, string folder)
    {
        var gate = new Gate();
        fs.FailOn((op, path) =>
        {
            if (op == "Enumerate" && path.Path == folder) gate.Wait();
            return false;
        }, () => new InvalidOperationException());
        return gate;
    }

    /// <summary>Completes once <paramref name="search"/> shows <paramref name="count"/> results.</summary>
    private static Task Shown(SearchViewModel search, int count)
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (search.Results.Count >= count) shown.SetResult();
        search.Results.CollectionChanged += OnChanged;
        return shown.Task;

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (search.Results.Count >= count) shown.TrySetResult();
        }
    }

    private static string[] Found(SearchViewModel search) =>
        search.Results.Select(r => r.Entry.Path.Path).Order(StringComparer.Ordinal).ToArray();

    [AvaloniaFact]
    public async Task Start_streams_results_and_ends_with_done_status()
    {
        var fs = FileSystem();
        using var gate = HoldAt(fs, "/r/b");
        using var search = New(fs);
        search.NameMasks = "*.txt";

        var run = search.StartAsync();
        await Shown(search, 2);

        Assert.True(search.IsRunning);
        Assert.StartsWith("Searching", search.StatusText);
        Assert.Equal(["/r/a/1.txt", "/r/a/2.txt"], Found(search));
        gate.Set();
        await run;

        Assert.False(search.IsRunning);
        Assert.Equal(["/r/a/1.txt", "/r/a/2.txt", "/r/b/3.txt"], Found(search));
        Assert.Equal(Loc.Format("Search_StatusDone", 3, 3, 0, 0), search.StatusText);
        Assert.StartsWith("Done", search.StatusText);
        Assert.Null(search.Error);
        var row = search.Results.Single(r => r.Name == "3.txt");
        Assert.Equal("/r/b", row.Folder);
        Assert.Equal("5 B", row.SizeText);
        Assert.Equal(row.Entry.Modified.ToLocalTime().ToString("g", Loc.Culture), row.DateText);
    }

    [AvaloniaFact]
    public async Task Stop_cancels_and_keeps_what_was_found()
    {
        var fs = FileSystem();
        using var gate = HoldAt(fs, "/r/b");
        using var search = New(fs);
        search.NameMasks = "*.txt";

        var run = search.StartAsync();
        await Shown(search, 2);
        search.Stop();

        Assert.False(search.IsRunning);
        Assert.StartsWith("Stopped", search.StatusText);
        gate.Set();
        await run;
        Assert.Equal(["/r/a/1.txt", "/r/a/2.txt"], Found(search));
        Assert.False(search.IsRunning);
        Assert.StartsWith("Stopped", search.StatusText);
    }

    [AvaloniaFact]
    public async Task Invalid_regex_shows_error_and_does_not_start()
    {
        using var search = New(FileSystem());
        search.NameMasks = "1.txt";
        await search.StartAsync();
        Assert.Single(search.Results);

        search.NameMasks = "(";
        search.NameIsRegex = true;
        await search.StartAsync();

        Assert.StartsWith(Loc.Format("Search_BadNameRegex", ""), search.Error);
        Assert.Single(search.Results); // not cleared: nothing started
        Assert.False(search.IsRunning);

        search.NameIsRegex = false;
        search.NameMasks = "";
        search.SearchContent = true;
        search.ContentText = "[";
        search.ContentIsRegex = true;
        await search.StartAsync();
        Assert.StartsWith(Loc.Format("Search_BadContent", ""), search.Error);
        Assert.Single(search.Results);

        search.ContentIsRegex = false;
        search.ContentText = "one";
        await search.StartAsync();
        Assert.Null(search.Error);
        Assert.Equal(["/r/a/1.txt"], Found(search));
    }

    [AvaloniaFact]
    public async Task Size_unit_and_date_fields_build_the_query()
    {
        var fs = FileSystem();
        var lastSecond = new DateTimeOffset(new DateTime(2026, 11, 10, 23, 59, 59, DateTimeKind.Local));
        fs.AddFile("/r/late.txt", "late", lastSecond);
        fs.AddFile("/r/next.txt", "next", lastSecond.AddSeconds(1));
        fs.AddFile("/r/early.txt", "early", new DateTimeOffset(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Local)));
        fs.AddFile("/r/before.txt", "before",
            new DateTimeOffset(new DateTime(2026, 10, 31, 23, 59, 59, DateTimeKind.Local)));
        using var search = New(fs);

        search.MinSizeText = "2";
        search.MaxSizeText = " 3 ";
        search.SizeUnit = SizeUnit.Megabytes;
        search.ModifiedFrom = new DateTime(2026, 11, 1, 15, 30, 0);
        search.ModifiedTo = new DateTime(2026, 11, 10);
        search.MaxDepthText = "1";
        search.ExcludedFolders = "b";
        search.IncludeHidden = true;
        search.NameMasks = "*.txt";
        search.SearchContent = true;
        search.ContentText = "x";
        search.ContentCaseSensitive = true;
        search.ContentEncoding = TextEncodings.Windows1251;
        var query = search.BuildQuery();

        Assert.NotNull(query);
        Assert.Equal(2_097_152, query.MinSize);
        Assert.Equal(3_145_728, query.MaxSize);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Local)), query.ModifiedFrom);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Local)).AddTicks(-1),
            query.ModifiedTo);
        Assert.Equal(1, query.MaxDepth);
        Assert.Equal("b", query.ExcludedFolders);
        Assert.True(query.IncludeHidden);
        Assert.Equal(VfsPath.Local("/r"), query.Root);
        Assert.Equal(new ContentQuery("x", false, true, TextEncodings.Windows1251.Id), query.Content);

        foreach (var (unit, factor) in new[]
                 {
                     (SizeUnit.Bytes, 1L), (SizeUnit.Kilobytes, 1024L), (SizeUnit.Gigabytes, 1024L * 1024 * 1024),
                 })
        {
            search.SizeUnit = unit;
            Assert.Equal(2 * factor, search.BuildQuery()!.MinSize);
        }

        // Only the dates and names: "to 10.11" takes all of that day and nothing after it.
        search.MinSizeText = search.MaxSizeText = search.MaxDepthText = search.ExcludedFolders = "";
        search.SearchContent = false;
        await search.StartAsync();
        Assert.Equal(["/r/early.txt", "/r/late.txt"], Found(search));
    }

    [AvaloniaFact]
    public async Task Bad_number_in_size_or_depth_shows_error()
    {
        using var search = New(FileSystem());

        foreach (var (field, text, key) in new[]
                 {
                     ("min", "abc", "Search_BadSize"), ("max", "-1", "Search_BadSize"), ("max", "1e3", "Search_BadSize"),
                     ("depth", "x", "Search_BadDepth"), ("depth", "-2", "Search_BadDepth"),
                     ("depth", "1.5", "Search_BadDepth"),
                 })
        {
            search.MinSizeText = field == "min" ? text : "";
            search.MaxSizeText = field == "max" ? text : "";
            search.MaxDepthText = field == "depth" ? text : "";
            await search.StartAsync();
            Assert.Equal(Loc.Get(key), search.Error);
            Assert.Empty(search.Results);
            Assert.False(search.IsRunning);
        }

        search.MaxDepthText = "";
        search.MinSizeText = "5";
        search.MaxSizeText = "4";
        Assert.Null(search.BuildQuery());
        Assert.Equal(Loc.Get("Search_BadSizeRange"), search.Error);

        search.MinSizeText = "1.5";
        search.MaxSizeText = "";
        search.SizeUnit = SizeUnit.Kilobytes;
        Assert.Equal(1536, search.BuildQuery()!.MinSize);
        Assert.Null(search.Error);

        search.MinSizeText = "";
        search.ModifiedFrom = new DateTime(2026, 11, 2);
        search.ModifiedTo = new DateTime(2026, 11, 1);
        Assert.Null(search.BuildQuery());
        Assert.Equal(Loc.Get("Search_BadDateRange"), search.Error);
    }

    [AvaloniaFact]
    public async Task Huge_size_shows_error_instead_of_overflowing()
    {
        using var search = New(FileSystem());

        foreach (var unit in Enum.GetValues<SizeUnit>())
        {
            search.SizeUnit = unit;
            search.MinSizeText = new string('9', 28); // fits a decimal; times 1024 it does not
            await search.StartAsync();
            Assert.Equal(Loc.Get("Search_BadSize"), search.Error);
            Assert.False(search.IsRunning);
        }

        search.SizeUnit = SizeUnit.Gigabytes;
        search.MinSizeText = (long.MaxValue / (1024L * 1024 * 1024) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Null(search.BuildQuery());
        Assert.Equal(Loc.Get("Search_BadSize"), search.Error);
        search.MinSizeText = (long.MaxValue / (1024L * 1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotNull(search.BuildQuery());
    }

    [AvaloniaFact]
    public async Task Second_start_replaces_previous_results()
    {
        using var search = New(FileSystem());
        search.NameMasks = "1.txt";
        await search.StartAsync();
        Assert.Equal(["/r/a/1.txt"], Found(search));

        search.NameMasks = "*.txt";
        await search.StartAsync();

        Assert.Equal(["/r/a/1.txt", "/r/a/2.txt", "/r/b/3.txt"], Found(search));
        Assert.Equal(Loc.Format("Search_StatusDone", 3, 3, 0, 0), search.StatusText);
    }

    [AvaloniaFact]
    public async Task Start_while_running_stops_the_first_search()
    {
        var fs = FileSystem();
        using var gate = HoldAt(fs, "/r/b");
        using var search = New(fs);
        search.NameMasks = "*.txt";
        var first = search.StartAsync();
        await Shown(search, 2);

        search.NameMasks = "*.md";
        var second = search.StartAsync();
        gate.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(["/r/b/note.md"], Found(search));
        Assert.StartsWith("Done", search.StatusText);
    }

    [AvaloniaFact]
    public async Task Search_stops_at_the_most_results_it_shows()
    {
        Assert.Equal(100_000, SearchViewModel.MaxShownResults);
        var fs = new InMemoryFileSystem(VfsPath.LocalProviderId);
        for (var i = 0; i < 5; i++) fs.AddFile($"/r/{i}.txt");
        var providers = new ProviderRegistry();
        providers.Register(fs);
        using var search = new SearchViewModel(providers, VfsPath.Local("/r"), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask, Immediate, maxShownResults: 3);

        await search.StartAsync();

        Assert.Equal(3, search.Results.Count);
        Assert.Equal(Loc.Format("Search_StatusLimit", 3, 0, 0, 0), search.StatusText);
    }

    [AvaloniaFact]
    public async Task Content_search_with_auto_encoding_finds_1251_file()
    {
        var fs = FileSystem();
        fs.AddFile("/r/cp1251.txt", TextEncodings.Windows1251.Encoding.GetBytes("Привет из старого файла, мир"));
        fs.AddFile("/r/other.txt", "nothing here");
        using var search = New(fs);
        search.SearchContent = true;
        search.ContentText = "привет";
        Assert.Null(search.ContentEncoding);
        Assert.Null(search.SelectedEncoding.Encoding); // "Auto"
        Assert.Equal(TextEncodings.All.Count + 1, search.EncodingChoices.Count);

        await search.StartAsync();

        Assert.Equal(["/r/cp1251.txt"], Found(search));
        Assert.Null(search.BuildQuery()!.Content!.EncodingId);
    }

    [AvaloniaFact]
    public async Task Status_counts_skipped_folders()
    {
        var fs = FileSystem();
        fs.AddFile("/r/secret/hidden.txt");
        fs.FailOn((op, path) => op == "Enumerate" && path.Path == "/r/secret",
            () => new VfsAccessDeniedException("denied"));
        using var search = New(fs);
        search.NameMasks = "*.txt";

        await search.StartAsync();

        Assert.Equal(Loc.Format("Search_StatusDone", 3, 3, 1, 0), search.StatusText);
    }

    [AvaloniaFact]
    public async Task Unreadable_root_shows_error()
    {
        var fs = FileSystem();
        fs.FailOn((op, path) => op == "Enumerate" && path.Path == "/r", () => new VfsAccessDeniedException("denied"));
        using var search = New(fs);

        await search.StartAsync();

        Assert.Equal(Loc.Format("Search_RootFailed", "/r", "denied"), search.Error);
        Assert.False(search.IsRunning);
        Assert.Empty(search.Results);
    }

    // ---- Through the app ----------------------------------------------------------------------------

    private static async Task<(TestApp App, SearchViewModel Search)> OpenAsync()
    {
        var fs = TestApp.DefaultFileSystem();
        fs.AddFile(TestApp.Home + "/docs/deep.txt", "deep");
        var app = await TestApp.StartAsync(fs);
        Assert.True(await app.Main.Commands.ExecuteAsync(CommandIds.SearchFiles));
        app.Pump();
        var window = app.Windows.Open.OfType<SearchWindow>().Single();
        return (app, window.Search);
    }

    [AvaloniaFact]
    public async Task GoTo_opens_the_folder_with_cursor_on_the_file_and_closes()
    {
        var (app, search) = await OpenAsync();
        await using var _ = app;
        var closes = 0;
        search.CloseRequested += (_, _) => closes++;
        search.NameMasks = "deep.txt";
        await search.StartAsync();

        await search.GoToAsync(Assert.Single(search.Results));
        app.Pump();

        Assert.Equal(VfsPath.Local("/home/docs"), app.Left.CurrentPath);
        Assert.Equal("deep.txt", app.Left.CursorRow?.Name);
        Assert.Equal(1, closes);
        Assert.Empty(app.Windows.Open.OfType<SearchWindow>());
        Assert.True(search.IsDisposed);
    }

    [AvaloniaFact]
    public async Task ShowInPanel_opens_a_virtual_folder_with_all_results()
    {
        var (app, search) = await OpenAsync();
        await using var _ = app;
        search.NameMasks = "*.txt;*.md";
        await search.StartAsync();
        var found = search.Results.Select(r => r.Entry.Path).ToArray();
        Assert.Equal(3, found.Length);

        await search.ShowInPanelAsync();
        app.Pump();

        var title = Loc.Format("Search_ResultsTitle", "*.txt;*.md", "/home");
        Assert.Equal("Search: *.txt;*.md in /home", title);
        Assert.Equal(SearchResultsProvider.ProviderId, app.Left.CurrentPath.ProviderId);
        Assert.Equal(title, app.Left.DisplayPath);
        Assert.Equal(title, app.Main.Workspace.Left.Active.Title);
        Assert.Equal(found.OrderBy(p => p.Path, StringComparer.Ordinal),
            app.Left.Rows.Where(r => !r.IsParentLink).Select(r => r.Entry!.Path)
                .OrderBy(p => p.Path, StringComparer.Ordinal));
        Assert.Empty(app.Windows.Open.OfType<SearchWindow>());

        // Up leads back to the folder searched.
        await app.Left.GoUpAsync();
        Assert.Equal(VfsPath.Local("/home"), app.Left.CurrentPath);
        Assert.Empty(app.Failures);
    }
}

/// <summary>A gate a search walk waits at; disposing it opens it, so no walk is left waiting after a test.</summary>
internal sealed class Gate : IDisposable
{
    private readonly ManualResetEventSlim _open = new();

    public void Wait() => _open.Wait();

    public void Set() => _open.Set();

    public void Dispose() => _open.Set();
}
