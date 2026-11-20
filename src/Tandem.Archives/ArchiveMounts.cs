using Tandem.Core.Vfs;

namespace Tandem.Archives;

/// <summary>
/// Opens archive files as folders: each archive file once, as an <see cref="ArchiveProvider"/> registered under
/// <c>"archive:" + n</c> in the <see cref="ProviderRegistry"/> until the application exits (<see cref="Dispose"/>
/// closes them); mounting the same file again gives the same root. A file inside an archive mounts the same way, its
/// host being a path of that archive's provider.
/// <para>An archive whose listing is encrypted is asked for its password at mount, through the prompt; an archive
/// whose listing is in the clear is asked when an encrypted entry is first read (by the provider, through the same
/// prompt). The password lives in the provider, in memory only.</para>
/// <para>Thread-safe: mounts of different files run side by side, mounts of one file one after another.</para>
/// </summary>
public sealed class ArchiveMounts : IFileMounter, IDisposable
{
    /// <summary>How many answers the prompt gets for one mount or one read before the password counts as
    /// wrong.</summary>
    public const int MaxPasswordAttempts = 3;

    /// <summary>Bytes read to recognise an archive by its content when the host's stream cannot seek: the
    /// signatures, and the first tar header inside a compressed stream.</summary>
    private const int DetectPrefixSize = 64 * 1024;

    /// <summary>Opens <paramref name="archive"/> as a provider with the given id and listing password; a seam for
    /// tests (an archive whose listing is encrypted).</summary>
    internal delegate Task<ArchiveProvider> ArchiveOpener(string id, VfsPath archive, string? password,
        CancellationToken ct);

    private readonly ProviderRegistry _providers;
    private readonly IArchivePasswordPrompt _prompt;
    private readonly ArchiveOpener _open;
    private readonly Lock _lock = new();
    private readonly Dictionary<VfsPath, ArchiveProvider> _mounted = [];
    private readonly List<ArchiveProvider> _mountOrder = [];
    private readonly Dictionary<VfsPath, Gate> _gates = [];
    private int _lastId;
    private bool _disposed;

    public ArchiveMounts(ProviderRegistry providers, TempFileStore temp, IArchivePasswordPrompt prompt)
        : this(providers, temp, prompt, open: null)
    {
    }

