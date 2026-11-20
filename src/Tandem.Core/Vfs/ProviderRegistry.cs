using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Tandem.Core.Vfs;

/// <summary>Finds the provider that owns a path by its <see cref="VfsPath.ProviderId"/>, opens files as folders through
/// its <see cref="IFileMounter"/>s, and answers where hosted locations (<see cref="IHostedProvider"/>) belong — so
/// that panels and operations never need to know what kind of provider a path is in. Thread-safe.</summary>
public sealed class ProviderRegistry
{
    private readonly ConcurrentDictionary<string, IFileSystemProvider> _providers = new(StringComparer.Ordinal);
    private readonly Lock _mountersLock = new();
    private IFileMounter[] _mounters = [];

    /// <exception cref="InvalidOperationException">A provider with the same id is registered; or the provider is a
    /// hosted <see cref="IBatchProvider"/> whose <see cref="IBatchProvider.BatchHost"/> is not the registered provider
    /// of its root's host location (or null when that provider takes no batches), or whose host is not
    /// registered.</exception>
    public void Register(IFileSystemProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        CheckBatchHost(provider);
        if (!_providers.TryAdd(provider.Id, provider))
            throw new InvalidOperationException($"A provider with id '{provider.Id}' is already registered.");
    }

    public IFileSystemProvider Get(string id) =>
        _providers.TryGetValue(id, out var provider)
            ? provider
            : throw new KeyNotFoundException($"No provider with id '{id}' is registered.");

    public bool TryGet(string id, [NotNullWhen(true)] out IFileSystemProvider? provider) =>
        _providers.TryGetValue(id, out provider);

    public IFileSystemProvider For(VfsPath path) => Get(path.ProviderId);

    /// <summary>Adds a mounter; <see cref="TryMountAsync"/> asks mounters in the order they were added.</summary>
    public void AddMounter(IFileMounter mounter)
    {
        ArgumentNullException.ThrowIfNull(mounter);
        lock (_mountersLock) _mounters = [.. _mounters, mounter];
    }

    /// <summary>The folder <paramref name="file"/> opens as: the answer of the first mounter that answers (they are
    /// asked in turn, in the order added); null when none does. A mounter's error ends the search and is
    /// thrown.</summary>
    /// <param name="byContent">See <see cref="IFileMounter.TryMountAsync"/>.</param>
    public async Task<VfsPath?> TryMountAsync(FileEntry file, bool byContent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        IFileMounter[] mounters;
        lock (_mountersLock) mounters = _mounters;
        foreach (var mounter in mounters)
        {
            ct.ThrowIfCancellationRequested();
            if (await mounter.TryMountAsync(file, byContent, ct).ConfigureAwait(false) is { } folder) return folder;
        }
        return null;
    }

    /// <summary>Where the hosted part of the provider that holds <paramref name="path"/> belongs in its host: the
    /// <see cref="IHostedProvider.GetHostLocation"/> of the nearest of <paramref name="path"/> and its ancestors that
    /// has one (the root of an archive, a search result set "/n"). Null for a provider that is not hosted, or a path
    /// none of whose ancestors has a host location.</summary>
    /// <exception cref="KeyNotFoundException">The path's provider is not registered.</exception>
    public HostLocation? HostLocationOf(VfsPath path)
    {
        if (For(path) is not IHostedProvider hosted) return null;
        for (VfsPath? at = path; at is { } current; at = current.Parent)
        {
            if (hosted.GetHostLocation(current) is { } location) return location;
        }
        return null;
    }

    /// <summary>The path as the user reads it: <see cref="IHostedProvider.GetDisplayPath"/> for a hosted provider,
    /// otherwise <see cref="VfsPath.Path"/>.</summary>
    /// <exception cref="KeyNotFoundException">The path's provider is not registered.</exception>
    public string DisplayPath(VfsPath path) =>
        For(path) is IHostedProvider hosted ? hosted.GetDisplayPath(path) : path.Path;

    /// <summary>A location worth saving (a tab, history) that will still exist after a restart:
    /// <paramref name="path"/> itself when <see cref="HostLocationOf"/> has none, otherwise the persistent location
    /// of the host location's directory — so a path deep in an archive inside an archive gives the folder of the
    /// outermost archive.</summary>
    /// <exception cref="KeyNotFoundException">A provider on the way is not registered.</exception>
    /// <exception cref="InvalidOperationException">The host locations lead around in a cycle.</exception>
    public VfsPath PersistentLocationOf(VfsPath path)
    {
        var seen = new HashSet<VfsPath>();
        var current = path;
        while (HostLocationOf(current) is { } location)
        {
            if (!seen.Add(current))
                throw new InvalidOperationException($"The host locations of '{path}' lead around in a cycle.");
            current = location.Directory;
        }
        return current;
    }

    /// <summary>A hosted batching provider must name its host's provider as its batch host (see
    /// <see cref="IHostedProvider"/>), or the batch lock order would not hold.</summary>
    private void CheckBatchHost(IFileSystemProvider provider)
    {
        if (provider is not (IBatchProvider batching and IHostedProvider hosted)) return;
        if (hosted.GetHostLocation(new VfsPath(provider.Id, "/")) is not { } location) return;
        if (!TryGet(location.Directory.ProviderId, out var host))
            throw new InvalidOperationException(
                $"The host '{location.Directory.ProviderId}' of provider '{provider.Id}' is not registered.");
        var expected = host as IBatchProvider;
        if (!ReferenceEquals(batching.BatchHost, expected))
            throw new InvalidOperationException(
                $"Provider '{provider.Id}' must report {(expected is null ? "no batch host" : $"'{host.Id}'")} " +
                "as its batch host: its host location is there.");
    }
}
