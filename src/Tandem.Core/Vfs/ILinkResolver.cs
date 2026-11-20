namespace Tandem.Core.Vfs;

/// <summary>
/// A provider that resolves symbolic links itself, the way its storage does: on the local disk ".." in a link's
/// target is the parent of the folder the link really is in, which the path text alone cannot tell when the path
/// passes through a linked folder. Providers without it have no such layer; their links are followed by path text.
/// </summary>
public interface ILinkResolver
{
    /// <summary>The path <paramref name="path"/> really names, with every link on the way (in its folders and at its
    /// end) followed. The result need not exist: a link that leads nowhere gives the missing target.</summary>
    /// <exception cref="VfsException">Links lead round in a loop, or too deep.</exception>
    Task<VfsPath> ResolveLinksAsync(VfsPath path, CancellationToken ct);
}