    internal ArchiveMounts(ProviderRegistry providers, TempFileStore temp, IArchivePasswordPrompt prompt,
        ArchiveOpener? open)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(temp);
        ArgumentNullException.ThrowIfNull(prompt);
        _providers = providers;
        _prompt = prompt;
        _open = open ?? ((id, archive, password, ct) =>
            ArchiveProvider.OpenAsync(id, providers, archive, temp, password, prompt, ct));
    }

    /// <summary>The root of <paramref name="file"/> as an archive, or null when it is not one: by name, a file whose
    /// name is an archive's (<see cref="IsArchiveName"/>); by content, any file whose content is an archive's (a
    /// .docx or .jar is a zip). Folders and links are never archives.</summary>
    /// <exception cref="ArchiveDamagedException">The file looks like an archive but is not a readable one.</exception>
    /// <exception cref="OperationCanceledException">The user declined to give the listing's password.</exception>
    public async Task<VfsPath?> TryMountAsync(FileEntry file, bool byContent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Kind != EntryKind.File) return null;
        var isArchive = byContent
            ? Mounted(file.Path) is not null || await HoldsArchiveAsync(file.Path, ct).ConfigureAwait(false)
            : IsArchiveName(file.Name);
        return isArchive ? await MountAsync(file.Path, ct).ConfigureAwait(false) : null;
    }

    /// <summary>The root of <paramref name="archive"/>, opened now or by an earlier mount. When the listing is
    /// encrypted the prompt is asked for the password, again after a wrong answer (with
    /// <c>previousWasWrong</c>), at most <see cref="MaxPasswordAttempts"/> times. A mount that fails leaves nothing
    /// mounted.</summary>
    /// <exception cref="VfsNotFoundException">There is no file at <paramref name="archive"/>.</exception>
    /// <exception cref="ArchiveDamagedException">The file is no archive, or a damaged one.</exception>
    /// <exception cref="ArchivePasswordRequiredException">Every answer was wrong.</exception>
    /// <exception cref="OperationCanceledException">The user declined to give the password, or
    /// <paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The mounts have been disposed (the application is exiting).
    /// </exception>
    public async Task<VfsPath> MountAsync(VfsPath archive, CancellationToken ct)
    {
        if (Mounted(archive) is { } known) return Root(known);
        var gate = EnterGate(archive);
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Mounted(archive) is { } raced) return Root(raced);
                var provider = await OpenAsync(archive, ct).ConfigureAwait(false);
                try
                {
                    Register(archive, provider);
                }
                catch
                {
                    provider.Dispose();
                    throw;
                }
                return Root(provider);
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            LeaveGate(archive, gate);
        }
    }

    /// <summary>Whether the name is an archive's (<see cref="ArchiveFormats.FromName"/>).</summary>
    public bool IsArchiveName(string fileName) => ArchiveFormats.FromName(fileName) is not null;

    /// <summary>Closes every archive mounted (newest first); later mounts throw
    /// <see cref="ObjectDisposedException"/>. The providers stay in the registry, closed: this is for the
    /// application's exit.</summary>
    public void Dispose()
    {
        ArchiveProvider[] providers;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            providers = [.. _mountOrder];
            _mountOrder.Clear();
            _mounted.Clear();
        }
        for (var i = providers.Length - 1; i >= 0; i--) providers[i].Dispose();
    }

    /// <summary>Opens the archive; if its listing needs a password, asks for it.</summary>
    private async Task<ArchiveProvider> OpenAsync(VfsPath archive, CancellationToken ct)
    {
        var id = NewId();
        try
        {
            return await _open(id, archive, null, ct).ConfigureAwait(false);
        }
        catch (ArchivePasswordRequiredException)
        {
            // the listing is encrypted: ask
        }
        var wrong = false;
        for (var attempt = 1; ; attempt++)
        {
            var password = await _prompt.AskAsync(archive, wrong, ct).ConfigureAwait(false)
                           ?? throw new OperationCanceledException("The archive's password was not given.");
            try
            {
                return await _open(id, archive, password, ct).ConfigureAwait(false);
            }
            catch (ArchivePasswordRequiredException) when (attempt < MaxPasswordAttempts)
            {
                wrong = true;
            }
            catch (ArchiveDamagedException e)
            {
                // An archive that asked for a password and then fails to decode under one (a 7z's encrypted header
                // decrypted with a wrong password fails its CRC) has been given a wrong password.
                if (attempt == MaxPasswordAttempts)
                    throw new ArchivePasswordRequiredException(archive, wrongPassword: true, e);
                wrong = true;
            }
        }
    }

    /// <summary>Whether the content of <paramref name="file"/> starts like an archive.</summary>
    private async Task<bool> HoldsArchiveAsync(VfsPath file, CancellationToken ct)
    {
        await using var stream = await _providers.For(file).OpenReadAsync(file, ct).ConfigureAwait(false);
        if (stream.CanSeek) return await ArchiveFormats.DetectAsync(stream, ct).ConfigureAwait(false) is not null;
        var head = new byte[DetectPrefixSize];
        var length = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);
        using var prefix = new MemoryStream(head, 0, length, writable: false);
        return await ArchiveFormats.DetectAsync(prefix, ct).ConfigureAwait(false) is not null;
    }

    private static VfsPath Root(ArchiveProvider provider) => new(provider.Id, "/");

    private ArchiveProvider? Mounted(VfsPath archive)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _mounted.GetValueOrDefault(archive);
        }
    }

    /// <summary>An id no provider has yet.</summary>
    private string NewId()
    {
        lock (_lock)
        {
            string id;
            do id = "archive:" + ++_lastId;
            while (_providers.TryGet(id, out _));
            return id;
        }
    }

    private void Register(VfsPath archive, ArchiveProvider provider)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _providers.Register(provider);
            _mounted.Add(archive, provider);
            _mountOrder.Add(provider);
        }
    }

    /// <summary>The gate that keeps mounts of one archive file one after another; it is dropped when its last user
    /// leaves.</summary>
    private Gate EnterGate(VfsPath archive)
    {
        lock (_lock)
        {
            if (!_gates.TryGetValue(archive, out var gate)) _gates.Add(archive, gate = new Gate());
            gate.Users++;
            return gate;
        }
    }

    private void LeaveGate(VfsPath archive, Gate gate)
    {
        lock (_lock)
        {
            if (--gate.Users == 0) _gates.Remove(archive);
        }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }
}
