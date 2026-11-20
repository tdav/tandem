using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.Core.Listing;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>Why the panel could not show a folder, so the view can offer the fitting action.</summary>
public enum PanelErrorKind { AccessDenied, NotFound, Other }

/// <summary>
/// One file panel: the listing of a folder, sorted, with a cursor, moving in and out of folders (and of files that open
/// as folders, through <see cref="ProviderRegistry.TryMountAsync"/>) and refreshing itself when the folder changes on
/// disk. Where "up" goes, and whether there is a ".." row, is <see cref="ProviderRegistry.UpOf"/>'s answer.
/// </summary>
/// <remarks>
/// Created and called on the UI thread; every observable change happens there. Reading, sorting and watching run in
/// the background. A newer load (navigation, refresh, re-sort) supersedes an unfinished one. While a large folder is
/// being read, its first <see cref="PreviewCount"/> entries are shown unsorted; <see cref="CurrentPath"/> changes only
/// when the folder has been read in full.
/// </remarks>
public sealed partial class PanelViewModel : ObservableObject, IDisposable
{
    /// <summary>How many entries are shown before a large folder has been read in full.</summary>
    internal const int PreviewCount = 500;

    // Change notifications come in bursts; one refresh covers a burst.
    private static readonly TimeSpan WatchRefreshDelay = TimeSpan.FromMilliseconds(100);

    private readonly ProviderRegistry _providers;
    private readonly SynchronizationContext _ui;
    private readonly RowCollection _rows = new();

    private int _cursorIndex = -1;
    private SortSpec _sort = new(SortColumn.Name);
    private bool _showHidden;

    private bool _hasPath;
    private List<FileEntry> _entries = []; // every entry of CurrentPath, hidden ones included, unsorted
    private CancellationTokenSource? _loadCts;
    private int _loadVersion;
    private int _navigationVersion; // counts the navigations started, so a slow mount can tell the panel moved on
    private LoadKind? _activeLoad;
    private bool _showingPreview;
    private int _previewVersion; // the load whose preview may still be shown; 0 once it has committed or failed
    private IReadOnlyList<EntryRowViewModel> _rowsBeforePreview = [];
    private int _cursorBeforePreview = -1;
    private WatchSubscription? _watch;
    private bool _watchRefreshScheduled;
    private bool _watchRefreshAfterLoad;
    private bool _disposed;

    public PanelViewModel(ProviderRegistry providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers;
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("A panel must be created on the UI thread.");
    }

    /// <summary>The folder shown; <c>default</c> until the first successful navigation.</summary>
    [ObservableProperty]
    public partial VfsPath CurrentPath { get; private set; }

    /// <summary><see cref="CurrentPath"/> as the user reads it (<see cref="ProviderRegistry.DisplayPath"/>), e.g.
    /// "/a/b.zip/docs"; "" until the first successful navigation. Changes together with <see cref="CurrentPath"/>, just
    /// before it.</summary>
    [ObservableProperty]
    public partial string DisplayPath { get; private set; } = "";

    /// <summary>The rows: ".." first (when <see cref="ProviderRegistry.UpOf"/> has somewhere to go), then the entries
    /// in <see cref="Sort"/> order. Raises one collection Reset per change of the listing.</summary>
    public IReadOnlyList<EntryRowViewModel> Rows => _rows;

    /// <summary>The row under the cursor; -1 when there are no rows. Values outside the rows are clamped. Moving it
    /// ends a quick search.</summary>
    public int CursorIndex
    {
        get => _cursorIndex;
        set
        {
            if (SetCursor(value)) EndQuickSearch();
        }
    }

    public EntryRowViewModel? CursorRow => _cursorIndex >= 0 && _cursorIndex < _rows.Count ? _rows[_cursorIndex] : null;

    /// <summary>Changing it re-sorts the rows in the background; the cursor stays on the same name.</summary>
    public SortSpec Sort
    {
        get => _sort;
        set
        {
            if (SetProperty(ref _sort, value)) OnViewOptionsChanged();
        }
    }

