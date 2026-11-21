using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.Core.Listing;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>Why the panel could not show a folder, so the view can offer the fitting action.</summary>
public enum PanelErrorKind { AccessDenied, NotFound, Other, ConnectionLost }

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
    private MountRequest? _mount; // the file being opened as a folder; a newer request or navigation replaces it
    private Task<bool>? _navigation; // the navigation started last
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

    /// <summary>A folder is being read, or a file is being opened as a folder.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why the last navigation or refresh failed, for the user; null after a successful one.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial PanelErrorKind? ErrorKind { get; private set; }

    /// <summary>A short message for the panel's status line ("Marked: left 3, right 1", "Connecting to …"), not an
    /// error: an error shows instead of it. Gone once the panel moves to a folder and after <see cref="ShowNotice"/>
    /// with null; a refresh of the folder keeps it.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>Shows <paramref name="text"/> as the <see cref="Notice"/>; null takes it away.</summary>
    public void ShowNotice(string? text) => Notice = text;

    /// <summary>The folder <see cref="Error"/> is about: the one shown, or the one a failed navigation was heading
    /// for (the panel stays where it was); null after a successful load.</summary>
    public VfsPath? ErrorPath { get; private set; }

    /// <summary>Raised after the panel has moved to a folder.</summary>
    public event EventHandler<VfsPath>? Navigated;

    /// <summary>Raised when <see cref="ActivateAsync"/> opens something with its application rather than entering
    /// it: a file that does not open as a folder, or an application bundle.</summary>
    public event EventHandler<FileEntry>? FileActivated;

    /// <summary>Raised when <see cref="EnterAsync"/> is used on a file that does not open as a folder.</summary>
    public event EventHandler<FileEntry>? NotAFolder;

    /// <summary>Raised when a file that opens as a folder could not be opened (any exception from the mounter but a
    /// cancellation); the panel stays where it is and <see cref="Error"/> is not set.</summary>
    public event EventHandler<Exception>? OpenFailed;

    /// <summary>Shows <paramref name="path"/> with the cursor on <paramref name="focusName"/> (or the first row).
    /// On failure the previous folder stays and <see cref="Error"/> says why. Never throws for file-system errors.</summary>
    public async Task NavigateToAsync(VfsPath path, string? focusName = null)
    {
        var navigation = LoadAsync(LoadKind.Navigate, path, focusName);
        _navigation = navigation;
        if (await navigation) Navigated?.Invoke(this, path);
    }

    /// <summary>Enter: opens the folder (or link to a folder) under the cursor, goes up on "..", enters a file that
    /// opens as a folder by its name, and raises <see cref="FileActivated"/> for any other file and for an application
    /// bundle (<see cref="IsPackage"/>), which is launched rather than entered.</summary>
    public Task ActivateAsync()
    {
        var row = CursorRow;
        if (row is null) return Task.CompletedTask;
        CancelMount(); // this activation is what the user wants now
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
        CancelMount();
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
        Up is { } up ? NavigateToAsync(up.Directory, up.FocusName) : Task.CompletedTask;

    /// <summary>Where "up" (and the ".." row) leads from the folder shown (<see cref="ProviderRegistry.UpOf"/>); null
    /// when there is no folder or no up.</summary>
    public HostLocation? Up => _hasPath ? UpOf(CurrentPath) : null;

    /// <summary>Reads the folder again, keeping the cursor and the selection on the same names. If the folder no
    /// longer exists, moves to its nearest existing ancestor; if the source lost its connection, stays on the folder
    /// and reports <see cref="PanelErrorKind.ConnectionLost"/> (refreshing again reconnects).</summary>
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
        ResetFolderSizes();
        _loadCts?.Cancel();
        _mount?.Cts.Cancel();
        _mount = null;
        var watch = _watch;
        _watch = null;
        DisposeInBackground(watch);
    }

    /// <summary>A file being opened as a folder: where the panel was when it was asked, the last load started
    /// before it (a navigation started later is the user's later action), and the way to stop it.</summary>
    private sealed class MountRequest(VfsPath from, int loadVersion)
    {
        public VfsPath From { get; } = from;
        public int LoadVersion { get; } = loadVersion;
        public CancellationTokenSource Cts { get; } = new();
    }

    /// <summary>Asks the mounters (off the UI thread) what <paramref name="file"/> opens as and goes there; a file
    /// that opens as nothing is activated (<paramref name="byContent"/> false) or reported as not a folder. Without
    /// mounters the answer is given at once. A mount the user cancelled does nothing; one that fails raises
    /// <see cref="OpenFailed"/>. While it runs, <see cref="IsLoading"/> is true.
    /// <para>The answer applies only if, when it comes, the panel still shows the folder it was asked in and nothing
    /// later replaced the request: another Enter or activation, a completed navigation, or <see cref="Dispose"/>
    /// (each of these also cancels the mount). A navigation that failed, or a cursor move, does not. An answer that
    /// comes while a navigation started after the request is still running waits for it: the navigation wins if it
    /// completes, and the answer applies if it fails.</para></summary>
    private Task OpenAsFolderAsync(FileEntry file, bool byContent)
    {
        if (_providers.HasMounters) return MountAsync(file, byContent);
        OpensAsNothing(file, byContent);
        return Task.CompletedTask;
    }

    private async Task MountAsync(FileEntry file, bool byContent)
    {
        var request = new MountRequest(CurrentPath, _loadVersion);
        _mount = request;
        UpdateLoading();
        var (providers, ct) = (_providers, request.Cts.Token);
        VfsPath? folder = null;
        Exception? failure = null;
        var applies = false;
        try
        {
            try
            {
                folder = await Task.Run(() => providers.TryMountAsync(file, byContent, ct));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                failure = e;
            }

            // A navigation the user started after asking decides: if it completes it cancels this request.
            while (_mount == request && !_disposed && _activeLoad == LoadKind.Navigate &&
                   _loadVersion > request.LoadVersion && _navigation is { } navigation)
                await navigation;

            applies = _mount == request && !_disposed && CurrentPath == request.From;
        }
        finally
        {
            if (_mount == request)
            {
                _mount = null;
                UpdateLoading();
            }
            request.Cts.Dispose();
        }
        if (!applies) return;
        if (failure is not null) OpenFailed?.Invoke(this, failure);
        else if (folder is { } target) await NavigateToAsync(target);
        else OpensAsNothing(file, byContent);
    }

    private void OpensAsNothing(FileEntry file, bool byContent)
    {
        if (byContent) NotAFolder?.Invoke(this, file);
        else FileActivated?.Invoke(this, file);
    }

    /// <summary>Stops a pending mount; its answer, if it still comes, is dropped.</summary>
    private void CancelMount()
    {
        if (_mount is not { } mount) return;
        _mount = null;
        mount.Cts.Cancel();
        UpdateLoading();
    }

    private void UpdateLoading() => IsLoading = _activeLoad is not null || _mount is not null;

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
        if (kind == LoadKind.Navigate && path != CurrentPath) _folderSizeCts?.Cancel();
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;
        var version = ++_loadVersion;
        _activeLoad = kind;
        IsLoading = true;
        _previewVersion = kind == LoadKind.Navigate ? version : 0;
        if (kind != LoadKind.Navigate && _showingPreview) RestoreRowsBeforePreview();

        Listing? listing = null;
        try
        {
            var view = CurrentView;
            var parentLink = _providers.UpOf(path) is not null;
            var shape = new RowShape(path, parentLink, DisplayPathOf);
            if (kind == LoadKind.Reorder)
            {
                var entries = _entries;
                var rows = await Task.Run(() => BuildRows(entries, shape, view, sort: true));
                listing = new Listing(entries, rows, view, null);
            }
            else
            {
                var provider = _providers.For(path);
                var preview = kind == LoadKind.Navigate ? version : (int?)null;
                listing = await Task.Run(() =>
                    ReadAsync(provider, path, shape, kind == LoadKind.Navigate, preview, view, ct));
            }

            // The options changed while the folder was being read or sorted: sort again before showing it.
            while (IsCurrent(version) && listing.View != CurrentView)
            {
                var newView = CurrentView;
                var entries = listing.Entries;
                var rows = await Task.Run(() => BuildRows(entries, shape, newView, sort: true));
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
    private async Task<Listing> ReadAsync(IFileSystemProvider provider, VfsPath path, RowShape shape, bool watch,
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
                    PostPreview(version, BuildRows(entries.GetRange(0, PreviewCount), shape, view, sort: false));
            }
            ct.ThrowIfCancellationRequested();
            return new Listing(entries, BuildRows(entries, shape, view, sort: true), view, subscription);
        }
        catch
        {
            DisposeInBackground(subscription);
            throw;
        }
    }

    /// <summary>What the rows of a folder need besides its entries: the folder (an entry whose parent is another
    /// folder is foreign and shows where it lies, through <paramref name="DisplayPathOf"/>) and whether there is a
    /// ".." row.</summary>
    private sealed record RowShape(VfsPath Folder, bool ParentLink, Func<VfsPath, string> DisplayPathOf);

    private static List<EntryRowViewModel> BuildRows(IReadOnlyList<FileEntry> entries, RowShape shape,
        ViewOptions view, bool sort)
    {
        var visible = new List<FileEntry>(entries.Count);
        foreach (var entry in entries)
            if (view.ShowHidden || !entry.IsHidden)
                visible.Add(entry);
        if (sort) visible.Sort(EntrySorter.Create(view.Sort));

        var rows = new List<EntryRowViewModel>(visible.Count + 1);
        if (shape.ParentLink) rows.Add(EntryRowViewModel.CreateParentLink());
        foreach (var entry in visible)
            rows.Add(entry.Path.Parent == shape.Folder
                ? new EntryRowViewModel(entry)
                : new EntryRowViewModel(entry, shape.DisplayPathOf));
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
            if (!_hasPath || CurrentPath != path) ResetFolderSizes();
            CancelMount(); // the panel has moved on
            var oldWatch = _watch;
            _watch = listing.Watch;
            // A change reported while the folder was being read may be missing from this listing.
            _watchRefreshAfterLoad = _watch?.ChangedBeforeCommit == true;
            DisposeInBackground(oldWatch);
            DisplayPath = DisplayPathOf(path);
            CurrentPath = path;
            _hasPath = true;
            Notice = null;
            EndQuickSearch();
            cursor = focusName is null ? 0 : Math.Max(IndexOf(rows, focusName), 0);
        }
        else
        {
            // Keep the cursor and the selection on the same items (by path: in search results names repeat); a
            // vanished cursor item keeps its position. A navigation keeps no selection: its rows are new.
            var index = IndexOfSame(rows, CursorRow);
            cursor = index >= 0 ? index : _cursorIndex;
            RestoreSelection(rows);
        }

        _entries = listing.Entries;
        PruneFolderSizes(listing.Entries);
        _showingPreview = false;
        _rowsBeforePreview = [];
        Publish(rows, cursor);
        ErrorPath = null;
        ErrorKind = null;
        Error = null;
    }

    private void RestoreSelection(List<EntryRowViewModel> rows)
    {
        HashSet<VfsPath>? selected = null;
        foreach (var row in _rows.Items)
            if (row is { IsSelected: true, Entry: { } entry })
                (selected ??= []).Add(entry.Path);
        if (selected is null) return;
        foreach (var row in rows)
            if (row.Entry is { } entry && selected.Contains(entry.Path))
                row.IsSelected = true;
    }

    /// <summary>The row of <paramref name="rows"/> for the same item as <paramref name="row"/> (the same path, or
    /// ".." for ".."); -1 when there is none.</summary>
    private static int IndexOfSame(List<EntryRowViewModel> rows, EntryRowViewModel? row) => row switch
    {
        null => -1,
        { IsParentLink: true } => rows.FindIndex(r => r.IsParentLink),
        _ => rows.FindIndex(r => r.Entry?.Path == row.Entry!.Path),
    };

    private static int IndexOf(List<EntryRowViewModel> rows, string name) =>
        rows.FindIndex(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    private void Publish(IReadOnlyList<EntryRowViewModel> rows, int cursor)
    {
        AttachFolderSizes(rows);
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
        UpdateLoading();
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
        VfsConnectionLostException => PanelErrorKind.ConnectionLost,
        VfsAccessDeniedException or UnauthorizedAccessException => PanelErrorKind.AccessDenied,
        VfsNotFoundException or DirectoryNotFoundException or FileNotFoundException => PanelErrorKind.NotFound,
        _ => PanelErrorKind.Other,
    };

    private void SetError(PanelErrorKind kind, VfsPath path)
    {
        ErrorPath = path;
        ErrorKind = kind;
        var key = kind == PanelErrorKind.ConnectionLost ? "Panel_ConnectionLost" : $"Panel_Error_{kind}";
        Error = Loc.Format(key, DisplayPathOf(path));
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
