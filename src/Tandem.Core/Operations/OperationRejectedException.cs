using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <summary>Why an operation was refused before it started.</summary>
public enum RejectionReason
{
    /// <summary>A new name was given for more than one source (<see cref="OperationRejectedException.Name"/>).</summary>
    NewNameWithMultipleSources,

    /// <summary>The destination directory is the source or lies inside it.</summary>
    DestinationInsideSource,

    /// <summary>The item would be copied onto itself.</summary>
    SourceEqualsDestination,

    /// <summary><see cref="OperationRejectedException.Name"/> is not a valid file name.</summary>
    InvalidName,

    /// <summary>Moving to the trash was asked for, but there is no trash for <see cref="OperationRejectedException.SourcePath"/>
    /// (it is not on the local disk, or no trash service is available).</summary>
    TrashNotSupported,

    /// <summary>The file to be created (<see cref="OperationRejectedException.DestinationPath"/>) already
    /// exists.</summary>
    DestinationExists,

    /// <summary>Files of the asked format cannot be written (<see cref="OperationRejectedException.DestinationPath"/>
    /// is the file that was to be created).</summary>
    FormatNotWritable,

    /// <summary><see cref="OperationRejectedException.SourcePath"/> is a whole volume (on Windows a drive or a share
    /// in the drive list), which is never deleted or moved.</summary>
    VolumeRoot,
}

/// <summary>The refusal of whole volumes as sources.</summary>
internal static class VolumeRoots
{
    /// <summary>Throws <see cref="RejectionReason.VolumeRoot"/> for the first source that is a whole volume.</summary>
    public static void Reject(IEnumerable<VfsPath> sources)
    {
        foreach (var source in sources)
            if (Vfs.Local.LocalPath.IsVolumeRoot(source))
                throw new OperationRejectedException(RejectionReason.VolumeRoot,
                    $"'{source}' is a whole volume.", source);
    }
}

/// <summary>The operation was refused before it touched anything (e.g. copying a folder into itself).
/// <see cref="Exception.Message"/> is English and meant for logs; the interface words the refusal from
/// <see cref="Reason"/> and the paths.</summary>
public sealed class OperationRejectedException(RejectionReason reason, string message, VfsPath? source = null,
    VfsPath? destination = null, string? name = null) : Exception(message)
{
    public RejectionReason Reason { get; } = reason;

    /// <summary>The source the refusal is about, when it is about one.</summary>
    public VfsPath? SourcePath { get; } = source;

    /// <summary>The destination involved: the destination directory, or the final path for
    /// <see cref="RejectionReason.SourceEqualsDestination"/>, or the archive file a pack was to create.</summary>
    public VfsPath? DestinationPath { get; } = destination;

    /// <summary>The name involved, for <see cref="RejectionReason.InvalidName"/> and
    /// <see cref="RejectionReason.NewNameWithMultipleSources"/>.</summary>
    public string? Name { get; } = name;
}
