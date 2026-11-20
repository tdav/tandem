using System.Globalization;
using System.Runtime.CompilerServices;
using Tandem.Core.Vfs;

namespace Tandem.Core.Search;

/// <summary>
/// Search results as folders: each <see cref="Add"/> makes a result set "/n" (n = 1, 2, … in the order added) that
/// lists the entries found, read afresh from their own providers, with their own paths
/// (<see cref="FileEntry.Path"/> is the path in the provider searched, never a "search" path), so operations on them
/// act on the real files. The root "/" lists the sets. At most <see cref="MaxResultSets"/> sets are kept; adding one
/// more drops the oldest (only its origin is kept, for <see cref="GetHostLocation"/>). Nothing can be written here: every changing call and <see cref="OpenReadAsync"/> throw
/// <see cref="VfsAccessDeniedException"/>. Thread-safe.
/// </summary>
/// <remarks>"Up" from a set leads to the folder searched (<see cref="GetHostLocation"/>), which is also what a saved
/// tab keeps (<see cref="ProviderRegistry.PersistentLocationOf"/>); the set itself displays as its title.</remarks>
public sealed class SearchResultsProvider(ProviderRegistry providers) : IFileSystemProvider, IHostedProvider
{
    public const string ProviderId = "search";

    public const int MaxResultSets = 20;

    private readonly ProviderRegistry _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly DateTimeOffset _created = DateTimeOffset.UtcNow;
    private readonly Lock _lock = new();
    private readonly List<ResultSet> _sets = []; // oldest first
    private readonly Dictionary<int, VfsPath> _droppedOrigins = []; // number → folder searched, of dropped sets
    private int _lastNumber;

    public string Id => ProviderId;

    public ProviderCapabilities Capabilities => ProviderCapabilities.None;

