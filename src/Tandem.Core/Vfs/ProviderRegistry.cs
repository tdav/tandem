using System.Collections.Concurrent;

namespace Tandem.Core.Vfs;

/// <summary>Finds the provider that owns a path by its <see cref="VfsPath.ProviderId"/>.</summary>
public sealed class ProviderRegistry
{
    private readonly ConcurrentDictionary<string, IFileSystemProvider> _providers = new(StringComparer.Ordinal);

    public void Register(IFileSystemProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_providers.TryAdd(provider.Id, provider))
            throw new InvalidOperationException($"A provider with id '{provider.Id}' is already registered.");
    }

    public IFileSystemProvider Get(string id) =>
        _providers.TryGetValue(id, out var provider)
            ? provider
            : throw new KeyNotFoundException($"No provider with id '{id}' is registered.");

    public IFileSystemProvider For(VfsPath path) => Get(path.ProviderId);
}
