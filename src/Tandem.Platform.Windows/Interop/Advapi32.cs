using System.Runtime.InteropServices;

namespace Tandem.Platform.Windows.Interop;

/// <summary>The Credential Manager (wincred.h).</summary>
internal static partial class Advapi32
{
    public const uint CRED_TYPE_GENERIC = 1;
    public const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE: the largest secret a credential holds, in bytes.</summary>
    public const int CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512;

    /// <summary>CREDENTIALW with its natural layout (80 bytes on x64); the FILETIME as its two DWORDs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public uint LastWrittenLow, LastWrittenHigh;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredWrite(in Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredDelete(string targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    public static partial void CredFree(IntPtr buffer);
}
