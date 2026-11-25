using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tandem.Platform.Windows.Interop;

namespace Tandem.Platform.Windows.Tests;

/// <summary>The structs handed to Win32 have the x64 layout of the Windows headers.</summary>
[SupportedOSPlatform("windows")]
public sealed class InteropLayoutTests
{
    [Fact]
    public void SHFILEOPSTRUCTW_has_its_x64_layout()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.Is64BitProcess, "Windows x64 only");

        Assert.Equal(56, Marshal.SizeOf<Shell32.ShFileOpStruct>());
        Assert.Equal(16, Offset<Shell32.ShFileOpStruct>(nameof(Shell32.ShFileOpStruct.From)));
        Assert.Equal(32, Offset<Shell32.ShFileOpStruct>(nameof(Shell32.ShFileOpStruct.Flags)));
        Assert.Equal(36, Offset<Shell32.ShFileOpStruct>(nameof(Shell32.ShFileOpStruct.AnyOperationsAborted)));
        Assert.Equal(48, Offset<Shell32.ShFileOpStruct>(nameof(Shell32.ShFileOpStruct.ProgressTitle)));
    }

    [Fact]
    public void CREDENTIALW_has_its_x64_layout()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.Is64BitProcess, "Windows x64 only");

        Assert.Equal(80, Marshal.SizeOf<Advapi32.Credential>());
        Assert.Equal(32, Offset<Advapi32.Credential>(nameof(Advapi32.Credential.CredentialBlobSize)));
        Assert.Equal(40, Offset<Advapi32.Credential>(nameof(Advapi32.Credential.CredentialBlob)));
        Assert.Equal(48, Offset<Advapi32.Credential>(nameof(Advapi32.Credential.Persist)));
        Assert.Equal(72, Offset<Advapi32.Credential>(nameof(Advapi32.Credential.UserName)));
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);
}
