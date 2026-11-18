using System.Runtime.Versioning;
using Tandem.Core.Platform;
using Tandem.Core.Vfs.Local;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac;

/// <summary>Finder tags through the <c>NSURLTagNamesKey</c> resource of a file <c>NSURL</c>; the owner through
/// <c>-[NSFileManager attributesOfItemAtPath:error:]</c>. Every item is reached inside a broker access scope.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacFileMetadataService(IFileAccessBroker broker) : IFileMetadataService
{
    private readonly IFileAccessBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));

    public IReadOnlyList<string> GetTags(string path)
    {
        ProcessLaunch.RequireAbsolute(path, nameof(path));
        using var access = _broker.Access(path);
        using var pool = new ObjC.AutoreleasePool();

        var tags = ObjC.GetResourceValue(ItemUrl(path), ObjC.FoundationConstant("NSURLTagNamesKey"));
        return ObjC.ToStringList(tags);
    }

    public void SetTags(string path, IReadOnlyList<string> tags)
    {
        ProcessLaunch.RequireAbsolute(path, nameof(path));
        ArgumentNullException.ThrowIfNull(tags);
        foreach (var tag in tags)
            if (string.IsNullOrEmpty(tag)) throw new ArgumentException("A tag name is null or empty.", nameof(tags));

        using var access = _broker.Access(path);
        using var pool = new ObjC.AutoreleasePool();

        ObjC.SetResourceValue(ItemUrl(path), ObjC.FoundationConstant("NSURLTagNamesKey"), ObjC.ToNSArray(tags));
    }

    public unsafe OwnerInfo GetOwner(string path)
    {
        ProcessLaunch.RequireAbsolute(path, nameof(path));
        using var access = _broker.Access(path);
        using var pool = new ObjC.AutoreleasePool();

        var manager = ObjC.Send(ObjC.Class("NSFileManager"), ObjC.Selector("defaultManager"));
        IntPtr error = IntPtr.Zero;
        var attributes = ObjC.SendError(manager, ObjC.Selector("attributesOfItemAtPath:error:"),
            ObjC.FileSystemPathString(path), &error);
        if (attributes == IntPtr.Zero) throw new IOException(ObjC.ErrorDescription(error));

        return new OwnerInfo(
            Attribute(attributes, "NSFileOwnerAccountName", "NSFileOwnerAccountID"),
            Attribute(attributes, "NSFileGroupOwnerAccountName", "NSFileGroupOwnerAccountID"));
    }

    /// <summary>The URL of the item itself: a symbolic link is not followed.</summary>
    private static IntPtr ItemUrl(string path) =>
        ObjC.FileUrl(path, new FileInfo(path).LinkTarget is null && Directory.Exists(path));

    /// <summary>The account name, or the numeric id when the id has no name on this system.</summary>
    private static string Attribute(IntPtr attributes, string nameKey, string idKey)
    {
        var objectForKey = ObjC.Selector("objectForKey:");
        return ObjC.ToManagedString(ObjC.Send(attributes, objectForKey, ObjC.FoundationConstant(nameKey)))
               ?? ObjC.DescriptionOf(ObjC.Send(attributes, objectForKey, ObjC.FoundationConstant(idKey)))
               ?? throw new IOException("The system did not report the item's owner.");
    }
}
