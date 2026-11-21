using Tandem.Core.Tools;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <summary>
/// Puts a file in place without ever leaving a damaged or missing file under the real name. The new content is
/// written under <c>.{name}.tandem-part</c> next to the destination and renamed into place once complete; when it
/// replaces an existing file, the old file is set aside as <c>.{name}.tandem-old</c> only after the part file is
/// complete, and deleted once the new file is in place (or put back if that fails). Used by copy and move, and by
/// anything else that rewrites a file in place.
/// </summary>
public static class SafeReplace
{
    // Copies, archive rewrites and editor uploads can run outside the same queue.
    // Serialize use of their shared part and backup names, including case-only aliases.
    private static readonly SemaphoreSlim[] DestinationGates = Enumerable.Range(0, 64)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public const string PartSuffix = ".tandem-part";
    public const string BackupSuffix = ".tandem-old";

    /// <summary>The suffix of the temporary name a group rename gives an item: <c>.{guid}.tandem-rename</c>.</summary>
    public const string RenameSuffix = ".tandem-rename";

    /// <summary>True for a name of the form <c>.{…}.tandem-part</c>, <c>.{…}.tandem-old</c> or
    /// <c>.{…}.tandem-rename</c>: what an interrupted operation left behind, not a file of the user's.</summary>
    public static bool IsServiceName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!name.StartsWith('.')) return false;
        foreach (var suffix in (ReadOnlySpan<string>)[PartSuffix, BackupSuffix, RenameSuffix])
            if (name.Length > suffix.Length + 1 && name.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary><c>.{name}.tandem-part</c> next to <paramref name="destination"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is a root, which has no name or
    /// parent.</exception>
    public static VfsPath PartPathFor(VfsPath destination) => SiblingPath(destination, PartSuffix);

    /// <summary>Has <paramref name="write"/> produce the item under its part name next to
    /// <paramref name="destination"/> (the path it is given), then renames it into place. With
    /// <paramref name="replace"/>, the existing destination is first set aside as <c>.{name}.tandem-old</c> and
    /// deleted only once the new item is in place; if moving the part into place fails, the backup is put back, so
    /// the destination is never left empty. If anything fails, the part file is removed and the original error
    /// propagates.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is a root.</exception>
    public static async Task WriteAsync(IFileSystemProvider target, VfsPath destination, bool replace,
        Func<VfsPath, Task> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(write);
        var part = PartPathFor(destination);
        var gate = GateFor(destination);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await write(part).ConfigureAwait(false);
            await CommitAsync(target, part, destination, replace).ConfigureAwait(false);
        }
        catch
        {
            await DeleteQuietlyAsync(part, target).ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Renames <paramref name="from"/> to <paramref name="destination"/>. With <paramref name="replace"/>,
    /// the existing destination is first set aside as <c>.{name}.tandem-old</c> and deleted only once
    /// <paramref name="from"/> is in place; if that rename fails, the backup is put back. <paramref name="from"/>
    /// itself is never removed here. Not cancellable.</summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is a root.</exception>
    public static async Task MoveIntoPlaceAsync(IFileSystemProvider target, VfsPath from, VfsPath destination,
        bool replace)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireNotRoot(destination);
        var gate = GateFor(destination);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CommitAsync(target, from, destination, replace).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static SemaphoreSlim GateFor(VfsPath path) =>
        DestinationGates[StringComparer.OrdinalIgnoreCase.GetHashCode(NameKey.Of(path.Path)) & (DestinationGates.Length - 1)];

    private static async Task CommitAsync(IFileSystemProvider target, VfsPath from, VfsPath destination, bool replace)
    {
        var ct = CancellationToken.None;
        if (!replace)
        {
            await target.MoveAsync(from, destination, ct).ConfigureAwait(false);
            return;
        }

        var backup = SiblingPath(destination, BackupSuffix);
        // Reached only with a destination in place (the conflict was asked about it), so a backup left over from an
        // earlier attempt is not the only copy of this name: it is an older version, set aside by an overwrite that
        // was interrupted (a crash between the two renames leaves the old file only there, and no file under the
        // real name until the user copies again). Removing it here keeps the older version from piling up.
        await DeleteQuietlyAsync(backup, target).ConfigureAwait(false);
        await target.MoveAsync(destination, backup, ct).ConfigureAwait(false);
        try
        {
            await target.MoveAsync(from, destination, ct).ConfigureAwait(false);
        }
        catch
        {
            await RestoreQuietlyAsync(backup, destination, target).ConfigureAwait(false);
            throw;
        }
        // The new item is in place; a backup that cannot be removed is harmless and cleared by the next overwrite.
        await DeleteQuietlyAsync(backup, target).ConfigureAwait(false);
    }

    /// <summary><c>.{name}{suffix}</c> next to <paramref name="destination"/>.</summary>
    private static VfsPath SiblingPath(VfsPath destination, string suffix)
    {
        RequireNotRoot(destination);
        return destination.Parent!.Value.Combine("." + destination.Name + suffix);
    }

    private static void RequireNotRoot(VfsPath destination)
    {
        if (destination.IsRoot)
            throw new ArgumentException($"The destination '{destination}' is a root, not an item to put in place.",
                nameof(destination));
    }

    /// <summary>Best-effort return of the set-aside original; a failure here must not hide the error being handled
    /// (the original then survives under its backup name).</summary>
    private static async Task RestoreQuietlyAsync(VfsPath backup, VfsPath destination, IFileSystemProvider target)
    {
        try
        {
            await target.MoveAsync(backup, destination, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing better to do: the caller reports the original error.
        }
    }

    /// <summary>Best-effort removal of a part or backup file; a failure here must not hide the error being
    /// handled.</summary>
    private static async Task DeleteQuietlyAsync(VfsPath part, IFileSystemProvider target)
    {
        try
        {
            if (await target.GetEntryAsync(part, CancellationToken.None).ConfigureAwait(false) is not null)
                await target.DeleteAsync(part, recursive: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original error is what gets reported; the stray part file has a recognisable name.
        }
    }
}
