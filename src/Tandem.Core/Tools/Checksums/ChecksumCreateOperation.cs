using System.Text;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tools.Checksums;

public sealed class ChecksumCreateOperation : FileOperation
{
    private readonly ProviderRegistry _providers;
    private readonly VfsPath _checksumFile;
    private readonly VfsPath _folder;
    private readonly ChecksumAlgorithm _algorithm;
    private readonly ItemErrors _errors;

    public ChecksumCreateOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources,
        VfsPath checksumFile, ChecksumAlgorithm algorithm, IOperationInteraction interaction)
    {
        _providers = providers;
        _checksumFile = checksumFile;
        _folder = checksumFile.Parent ?? throw new ArgumentException("The checksum path has no folder.", nameof(checksumFile));
        _algorithm = algorithm;
        Sources = Array.AsReadOnly(sources.ToArray());
        if (Sources.Any(path => !path.IsSameOrDescendantOf(_folder)))
            throw new ArgumentException("A source lies outside the checksum folder.", nameof(sources));
        _errors = new ItemErrors(this, interaction);
    }

    public override OperationKind Kind => OperationKind.ChecksumCreate;
    public override int ItemCount => Sources.Count;
    public override IReadOnlyList<VfsPath> Sources { get; }
    public override VfsPath? Destination => _folder;

    public override async Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct) =>
        [$"{_folder.ProviderId}:{await _providers.For(_folder).GetVolumeKeyAsync(_folder, ct).ConfigureAwait(false)}"];

    protected override Task ExecuteAsync(CancellationToken ct) =>
        RunInBatchAsync(_providers, [_folder], _ => CreateAsync(ct), afterCommit: null, ct);

    private async Task CreateAsync(CancellationToken ct)
    {
        var provider = _providers.For(_folder);
        if (await provider.GetEntryAsync(_checksumFile, ct).ConfigureAwait(false) is not null)
            throw DestinationExists();

        SetState(OperationState.Scanning);
        var files = await ScanAsync(provider, ct).ConfigureAwait(false);
        SetTotals(files.Count, files.Sum(file => file.Size));
        SetState(OperationState.Running);
        var lines = new List<ChecksumLine>(files.Count);
        foreach (var file in files)
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            BeginItem(file.Name);
            string? hash = null;
            if (await _errors.RunAsync(file.Path, async () =>
            {
                hash = await ChecksumOperationSupport.HashAsync(this, provider, file.Path, _algorithm, ct)
                    .ConfigureAwait(false);
            }, ct).ConfigureAwait(false))
                lines.Add(new(RelativePath(file.Path), hash!));
            else
                DropBytesFromTotal(file.Size);
            CompleteFile();
        }

        await WaitWhilePausedAsync(ct).ConfigureAwait(false);
        var content = ChecksumFile.Format(_algorithm, lines);
        if (Encoding.UTF8.GetByteCount(content) > ChecksumFile.MaxBytes)
            throw new IOException("The checksum file is too large.");
        var bytes = Encoding.UTF8.GetBytes(content);
        try
        {
            await SafeReplace.WriteAsync(provider, _checksumFile, replace: false, async part =>
            {
                await using (var output = await provider.OpenWriteAsync(part, ct).ConfigureAwait(false))
                    await output.WriteAsync(bytes, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }, ct).ConfigureAwait(false);
        }
        catch (VfsAlreadyExistsException)
        {
            throw DestinationExists();
        }
    }

    private async Task<List<FileEntry>> ScanAsync(IFileSystemProvider provider, CancellationToken ct)
    {
        var stack = new Stack<FileEntry>();
        var visited = new HashSet<VfsPath>();
        var files = new List<FileEntry>();
        foreach (var path in Sources.Reverse())
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            FileEntry? entry = null;
            if (await _errors.RunAsync(path, async () =>
            {
                entry = await provider.GetEntryAsync(path, ct).ConfigureAwait(false)
                    ?? throw new VfsNotFoundException($"'{path.Path}' does not exist.");
            }, ct).ConfigureAwait(false))
                stack.Push(entry!);
        }

        while (stack.TryPop(out var entry))
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            if (entry.Path == _checksumFile || !entry.Path.IsSameOrDescendantOf(_folder) ||
                !visited.Add(entry.Path)) continue;
            if (entry.Kind == EntryKind.Symlink)
            {
                AddSkipped(new SkippedItem(entry.Path, SkipReason.LinkSkipped));
            }
            else if (entry.Kind == EntryKind.Directory)
            {
                var children = new List<FileEntry>();
                if (await _errors.RunAsync(entry.Path, async () =>
                {
                    children.Clear();
                    await foreach (var child in provider.EnumerateAsync(entry.Path, ct).ConfigureAwait(false))
                    {
                        await WaitWhilePausedAsync(ct).ConfigureAwait(false);
                        children.Add(child);
                    }
                }, ct).ConfigureAwait(false))
                    for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            else if (RelativePath(entry.Path).IndexOfAny(['\r', '\n', '\\']) >= 0)
            {
                AddSkipped(new SkippedItem(entry.Path, SkipReason.NameNotStorable));
            }
            else
            {
                files.Add(entry);
            }
        }
        return files;
    }

    private string RelativePath(VfsPath path) => path.Path[(_folder.IsRoot ? 1 : _folder.Path.Length + 1)..];

    private OperationRejectedException DestinationExists() => new(RejectionReason.DestinationExists,
        "The checksum file already exists.", destination: _checksumFile);
}

internal static class ChecksumOperationSupport
{
    public static async Task<string> HashAsync(FileOperation operation, IFileSystemProvider provider, VfsPath path,
        ChecksumAlgorithm algorithm, CancellationToken ct)
    {
        var progress = new CountProgress(operation);
        try
        {
            await using var input = await provider.OpenReadAsync(path, ct).ConfigureAwait(false);
            return await Checksum.ComputeAsync(input, algorithm, progress, operation.WaitWhilePausedAsync, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            operation.AddBytesDone(-progress.Bytes);
            throw;
        }
    }

    private sealed class CountProgress(FileOperation operation) : IProgress<long>
    {
        public long Bytes { get; private set; }

        public void Report(long value)
        {
            Bytes += value;
            operation.AddBytesDone(value);
        }
    }
}
