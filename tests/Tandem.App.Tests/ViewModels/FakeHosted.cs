using System.Runtime.CompilerServices;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>
/// A hosted provider (id <see cref="ProviderId"/>) over an in-memory file system, standing in for an archive, and the
/// mounter that opens files as it: a "*.pack" file by its name, and with <c>byContent</c> also any file whose content
/// starts with "PK". The file mounted last is the host: the root leads up to its folder with it under the cursor, the
/// display path is the host file's path followed by the path inside, and once the host file is gone from
/// <paramref name="local"/> nothing inside can be read (<see cref="VfsNotFoundException"/>).
/// </summary>
internal sealed class FakeHosted(InMemoryFileSystem local) : IFileSystemProvider, IHostedProvider, IFileMounter
{
    public const string ProviderId = "hosted";

    /// <summary>What the mounted file holds.</summary>
    public InMemoryFileSystem Files { get; } = new(ProviderId);

    /// <summary>The file mounted last; null before the first mount.</summary>
    public VfsPath? Host { get; private set; }

    /// <summary>When set, a mount that would succeed throws it instead.</summary>
    public Exception? MountFailure { get; set; }

    /// <summary>Every mount asked for: the file's name and whether its content could be read.</summary>
    public List<(string Name, bool ByContent)> MountRequests { get; } = [];

    /// <summary>For each mount, whether it ran on the UI thread.</summary>
    public List<bool> MountsOnUiThread { get; } = [];

    /// <summary>For each mount, the token it was given.</summary>
    public List<CancellationToken> MountTokens { get; } = [];

    /// <summary>When set, every mount waits for it before answering, and does not observe its token: a mounter
    /// that answers late whatever happens.</summary>
    public TaskCompletionSource? MountGate { get; set; }

    /// <summary>When set, opening a file for reading waits for it first.</summary>
    public TaskCompletionSource? ReadGate { get; set; }

    /// <summary>How many files have been opened for reading (counted before <see cref="ReadGate"/>).</summary>
    public int ReadsRequested => Volatile.Read(ref _readsRequested);

    private int _readsRequested;

    public static VfsPath Root => new(ProviderId, "/");

    public async Task<VfsPath?> TryMountAsync(FileEntry file, bool byContent, CancellationToken ct)
    {
        lock (MountRequests)
        {
            MountRequests.Add((file.Name, byContent));
            MountsOnUiThread.Add(Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
            MountTokens.Add(ct);
        }
        if (MountGate is { } gate) await gate.Task.ConfigureAwait(false);
        var opens = file.Name.EndsWith(".pack", StringComparison.OrdinalIgnoreCase) ||
                    byContent && await StartsWithPkAsync(file, ct);
        if (!opens) return null;
        if (MountFailure is { } failure) throw failure;
        Host = file.Path;
        return Root;
    }

    private async Task<bool> StartsWithPkAsync(FileEntry file, CancellationToken ct)
    {
        await using var stream = await local.OpenReadAsync(file.Path, ct);
        var head = new byte[2];
        return await stream.ReadAtLeastAsync(head, 2, throwOnEndOfStream: false, ct) == 2 &&
               head[0] == 'P' && head[1] == 'K';
    }

    public HostLocation? GetHostLocation(VfsPath path) =>
        path.IsRoot && Host is { } host ? new HostLocation(host.Parent!.Value, host.Name) : null;

    public string GetDisplayPath(VfsPath path) =>
        Host is { } host ? host.Path + (path.IsRoot ? "" : path.Path) : path.Path;

    private void CheckHost()
    {
        if (Host is { } host && !local.Exists(host.Path))
            throw new VfsNotFoundException($"'{host.Path}' does not exist.");
    }

    // ---- IFileSystemProvider: through to Files while the host file exists -----------------------------

    public string Id => ProviderId;

    public ProviderCapabilities Capabilities => Files.Capabilities;

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(VfsPath directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        CheckHost();
        await foreach (var entry in Files.EnumerateAsync(directory, ct)) yield return entry;
    }

    public Task<FileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct)
    {
        CheckHost();
        return Files.GetEntryAsync(path, ct);
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct)
    {
        Interlocked.Increment(ref _readsRequested);
        if (ReadGate is { } gate) await gate.Task.ConfigureAwait(false);
        CheckHost();
        return await Files.OpenReadAsync(path, ct).ConfigureAwait(false);
    }

    public Task<Stream> OpenWriteAsync(VfsPath path, CancellationToken ct) => Files.OpenWriteAsync(path, ct);

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => Files.CreateDirectoryAsync(path, ct);

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
        Files.DeleteAsync(path, recursive, ct);

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
        Files.MoveAsync(source, destination, ct);

    public Task CreateSymlinkAsync(VfsPath path, string target, CancellationToken ct) =>
        Files.CreateSymlinkAsync(path, target, ct);

    public Task SetModifiedAsync(VfsPath path, DateTimeOffset modified, CancellationToken ct) =>
        Files.SetModifiedAsync(path, modified, ct);

    public Task SetModeAsync(VfsPath path, UnixFileMode mode, CancellationToken ct) => Files.SetModeAsync(path, mode, ct);

    public Task<bool> IsSameEntryAsync(VfsPath a, VfsPath b, CancellationToken ct) => Files.IsSameEntryAsync(a, b, ct);

    public Task<string> GetVolumeKeyAsync(VfsPath path, CancellationToken ct) => Files.GetVolumeKeyAsync(path, ct);

    public IDisposable? Watch(VfsPath directory, Action changed) => Files.Watch(directory, changed);
}
