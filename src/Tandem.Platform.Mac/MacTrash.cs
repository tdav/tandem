using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Core.Vfs.Local;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac;

/// <summary>The Finder's Trash, through <c>-[NSFileManager trashItemAtURL:resultingItemURL:error:]</c>.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacTrash(IFileAccessBroker broker) : ITrash
{
    private readonly IFileAccessBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));

    public Task MoveToTrashAsync(string localPath, CancellationToken ct) => MoveToTrashAndGetLocationAsync(localPath, ct);

    /// <summary>Moves the item to the trash and returns where it ended up there (the trash may rename it).</summary>
    /// <exception cref="IOException">The item could not be moved; the message is the system's description.</exception>
    public Task<string> MoveToTrashAndGetLocationAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (ct.IsCancellationRequested) return Task.FromCanceled<string>(ct);

        // Once started the move is not interrupted, so an item is never left half in the trash.
        return Task.Run(() =>
        {
            using var access = _broker.Access(path);
            return TrashItem(path);
        }, CancellationToken.None);
    }

    private static unsafe string TrashItem(string path)
    {
        using var pool = new ObjC.AutoreleasePool();

        // The URL is built without following a symbolic link, so a link is trashed itself and never its target.
        var info = new FileInfo(path);
        var isDirectory = info.LinkTarget is null && Directory.Exists(path);
        var url = ObjC.FileUrl(path, isDirectory);

        var manager = ObjC.Send(ObjC.Class("NSFileManager"), ObjC.Selector("defaultManager"));
        IntPtr resultingUrl = IntPtr.Zero, error = IntPtr.Zero;
        var moved = ObjC.SendOutOut(manager, ObjC.Selector("trashItemAtURL:resultingItemURL:error:"), url,
            &resultingUrl, &error);
        if (moved == 0) throw new IOException(ObjC.ErrorDescription(error));

        return ObjC.PathOf(resultingUrl) ?? throw new IOException($"The trash did not report where '{path}' went.");
    }
}
