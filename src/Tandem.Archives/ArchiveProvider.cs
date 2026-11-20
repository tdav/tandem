using System.Runtime.CompilerServices;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Tar;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;
using Tandem.Core.Vfs;

namespace Tandem.Archives;

/// <summary>
/// An archive file of a host provider, opened as a folder tree (<see cref="ArchiveIndex"/>); a zip can also be changed
/// (see <c>ArchiveProvider.Zip.cs</c>), every other format is read-only.
/// <para>Formats with random access (zip, tar, 7z and rar that are not solid) open an entry directly; the others
/// (tar.gz, tar.bz2, tar.xz, solid 7z and rar) read the archive from its start up to the entry. In a batch
/// (<see cref="BeginBatchAsync"/>, then <see cref="IProviderBatch.PrepareReadAsync"/>) such an archive is read once:
/// the named files go to the <see cref="TempFileStore"/>, reads come from there, and the end of the batch removes
/// them. A host that hands out a stream that cannot seek (an archive inside an archive) is copied to the
/// <see cref="TempFileStore"/> once per version of the host file.</para>
/// <para>Every call first compares the host file's size and time with those the listing was read at: when they
/// differ the listing is read again; when the file is gone the call fails with <see cref="VfsNotFoundException"/>.
/// </para>
/// <para>Library errors do not leave the provider: they become <see cref="ArchiveDamagedException"/>, and a missing
/// or wrong password <see cref="ArchivePasswordRequiredException"/>. Thread-safe; the calls do the archive's reading
/// on the calling thread, so callers keep them off the UI thread.</para>
/// </summary>
public sealed partial class ArchiveProvider : IFileSystemProvider, IHostedProvider, IBatchProvider, IDisposable
{
    /// <summary>Opened archives kept for random access; more readers at once open more, beyond it they are
    /// closed after use.</summary>
    private const int PoolSize = 4;

    private const int CopyBufferSize = 81920;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding Cp866;

    private readonly ProviderRegistry _providers;
    private readonly TempFileStore _temp;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private readonly SemaphoreSlim _batchGate = new(1, 1);
    private readonly SemaphoreSlim _asking = new(1, 1);
    private readonly IArchivePasswordPrompt? _prompt;
    private readonly Lock _preparedLock = new();
    private readonly Dictionary<string, string> _prepared = new(StringComparer.Ordinal);
    private Snapshot? _preparedFor;
    private volatile Snapshot _snapshot = null!;
    private volatile string? _password;
    private volatile bool _stale; // the listing must be read again even if the host file looks the same
    private bool _disposed;

