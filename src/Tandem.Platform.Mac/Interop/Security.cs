using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tandem.Platform.Mac.Interop;

[SupportedOSPlatform("macos")]
internal static unsafe partial class Security
{
    private const string Framework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal const int NotFound = -25300;
    internal const int Duplicate = -25299;

    private static readonly Lazy<IntPtr> Library = new(() => NativeLibrary.Load(Framework));
    private static readonly Lazy<IntPtr> TrueValue = new(() =>
        *(IntPtr*)NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFBooleanTrue"));

    internal static IntPtr Constant(string symbol) => *(IntPtr*)NativeLibrary.GetExport(Library.Value, symbol);
    internal static IntPtr True => TrueValue.Value;

    [LibraryImport(Framework)]
    internal static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(Framework)]
    internal static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(Framework)]
    internal static partial int SecItemUpdate(IntPtr query, IntPtr attributes);

    [LibraryImport(Framework)]
    internal static partial int SecItemDelete(IntPtr query);

    [LibraryImport(Framework)]
    internal static partial IntPtr SecCopyErrorMessageString(int status, IntPtr reserved);

    [LibraryImport(CoreFoundation)]
    internal static partial void CFRelease(IntPtr value);
}
