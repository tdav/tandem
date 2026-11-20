namespace Tandem.Core.Vfs;

/// <summary>Where "up" from a hosted location leads: <paramref name="Directory"/> in the host, with the cursor on
/// <paramref name="FocusName"/> (the file the provider lives in), or on nothing particular when null.</summary>
public readonly record struct HostLocation(VfsPath Directory, string? FocusName);

/// <summary>
/// A provider that lives inside another one: in a file of a host provider (an archive opened as a folder), or above a
/// place in it (search results, each result set a folder "/n" that leads back to where the search ran). Panels ask
/// <see cref="ProviderRegistry.UpOf"/>, <see cref="ProviderRegistry.HostLocationOf"/>,
/// <see cref="ProviderRegistry.DisplayPath"/> and <see cref="ProviderRegistry.PersistentLocationOf"/>, never this
/// interface or the kind of provider directly.
/// <para>If such a provider is also an <see cref="IBatchProvider"/>, its <see cref="IBatchProvider.BatchHost"/> is the
/// provider of its root's host location when that provider takes batches, and null otherwise: the changes of the
/// hosted provider end up in that file, so a <see cref="BatchScope"/> batches the host first and commits it last,
/// which keeps the global lock order. <see cref="ProviderRegistry.Register"/> refuses a provider that breaks
/// this (it checks the host location of the root only).</para>
/// </summary>
public interface IHostedProvider
{
    /// <summary>For a path whose "up" leaves the provider — its root, or a top folder such as a search result set —
    /// where "up" leads (the folder, and the name to put the cursor on). For other paths null; the registry then
    /// looks at the path's ancestors. May be called from any thread (panels ask it off the UI thread too), so it
    /// must be thread-safe and must not block.</summary>
    HostLocation? GetHostLocation(VfsPath path);

    /// <summary>The path as the user reads it, e.g. "/a/b.zip/docs". May be called from any thread.</summary>
    string GetDisplayPath(VfsPath path);
}
