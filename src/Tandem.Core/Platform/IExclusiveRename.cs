namespace Tandem.Core.Platform;

/// <summary>The platform's rename that refuses an existing destination in the same system call (on macOS,
/// <c>renamex_np</c> with <c>RENAME_EXCL</c>), so that no other process can create the destination between a check
/// and the rename. Used by the local provider for every rename of its own.</summary>
public interface IExclusiveRename
{
    /// <summary>Renames a file, a directory or a link (the link itself). If the destination exists — even an empty
    /// directory or a broken link — <see cref="Vfs.VfsAlreadyExistsException"/> is thrown and nothing is replaced.
    /// <see cref="NotSupportedException"/> means the volume has no exclusive rename (some network and FAT volumes);
    /// nothing has changed, and the caller may fall back to a check followed by a plain rename. Other refusals of the
    /// system are an <see cref="IOException"/> (a move to another volume among them),
    /// <see cref="FileNotFoundException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
    void Rename(string sourcePath, string destinationPath);

    /// <summary>Gives an entry another spelling of its own name — other letter case, or another Unicode normalization
    /// form — with a plain rename. The caller has established that <paramref name="destinationPath"/> names the
    /// entry <paramref name="sourcePath"/> itself (the volume ignores the difference), so nothing else can be
    /// replaced (.NET's <see cref="Directory.Move"/> refuses a change of normalization as "already exists"). Errors as for
    /// <see cref="Rename"/>, without <see cref="NotSupportedException"/>.</summary>
    void RenameSameEntry(string sourcePath, string destinationPath);
}
