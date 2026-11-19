using Tandem.App.Resources;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.Services;

/// <summary>Words for what the operations engine reports: item errors by exception type, refusals by reason, and
/// operation titles. The engine's own messages are English and for logs; they are shown only as secondary detail.</summary>
public static class OperationTexts
{
    private const int MacDiskFull = 28; // ENOSPC, the HResult .NET gives an IOException on macOS
    private const int WindowsDiskFull = unchecked((int)0x80070070);
    private const int WindowsHandleDiskFull = unchecked((int)0x80070027);

    /// <summary>What went wrong with <paramref name="item"/>, worded from the kind of <paramref name="error"/>.</summary>
    public static string ForError(VfsPath item, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var key = error switch
        {
            VfsAccessDeniedException or UnauthorizedAccessException => "OperationError_AccessDenied",
            VfsNotFoundException or FileNotFoundException or DirectoryNotFoundException => "OperationError_NotFound",
            VfsAlreadyExistsException => "OperationError_AlreadyExists",
            ContentMismatchException => "OperationError_ContentMismatch",
            IOException { HResult: MacDiskFull or WindowsDiskFull or WindowsHandleDiskFull } =>
                "OperationError_DiskFull",
            IOException => "OperationError_IO",
            _ => "OperationError_Other",
        };
        return Loc.Format(key, item.Path);
    }

    /// <summary>Why the engine refused to start, with the item it names when there is one.</summary>
    public static string ForRejection(OperationRejectedException rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        var reason = Loc.Enum(rejection.Reason);
        return rejection.SourcePath is { } source
            ? Loc.Format("Operation_RejectedItem", reason, source.Path)
            : reason;
    }

    /// <summary>"Copying: “a.txt” → /dst", "Deleting: 3 items": the kind, the single item's name or the count, and
    /// the destination when there is one.</summary>
    public static string Title(OperationKind kind, IReadOnlyList<VfsPath> sources, VfsPath? destination)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var items = sources.Count == 1
            ? Loc.Format("Operation_ItemName", sources[0].Name)
            : Loc.Count("Count_Items", sources.Count);
        return destination is { } to
            ? Loc.Format("Operation_TitleTo", Loc.Enum(kind), items, to.Path)
            : Loc.Format("Operation_Title", Loc.Enum(kind), items);
    }
}
