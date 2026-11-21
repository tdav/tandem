using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Tandem.Net.Connections;

/// <summary>Stores and unlocks the account's encrypted connection profiles.</summary>
public sealed class CredentialVault(string directory) : IDisposable
{
    public const string FileName = "credentials.tandemvault";
    public const int MaxKeyBytes = 1024 * 1024;
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const string OpenError = "The vault could not be unlocked.";
    private const string SaveError = "The vault could not be saved.";

    private readonly SemaphoreSlim _gate = new(1);
    private volatile Session? _session;
    private volatile bool _configured;
    private bool _disposed;

    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), FileName);

    // Once selected, the vault stays active for this process even if its file disappears.
    public bool IsConfigured
    {
        get
        {
            if (_configured) return true;
            if (VaultPathExists()) _configured = true;
            return _configured;
        }
    }

    public bool IsUnlocked => _session is not null;

    public async Task CreateAsync(string password, IReadOnlyList<VaultEntry> entries, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        VaultEntry[]? copies = null;
        byte[]? key = null;
        try
        {
            ThrowIfDisposed();
            if (IsConfigured) throw new CredentialVaultException("The vault already exists.");
            if (string.IsNullOrEmpty(password)) throw new CredentialVaultException("The master password is required.");
            CredentialVaultFormat.ValidateEntries(entries);
            copies = entries.Select(Copy).ToArray();
            var salt = RandomNumberGenerator.GetBytes(CredentialVaultFormat.SaltSize);
            key = await Task.Run(() => CredentialVaultFormat.DeriveKey(password, salt), ct).ConfigureAwait(false);
            var encrypted = CredentialVaultFormat.Encrypt(copies, key, salt);
            var session = new Session(key, salt, SHA256.HashData(encrypted), copies);
            await WriteAsync(encrypted, expectedHash: null, ct).ConfigureAwait(false);
            _configured = true;
            _session = session;
            key = null;
            copies = null;
        }
        catch (Exception error) when (error is not CredentialVaultException &&
            error is IOException or UnauthorizedAccessException or CryptographicException or EncoderFallbackException)
        {
            throw new CredentialVaultException(SaveError);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (copies is not null) ClearEntries(copies);
            _gate.Release();
        }
    }

    public async Task UnlockAsync(string password, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        VaultEntry[]? entries = null;
        byte[]? key = null;
        try
        {
            ThrowIfDisposed();
            using var file = OpenVault();
            _configured = true;
            var length = file.Length;
            CredentialVaultFormat.ValidateFileLength(length);
            var data = new byte[(int)length];
            await file.ReadExactlyAsync(data, ct).ConfigureAwait(false);
            if (file.ReadByte() != -1) throw new CredentialVaultException(OpenError);
            var salt = CredentialVaultFormat.ReadSalt(data);
            if (string.IsNullOrEmpty(password)) throw new CredentialVaultException(OpenError);
            key = await Task.Run(() => CredentialVaultFormat.DeriveKey(password, salt), ct).ConfigureAwait(false);
            entries = CredentialVaultFormat.Decrypt(data, key);
            ct.ThrowIfCancellationRequested();

            var session = new Session(key, salt, SHA256.HashData(data), entries);
            var previous = _session;
            _session = session;
            previous?.Clear();
            key = null;
            entries = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or EncoderFallbackException)
        {
            throw new CredentialVaultException(OpenError);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (entries is not null) ClearEntries(entries);
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<VaultEntry>> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return GetSession().Entries.Select(Copy).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<IReadOnlyList<ConnectionProfile>> ReadProfilesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return GetSession().Entries.Select(entry => entry.Profile).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<VaultEntry?> FindAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = Array.Find(GetSession().Entries, existing => existing.Profile.Id == id);
            return entry is null ? null : Copy(entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(VaultEntry entry, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        VaultEntry? copy = null;
        try
        {
            var session = GetSession();
            CredentialVaultFormat.ValidateEntry(entry);
            copy = Copy(entry);
            var entries = session.Entries.ToList();
            var index = entries.FindIndex(existing => existing.Profile.Id == entry.Profile.Id);
            if (index < 0)
                entries.Add(copy);
            else
                entries[index] = copy;

            await SaveAsync(session, entries.ToArray(), ct).ConfigureAwait(false);
            if (index >= 0) ClearEntry(session.Entries[index]);
            copy = null;
        }
        finally
        {
            if (copy is not null) ClearEntry(copy);
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = GetSession();
            var entry = Array.Find(session.Entries, existing => existing.Profile.Id == id);
            if (entry is null) return;

            await SaveAsync(session, session.Entries.Where(existing => existing != entry).ToArray(), ct)
                .ConfigureAwait(false);
            ClearEntry(entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            var session = _session;
            _session = null;
            session?.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool VaultPathExists()
    {
        try
        {
            File.GetAttributes(FilePath);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An inaccessible vault must not send callers back to the old store.
            return true;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private Session GetSession()
    {
        ThrowIfDisposed();
        return _session ?? throw new CredentialVaultException("The vault is locked.");
    }

    private async Task SaveAsync(Session session, VaultEntry[] entries, CancellationToken ct)
    {
        try
        {
            var encrypted = CredentialVaultFormat.Encrypt(entries, session.Key, session.Salt);
            var updated = session with { Hash = SHA256.HashData(encrypted), Entries = entries };
            await WriteAsync(encrypted, session.Hash, ct).ConfigureAwait(false);
            _session = updated;
        }
        catch (Exception error) when (error is not CredentialVaultException &&
            error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new CredentialVaultException(SaveError);
        }
    }

    private FileStream OpenVault() => new(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private async Task WriteAsync(byte[] data, byte[]? expectedHash, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(FilePath)!;
        if (expectedHash is null) Directory.CreateDirectory(folder);

        // Keep the same lock file across replacements so cooperating processes lock the same inode.
        using var writeLock = OpenOwnerFile(Path.Combine(folder, $".{FileName}.lock"), FileMode.OpenOrCreate);
        var temporary = Path.Combine(folder, $".{FileName}.{Guid.NewGuid():N}.tandem-part");
        var staged = false;
        try
        {
            using (var file = OpenOwnerFile(temporary, FileMode.CreateNew))
            {
                staged = true;
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file.SafeFileHandle, OwnerReadWrite);
                await file.WriteAsync(data, ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            if (expectedHash is null)
            {
                if (IsConfigured) throw new CredentialVaultException("The vault already exists.");
            }
            else
            {
                await VerifyCurrentFileAsync(expectedHash, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: expectedHash is not null);
            staged = false;
        }
        finally
        {
            if (staged)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Leave the encrypted temporary file if the directory became inaccessible.
                }
            }
        }
    }

    private async Task VerifyCurrentFileAsync(byte[] expectedHash, CancellationToken ct)
    {
        const string conflict = "The vault changed on disk. Unlock it again before saving.";
        try
        {
            using var file = OpenVault();
            var length = file.Length;
            CredentialVaultFormat.ValidateFileLength(length);
            var actualHash = await HashStreamAsync(file, (int)length, ct).ConfigureAwait(false);
            if (file.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                throw new CredentialVaultException(conflict);
        }
        catch (IOException)
        {
            throw new CredentialVaultException(conflict);
        }
    }

    private static async Task<byte[]> HashStreamAsync(Stream file, int length, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (length > 0)
            {
                var count = Math.Min(length, buffer.Length);
                await file.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, count));
                length -= count;
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static FileStream OpenOwnerFile(string path, FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerReadWrite;
        return new FileStream(path, options);
    }

    private static VaultEntry Copy(VaultEntry entry) => entry with { PrivateKey = entry.PrivateKey?.ToArray() };

    internal static void ClearEntries(IEnumerable<VaultEntry> entries)
    {
        foreach (var entry in entries) ClearEntry(entry);
    }

    private static void ClearEntry(VaultEntry entry)
    {
        if (entry.PrivateKey is { } key) CryptographicOperations.ZeroMemory(key);
    }

    private sealed record Session(byte[] Key, byte[] Salt, byte[] Hash, VaultEntry[] Entries)
    {
        public void Clear()
        {
            CryptographicOperations.ZeroMemory(Key);
            ClearEntries(Entries);
        }
    }
}
