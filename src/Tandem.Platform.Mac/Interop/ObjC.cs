using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Tandem.Platform.Mac.Interop;

/// <summary>
/// The Objective-C runtime and the few Foundation conversions the Mac platform needs. Objects returned here are
/// autoreleased: call these inside an <see cref="AutoreleasePool"/> and copy out what you need before it ends.
/// <para><c>objc_msgSend</c> is declared once per call shape, with exact non-variadic signatures (as arm64
/// requires); <c>BOOL</c> is one byte there.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string FoundationPath = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static readonly Lazy<IntPtr> AppKit = new(() => NativeLibrary.Load(AppKitPath));
    private static readonly IntPtr Foundation;

    static ObjC()
    {
        // Foundation's classes (NSString, NSFileManager, …) are registered with the runtime only once it is loaded.
        Foundation = NativeLibrary.Load(FoundationPath);
    }

    /// <summary>Loads AppKit, so that its classes (<c>NSWorkspace</c>, …) are registered with the runtime.</summary>
    public static void LoadAppKit() => _ = AppKit.Value;

    /// <summary>The value of an exported Foundation <c>NSString *</c> constant (such as <c>NSURLTagNamesKey</c>),
    /// looked up by symbol name; such constants are never released.</summary>
    /// <exception cref="EntryPointNotFoundException">Foundation exports no such symbol.</exception>
    public static IntPtr FoundationConstant(string name) => *(IntPtr*)NativeLibrary.GetExport(Foundation, name);

    /// <summary>Returns the class named <paramref name="name"/>.</summary>
    /// <exception cref="InvalidOperationException">No such class is loaded.</exception>
    public static IntPtr Class(string name)
    {
        var cls = objc_getClass(name);
        return cls != IntPtr.Zero ? cls : throw new InvalidOperationException($"Objective-C class '{name}' not found.");
    }

    public static IntPtr Selector(string name) => sel_registerName(name);

    /// <summary>An autoreleased <c>NSString</c> with the text of <paramref name="value"/>.</summary>
    public static IntPtr ToNSString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0')) throw new ArgumentException("The string contains a NUL character.", nameof(value));
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Class("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>The text of an <c>NSString</c>; null for nil.</summary>
    public static string? ToManagedString(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, Selector("UTF8String")));

    /// <summary>An autoreleased <c>NSArray</c> of <c>NSString</c>s.</summary>
    public static IntPtr ToNSArray(IReadOnlyList<string> values)
    {
        var objects = new IntPtr[values.Count];
        for (var i = 0; i < objects.Length; i++) objects[i] = ToNSString(values[i]);
        fixed (IntPtr* first = objects)
            return SendObjectsCount(Class("NSArray"), Selector("arrayWithObjects:count:"), first, (nuint)objects.Length);
    }

    /// <summary>The texts of an <c>NSArray</c> of <c>NSString</c>s; empty for nil.</summary>
    public static List<string> ToStringList(IntPtr nsArray)
    {
        var result = new List<string>();
        if (nsArray == IntPtr.Zero) return result;
        var count = SendNUInt(nsArray, Selector("count"));
        var objectAtIndex = Selector("objectAtIndex:");
        for (nuint i = 0; i < count; i++)
            result.Add(ToManagedString(SendIndex(nsArray, objectAtIndex, i)) ?? "");
        return result;
    }

    /// <summary>An autoreleased file <c>NSURL</c> for <paramref name="path"/>; it does not touch the disk. The path's
    /// bytes are kept as given (<c>fileURLWithPath:</c> would decompose its Unicode).</summary>
    public static IntPtr FileUrl(string path, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains('\0')) throw new ArgumentException("The path contains a NUL character.", nameof(path));
        var utf8 = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            return SendFileUrl(Class("NSURL"), Selector("fileURLWithFileSystemRepresentation:isDirectory:relativeToURL:"),
                utf8, isDirectory ? (byte)1 : (byte)0, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>An autoreleased <c>NSString</c> path for <c>NSFileManager</c> methods that take one, made by
    /// <c>-[NSFileManager stringWithFileSystemRepresentation:length:]</c> so that it names exactly the bytes of
    /// <paramref name="path"/>.</summary>
    public static IntPtr FileSystemPathString(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains('\0')) throw new ArgumentException("The path contains a NUL character.", nameof(path));
        var utf8 = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var manager = Send(Class("NSFileManager"), Selector("defaultManager"));
            var length = (nuint)System.Text.Encoding.UTF8.GetByteCount(path);
            return SendBytesLength(manager, Selector("stringWithFileSystemRepresentation:length:"), utf8, length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>The <c>boolValue</c> of an <c>NSNumber</c>; false for nil.</summary>
    public static bool ToBool(IntPtr nsNumber) => nsNumber != IntPtr.Zero && SendBool(nsNumber, Selector("boolValue")) != 0;

    /// <summary>The <c>description</c> of an object; null for nil.</summary>
    public static string? DescriptionOf(IntPtr obj) =>
        obj == IntPtr.Zero ? null : ToManagedString(Send(obj, Selector("description")));

    /// <summary>The value of a resource of a file <c>NSURL</c> (<c>-getResourceValue:forKey:error:</c>); nil when it
    /// has none.</summary>
    /// <exception cref="IOException">The value could not be read; the message is the system's description.</exception>
    public static IntPtr GetResourceValue(IntPtr url, IntPtr key)
    {
        IntPtr value = IntPtr.Zero, error = IntPtr.Zero;
        if (SendGetResource(url, Selector("getResourceValue:forKey:error:"), &value, key, &error) == 0)
            throw new IOException(ErrorDescription(error));
        return value;
    }

    /// <summary>Sets a resource of a file <c>NSURL</c> (<c>-setResourceValue:forKey:error:</c>).</summary>
    /// <exception cref="IOException">The value could not be written; the message is the system's description.</exception>
    public static void SetResourceValue(IntPtr url, IntPtr key, IntPtr value)
    {
        IntPtr error = IntPtr.Zero;
        if (SendSetResource(url, Selector("setResourceValue:forKey:error:"), value, key, &error) == 0)
            throw new IOException(ErrorDescription(error));
    }

    /// <summary>The path of a file <c>NSURL</c>, as its file system representation; null for nil.</summary>
    public static string? PathOf(IntPtr url) =>
        url == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(url, Selector("fileSystemRepresentation")));

    /// <summary>The paths of an <c>NSArray</c> of file <c>NSURL</c>s; empty for nil.</summary>
    public static List<string> ToPathList(IntPtr nsArray)
    {
        var result = new List<string>();
        if (nsArray == IntPtr.Zero) return result;
        var count = SendNUInt(nsArray, Selector("count"));
        var objectAtIndex = Selector("objectAtIndex:");
        for (nuint i = 0; i < count; i++)
            if (PathOf(SendIndex(nsArray, objectAtIndex, i)) is { } path) result.Add(path);
        return result;
    }

    /// <summary>The <c>localizedDescription</c> of an <c>NSError</c>.</summary>
    public static string ErrorDescription(IntPtr nsError) =>
        nsError == IntPtr.Zero
            ? "Unknown error."
            : ToManagedString(Send(nsError, Selector("localizedDescription"))) ?? "Unknown error.";

    /// <summary>An <c>NSAutoreleasePool</c> for the current thread; dispose it on the same thread.</summary>
    public readonly struct AutoreleasePool : IDisposable
    {
        private readonly IntPtr _pool;

        public AutoreleasePool() =>
            _pool = Send(Send(Class("NSAutoreleasePool"), Selector("alloc")), Selector("init"));

        public void Dispose()
        {
            if (_pool != IntPtr.Zero) SendVoid(_pool, Selector("drain"));
        }
    }

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(LibObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    /// <summary><c>id (id, SEL)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    /// <summary><c>id (id, SEL, id)</c> — also for a <c>const char *</c> argument.</summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary><c>void (id, SEL, id, id)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

    /// <summary><c>void (id, SEL)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(IntPtr receiver, IntPtr selector);

    /// <summary><c>NSUInteger (id, SEL)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial nuint SendNUInt(IntPtr receiver, IntPtr selector);

    /// <summary><c>id (id, SEL, NSUInteger)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint index);

    /// <summary><c>id (id, SEL, const char *, BOOL, id)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendFileUrl(IntPtr receiver, IntPtr selector, IntPtr path, byte isDirectory,
        IntPtr relativeTo);

    /// <summary><c>id (id, SEL, const id *, NSUInteger)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendObjectsCount(IntPtr receiver, IntPtr selector, IntPtr* objects, nuint count);

    /// <summary><c>BOOL (id, SEL)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial byte SendBool(IntPtr receiver, IntPtr selector);

    /// <summary><c>id (id, SEL, id, NSUInteger)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendObjectOptions(IntPtr receiver, IntPtr selector, IntPtr arg, nuint options);

    /// <summary><c>id (id, SEL, const char *, NSUInteger)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendBytesLength(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

    /// <summary><c>id (id, SEL, id, NSError **)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr SendError(IntPtr receiver, IntPtr selector, IntPtr arg, IntPtr* outError);

    /// <summary><c>BOOL (id, SEL, id *, id, NSError **)</c> — <c>getResourceValue:forKey:error:</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendGetResource(IntPtr receiver, IntPtr selector, IntPtr* value, IntPtr key,
        IntPtr* outError);

    /// <summary><c>BOOL (id, SEL, id, id, NSError **)</c> — <c>setResourceValue:forKey:error:</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendSetResource(IntPtr receiver, IntPtr selector, IntPtr value, IntPtr key,
        IntPtr* outError);

    /// <summary><c>BOOL (id, SEL, id, id *, NSError **)</c></summary>
    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial byte SendOutOut(IntPtr receiver, IntPtr selector, IntPtr arg, IntPtr* out1, IntPtr* outError);
}
