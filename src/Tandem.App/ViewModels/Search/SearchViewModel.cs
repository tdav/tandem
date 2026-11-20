using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Search;
using Tandem.Core.Vfs;
using Tandem.Core.Viewing;

namespace Tandem.App.ViewModels.Search;

/// <summary>One found entry as the result list shows it: its name, the folder it is in (as the user reads it), its
/// size ("Folder" for a folder) and its modification time.</summary>
public sealed record SearchResultRow(FileEntry Entry, string Name, string Folder, string SizeText, string DateText);

/// <summary>The unit of the size fields: × 1, 1024, 1024², 1024³ bytes.</summary>
public enum SizeUnit { Bytes, Kilobytes, Megabytes, Gigabytes }

/// <summary>A size unit as the unit list shows it.</summary>
public sealed record SizeUnitChoice(SizeUnit Unit, string Label);

/// <summary>An encoding as the encoding list shows it; a null <paramref name="Encoding"/> is "Auto" (detected per
/// file).</summary>
public sealed record EncodingChoice(TextEncodingInfo? Encoding, string Label);

/// <summary>
/// The file search window's model: the criteria as typed, a search under <see cref="Root"/> run off the UI thread
/// with what it finds streamed into <see cref="Results"/>, and the two ways out — go to one result in the active
/// panel, or show them all there as a folder.
/// </summary>
/// <remarks>
/// Created and called on the UI thread; the walk runs on the thread pool. Found entries are added to
/// <see cref="Results"/> in batches, after a pause of <see cref="FlushInterval"/> (the injectable flush delay), with
/// one collection notification per batch. A search stops by itself at <see cref="MaxShownResults"/> results.
/// <see cref="Stop"/> ends the search at once: <see cref="IsRunning"/> is false when it returns, and what was found
/// so far stays; the walk notices the cancellation on its own thread and its later finds are dropped.
/// <see cref="Dispose"/> stops it too.
/// </remarks>
public sealed partial class SearchViewModel : ObservableObject, IDisposable
{
    public const int MaxShownResults = 100_000;

    /// <summary>How long found entries gather before they are added to <see cref="Results"/>.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private static readonly long[] UnitFactors = [1, 1024, 1024L * 1024, 1024L * 1024 * 1024];

    private readonly ProviderRegistry _providers;
    private readonly Func<FileEntry, Task> _goTo;
    private readonly Func<IReadOnlyList<VfsPath>, string, Task> _showInPanel;
    private readonly Func<TimeSpan, CancellationToken, Task> _flushDelay;
    private readonly int _maxShownResults;
    private readonly ResultCollection _results = new();
    private Run? _run;
    private string _resultsTitle = "";
    private EncodingChoice _selectedEncoding;
    private SizeUnitChoice _selectedSizeUnit;