    /// <summary>A new folder "/{n}" (n = 1, 2, …) listing <paramref name="results"/> in their order; up from it leads
    /// to <paramref name="origin"/>, the folder searched. <paramref name="title"/> is the text for the path line, as it
    /// is shown. A path given more than once is listed once, where it first appears.</summary>
    /// <exception cref="ArgumentException">A result is a path of this provider.</exception>
    public VfsPath Add(VfsPath origin, string title, IReadOnlyList<VfsPath> results)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(results);
        foreach (var result in results)
            if (string.Equals(result.ProviderId, ProviderId, StringComparison.Ordinal))
                throw new ArgumentException($"A result cannot be a search path: '{result}'.", nameof(results));
        var paths = results.Distinct().ToList();
        lock (_lock)
        {
            var set = new ResultSet(++_lastNumber, origin, title, paths, DateTimeOffset.UtcNow);
            _sets.Add(set);
            if (_sets.Count > MaxResultSets)
            {
                _droppedOrigins[_sets[0].Number] = _sets[0].Origin;
                _sets.RemoveAt(0);
            }
            return SetPath(set.Number);
        }
    }

    // ---- IHostedProvider ----------------------------------------------------------------------------

    /// <summary>For a result set, also one dropped since (so a panel or tab left on it still leads to where the
    /// search was made), the folder searched with nothing particular focused; null otherwise.</summary>
    public HostLocation? GetHostLocation(VfsPath path)
    {
        CheckOwn(path);
        if (NumberOf(path) is not { } number) return null;
        lock (_lock)
        {
            if (_sets.Find(s => s.Number == number) is { } set) return new HostLocation(set.Origin, null);
            return _droppedOrigins.TryGetValue(number, out var origin) ? new HostLocation(origin, null) : null;
        }
    }

    /// <summary>A result set's title; otherwise the path itself.</summary>
    public string GetDisplayPath(VfsPath path)
    {
        CheckOwn(path);
        return Find(path)?.Title ?? path.Path;
    }

    // ---- IFileSystemProvider ------------------------------------------------------------------------

    /// <summary>The root lists the result sets, oldest first. A result set lists its entries as their providers
    /// report them now; a path whose provider is not registered, that no longer exists or cannot be read
    /// (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>) is left out and removed from the set.
    /// Cancellation is checked before every entry.</summary>
    /// <exception cref="VfsNotFoundException">The path is neither the root nor a result set.</exception>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        CheckOwn(directory);
        ct.ThrowIfCancellationRequested();
        if (directory.IsRoot)
        {
            ResultSet[] sets;
            lock (_lock) sets = _sets.ToArray();
            foreach (var each in sets)
            {
                ct.ThrowIfCancellationRequested();
                yield return SetEntry(each);
            }
            yield break;
        }

        var set = Find(directory) ?? throw new VfsNotFoundException($"'{directory.Path}' does not exist.");
        VfsPath[] paths;
        lock (_lock) paths = set.Results.ToArray();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var entry = await ReadAsync(path, ct).ConfigureAwait(false);
            if (entry is null)
            {
                lock (_lock) set.Results.Remove(path);
                continue;
            }
            yield return entry;
        }
    }

    /// <summary>The root and the result sets are folders; anything else is null.</summary>
    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct)
    {
        CheckOwn(path);
        if (ct.IsCancellationRequested) return Task.FromCanceled<FileEntry?>(ct);
        if (path.IsRoot) return Task.FromResult<FileEntry?>(Folder(path, _created));
        return Task.FromResult(Find(path) is { } set ? SetEntry(set) : null);
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct) => Refuse<Stream>(path);

    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => Refuse<Stream>(path);

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => Refuse<object?>(path);

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => Refuse<object?>(path);

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        CheckOwn(destination);
        return Refuse<object?>(source);
    }

    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) => Refuse<object?>(path);

    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) => Refuse<object?>(path);

    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) => Refuse<object?>(path);

    /// <summary>Equal paths of an entry that exists (the root or a result set).</summary>
    public async Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct)
    {
        CheckOwn(a);
        CheckOwn(b);
        return a == b && await GetEntryAsync(a, ct).ConfigureAwait(false) is not null;
    }

    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct)
    {
        CheckOwn(path);
        return Task.FromResult(ProviderId);
    }

    public IDisposable? Watch(VfsPath directory, Action changed) => null;

    // ---- Internals ----------------------------------------------------------------------------------

    private static VfsPath SetPath(int number) => new(ProviderId, "/" + number.ToString(CultureInfo.InvariantCulture));

    /// <summary>The result set at <paramref name="path"/> ("/n" exactly as <see cref="Add"/> spells it); null for any
    /// other path or a set that was dropped.</summary>
    private ResultSet? Find(VfsPath path)
    {
        if (NumberOf(path) is not { } number) return null;
        lock (_lock) return _sets.Find(s => s.Number == number);
    }

    /// <summary>n of a path "/n" spelt as <see cref="Add"/> spells it; null for any other path.</summary>
    private static int? NumberOf(VfsPath path)
    {
        if (!string.Equals(path.ProviderId, ProviderId, StringComparison.Ordinal) || path.Parent is not { IsRoot: true })
            return null;
        if (!int.TryParse(path.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            SetPath(number).Path != path.Path)
            return null;
        return number;
    }

    /// <summary>The entry at <paramref name="path"/> as its own provider reports it; null when that provider is not
    /// registered, the entry is gone or cannot be read.</summary>
    private async Task<FileEntry?> ReadAsync(VfsPath path, CancellationToken ct)
    {
        if (!_providers.TryGet(path.ProviderId, out var provider)) return null;
        try
        {
            return await provider.GetEntryAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileEntry SetEntry(ResultSet set) => Folder(SetPath(set.Number), set.Created);

    private static FileEntry Folder(VfsPath path, DateTimeOffset created) =>
        new(path, EntryKind.Directory, 0, created, created, Mode: null, IsHidden: false);

    private Task<T> Refuse<T>(VfsPath path)
    {
        CheckOwn(path);
        return Task.FromException<T>(new VfsAccessDeniedException($"Search results cannot be changed: '{path.Path}'."));
    }

    private void CheckOwn(VfsPath path)
    {
        if (!string.Equals(path.ProviderId, ProviderId, StringComparison.Ordinal))
            throw new ArgumentException($"Path '{path}' does not belong to provider '{Id}'.", nameof(path));
    }

    /// <summary>One result set; <see cref="Results"/> is guarded by the provider's lock.</summary>
    private sealed record ResultSet(int Number, VfsPath Origin, string Title, List<VfsPath> Results,
        DateTimeOffset Created);
}
