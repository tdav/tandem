using Tandem.Core.Vfs;

namespace Tandem.Archives;

/// <summary>The file is not an archive Tandem can read, or the archive is damaged.</summary>
public class ArchiveDamagedException(string message, Exception? inner = null) : VfsException(message, inner);

/// <summary>Reading needs the archive's password: none was given (<see cref="WrongPassword"/> false) or the one given
/// did not fit (true). The library cannot tell the two apart; the provider knows whether it gave one.</summary>
public sealed class ArchivePasswordRequiredException(VfsPath archive, bool wrongPassword, Exception? inner = null)
    : VfsException(wrongPassword
        ? $"The password for '{archive.Path}' is wrong."
        : $"'{archive.Path}' needs a password.", inner)
{
    /// <summary>The archive file, in its host.</summary>
    public VfsPath Archive { get; } = archive;

    public bool WrongPassword { get; } = wrongPassword;
}

/// <summary>The archive cannot store <see cref="Name"/> as it is: its writer would change it (see
/// <see cref="ArchiveEntryNames.IsStorable"/>), so the change is refused.</summary>
public sealed class ArchiveEntryNameException(string name, string message) : VfsException(message)
{
    /// <summary>The name that cannot be stored.</summary>
    public string Name { get; } = name;
}