    /// <param name="goTo">Shows a result in the active panel: its folder, with the cursor on it.</param>
    /// <param name="showInPanel">Shows the given results as a folder in the active panel, under the given
    /// title.</param>
    /// <param name="flushDelay">Waits <see cref="FlushInterval"/> before a batch is added;
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when null.</param>
    /// <param name="maxShownResults">The most results a search gives (<see cref="MaxShownResults"/>).</param>
    public SearchViewModel(ProviderRegistry providers, VfsPath root, Func<FileEntry, Task> goTo,
        Func<IReadOnlyList<VfsPath>, string, Task> showInPanel, Func<TimeSpan, CancellationToken, Task>? flushDelay = null,
        int maxShownResults = MaxShownResults)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _goTo = goTo ?? throw new ArgumentNullException(nameof(goTo));
        _showInPanel = showInPanel ?? throw new ArgumentNullException(nameof(showInPanel));
        _flushDelay = flushDelay ?? Task.Delay;
        ArgumentOutOfRangeException.ThrowIfLessThan(maxShownResults, 1);
        _maxShownResults = maxShownResults;
        Root = root;
        RootText = DisplayPathOf(root);
        EncodingChoices =
            [new EncodingChoice(null, Loc.Get("Search_EncodingAuto")), .. TextEncodings.All.Select(e => new EncodingChoice(e, e.Id))];
        _selectedEncoding = EncodingChoices[0];
        SizeUnitChoices =
        [
            new(SizeUnit.Bytes, Loc.Get("Unit_B")), new(SizeUnit.Kilobytes, Loc.Get("Unit_KB")),
            new(SizeUnit.Megabytes, Loc.Get("Unit_MB")), new(SizeUnit.Gigabytes, Loc.Get("Unit_GB")),
        ];
        _selectedSizeUnit = SizeUnitChoices[0];
    }

    /// <summary>The folder searched.</summary>
    public VfsPath Root { get; }

    /// <summary><see cref="Root"/> as the user reads it.</summary>
    public string RootText { get; }

    /// <summary>File masks ("*.txt;*.md"; empty for every name), or a regular expression when
    /// <see cref="NameIsRegex"/>.</summary>
    [ObservableProperty]
    public partial string NameMasks { get; set; } = "";

    [ObservableProperty]
    public partial bool NameIsRegex { get; set; }

    /// <summary>Whether only files containing <see cref="ContentText"/> are found.</summary>
    [ObservableProperty]
    public partial bool SearchContent { get; set; }

    [ObservableProperty]
    public partial string ContentText { get; set; } = "";

    [ObservableProperty]
    public partial bool ContentIsRegex { get; set; }

    [ObservableProperty]
    public partial bool ContentCaseSensitive { get; set; }

    /// <summary>The encoding files are read in; null to detect it per file ("Auto").</summary>
    public TextEncodingInfo? ContentEncoding
    {
        get => _selectedEncoding.Encoding;
        set => SelectedEncoding = EncodingChoices.FirstOrDefault(c => c.Encoding == value) ?? EncodingChoices[0];
    }

    /// <summary>"Auto" and every encoding of <see cref="TextEncodings.All"/>.</summary>
    public IReadOnlyList<EncodingChoice> EncodingChoices { get; }

    /// <summary>The item of <see cref="EncodingChoices"/> for <see cref="ContentEncoding"/>; setting null selects
    /// "Auto".</summary>
    public EncodingChoice SelectedEncoding
    {
        get => _selectedEncoding;
        set
        {
            if (!SetProperty(ref _selectedEncoding, value ?? EncodingChoices[0])) return;
            OnPropertyChanged(nameof(ContentEncoding));
        }
    }

    /// <summary>The smallest size, in <see cref="SizeUnit"/>; empty for none.</summary>
    [ObservableProperty]
    public partial string MinSizeText { get; set; } = "";

    /// <summary>The largest size, in <see cref="SizeUnit"/>; empty for none.</summary>
    [ObservableProperty]
    public partial string MaxSizeText { get; set; } = "";

    public SizeUnit SizeUnit
    {
        get => _selectedSizeUnit.Unit;
        set => SelectedSizeUnit = SizeUnitChoices.First(c => c.Unit == value);
    }

    public IReadOnlyList<SizeUnitChoice> SizeUnitChoices { get; }

    /// <summary>The item of <see cref="SizeUnitChoices"/> for <see cref="SizeUnit"/>.</summary>
    public SizeUnitChoice SelectedSizeUnit
    {
        get => _selectedSizeUnit;
        set
        {
            if (!SetProperty(ref _selectedSizeUnit, value ?? SizeUnitChoices[0])) return;
            OnPropertyChanged(nameof(SizeUnit));
        }
    }

    /// <summary>The first day of modification; only the date counts.</summary>
    [ObservableProperty]
    public partial DateTime? ModifiedFrom { get; set; }

    /// <summary>The last day of modification, the whole day included; only the date counts.</summary>
    [ObservableProperty]
    public partial DateTime? ModifiedTo { get; set; }

    /// <summary>How many folder levels below the root are searched (0: the root's own entries); empty for no
    /// limit.</summary>
    [ObservableProperty]
    public partial string MaxDepthText { get; set; } = "";

    /// <summary>Masks of folder names that are not searched.</summary>
    [ObservableProperty]
    public partial string ExcludedFolders { get; set; } = "";

    [ObservableProperty]
    public partial bool IncludeHidden { get; set; }

    /// <summary>What the last search found, in the order found.</summary>
    public ObservableCollection<SearchResultRow> Results => _results;

    /// <summary>Whether there are results to show in a panel.</summary>
    public bool HasResults => _results.Count > 0;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>What the search has found and read so far, and how it ended.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>Why the search could not start, or why it ended early; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>True once disposed (its window closed).</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>The window should close: a result was gone to, or the results were shown in a panel.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Takes the criteria of <paramref name="other"/> (a search window shown again for another
    /// folder).</summary>
    public void CopyCriteriaFrom(SearchViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);
        NameMasks = other.NameMasks;
        NameIsRegex = other.NameIsRegex;
        SearchContent = other.SearchContent;
        ContentText = other.ContentText;
        ContentIsRegex = other.ContentIsRegex;
        ContentCaseSensitive = other.ContentCaseSensitive;
        ContentEncoding = other.ContentEncoding;
        MinSizeText = other.MinSizeText;
        MaxSizeText = other.MaxSizeText;
        SizeUnit = other.SizeUnit;
        ModifiedFrom = other.ModifiedFrom;
        ModifiedTo = other.ModifiedTo;
        MaxDepthText = other.MaxDepthText;
        ExcludedFolders = other.ExcludedFolders;
        IncludeHidden = other.IncludeHidden;
    }

    /// <summary>The query the fields describe, checked as <see cref="FileSearch.Validate"/> does; null, with the
    /// reason in <see cref="Error"/>, when a size or the depth is not a number, a range is reversed or the query is
    /// invalid.</summary>
    public SearchQuery? BuildQuery()
    {
        Error = null;
        if (!TryParseSize(MinSizeText, out var minSize) || !TryParseSize(MaxSizeText, out var maxSize))
            return Fail("Search_BadSize");
        if (minSize > maxSize) return Fail("Search_BadSizeRange");
        if (!TryParseDepth(MaxDepthText, out var depth)) return Fail("Search_BadDepth");
        if (ModifiedFrom?.Date > ModifiedTo?.Date) return Fail("Search_BadDateRange");

        var query = new SearchQuery(Root, NameMasks.Trim(), NameIsRegex,
            SearchContent ? new ContentQuery(ContentText, ContentIsRegex, ContentCaseSensitive, ContentEncoding?.Id) : null,
            minSize, maxSize, StartOfDay(ModifiedFrom), EndOfDay(ModifiedTo), depth, ExcludedFolders.Trim(),
            IncludeHidden);
        try
        {
            FileSearch.Validate(query);
        }
        catch (SearchQueryException e)
        {
            Error = e.Field switch
            {
                SearchField.Name => Loc.Format("Search_BadNameRegex", e.Message),
                SearchField.Content when ContentText.Length == 0 => Loc.Get("Search_EmptyContent"),
                SearchField.Content => Loc.Format("Search_BadContent", e.Message),
                _ => Loc.Format("Search_BadEncoding", e.Message),
            };
            return null;
        }
        return query;

        SearchQuery? Fail(string key)
        {
            Error = Loc.Get(key);
            return null;
        }
    }

    /// <summary>Starts a search with the fields as they are: a running one is stopped first; when the fields are
    /// wrong, <see cref="Error"/> says why and nothing starts (the results stay). Otherwise the results are cleared
    /// and the search runs; completes when it has ended or been stopped.</summary>
    /// <exception cref="Exception">Anything unexpected the walk throws (not a read error).</exception>
    public async Task StartAsync()
    {
        if (IsDisposed) return;
        Stop();
        if (BuildQuery() is not { } query) return;

        var run = new Run();
        _run = run;
        _resultsTitle = Loc.Format("Search_ResultsTitle",
            query.NameMasks.Length == 0 ? Loc.Get("Search_AnyName") : query.NameMasks, RootText);
        _results.Clear();
        OnPropertyChanged(nameof(HasResults));
        IsRunning = true;
        UpdateStatus(run, "Search_StatusRunning");

        var search = new FileSearch(_providers);
        var limit = _maxShownResults;
        var walk = Task.Run(() => WalkAsync(search, query, run, limit));
        // The end is signalled once the walk has completed, so the loop below always sees it completed when woken
        // for it. A walk left behind by Stop may still fail on its own (the root unreadable): observed, not reported.
        _ = walk.ContinueWith(t =>
        {
            _ = t.Exception;
            run.Signal();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        while (true)
        {
            await run.Wake.WaitAsync();
            run.ClearSignal();
            if (run.Finished) return; // stopped: Stop has shown the end already
            if (!walk.IsCompleted)
            {
                try
                {
                    await _flushDelay(FlushInterval, run.Cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // stopped meanwhile
                }
                if (run.Finished) return;
            }
            Flush(run);
            if (run.Finished) return; // stopped by someone the new results told (the collection's handlers)
            if (walk.IsCompleted) break;
            UpdateStatus(run, "Search_StatusRunning");
        }

        Flush(run);
        if (run.Finished) return;
        try
        {
            await walk;
            Finish(run, run.LimitReached ? "Search_StatusLimit" : "Search_StatusDone");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = Loc.Format("Search_RootFailed", RootText, e.Message);
            Finish(run, "Search_StatusStopped");
        }
        catch
        {
            Finish(run, "Search_StatusStopped");
            throw;
        }
    }

    /// <summary>Stops a running search; what it found so far stays. Does nothing when none runs.</summary>
    public void Stop()
    {
        if (_run is not { Finished: false } run) return;
        run.Cts.Cancel();
        Flush(run);
        Finish(run, "Search_StatusStopped");
        run.Signal(); // the run's loop wakes, sees it has finished, and returns
    }

    /// <summary>Shows <paramref name="row"/> in the active panel (its folder, the cursor on it) and asks for the
    /// window to close.</summary>
    public async Task GoToAsync(SearchResultRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        await _goTo(row.Entry);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows every result as a folder in the active panel, titled with the masks and the folder searched
    /// (a running search is stopped first), and asks for the window to close. Does nothing without results.</summary>
    public async Task ShowInPanelAsync()
    {
        Stop();
        if (_results.Count == 0) return;
        var paths = _results.Select(r => r.Entry.Path).ToArray();
        await _showInPanel(paths, _resultsTitle);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        Stop();
        IsDisposed = true;
    }

    // ---- The walk (thread pool) ----------------------------------------------------------------------------

    private static async Task WalkAsync(FileSearch search, SearchQuery query, Run run, int limit)
    {
        try
        {
            var count = 0;
            await foreach (var entry in search.RunAsync(query, run, run.Cts.Token).ConfigureAwait(false))
            {
                run.Found.Enqueue(entry);
                run.Signal();
                if (++count < limit) continue;
                run.LimitReached = true;
                break;
            }
        }
        catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
        {
            // stopped
        }
    }

    // ---- UI thread -----------------------------------------------------------------------------------------

    /// <summary>Adds what the walk found since the last flush, as one batch; nothing once another run has
    /// started.</summary>
    private void Flush(Run run)
    {
        if (_run != run || run.Finished) return;
        var rows = new List<SearchResultRow>();
        while (run.Found.TryDequeue(out var entry)) rows.Add(RowOf(entry));
        if (rows.Count == 0) return;
        _results.AddRange(rows);
        OnPropertyChanged(nameof(HasResults));
    }

    private void Finish(Run run, string statusKey)
    {
        if (run.Finished) return;
        if (_run == run)
        {
            UpdateStatus(run, statusKey);
            IsRunning = false;
        }
        run.Finished = true;
    }

    private void UpdateStatus(Run run, string key)
    {
        if (_run != run) return;
        var progress = run.Latest;
        StatusText = Loc.Format(key, _results.Count, progress?.FoldersScanned ?? 0, progress?.FoldersSkipped ?? 0,
            progress?.FilesSkipped ?? 0);
    }

    private SearchResultRow RowOf(FileEntry entry) => new(entry, entry.Name,
        entry.Path.Parent is { } folder ? DisplayPathOf(folder) : "",
        entry.IsDirectoryLike ? Loc.Get("Panel_Folder") : SizeFormatter.Format(entry.Size),
        entry.Modified.ToLocalTime().ToString("g", Loc.Culture));

    private string DisplayPathOf(VfsPath path)
    {
        try
        {
            return _providers.DisplayPath(path);
        }
        catch (KeyNotFoundException)
        {
            return path.Path;
        }
    }

    // ---- Parsing -------------------------------------------------------------------------------------------

    /// <summary>A size of zero or more in <see cref="SizeUnit"/>, decimals allowed; null for an empty field.</summary>
    private bool TryParseSize(string text, out long? bytes)
    {
        bytes = null;
        text = text.Trim();
        if (text.Length == 0) return true;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, Loc.Culture, out var value) &&
            !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            return false;
        var total = value * UnitFactors[(int)SizeUnit];
        if (total > long.MaxValue) return false;
        bytes = (long)decimal.Round(total, MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool TryParseDepth(string text, out int? depth)
    {
        depth = null;
        text = text.Trim();
        if (text.Length == 0) return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
        depth = value;
        return true;
    }

    private static DateTimeOffset? StartOfDay(DateTime? day) =>
        day is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d.Date, DateTimeKind.Local)) : null;

    private static DateTimeOffset? EndOfDay(DateTime? day) =>
        day is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d.Date.AddDays(1), DateTimeKind.Local)).AddTicks(-1) : null;

    /// <summary>One search: what the walk found and has not been shown yet, its latest progress, and the signal that
    /// wakes the UI side. <see cref="Finished"/> is UI thread only.</summary>
    private sealed class Run : IProgress<SearchProgress>
    {
        private int _signaled;
        private SearchProgress? _latest;

        public CancellationTokenSource Cts { get; } = new();
        public ConcurrentQueue<FileEntry> Found { get; } = new();
        public SemaphoreSlim Wake { get; } = new(0);
        public volatile bool LimitReached;
        public bool Finished { get; set; }

        public SearchProgress? Latest => Volatile.Read(ref _latest);

        public void Report(SearchProgress value)
        {
            Volatile.Write(ref _latest, value);
            Signal();
        }

        /// <summary>Wakes the UI side once, however often it is called before the UI side looks.</summary>
        public void Signal()
        {
            if (Interlocked.Exchange(ref _signaled, 1) == 0) Wake.Release();
        }

        public void ClearSignal() => Interlocked.Exchange(ref _signaled, 0);
    }

    /// <summary>The results, with a batch added under one notification.</summary>
    private sealed class ResultCollection : ObservableCollection<SearchResultRow>
    {
        public void AddRange(IReadOnlyList<SearchResultRow> rows)
        {
            CheckReentrancy();
            var start = Items.Count;
            foreach (var row in rows) Items.Add(row);
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add,
                rows as System.Collections.IList ?? rows.ToList(), start));
        }
    }
}