    static ArchiveProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp866 = Encoding.GetEncoding(866);
    }

    private ArchiveProvider(string id, ProviderRegistry providers, VfsPath host, TempFileStore temp, string? password,
        IArchivePasswordPrompt? prompt)
    {
        Id = id;
        _providers = providers;
        Host = host;
        _temp = temp;
        _password = password;
        _prompt = prompt;
        BatchHost = providers.Get(host.ProviderId) as IBatchProvider;
    }

    /// <summary>Opens <paramref name="host"/> as an archive: the format by its content (by its name when the content
    /// tells nothing), and the listing.</summary>
    /// <param name="id">The provider id to register the result under.</param>
    /// <param name="password">For an archive whose listing is encrypted; kept in memory only.</param>
    /// <param name="prompt">Asked when an entry needs a password the provider does not have (see
    /// <see cref="OpenReadAsync"/>); null: such a read fails with <see cref="ArchivePasswordRequiredException"/>.
    /// The listing's own password is not asked for here: that is the caller's (<see cref="ArchiveMounts"/>).</param>
    /// <exception cref="VfsNotFoundException">There is no file at <paramref name="host"/>.</exception>
    /// <exception cref="ArchiveDamagedException">The file is no archive, or a damaged one.</exception>
    /// <exception cref="ArchivePasswordRequiredException">The listing is encrypted and the password did not fit or
    /// was not given.</exception>
    public static async Task<ArchiveProvider> OpenAsync(string id, ProviderRegistry providers, VfsPath host,
        TempFileStore temp, string? password, IArchivePasswordPrompt? prompt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(temp);
        if (host.IsRoot) throw new ArgumentException("The archive must be a file.", nameof(host));
        var provider = new ArchiveProvider(id, providers, host, temp, password, prompt);
        try
        {
            var stamp = await provider.HostStampAsync(ct).ConfigureAwait(false);
            provider._snapshot = await provider.LoadAsync(stamp, ct).ConfigureAwait(false);
            return provider;
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    public string Id { get; }

    /// <summary>The archive file, in its host provider.</summary>
    public VfsPath Host { get; }

    /// <summary>The format of the archive as last read.</summary>
    public ArchiveFormat Format => _snapshot.Format;

    /// <summary>Whether any entry of the archive as last read is encrypted.</summary>
    public bool HasEncryptedEntries => _snapshot.HasEncryptedEntries;

    /// <summary>The password reads use from now on: the one the archive was opened with, then the one a read found
    /// through the prompt. Kept in memory only. Setting it does not read the listing again.</summary>
    public string? Password
    {
        get => _password;
        set => _password = value;
    }

    /// <summary>How many entries the listing as last read left out or shows under another name
    /// (<see cref="ArchiveIndex.RenamedOrDroppedCount"/>).</summary>
    public int RenamedOrDroppedCount => _snapshot.Index.RenamedOrDroppedCount;

    public IBatchProvider? BatchHost { get; }

    // ---- Reading -------------------------------------------------------------------------------------------

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (PendingEdits() is { } edits)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var entry in PendingChildren(edits, directory))
            {
                ct.ThrowIfCancellationRequested();
                yield return entry;
            }
            yield break;
        }
        var snapshot = await CurrentAsync(ct).ConfigureAwait(false);
        var children = snapshot.Index.ChildrenOf(directory.Path);
        if (children is null)
        {
            if (snapshot.Index.Find(directory.Path) is null)
                throw new VfsNotFoundException($"'{GetDisplayPath(directory)}' does not exist.");
            throw new VfsException($"'{GetDisplayPath(directory)}' is not a directory.");
        }
        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToEntry(child);
        }
    }

    public async Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct)
    {
        if (PendingEdits() is { } edits)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return PendingEntry(edits, path);
        }
        var snapshot = await CurrentAsync(ct).ConfigureAwait(false);
        return snapshot.Index.Find(path.Path) is { } node ? ToEntry(node) : null;
    }

    /// <summary>The entry's content. For an encrypted entry without a password that fits, the prompt is asked for one
    /// (one question at a time for the whole archive, again after a wrong answer); the password that fits is kept
    /// for later reads.</summary>
    /// <exception cref="ArchivePasswordRequiredException">The entry is encrypted and no password fitted: none was
    /// given, the user declined (now or earlier in the batch the caller holds, which then does not ask again), or
    /// <see cref="ArchiveMounts.MaxPasswordAttempts"/> answers were wrong.</exception>
    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct)
    {
        if (PendingEdits() is { } edits)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await OpenPendingAsync(edits, path, ct).ConfigureAwait(false);
        }
        var snapshot = await CurrentAsync(ct).ConfigureAwait(false);
        var node = snapshot.Index.Find(path.Path)
                   ?? throw new VfsNotFoundException($"'{GetDisplayPath(path)}' does not exist.");
        if (node.Kind != EntryKind.File) throw new VfsException($"'{GetDisplayPath(path)}' is not a file.");

        lock (_preparedLock)
        {
            if (ReferenceEquals(_preparedFor, snapshot) && _prepared.TryGetValue(node.Path, out var file))
                return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize,
                    useAsync: true);
        }
        var password = _password;
        try
        {
            return await OpenEntryAsync(snapshot, node, password, ct).ConfigureAwait(false);
        }
        catch (ArchivePasswordRequiredException) when (_prompt is not null && !DeclinedInHeldBatch())
        {
            return await OpenAskingAsync(snapshot, node, password, ct).ConfigureAwait(false);
        }
    }

    /// <summary>True when the calling flow holds the open batch (<see cref="BatchScope.Holds"/>) and the user
    /// declined the password in it: the rest of that operation's reads fail without asking again.</summary>
    private bool DeclinedInHeldBatch() => BatchScope.Holds(this) && OpenBatch() is { PasswordDeclined: true };

    /// <summary>True when both are the same existing path of this provider.</summary>
    public async Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct)
    {
        if (a != b || !string.Equals(a.ProviderId, Id, StringComparison.Ordinal)) return false;
        return await GetEntryAsync(a, ct).ConfigureAwait(false) is not null;
    }

    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => Task.FromResult("archive");

    public IDisposable? Watch(VfsPath directory, Action changed) => null;

    // ---- Hosting -------------------------------------------------------------------------------------------

    /// <summary>"Up" from the root leads to the archive's folder, with the archive focused.</summary>
    public HostLocation? GetHostLocation(VfsPath path) =>
        path.IsRoot ? new HostLocation(Host.Parent!.Value, Host.Name) : null;

    public string GetDisplayPath(VfsPath path) => _providers.DisplayPath(Host) + (path.IsRoot ? "" : path.Path);

    // ---- Batches -------------------------------------------------------------------------------------------

    /// <summary>A batch collects the changes of a zip until <see cref="IProviderBatch.CommitAsync"/> rewrites it once
    /// (see the remarks of the zip part), and gathers reads: <see cref="IProviderBatch.PrepareReadAsync"/> unpacks the
    /// files of a sequential archive in one pass. A second batch waits until the first one is disposed.</summary>
    public async Task<IProviderBatch> BeginBatchAsync(CancellationToken ct)
    {
        BeginningBatch?.Invoke();
        await _batchGate.WaitAsync(ct).ConfigureAwait(false);
        var batch = new Batch(this);
        lock (_editLock) _openBatch = batch;
        return batch;
    }

    /// <summary>Closes the archives kept open and removes the provider's temporary files.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OpenBatch()?.Discard();
        ReleasePrepared();
        _snapshot?.Retire(_temp);
    }

    // ---- Listing -------------------------------------------------------------------------------------------

    private IFileSystemProvider HostProvider => _providers.For(Host);

    private async Task<HostStamp> HostStampAsync(CancellationToken ct)
    {
        var entry = await HostProvider.GetEntryAsync(Host, ct).ConfigureAwait(false);
        if (entry is null || entry.Kind == EntryKind.Directory)
            throw new VfsNotFoundException($"'{_providers.DisplayPath(Host)}' does not exist.");
        return new HostStamp(entry.Size, entry.Modified);
    }

    /// <summary>The listing for the host file as it is now, read again if the file has changed.</summary>
    private async Task<Snapshot> CurrentAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stamp = await HostStampAsync(ct).ConfigureAwait(false);
        var snapshot = _snapshot;
        if (snapshot.Stamp == stamp && !_stale) return snapshot;
        await _reload.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = _snapshot;
            if (snapshot.Stamp == stamp && !_stale) return snapshot;
            var fresh = await LoadAsync(stamp, ct).ConfigureAwait(false);
            _snapshot = fresh;
            _stale = false;
            snapshot.Retire(_temp);
            return fresh;
        }
        finally
        {
            _reload.Release();
        }
    }

    private async Task<Snapshot> LoadAsync(HostStamp stamp, CancellationToken ct)
    {
        var password = _password;
        string? localCopy = null;
        Stream? stream = null;
        try
        {
            stream = await HostProvider.OpenReadAsync(Host, ct).ConfigureAwait(false);
            if (!stream.CanSeek)
            {
                localCopy = _temp.NewFile(Host.Name);
                await using (var copy = new FileStream(localCopy, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, CopyBufferSize, useAsync: true))
                    await stream.CopyToAsync(copy, CopyBufferSize, ct).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                stream = OpenLocalCopy(localCopy);
            }
            var format = await ArchiveFormats.DetectAsync(stream, ct).ConfigureAwait(false)
                         ?? ArchiveFormats.FromName(Host.Name)
                         ?? throw new ArchiveDamagedException($"'{_providers.DisplayPath(Host)}' is not an archive.");
            ct.ThrowIfCancellationRequested();
            var snapshot = Guard(() => ReadListing(stream, format, stamp, localCopy, password), password);
            stream = null; // the snapshot owns it now, or it was closed
            return snapshot;
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            if (localCopy is not null) ReleaseQuietly(localCopy);
            throw;
        }
    }

    /// <summary>Reads the entries. A random-access archive stays open in the new snapshot's pool, owning
    /// <paramref name="stream"/>; otherwise the stream is closed here.</summary>
    private Snapshot ReadListing(Stream stream, ArchiveFormat format, HostStamp stamp, string? localCopy,
        string? password)
    {
        var options = Options(password);
        if (format is ArchiveFormat.TarGz or ArchiveFormat.TarBz2 or ArchiveFormat.TarXz)
        {
            var entries = new List<RawEntry>();
            using (stream)
            using (var reader = ReaderFactory.Open(stream, options))
                while (reader.MoveToNextEntry())
                    entries.Add(ToRaw(reader.Entry));
            return new Snapshot(stamp, format, ArchiveIndex.Build(entries, stamp.Modified), sequential: true,
                localCopy, HostProvider, Host);
        }

        IArchive? archive = null;
        try
        {
            archive = OpenArchive(stream, format, options);
            var listed = archive.Entries.ToList();
            var index = ArchiveIndex.Build(listed.Select(ToRaw), stamp.Modified);
            var sequential = format is ArchiveFormat.SevenZip or ArchiveFormat.Rar && archive.IsSolid;
            var snapshot = new Snapshot(stamp, format, index, sequential, localCopy, HostProvider, Host,
                storedLinks: format == ArchiveFormat.Zip && listed.Any(IsStoredLink),
                comments: archive is ZipArchive zip ? ZipComments.Of(zip, listed) : null);
            if (!sequential)
            {
                snapshot.Return(new OpenedArchive(stream, archive, password, listed));
                archive = null;
            }
            return snapshot;
        }
        finally
        {
            if (archive is not null)
            {
                archive.Dispose();
                stream.Dispose();
            }
        }
    }

    /// <summary>A zip entry kept as a file whose Unix mode (the high half of its external attributes) marks a
    /// link; it is listed as the file it is stored as.</summary>
    private static bool IsStoredLink(IArchiveEntry entry) =>
        entry.Attrib is { } attributes && ((attributes >>> 16) & 0xF000) == 0xA000;

    private static IArchive OpenArchive(Stream stream, ArchiveFormat format, ReaderOptions options) => format switch
    {
        ArchiveFormat.Zip => ZipArchive.Open(stream, options),
        ArchiveFormat.Tar => TarArchive.Open(stream, options),
        ArchiveFormat.SevenZip => SevenZipArchive.Open(stream, options),
        ArchiveFormat.Rar => RarArchive.Open(stream, options),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static ReaderOptions Options(string? password) => new()
    {
        Password = password,
        LeaveStreamOpen = true,
        ArchiveEncoding = new ArchiveEncoding { CustomDecoder = DecodeName },
    };

    /// <summary>A zip name flagged as UTF-8 is UTF-8. One without the flag is UTF-8 too when its bytes are valid
    /// UTF-8 (macOS writes names so, without the flag), otherwise CP866, the DOS code page of Russian
    /// archivers.</summary>
    private static string DecodeName(byte[] data, int start, int count, EncodingType type)
    {
        if (type == EncodingType.UTF8) return Encoding.UTF8.GetString(data, start, count);
        try
        {
            return StrictUtf8.GetString(data, start, count);
        }
        catch (DecoderFallbackException)
        {
            return Cp866.GetString(data, start, count);
        }
    }

    private static RawEntry ToRaw(IEntry entry)
    {
        DateTimeOffset? modified = null;
        try
        {
            if (entry.LastModifiedTime is { } time) modified = new DateTimeOffset(time);
        }
        catch (ArgumentOutOfRangeException)
        {
            // a time the archive cannot mean: the archive's own time stands in
        }
        string? link;
        try
        {
            link = string.IsNullOrEmpty(entry.LinkTarget) ? null : entry.LinkTarget;
        }
        catch (Exception e) when (e is NotImplementedException or NotSupportedException)
        {
            link = null;
        }
        return new RawEntry(entry.Key ?? "", entry.IsDirectory, entry.Size, modified, link, entry.IsEncrypted);
    }

    private FileEntry ToEntry(ArchiveNode node) =>
        new(new VfsPath(Id, node.Path), node.Kind, node.Size, node.Modified, node.Modified, Mode: null,
            IsHidden: node.Name.StartsWith('.'), node.LinkTarget);

    // ---- Opening entries -----------------------------------------------------------------------------------

    private Task<Stream> OpenEntryAsync(Snapshot snapshot, ArchiveNode node, string? password, CancellationToken ct) =>
        snapshot.Sequential
            ? OpenInPassAsync(snapshot, node, password, ct)
            : OpenDirectAsync(snapshot, node, password, ct);

    /// <summary>Opens an entry that <paramref name="failed"/> did not open, asking the prompt for the password: one
    /// question at a time for the whole archive (a read that waited first tries what an earlier one found), with
    /// <c>previousWasWrong</c> when the password tried last did not fit, at most
    /// <see cref="ArchiveMounts.MaxPasswordAttempts"/> times. The password that fits is kept. A decline from a flow
    /// that holds the open batch is remembered by that batch (<see cref="DeclinedInHeldBatch"/>).</summary>
    /// <exception cref="ArchivePasswordRequiredException">The user declined, or every answer was wrong.</exception>
    private async Task<Stream> OpenAskingAsync(Snapshot snapshot, ArchiveNode node, string? failed,
        CancellationToken ct)
    {
        await _asking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var known = _password;
            if (known != failed)
            {
                try
                {
                    return await OpenEntryAsync(snapshot, node, known, ct).ConfigureAwait(false);
                }
                catch (ArchivePasswordRequiredException)
                {
                    failed = known;
                }
            }
            var wrong = failed is not null;
            // A read that waited for another one's question in the same operation does not ask after its decline.
            if (DeclinedInHeldBatch()) throw new ArchivePasswordRequiredException(Host, wrong);
            for (var attempt = 1; ; attempt++)
            {
                var answer = await _prompt!.AskAsync(Host, wrong, ct).ConfigureAwait(false);
                if (answer is null)
                {
                    if (BatchScope.Holds(this) && OpenBatch() is { } held) held.PasswordDeclined = true;
                    throw new ArchivePasswordRequiredException(Host, wrong);
                }
                try
                {
                    var stream = await OpenEntryAsync(snapshot, node, answer, ct).ConfigureAwait(false);
                    _password = answer;
                    return stream;
                }
                catch (ArchivePasswordRequiredException) when (attempt < ArchiveMounts.MaxPasswordAttempts)
                {
                    wrong = true;
                }
            }
        }
        finally
        {
            _asking.Release();
        }
    }

    /// <summary>Random access: the entry's stream of an archive from the pool, which goes back to the pool when
    /// the stream is closed.</summary>
    private async Task<Stream> OpenDirectAsync(Snapshot snapshot, ArchiveNode node, string? password,
        CancellationToken ct)
    {
        var opened = snapshot.Rent(password) ?? await OpenPooledAsync(snapshot, password, ct).ConfigureAwait(false);
        try
        {
            var entry = opened.Find(node)
                        ?? throw new ArchiveDamagedException($"'{_providers.DisplayPath(Host)}' has changed.");
            var stream = Guard(entry.OpenEntryStream, password);
            return new EntryStream(this, stream, node.Size, password, ok =>
            {
                if (ok) snapshot.Return(opened);
                else opened.Dispose();
            });
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    private async Task<OpenedArchive> OpenPooledAsync(Snapshot snapshot, string? password, CancellationToken ct)
    {
        var stream = await snapshot.OpenSourceAsync(ct).ConfigureAwait(false);
        try
        {
            var archive = Guard(() => OpenArchive(stream, snapshot.Format, Options(password)), password);
            return new OpenedArchive(stream, archive, password, entries: null);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sequential access: a pass from the start of the archive to the entry.</summary>
    private async Task<Stream> OpenInPassAsync(Snapshot snapshot, ArchiveNode node, string? password,
        CancellationToken ct)
    {
        var pass = await OpenPassAsync(snapshot, password, ct).ConfigureAwait(false);
        try
        {
            while (Guard(pass.MoveNext, password))
            {
                ct.ThrowIfCancellationRequested();
                if (!pass.Is(node)) continue;
                var stream = Guard(pass.Reader.OpenEntryStream, password);
                return new EntryStream(this, stream, node.Size, password, _ => pass.Dispose());
            }
            throw new ArchiveDamagedException($"'{_providers.DisplayPath(Host)}' has changed.");
        }
        catch
        {
            pass.Dispose();
            throw;
        }
    }

    private async Task<Pass> OpenPassAsync(Snapshot snapshot, string? password, CancellationToken ct)
    {
        var stream = await snapshot.OpenSourceAsync(ct).ConfigureAwait(false);
        try
        {
            return Guard(() =>
            {
                var options = Options(password);
                if (snapshot.Format is ArchiveFormat.SevenZip or ArchiveFormat.Rar)
                {
                    var archive = OpenArchive(stream, snapshot.Format, options);
                    try
                    {
                        return new Pass(archive.ExtractAllEntries(), archive, stream);
                    }
                    catch
                    {
                        archive.Dispose();
                        throw;
                    }
                }
                return new Pass(ReaderFactory.Open(stream, options), null, stream);
            }, password);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>One pass over a sequential archive: unpacks those of <paramref name="files"/> that are its files
    /// into the temporary store, for the reads of the batch. A hint only: if the pass fails, what it had unpacked
    /// stays, and the reads of the rest meet the error themselves.</summary>
    private async Task PrepareAsync(IReadOnlyList<VfsPath> files, CancellationToken ct)
    {
        Snapshot snapshot;
        try
        {
            snapshot = await CurrentAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException)
        {
            return;
        }
        if (!snapshot.Sequential) return;
        var wanted = new Dictionary<(string Key, int Occurrence), ArchiveNode>();
        foreach (var file in files)
            if (snapshot.Index.Find(file.Path) is { Kind: EntryKind.File, EntryKey: { } key } node)
                wanted.TryAdd((key, node.KeyOccurrence), node);
        if (wanted.Count == 0) return;

        lock (_preparedLock)
        {
            if (!ReferenceEquals(_preparedFor, snapshot)) ReleasePreparedLocked();
            _preparedFor = snapshot;
        }
        var password = _password;
        try
        {
            using var pass = await OpenPassAsync(snapshot, password, ct).ConfigureAwait(false);
            while (wanted.Count > 0 && Guard(pass.MoveNext, password))
            {
                ct.ThrowIfCancellationRequested();
                if (!wanted.Remove(pass.Current, out var node)) continue;
                var target = _temp.NewFile(node.Name);
                try
                {
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                                     FileShare.None, CopyBufferSize, useAsync: true))
                    await using (var input = new EntryStream(this, Guard(pass.Reader.OpenEntryStream, password),
                                     node.Size, password, _ => { }))
                        await input.CopyToAsync(output, CopyBufferSize, ct).ConfigureAwait(false);
                }
                catch
                {
                    ReleaseQuietly(target);
                    throw;
                }
                lock (_preparedLock) _prepared[node.Path] = target;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a hint only: the files not unpacked are read the usual way
        }
    }

    private void ReleasePrepared()
    {
        lock (_preparedLock) ReleasePreparedLocked();
    }

    private void ReleasePreparedLocked()
    {
        foreach (var file in _prepared.Values) ReleaseQuietly(file);
        _prepared.Clear();
        _preparedFor = null;
    }

    private void ReleaseQuietly(string file)
    {
        try
        {
            _temp.Release(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the store's next Clear removes it
        }
    }

    private static FileStream OpenLocalCopy(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: false);

    // ---- Errors --------------------------------------------------------------------------------------------

    /// <summary>Runs library code, turning its errors into <see cref="VfsException"/>s.</summary>
    private T Guard<T>(Func<T> action, string? password)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (Translate(e, password) is { } translated && translated != e)
        {
            throw translated;
        }
    }

    /// <summary>The exception to throw for <paramref name="error"/>: the same one for cancellation, provider
    /// errors and the host's I/O errors; a password error for the library's cryptographic error; damage for any
    /// other library error.</summary>
    private Exception Translate(Exception error, string? password) => error switch
    {
        OperationCanceledException or VfsException => error,
        CryptographicException => new ArchivePasswordRequiredException(Host, wrongPassword: password is not null,
            error),
        IOException and not EndOfStreamException => error,
        _ => new ArchiveDamagedException($"'{_providers.DisplayPath(Host)}' is damaged: {error.Message}", error),
    };

    // ---- Parts ---------------------------------------------------------------------------------------------

    private readonly record struct HostStamp(long Size, DateTimeOffset Modified);

    /// <summary>A zip's comments: the archive's, and the entries' by (key, occurrence of that key).</summary>
    private sealed record ZipComments(string? Archive,
        IReadOnlyDictionary<(string Key, int Occurrence), string> Entries)
    {
        public static ZipComments Of(ZipArchive zip, IEnumerable<IArchiveEntry> listed)
        {
            var entries = new Dictionary<(string, int), string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in listed)
            {
                var key = entry.Key ?? "";
                var occurrence = counts.GetValueOrDefault(key);
                counts[key] = occurrence + 1;
                if (entry is ZipArchiveEntry { Comment: { Length: > 0 } comment }) entries[(key, occurrence)] = comment;
            }
            return new ZipComments(zip.Volumes.FirstOrDefault()?.Comment, entries);
        }
    }

    /// <summary>The listing of one version of the host file, and the archives opened on that version.</summary>
    /// <param name="storedLinks">Whether the archive keeps links its listing shows as files (a zip's).</param>
    /// <param name="comments">A zip's comments, which a rewrite carries over.</param>
    private sealed class Snapshot(HostStamp stamp, ArchiveFormat format, ArchiveIndex index, bool sequential,
        string? localCopy, IFileSystemProvider hostProvider, VfsPath host, bool storedLinks = false,
        ZipComments? comments = null)
    {
        private readonly Lock _lock = new();
        private readonly Stack<OpenedArchive> _pool = new();
        private bool _retired;

        public HostStamp Stamp { get; } = stamp;
        public ArchiveFormat Format { get; } = format;
        public ArchiveIndex Index { get; } = index;
        public bool Sequential { get; } = sequential;

        public bool HasEncryptedEntries { get; } = Any(index, node => node.IsEncrypted);

        public string? ArchiveComment => comments?.Archive;

        /// <summary>The comment of the entry <paramref name="node"/> shows; null for none.</summary>
        public string? CommentOf(ArchiveNode? node) =>
            node?.EntryKey is { } key ? comments?.Entries.GetValueOrDefault((key, node.KeyOccurrence)) : null;

        /// <summary>Whether the archive holds links: listed as such, or kept as files (<c>storedLinks</c>).</summary>
        public bool HasLinks { get; } = storedLinks || Any(index, node => node.Kind == EntryKind.Symlink);

        /// <summary>A new stream over the archive's bytes: the local copy, or the host file.</summary>
        public async Task<Stream> OpenSourceAsync(CancellationToken ct)
        {
            if (localCopy is not null) return OpenLocalCopy(localCopy);
            var stream = await hostProvider.OpenReadAsync(host, ct).ConfigureAwait(false);
            if (stream.CanSeek) return stream;
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new VfsException($"'{host.Path}' can no longer be read at random.");
        }

        /// <summary>An archive opened with <paramref name="password"/> from the pool; null if there is none.</summary>
        public OpenedArchive? Rent(string? password)
        {
            lock (_lock)
            {
                while (_pool.TryPop(out var opened))
                {
                    if (opened.Password == password) return opened;
                    opened.Dispose();
                }
                return null;
            }
        }

        public void Return(OpenedArchive opened)
        {
            lock (_lock)
            {
                if (!_retired && _pool.Count < PoolSize)
                {
                    _pool.Push(opened);
                    return;
                }
            }
            opened.Dispose();
        }

        /// <summary>The host file has changed (or the provider is done): the pooled archives are closed, those still
        /// in use as they are returned, and the local copy goes.</summary>
        public void Retire(TempFileStore temp)
        {
            lock (_lock)
            {
                _retired = true;
                while (_pool.TryPop(out var opened)) opened.Dispose();
            }
            if (localCopy is null) return;
            try
            {
                temp.Release(localCopy); // open copies stay readable until closed
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the store's next Clear removes it
            }
        }

        private static bool Any(ArchiveIndex index, Func<ArchiveNode, bool> predicate)
        {
            var pending = new Stack<string>([Root]);
            while (pending.TryPop(out var path))
                foreach (var child in index.ChildrenOf(path) ?? [])
                {
                    if (predicate(child)) return true;
                    if (child.Kind == EntryKind.Directory) pending.Push(child.Path);
                }
            return false;
        }

        private const string Root = "/";
    }

    /// <summary>An archive opened for random access, owning its stream.</summary>
    private sealed class OpenedArchive(Stream stream, IArchive archive, string? password, List<IArchiveEntry>? entries)
        : IDisposable
    {
        private Dictionary<(string, int), IArchiveEntry>? _byKey;

        public string? Password { get; } = password;

        public IArchiveEntry? Find(ArchiveNode node)
        {
            if (node.EntryKey is null) return null;
            if (_byKey is null)
            {
                var byKey = new Dictionary<(string, int), IArchiveEntry>();
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var entry in entries ?? archive.Entries)
                {
                    var key = entry.Key ?? "";
                    var occurrence = counts.GetValueOrDefault(key);
                    counts[key] = occurrence + 1;
                    byKey[(key, occurrence)] = entry;
                }
                _byKey = byKey;
            }
            return _byKey.GetValueOrDefault((node.EntryKey, node.KeyOccurrence));
        }

        public void Dispose()
        {
            archive.Dispose();
            stream.Dispose();
        }
    }

    private sealed class Batch(ArchiveProvider owner) : IProviderBatch
    {
        private int _disposed;

        /// <summary>The changes collected so far; null until the first change. Guarded by the owner's edit
        /// lock.</summary>
        public Edits? PendingEdits { get; private set; }

        /// <summary>The user declined the password for a read of this batch's operation.</summary>
        public volatile bool PasswordDeclined;

        public Task PrepareReadAsync(IReadOnlyList<VfsPath> files, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(files);
            ThrowIfDisposed();
            return owner.PrepareAsync(files, ct);
        }

        /// <summary>Rewrites the archive with the collected changes, if there are any.</summary>
        public Task CommitAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            return owner.CommitEditsAsync(this, ct);
        }

        /// <summary>The changes to add to: those collected so far, or a new set over the current listing.</summary>
        public async Task<Edits> EditsAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            lock (owner._editLock)
                if (PendingEdits is { } edits)
                    return edits;
            var snapshot = await owner.CurrentAsync(ct).ConfigureAwait(false);
            // The listing may have just been read again: the archive may have become one that cannot be changed.
            if (!owner.IsEditable(snapshot)) throw owner.ReadOnly(new VfsPath(owner.Id, "/"));
            var fresh = Edits.From(snapshot);
            lock (owner._editLock) return PendingEdits ??= fresh;
        }

        /// <summary>Drops the collected changes and their temporary files.</summary>
        public void Discard()
        {
            Edits? edits;
            lock (owner._editLock)
            {
                edits = PendingEdits;
                PendingEdits = null;
            }
            if (edits is null) return;
            foreach (var file in edits.TempFiles) owner.ReleaseQuietly(file);
        }

        /// <summary>Drops uncommitted changes, removes the files unpacked for the batch and lets the next batch
        /// begin.</summary>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            lock (owner._editLock)
                if (ReferenceEquals(owner._openBatch, this))
                    owner._openBatch = null;
            Discard();
            owner.ReleasePrepared();
            owner._batchGate.Release();
            return ValueTask.CompletedTask;
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <summary>A forward pass over the entries, telling each by key and occurrence as the listing did.</summary>
    private sealed class Pass(IReader reader, IArchive? archive, Stream stream) : IDisposable
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public IReader Reader { get; } = reader;

        public (string Key, int Occurrence) Current { get; private set; }

        public bool MoveNext()
        {
            if (!Reader.MoveToNextEntry()) return false;
            var key = Reader.Entry.Key ?? "";
            var occurrence = _counts.GetValueOrDefault(key);
            _counts[key] = occurrence + 1;
            Current = (key, occurrence);
            return true;
        }

        public bool Is(ArchiveNode node) => node.EntryKey == Current.Key && node.KeyOccurrence == Current.Occurrence;

        public void Dispose()
        {
            Reader.Dispose();
            archive?.Dispose();
            stream.Dispose();
        }
    }

    /// <summary>An entry's content as the library streams it, with the library's errors translated; closing it
    /// calls <c>closed</c> with whether every read went well, even when closing the library's stream throws.
    /// Internal for tests.</summary>
    internal sealed class EntryStream(ArchiveProvider owner, Stream inner, long length, string? password,
        Action<bool> closed) : Stream
    {
        private bool _failed;
        private bool _closed;

        public override bool CanRead => !_closed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return inner.Read(buffer);
            }
            catch (Exception e)
            {
                throw Fail(e);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw Fail(e);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                try
                {
                    inner.Dispose();
                }
                catch (Exception e) when (owner.Translate(e, password) != e)
                {
                    _failed = true;
                }
                catch
                {
                    _failed = true; // passes on (cancellation, the host's I/O error): the archive is not reused
                    throw;
                }
                finally
                {
                    closed(!_failed);
                }
            }
            base.Dispose(disposing);
        }

        private Exception Fail(Exception error)
        {
            _failed = true;
            return owner.Translate(error, password);
        }
    }
}
