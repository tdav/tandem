using System.Runtime.InteropServices;
using Tandem.Core.Platform;
using Tandem.Platform.Windows.Interop;
using static Tandem.Platform.Windows.Interop.Shell32;

namespace Tandem.Platform.Windows;

/// <summary>
/// The Recycle Bin, through <c>SHFileOperationW(FO_DELETE)</c> with <c>FOF_ALLOWUNDO</c>, no confirmation, progress
/// or error dialogs, on a background STA thread of its own. A link or junction is recycled itself, never its target.
/// <para>On a volume without a Recycle Bin (a network share, some removable media), or for an item too large for it,
/// the shell would delete the item for good; <c>FOF_WANTNUKEWARNING</c> makes it ask the user first instead of doing
/// that silently, and a "no" there is an <see cref="IOException"/>.</para>
/// </summary>
public sealed class WinTrash : ITrash
{
    private const ushort Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI |
                                 FOF_NOCONFIRMMKDIR | FOF_WANTNUKEWARNING;

    /// <exception cref="IOException">The item was not moved (it is missing, in use, or the user declined).</exception>
    public Task MoveToTrashAsync(string localPath, CancellationToken ct)
    {
        WinPath.RequireAbsolute(localPath, nameof(localPath));
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        // Once started the move is not interrupted, so an item is never left half in the bin. The shell's file
        // operation wants a thread of its own apartment; a pool thread is MTA.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Recycle(localPath);
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        })
        {
            IsBackground = true,
            Name = "Tandem Recycle Bin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void Recycle(string path)
    {
        // The shell does not take the \\?\ prefix, nor a trailing separator; it does take a long path.
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // pFrom is a list of NUL-terminated names ended by an empty one: the string's own NUL plus one more.
        var from = Marshal.StringToHGlobalUni(full + '\0');
        try
        {
            var operation = new ShFileOpStruct { Func = FO_DELETE, From = from, Flags = Flags };
            var result = SHFileOperation(ref operation);
            if (result != 0)
                throw new IOException(result is Kernel32.ERROR_FILE_NOT_FOUND or Kernel32.ERROR_PATH_NOT_FOUND or 0x7C
                    ? $"'{path}' does not exist."
                    : $"'{path}' could not be moved to the Recycle Bin (shell error 0x{result:X}).",
                    SystemErrors.HResultOf(result));
            if (operation.AnyOperationsAborted != 0)
                throw new IOException($"Moving '{path}' to the Recycle Bin was cancelled.");
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}
