using System.Runtime.InteropServices;

namespace Tandem.Platform.Windows.Interop;

/// <summary>shell32's file operation (shellapi.h).</summary>
internal static partial class Shell32
{
    public const uint FO_DELETE = 0x3;

    public const ushort FOF_SILENT = 0x0004;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    public const ushort FOF_NOERRORUI = 0x0400;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>SHFILEOPSTRUCTW. shellapi.h packs it to 1 byte on 32-bit Windows only; on x64 it has the natural
    /// layout, which is what <see cref="LayoutKind.Sequential"/> gives (56 bytes: fFlags at 32, the BOOL at 36).
    /// The strings are passed as pointers: <see cref="From"/> must be double-NUL terminated.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    public static partial int SHFileOperation(ref ShFileOpStruct fileOp);
}