    /// <summary>Whether hidden entries are listed. Changing it rebuilds the rows without reading the folder again.</summary>
    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (SetProperty(ref _showHidden, value)) OnViewOptionsChanged();
        }
    }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why the last navigation or refresh failed, for the user; null after a successful one.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial PanelErrorKind? ErrorKind { get; private set; }

    /// <summary>Raised after the panel has moved to a folder.</summary>
    public event EventHandler<VfsPath>? Navigated;

    /// <summary>Raised when <see cref="ActivateAsync"/> opens something with its application rather than entering
    /// it: a file that does not open as a folder, or an application bundle.</summary>
    public event EventHandler<FileEntry>? FileActivated;

    /// <summary>Raised when <see cref="EnterAsync"/> is used on a file that does not open as a folder.</summary>
    public event EventHandler<FileEntry>? NotAFolder;

    /// <summary>Raised when a file that opens as a folder could not be opened (<see cref="IOException"/>); the panel
    /// stays where it is and <see cref="Error"/> is not set.</summary>
    public event EventHandler<Exception>? OpenFailed;

    /// <summary>Shows <paramref name="path"/> with the cursor on <paramref name="focusName"/> (or the first row).
    /// On failure the previous folder stays and <see cref="Error"/> says why. Never throws for file-system errors.</summary>
    public async Task NavigateToAsync(VfsPath path, string? focusName = null)
    {
        if (await LoadAsync(LoadKind.Navigate, path, focusName)) Navigated?.Invoke(this, path);
    }

    /// <summary>Enter: opens the folder (or link to a folder) under the cursor, goes up on "..", enters a file that
    /// opens as a folder by its name, and raises <see cref="FileActivated"/> for any other file and for an application
    /// bundle (<see cref="IsPackage"/>), which is launched rather than entered.</summary>
    public Task ActivateAsync()
    {
        var row = CursorRow;
        if (row is null) return Task.CompletedTask;
        if (row.IsParentLink) return GoUpAsync();
        var entry = row.Entry!;
        if (IsPackage(entry))
        {
            FileActivated?.Invoke(this, entry);
            return Task.CompletedTask;
        }
        if (entry.IsDirectoryLike) return NavigateToAsync(entry.Path);
        return OpenAsFolderAsync(entry, byContent: false);
    }

    /// <summary>Cmd+Down: goes into the folder, link to a folder or application bundle under the cursor (up on ".."),
    /// or into a file that opens as a folder, its content read to decide; raises <see cref="NotAFolder"/> for a file
    /// that does not.</summary>
    public Task EnterAsync()
    {
        var row = CursorRow;
        if (row is null) return Task.CompletedTask;
        if (row.IsParentLink) return GoUpAsync();
        var entry = row.Entry!;
        return entry.IsDirectoryLike ? NavigateToAsync(entry.Path) : OpenAsFolderAsync(entry, byContent: true);
    }

    /// <summary>An application bundle: a folder (or link to one) on the local disk whose name ends in ".app".</summary>
    public static bool IsPackage(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.IsDirectoryLike && entry.Path.ProviderId == VfsPath.LocalProviderId &&
               entry.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Moves to where <see cref="ProviderRegistry.UpOf"/> leads (the parent, or the folder a hosted
    /// provider lives in) with the cursor on the name it gives. Does nothing where there is no up.</summary>
    public Task GoUpAsync() =>
        _hasPath && UpOf(CurrentPath) is { } up ? NavigateToAsync(up.Directory, up.FocusName) : Task.CompletedTask;

    /// <summary>Reads the folder again, keeping the cursor and the selection on the same names. If the folder no
    /// longer exists, moves to its nearest existing ancestor.</summary>
    public async Task RefreshAsync()
    {
        if (!_hasPath) return;
        await LoadAsync(LoadKind.Refresh, CurrentPath, null);
    }

    /// <summary>Stops watching the folder and cancels an unfinished load.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadCts?.Cancel();
        var watch = _watch;
        _watch = null;
        DisposeInBackground(watch);
    }

    /// <summary>Asks the mounters (off the UI thread) what <paramref name="file"/> opens as and goes there; a file
    /// that opens as nothing is activated (<paramref name="byContent"/> false) or reported as not a folder. A mount the
    /// user cancelled does nothing; one that fails raises <see cref="OpenFailed"/>. Nothing happens either when the
    /// panel has moved on meanwhile.</summary>
    private async Task OpenAsFolderAsync(FileEntry file, bool byContent)
    {
        var navigation = _navigationVersion;
        bool StillHere() => !_disposed && navigation == _navigationVersion;
        var providers = _providers;
        VfsPath? folder;
        try
        {
            folder = await Task.Run(() => providers.TryMountAsync(file, byContent, CancellationToken.None));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException e)
        {
            if (StillHere()) OpenFailed?.Invoke(this, e);
            return;
        }
        if (!StillHere()) return;
        if (folder is { } target) await NavigateToAsync(target);
        else if (byContent) NotAFolder?.Invoke(this, file);
        else FileActivated?.Invoke(this, file);
    }

    /// <summary><see cref="ProviderRegistry.UpOf"/>; null when the path's provider is no longer registered.</summary>
    private HostLocation? UpOf(VfsPath path)
    {
        try
        {
            return _providers.UpOf(path);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    // ---- Loading -----------------------------------------------------------------------------------------

    private enum LoadKind { Navigate, Refresh, Reorder }

    private readonly record struct ViewOptions(SortSpec Sort, bool ShowHidden);

    private sealed record Listing(List<FileEntry> Entries, List<EntryRowViewModel> Rows, ViewOptions View,
        WatchSubscription? Watch);

    private ViewOptions CurrentView => new(_sort, _showHidden);

    private void OnViewOptionsChanged()
    {
        // A load that reads the folder applies the options it finds when it finishes.
        if (!_hasPath || _activeLoad is LoadKind.Navigate or LoadKind.Refresh) return;
        _ = LoadAsync(LoadKind.Reorder, CurrentPath, null);
    }

    /// <summary>Runs one load to the end; true when it changed the listing. File-system errors end up in
    /// <see cref="Error"/>, never as exceptions.</summary>
    private async Task<bool> LoadAsync(LoadKind kind, VfsPath path, string? focusName)
    {
        if (_disposed) return false;
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;
        var version = ++_loadVersion;
        if (kind == LoadKind.Navigate) _navigationVersion++;
        _activeLoad = kind;
        IsLoading = true;
        _previewVersion = kind == LoadKind.Navigate ? version : 0;
        if (kind != LoadKind.Navigate && _showingPreview) RestoreRowsBeforePreview();

        Listing? listing = null;
        try
        {
            var view = CurrentView;
            var parentLink = _providers.UpOf(path) is not null;
            if (kind == LoadKind.Reorder)
            {
                var entries = _entries;
                var rows = await Task.Run(() => BuildRows(entries, parentLink, view, sort: true));
                listing = new Listing(entries, rows, view, null);
            }
            else
            {
                var provider = _providers.For(path);
                var preview = kind == LoadKind.Navigate ? version : (int?)null;
                listing = await Task.Run(() =>
                    ReadAsync(provider, path, parentLink, kind == LoadKind.Navigate, preview, view, ct));
            }

            // The options changed while the folder was being read or sorted: sort again before showing it.
            while (IsCurrent(version) && listing.View != CurrentView)
            {
                var newView = CurrentView;
                var entries = listing.Entries;
                var rows = await Task.Run(() => BuildRows(entries, parentLink, newView, sort: true));
                listing = listing with { Rows = rows, View = newView };
            }

            if (!IsCurrent(version))
            {
                DisposeInBackground(listing.Watch);
                return false;
            }
            _previewVersion = 0; // a preview posted earlier but delivered late must not replace the sorted rows
            Commit(kind, path, focusName, listing);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e)
        {
            if (_previewVersion == version) _previewVersion = 0;
            if (listing?.Watch != _watch) DisposeInBackground(listing?.Watch);
            if (!IsCurrent(version)) return false;
            if (_showingPreview) RestoreRowsBeforePreview();
            var errorKind = KindOf(e);
            if (kind == LoadKind.Refresh && errorKind == PanelErrorKind.NotFound)
                return await FallBackToAncestorAsync(path, version, ct);
            SetError(errorKind, path);
            return false;
        }
        finally
        {
            if (version == _loadVersion) FinishLoading();
        }
    }

    /// <summary>Moves the cursor (clamped) without touching the quick search; true when it moved.</summary>
    private bool SetCursor(int index)
    {
        var clamped = _rows.Count == 0 ? -1 : Math.Clamp(index, 0, _rows.Count - 1);
        if (!SetProperty(ref _cursorIndex, clamped, nameof(CursorIndex))) return false;
        OnPropertyChanged(nameof(CursorRow));
        return true;
    }

    private bool IsCurrent(int version) => version == _loadVersion && !_disposed;

    /// <summary>Background: subscribes to changes (before reading, so no change is missed), reads the folder and
    /// builds the sorted rows. Posts the first entries to the UI as a preview when the folder is large.</summary>
    private async Task<Listing> ReadAsync(IFileSystemProvider provider, VfsPath path, bool parentLink, bool watch,
        int? previewVersion, ViewOptions view, CancellationToken ct)
    {
        var subscription = watch ? Subscribe(provider, path) : null;
        try
        {
            var entries = new List<FileEntry>();
            await foreach (var entry in provider.EnumerateAsync(path, ct).ConfigureAwait(false))
            {
                entries.Add(entry);
                if (previewVersion is { } version && entries.Count == PreviewCount + 1)
                    PostPreview(version, BuildRows(entries.GetRange(0, PreviewCount), parentLink, view, sort: false));
            }
            ct.ThrowIfCancellationRequested();
            return new Listing(entries, BuildRows(entries, parentLink, view, sort: true), view, subscription);
        }
        catch
        {
            DisposeInBackground(subscription);
            throw;
        }
    }

    private static List<EntryRowViewModel> BuildRows(IReadOnlyList<FileEntry> entries, bool parentLink,
        ViewOptions view, bool sort)
    {
        var visible = new List<FileEntry>(entries.Count);
        foreach (var entry in entries)
            if (view.ShowHidden || !entry.IsHidden)
                visible.Add(entry);
        if (sort) visible.Sort(EntrySorter.Create(view.Sort));

        var rows = new List<EntryRowViewModel>(visible.Count + 1);
        if (parentLink) rows.Add(EntryRowViewModel.CreateParentLink());
        foreach (var entry in visible) rows.Add(new EntryRowViewModel(entry));
        return rows;
    }

    private void PostPreview(int version, List<EntryRowViewModel> rows) => _ui.Post(_ =>
    {
        if (!IsCurrent(version) || version != _previewVersion) return;
        if (!_showingPreview)
        {
            _rowsBeforePreview = _rows.Items;
            _cursorBeforePreview = _cursorIndex;
            _showingPreview = true;
        }
        Publish(rows, 0);
    }, null);

    private void RestoreRowsBeforePreview()
    {
        _showingPreview = false;
        Publish(_rowsBeforePreview, _cursorBeforePreview);
        _rowsBeforePreview = [];
    }

    private void Commit(LoadKind kind, VfsPath path, string? focusName, Listing listing)
    {
        var rows = listing.Rows;
        int cursor;
        if (kind == LoadKind.Navigate)
        {
            var oldWatch = _watch;
            _watch = listing.Watch;
            // A change reported while the folder was being read may be missing from this listing.
            _watchRefreshAfterLoad = _watch?.ChangedBeforeCommit == true;
            DisposeInBackground(oldWatch);
            DisplayPath = DisplayPathOf(path);
            CurrentPath = path;
            _hasPath = true;
            EndQuickSearch();
            cursor = focusName is null ? 0 : Math.Max(IndexOf(rows, focusName), 0);
        }
        else
        {
            // Keep the cursor and the selection on the same names; a vanished cursor name keeps its position. A
            // navigation keeps no selection: its rows are new.
            var cursorName = CursorRow?.Name;
            var index = cursorName is null ? -1 : IndexOf(rows, cursorName);
            cursor = index >= 0 ? index : _cursorIndex;
            RestoreSelection(rows);
        }

        _entries = listing.Entries;
        _showingPreview = false;
        _rowsBeforePreview = [];
        Publish(rows, cursor);
        ErrorKind = null;
        Error = null;
    }

    private void RestoreSelection(List<EntryRowViewModel> rows)
    {
        HashSet<string>? selected = null;
        foreach (var row in _rows.Items)
            if (row.IsSelected)
                (selected ??= new HashSet<string>(StringComparer.Ordinal)).Add(row.Name);
        if (selected is null) return;
        foreach (var row in rows)
            if (!row.IsParentLink && selected.Contains(row.Name))
                row.IsSelected = true;
    }

    private static int IndexOf(List<EntryRowViewModel> rows, string name) =>
        rows.FindIndex(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    private void Publish(IReadOnlyList<EntryRowViewModel> rows, int cursor)
    {
        AttachRows(rows);
        _rows.Replace(rows);
        _cursorIndex = rows.Count == 0 ? -1 : Math.Clamp(cursor, 0, rows.Count - 1);
        OnPropertyChanged(nameof(CursorIndex));
        OnPropertyChanged(nameof(CursorRow));
        RaiseListingTotalsChanged();
    }

    private void FinishLoading()
    {
        _activeLoad = null;
        IsLoading = false;
        if (_watchRefreshAfterLoad)
        {
            _watchRefreshAfterLoad = false;
            if (_watch is { } watch) _ = OnDirectoryChangedAsync(watch);
        }
    }

    /// <summary>The folder shown has disappeared: shows the nearest existing folder up from it instead, going on
    /// past the root of a hosted provider into its host (<see cref="ProviderRegistry.UpOf"/>).</summary>
    private async Task<bool> FallBackToAncestorAsync(VfsPath path, int version, CancellationToken ct)
    {
        HostLocation? ancestor = null;
        try
        {
            var providers = _providers;
            ancestor = await Task.Run(() => FindNearestExistingAncestorAsync(providers, path, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            // treated as no ancestor found
        }
        if (!IsCurrent(version)) return false;
        if (ancestor is not { } target)
        {
            SetError(PanelErrorKind.NotFound, path);
            return false;
        }
        await NavigateToAsync(target.Directory, target.FocusName);
        return false; // the navigation reported its own result
    }

    private static async Task<HostLocation?> FindNearestExistingAncestorAsync(ProviderRegistry providers,
        VfsPath path, CancellationToken ct)
    {
        var seen = new HashSet<VfsPath> { path };
        for (var up = providers.UpOf(path); up is { } location; up = providers.UpOf(location.Directory))
        {
            var directory = location.Directory;
            if (!seen.Add(directory)) return null; // host locations that lead around in a cycle
            try
            {
                var entry = await providers.For(directory).GetEntryAsync(directory, ct).ConfigureAwait(false);
                if (entry?.IsDirectoryLike == true) return location;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // unreadable: look further up
            }
        }
        return null;
    }

    private static PanelErrorKind KindOf(Exception e) => e switch
    {
        VfsAccessDeniedException or UnauthorizedAccessException => PanelErrorKind.AccessDenied,
        VfsNotFoundException or DirectoryNotFoundException or FileNotFoundException => PanelErrorKind.NotFound,
        _ => PanelErrorKind.Other,
    };

    private void SetError(PanelErrorKind kind, VfsPath path)
    {
        ErrorKind = kind;
        Error = Loc.Format($"Panel_Error_{kind}", DisplayPathOf(path));
    }

    /// <summary><see cref="ProviderRegistry.DisplayPath"/>; the path itself when its provider is not
    /// registered.</summary>
    internal string DisplayPathOf(VfsPath path)
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

    // ---- Watching ----------------------------------------------------------------------------------------

    /// <summary>One subscription to a folder's changes; callbacks name it, so late ones for a folder the panel has
    /// left are recognised and ignored.</summary>
    private sealed class WatchSubscription
    {
        public IDisposable? Handle;

        /// <summary>UI thread: a change arrived before the navigation that made this subscription had committed.</summary>
        public bool ChangedBeforeCommit;
    }

    /// <summary>Background. Watching is best effort: a provider that cannot watch, or fails to, gives no refreshes.</summary>
    private WatchSubscription Subscribe(IFileSystemProvider provider, VfsPath path)
    {
        var subscription = new WatchSubscription();
        try
        {
            subscription.Handle = provider.Watch(path, () => OnWatchCallback(subscription));
        }
        catch (Exception)
        {
            // no watching for this folder
        }
        return subscription;
    }

    /// <summary>Any thread. Must not throw: an exception here would escape into the provider's thread.</summary>
    private void OnWatchCallback(WatchSubscription subscription)
    {
        try
        {
            _ui.Post(_ => _ = OnDirectoryChangedAsync(subscription), null);
        }
        catch (Exception)
        {
            // the UI is shutting down
        }
    }

    private async Task OnDirectoryChangedAsync(WatchSubscription subscription)
    {
        try
        {
            if (_disposed) return;
            if (subscription != _watch)
            {
                // Either a navigation still reading this folder (its commit then refreshes once) or a folder the
                // panel has left (the subscription is never installed, so the mark is harmless).
                subscription.ChangedBeforeCommit = true;
                return;
            }
            if (_watchRefreshScheduled) return;
            _watchRefreshScheduled = true;
            try
            {
                await Task.Delay(WatchRefreshDelay);
            }
            finally
            {
                _watchRefreshScheduled = false;
            }
            if (_disposed || subscription != _watch) return;
            if (_activeLoad is not null)
            {
                // Never cancel what the user started; refresh once it is done (a navigation away clears this).
                _watchRefreshAfterLoad = true;
                return;
            }
            await RefreshAsync();
        }
        catch (Exception)
        {
            // a refresh nobody asked for must not crash the app; errors of the folder itself are in Error
        }
    }

    private static void DisposeInBackground(WatchSubscription? subscription)
    {
        if (subscription?.Handle is not { } handle) return;
        _ = Task.Run(() =>
        {
            try
            {
                handle.Dispose();
            }
            catch (Exception)
            {
                // unsubscribing is best effort; late callbacks are ignored anyway
            }
        });
    }
}
