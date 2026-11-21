using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Tandem.Core.Platform;
using Tandem.Platform.Mac.Interop;

namespace Tandem.Platform.Mac;

/// <summary>Generic passwords scoped by service and account in the macOS login Keychain.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainSecretStore(string service = "Tandem") : ISecretStore
{
    private readonly Lock _writes = new();
    private readonly string _service = ValidateName(service, nameof(service));

    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        ValidateName(key, nameof(key));
        return Task.Run(() => Get(key), ct);
    }

    public Task SetAsync(string key, string secret, CancellationToken ct)
    {
        ValidateName(key, nameof(key));
        ArgumentNullException.ThrowIfNull(secret);
        return Task.Run(() => Set(key, secret, ct), ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ValidateName(key, nameof(key));
        return Task.Run(() =>
        {
            lock (_writes)
            {
                ct.ThrowIfCancellationRequested();
                using var pool = new ObjC.AutoreleasePool();
                Check(Security.SecItemDelete(Query(key)));
            }
        }, ct);
    }

    private unsafe string? Get(string key)
    {
        using var pool = new ObjC.AutoreleasePool();
        var query = Query(key);
        Put(query, "kSecReturnData", Security.True);
        var status = Security.SecItemCopyMatching(query, out var data);
        try
        {
            Check(status);
            if (status == Security.NotFound) return null;
            var length = checked((int)ObjC.SendNUInt(data, ObjC.Selector("length")));
            var bytes = ObjC.Send(data, ObjC.Selector("bytes"));
            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)bytes, length));
        }
        finally
        {
            if (data != IntPtr.Zero) Security.CFRelease(data);
        }
    }

    private unsafe void Set(string key, string secret, CancellationToken ct)
    {
        lock (_writes)
        {
            ct.ThrowIfCancellationRequested();
            using var pool = new ObjC.AutoreleasePool();
            var bytes = Encoding.UTF8.GetBytes(secret);
            IntPtr data;
            try
            {
                fixed (byte* address = bytes)
                    data = ObjC.SendBytesLength(ObjC.Class("NSData"), ObjC.Selector("dataWithBytes:length:"),
                        (IntPtr)address, (nuint)bytes.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            var query = Query(key);
            var attributes = Query(key);
            Put(attributes, "kSecValueData", data);
            var status = Security.SecItemAdd(attributes, IntPtr.Zero);
            if (status == Security.Duplicate)
            {
                var changes = NewDictionary();
                Put(changes, "kSecValueData", data);
                status = Security.SecItemUpdate(query, changes);
                if (status == Security.NotFound)
                    status = Security.SecItemAdd(attributes, IntPtr.Zero);
            }
            if (status != 0) throw CreateError(status);
        }
    }

    internal static Exception? ErrorFor(int status) =>
        status is 0 or Security.NotFound ? null : CreateError(status);

    private static SecretStoreException CreateError(int status)
    {
        using var pool = new ObjC.AutoreleasePool();
        var description = Security.SecCopyErrorMessageString(status, IntPtr.Zero);
        try
        {
            return new SecretStoreException($"Keychain error {status}: {ObjC.ToManagedString(description)}");
        }
        finally
        {
            if (description != IntPtr.Zero) Security.CFRelease(description);
        }
    }

    private static void Check(int status)
    {
        if (ErrorFor(status) is { } error) throw error;
    }

    private IntPtr Query(string key)
    {
        var query = NewDictionary();
        Put(query, "kSecClass", Security.Constant("kSecClassGenericPassword"));
        Put(query, "kSecAttrService", ObjC.ToNSString(_service));
        Put(query, "kSecAttrAccount", ObjC.ToNSString(key));
        return query;
    }

    private static IntPtr NewDictionary() => ObjC.Send(ObjC.Class("NSMutableDictionary"), ObjC.Selector("dictionary"));

    private static void Put(IntPtr dictionary, string key, IntPtr value) =>
        ObjC.SendVoid(dictionary, ObjC.Selector("setObject:forKey:"), value, Security.Constant(key));

    private static string ValidateName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        if (value.Contains('\0')) throw new ArgumentException("The name contains a NUL character.", parameterName);
        return value;
    }
}
