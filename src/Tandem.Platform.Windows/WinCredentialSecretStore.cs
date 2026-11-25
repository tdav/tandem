using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Tandem.Core.Platform;
using Tandem.Platform.Windows.Interop;
using static Tandem.Platform.Windows.Interop.Advapi32;

namespace Tandem.Platform.Windows;

/// <summary>
/// Generic credentials in the user's Windows Credential Manager, named <c>Tandem/{key}</c> and kept on this machine
/// (<c>CRED_PERSIST_LOCAL_MACHINE</c>: not roamed). The secret is stored as UTF-8, at most
/// <see cref="CRED_MAX_CREDENTIAL_BLOB_SIZE"/> bytes. A missing credential reads as null and deletes quietly; every
/// other failure is a <see cref="SecretStoreException"/> whose inner exception is the
/// <see cref="Win32Exception"/>.
/// </summary>
public sealed class WinCredentialSecretStore : ISecretStore
{
    private const string Prefix = "Tandem/";

    private readonly Lock _writes = new();

    internal static string TargetFor(string key) => Prefix + key;

    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        ValidateKey(key);
        return Task.Run(() => Get(TargetFor(key)), ct);
    }

    public Task SetAsync(string key, string secret, CancellationToken ct)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);
        return Task.Run(() =>
        {
            lock (_writes)
            {
                ct.ThrowIfCancellationRequested();
                Set(TargetFor(key), secret);
            }
        }, ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ValidateKey(key);
        return Task.Run(() =>
        {
            lock (_writes)
            {
                ct.ThrowIfCancellationRequested();
                if (!CredDelete(TargetFor(key), CRED_TYPE_GENERIC, 0))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != Kernel32.ERROR_NOT_FOUND) throw CreateError(error, "deleting", TargetFor(key));
                }
            }
        }, ct);
    }

    private static unsafe string? Get(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == Kernel32.ERROR_NOT_FOUND ? null : throw CreateError(error, "reading", target);
        }
        try
        {
            var credential = (Credential*)pointer;
            if (credential->CredentialBlobSize == 0 || credential->CredentialBlob == IntPtr.Zero) return "";
            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)credential->CredentialBlob,
                checked((int)credential->CredentialBlobSize)));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static unsafe void Set(string target, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            if (bytes.Length > CRED_MAX_CREDENTIAL_BLOB_SIZE)
                throw new SecretStoreException(
                    $"The secret for '{target}' is {bytes.Length} bytes; Credential Manager holds at most " +
                    $"{CRED_MAX_CREDENTIAL_BLOB_SIZE}.");
            fixed (char* name = target)
            fixed (byte* blob = bytes)
            {
                var credential = new Credential
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = (IntPtr)name,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = (IntPtr)blob,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                };
                // CredWrite creates the credential or replaces the one with this target name.
                if (!CredWrite(credential, 0))
                    throw CreateError(Marshal.GetLastPInvokeError(), "writing", target);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static SecretStoreException CreateError(int error, string action, string target)
    {
        var inner = new Win32Exception(error);
        return new SecretStoreException($"Credential Manager error {error} {action} '{target}': {inner.Message}",
            inner);
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Contains('\0')) throw new ArgumentException("The key contains a NUL character.", nameof(key));
    }
}
