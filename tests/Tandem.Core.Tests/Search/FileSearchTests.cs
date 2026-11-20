using System.Text;
using Tandem.Core.Search;
using Tandem.Core.Tests.Vfs;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.Core.Tests.Search;

public class FileSearchTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryFileSystem _fs = new();
    private readonly CountingProvider _counting;
    private readonly FileSearch _search;

    public FileSearchTests()
    {
        _counting = new CountingProvider(_fs);
        var providers = new ProviderRegistry();
        providers.Register(_counting);
        _search = new FileSearch(providers);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VfsPath P(string path) => new("mem", path);

    private static SearchQuery Query(string root = "/r") => new(P(root));

    private async Task<List<string>> Run(SearchQuery query, IProgress<SearchProgress>? progress = null)
    {
        var found = new List<string>();
        await foreach (var entry in _search.RunAsync(query, progress, Ct)) found.Add(entry.Path.Path);
        return found;
    }

    private static List<string> Sorted(IEnumerable<string> paths) => paths.Order(StringComparer.Ordinal).ToList();

    /// <summary>Records every report, in order, on the reporting thread.</summary>
    private sealed class Recorder : IProgress<SearchProgress>
    {
        private readonly Lock _lock = new();
        private readonly List<SearchProgress> _reports = [];

        public IReadOnlyList<SearchProgress> Reports
        {
            get { lock (_lock) return _reports.ToArray(); }
        }

        public void Report(SearchProgress value)
        {
            lock (_lock) _reports.Add(value);
        }
    }

    [Fact]
    public async Task Finds_by_several_masks()
    {
        _fs.AddFile("/r/a.txt");
        _fs.AddFile("/r/b.MD");
        _fs.AddFile("/r/c.log");
        _fs.AddFile("/r/sub/d.txt");
        _fs.AddFile("/r/sub/e.png");

        var found = await Run(Query() with { NameMasks = "*.txt;*.md" });

        Assert.Equal(["/r/a.txt", "/r/b.MD", "/r/sub/d.txt"], Sorted(found));
    }

    [Fact]
    public async Task Regex_name_matches_and_invalid_regex_is_rejected()
    {
        _fs.AddFile("/r/report-2026.pdf");
        _fs.AddFile("/r/REPORT-1999.pdf");
        _fs.AddFile("/r/report-x.pdf");
        _fs.AddFile("/r/notes.txt");

        var found = await Run(Query() with { NameMasks = @"^report-\d+\.pdf$", NameIsRegex = true });

        Assert.Equal(["/r/REPORT-1999.pdf", "/r/report-2026.pdf"], Sorted(found));

        var enumerated = _counting.Count("Enumerate");
        var invalid = Query() with { NameMasks = "report-(", NameIsRegex = true };
        var thrown = Assert.Throws<SearchQueryException>(() => FileSearch.Validate(invalid));
        Assert.Equal(SearchField.Name, thrown.Field);
        var fromRun = await Assert.ThrowsAsync<SearchQueryException>(() => Run(invalid));
        Assert.Equal(SearchField.Name, fromRun.Field);
        Assert.Equal(enumerated, _counting.Count("Enumerate")); // rejected before anything is read
    }

    [Fact]
    public void Content_is_rejected_until_content_search_exists()
    {
        var query = Query() with { Content = new ContentQuery("hello") };

        var thrown = Assert.Throws<SearchQueryException>(() => FileSearch.Validate(query));

        Assert.Equal(SearchField.Content, thrown.Field);
    }

    [Fact]
    public async Task Size_range_is_inclusive_and_leaves_folders_out()
    {
        _fs.AddFile("/r/s9.bin", new byte[9]);
        _fs.AddFile("/r/s10.bin", new byte[10]);
        _fs.AddFile("/r/s15.bin", new byte[15]);
        _fs.AddFile("/r/dir/s20.bin", new byte[20]);
        _fs.AddFile("/r/dir/s21.bin", new byte[21]);

        var found = await Run(Query() with { MinSize = 10, MaxSize = 20 });

        Assert.Equal(["/r/dir/s20.bin", "/r/s10.bin", "/r/s15.bin"], Sorted(found));
        Assert.Equal(["/r/dir/s20.bin", "/r/dir/s21.bin"], Sorted(await Run(Query() with { MinSize = 20 })));
        Assert.Equal(["/r/s9.bin"], Sorted(await Run(Query() with { MaxSize = 9 })));
    }

    [Fact]
    public async Task Date_range_is_inclusive()
    {
        _fs.AddFile("/r/before.txt", modified: Day.AddDays(-1).AddTicks(-1));
        _fs.AddFile("/r/from.txt", modified: Day.AddDays(-1));
        _fs.AddFile("/r/to.txt", modified: Day.AddDays(1));
        _fs.AddFile("/r/after.txt", modified: Day.AddDays(1).AddTicks(1));
        _fs.AddDirectory("/r/old");
        _fs.AddDirectory("/r/mid");
        await _fs.SetModifiedAsync(P("/r/old"), Day.AddDays(-10), Ct);
        await _fs.SetModifiedAsync(P("/r/mid"), Day, Ct);
        _fs.AddFile("/r/old/inside.txt", modified: Day); // the folder is not a result, but it is still searched

        var found = await Run(Query() with { ModifiedFrom = Day.AddDays(-1), ModifiedTo = Day.AddDays(1) });

        Assert.Equal(["/r/from.txt", "/r/mid", "/r/old/inside.txt", "/r/to.txt"], Sorted(found));
    }

    [Fact]
    public async Task MaxDepth_zero_stays_in_the_root()
    {
        _fs.AddFile("/r/top.txt");
        _fs.AddFile("/r/a/one.txt");
        _fs.AddFile("/r/a/b/two.txt");

        Assert.Equal(["/r/a", "/r/top.txt"], Sorted(await Run(Query() with { MaxDepth = 0 })));
        Assert.Equal(0, _counting.Count("Enumerate", P("/r/a")));

        Assert.Equal(["/r/a", "/r/a/b", "/r/a/one.txt", "/r/top.txt"], Sorted(await Run(Query() with { MaxDepth = 1 })));
        Assert.Equal(0, _counting.Count("Enumerate", P("/r/a/b")));

        Assert.Equal(["/r/a", "/r/a/b", "/r/a/b/two.txt", "/r/a/one.txt", "/r/top.txt"],
            Sorted(await Run(Query() with { MaxDepth = null })));
    }

    [Fact]
    public async Task Negative_MaxDepth_is_rejected()
    {
        var query = Query() with { MaxDepth = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(() => FileSearch.Validate(query));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Run(query));
        Assert.Equal(0, _counting.Count("Enumerate"));
    }

    [Fact]
    public async Task Excluded_folder_is_neither_listed_nor_entered()
    {
        _fs.AddFile("/r/keep/a.txt");
        _fs.AddFile("/r/node_modules/b.txt");
        _fs.AddFile("/r/keep/.git/c.txt");
        _fs.AddFile("/r/keep/GIT.txt"); // a file named like an excluded folder is not excluded

        var found = await Run(Query() with { ExcludedFolders = "node_modules;.git;git*", IncludeHidden = true });

        Assert.Equal(["/r/keep", "/r/keep/GIT.txt", "/r/keep/a.txt"], Sorted(found));
        Assert.Equal(0, _counting.Count("Enumerate", P("/r/node_modules")));
        Assert.Equal(0, _counting.Count("Enumerate", P("/r/keep/.git")));
    }

    [Fact]
    public async Task Hidden_entries_are_skipped_unless_asked()
    {
        _fs.AddFile("/r/visible.txt");
        _fs.AddFile("/r/.hidden.txt");
        _fs.AddFile("/r/.config/inside.txt");

        Assert.Equal(["/r/visible.txt"], Sorted(await Run(Query())));
        Assert.Equal(0, _counting.Count("Enumerate", P("/r/.config")));

        Assert.Equal(["/r/.config", "/r/.config/inside.txt", "/r/.hidden.txt", "/r/visible.txt"],
            Sorted(await Run(Query() with { IncludeHidden = true })));
    }

    [Fact]
    public async Task Folders_match_by_name_when_no_content_and_no_size()
    {
        _fs.AddFile("/r/photos/beach.jpg");
        _fs.AddFile("/r/photos-old/x.txt");
        _fs.AddDirectory("/r/docs/photos");

        var byName = await Run(Query() with { NameMasks = "photos*" });
        Assert.Equal(["/r/docs/photos", "/r/photos", "/r/photos-old"], Sorted(byName));

        var withSize = await Run(Query() with { NameMasks = "photos*", MinSize = 0 });
        Assert.Empty(withSize);
        Assert.Equal(["/r/photos/beach.jpg"], Sorted(await Run(Query() with { NameMasks = "*.jpg", MinSize = 0 })));
    }

    [Fact]
    public async Task Nfd_name_matches_nfc_mask()
    {
        var nfd = "Café menu.txt".Normalize(NormalizationForm.FormD);
        _fs.AddFile("/r/" + nfd);
        _fs.AddFile("/r/other.txt");

        Assert.Equal(["/r/" + nfd], await Run(Query() with { NameMasks = "café*" }));
        Assert.Equal(["/r/" + nfd], await Run(Query() with { NameMasks = "^café m", NameIsRegex = true }));
        Assert.Equal(["/r/" + nfd], await Run(Query() with { NameMasks = "^caf. m", NameIsRegex = true }));
    }

    [Fact]
    public async Task Symlink_loop_ends_and_each_file_is_found_once()
    {
        _fs.AddFile("/r/a/x.txt");
        await _fs.CreateSymlinkAsync(P("/r/a/loop"), "/r", Ct);
        await _fs.CreateSymlinkAsync(P("/r/self"), "self", Ct);

        var found = await Run(Query());

        Assert.Equal(["/r/a", "/r/a/loop", "/r/a/x.txt", "/r/self"], Sorted(found));
        Assert.Single(found, p => p == "/r/a/x.txt");
        Assert.Equal(0, _counting.Count("Enumerate", P("/r/a/loop")));
        Assert.Equal(0, _counting.Count("OpenRead"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreadable_folder_is_skipped_and_counted(bool unauthorized)
    {
        _fs.AddFile("/r/a.txt");
        _fs.AddFile("/r/secret/hidden-away.txt");
        _fs.AddFile("/r/z/b.txt");
        _fs.FailOn((op, path) => op == "Enumerate" && path.Path == "/r/secret",
            unauthorized ? () => new UnauthorizedAccessException("denied") : () => new VfsAccessDeniedException("denied"));
        var progress = new Recorder();

        var found = await Run(Query(), progress);

        Assert.Equal(["/r/a.txt", "/r/secret", "/r/z", "/r/z/b.txt"], Sorted(found));
        var last = progress.Reports[^1];
        Assert.Equal(1, last.FoldersSkipped);
        Assert.Equal(2, last.FoldersScanned); // /r and /r/z
    }

    [Fact]
    public async Task Unreadable_root_throws()
    {
        _fs.AddFile("/r/a.txt");
        _fs.FailOn((op, path) => op == "Enumerate" && path.Path == "/r", () => new VfsAccessDeniedException("denied"));

        await Assert.ThrowsAsync<VfsAccessDeniedException>(() => Run(Query()));
        await Assert.ThrowsAsync<VfsNotFoundException>(() => Run(Query("/missing")));
    }

    [Fact]
    public async Task Root_itself_is_never_a_result()
    {
        _fs.AddFile("/r/r");

        Assert.Equal(["/r/r"], await Run(Query() with { NameMasks = "r" }));
    }

    [Fact]
    public async Task First_result_arrives_before_the_walk_ends_and_cancel_stops_it()
    {
        for (var i = 0; i < 1000; i++) _fs.AddFile($"/r/d{i:D4}/f.txt");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await using var results = _search.RunAsync(Query() with { NameMasks = "*.txt" }, null, cts.Token)
            .GetAsyncEnumerator(Ct);
        Assert.True(await results.MoveNextAsync());
        Assert.Equal("f.txt", results.Current.Name);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await results.MoveNextAsync());
        Assert.True(_counting.Count("Enumerate") < 1000, $"Enumerate was called {_counting.Count("Enumerate")} times");
    }

    [Fact]
    public async Task Hundred_thousand_files_are_searched()
    {
        for (var d = 0; d < 100; d++)
            for (var f = 0; f < 1000; f++)
                _fs.AddFile($"/r/d{d:D2}/f{f:D4}.{(f % 4 == 0 ? "log" : "txt")}");
        var progress = new Recorder();

        var found = 0;
        await foreach (var _ in _search.RunAsync(Query() with { NameMasks = "*.log" }, progress, Ct)) found++;

        Assert.Equal(25_000, found);
        var last = progress.Reports[^1];
        Assert.Equal(100_000, last.FilesChecked);
        Assert.Equal(25_000, last.Found);
        Assert.Equal(101, last.FoldersScanned);
    }

    [Fact]
    public async Task Progress_reports_final_counts()
    {
        _fs.AddFile("/r/a.txt");
        _fs.AddFile("/r/b.log");
        _fs.AddFile("/r/.h.txt");
        _fs.AddFile("/r/sub/c.txt");
        _fs.AddFile("/r/bad/d.txt");
        _fs.FailOn((op, path) => op == "Enumerate" && path.Path == "/r/bad", () => new VfsException("broken"));
        var progress = new Recorder();

        var found = await Run(Query() with { NameMasks = "*.txt" }, progress);

        Assert.Equal(2, found.Count);
        Assert.NotEmpty(progress.Reports);
        Assert.Equal(new SearchProgress(FoldersScanned: 2, FilesChecked: 3, Found: 2, FoldersSkipped: 1,
            FilesSkipped: 0, CurrentFolder: null), progress.Reports[^1]);
        Assert.Single(progress.Reports, r => r.CurrentFolder is null);
    }
}
