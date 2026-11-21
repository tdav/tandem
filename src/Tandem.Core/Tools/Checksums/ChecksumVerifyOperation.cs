using Tandem.Core.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Viewing;

namespace Tandem.Core.Tools.Checksums;

public enum VerifyStatus { Ok, Mismatch, Missing, Unreadable, OutsideFolder }

public sealed record VerifyResult(string RelativePath, VerifyStatus Status, string Expected, string? Actual);

public sealed class ChecksumVerifyOperation : FileOperation
{
    private readonly ProviderRegistry _providers;
    private readonly VfsPath _checksumFile;
    private readonly VfsPath _folder;
    private readonly ItemErrors _errors;
    private readonly List<VerifyResult> _results = [];

    public ChecksumVerifyOperation(ProviderRegistry providers, VfsPath checksumFile, IOperationInteraction interaction)
    {
        _providers = providers;
        _checksumFile = checksumFile;
        _folder = checksumFile.Parent ?? throw new ArgumentException("The checksum path has no folder.", nameof(checksumFile));
        _errors = new ItemErrors(this, interaction);
        Sources = [checksumFile];
    }

    public IReadOnlyList<VerifyResult> Results => _results.AsReadOnly();
    public IReadOnlyList<int> UnreadableLineNumbers { get; private set; } = [];
    public override OperationKind Kind => OperationKind.ChecksumVerify;
    public override int ItemCount => 1;
    public override IReadOnlyList<VfsPath> Sources { get; }
    public override VfsPath? Destination => null;

    public override async Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct) =>
        [$"{_folder.ProviderId}:{await _providers.For(_folder).GetVolumeKeyAsync(_folder, ct).ConfigureAwait(false)}"];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var provider = _providers.For(_folder);
        var algorithm = Checksum.FromFileName(_checksumFile.Name)
            ?? throw new OperationRejectedException(RejectionReason.InvalidName, "Unknown checksum extension.",
                source: _checksumFile, name: _checksumFile.Name);
        SetState(OperationState.Scanning);
        ChecksumFileContent? content = null;
        if (!await _errors.RunAsync(_checksumFile, async () =>
        {
            content = ChecksumFile.Parse(algorithm, await ReadChecksumAsync(provider, ct).ConfigureAwait(false));
        }, ct).ConfigureAwait(false)) return;
        UnreadableLineNumbers = content!.UnreadableLineNumbers;

        var plan = new List<(ChecksumLine Line, VfsPath? Path, VerifyStatus Status, long Size)>(content.Lines.Count);
        foreach (var line in content.Lines)
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            if (!TryResolve(line.RelativePath, out var path))
            {
                plan.Add((line, null, VerifyStatus.OutsideFolder, 0));
                continue;
            }

            var status = VerifyStatus.Unreadable;
            long size = 0;
            var found = await _errors.RunAsync(path, async () =>
            {
                (status, size) = await StatWithoutLinksAsync(provider, path, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            plan.Add((line, found && status == VerifyStatus.Ok ? path : null, status, size));
        }

        SetTotals(plan.Count, plan.Sum(item => item.Size));
        SetState(OperationState.Running);
        foreach (var (line, path, verdict, size) in plan)
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            BeginItem(line.RelativePath);
            string? actual = null;
            var status = verdict;
            if (path is { } file)
            {
                var read = await _errors.RunAsync(file, async () =>
                {
                    // Check once more after the scan: a path component may now be a symbolic link.
                    (status, _) = await StatWithoutLinksAsync(provider, file, ct).ConfigureAwait(false);
                    if (status == VerifyStatus.Ok)
                        actual = await ChecksumOperationSupport.HashAsync(this, provider, file, algorithm, ct)
                            .ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
                if (!read) status = VerifyStatus.Unreadable;
                if (actual is null) DropBytesFromTotal(size);
                else if (!string.Equals(line.Hash, actual, StringComparison.OrdinalIgnoreCase)) status = VerifyStatus.Mismatch;
            }
            _results.Add(new(line.RelativePath, status, line.Hash, actual));
            CompleteFile();
        }
    }

    private async Task<string> ReadChecksumAsync(IFileSystemProvider provider, CancellationToken ct)
    {
        await using var input = await provider.OpenReadAsync(_checksumFile, ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > ChecksumFile.MaxBytes) throw new IOException("The checksum file is too large.");
            output.Write(buffer, 0, count);
        }
        var bytes = output.GetBuffer().AsSpan(0, (int)output.Length);
        var encoding = TextEncodingDetector.Detect(bytes, sampleIsWholeFile: true);
        return encoding.Encoding.Encoding.GetString(bytes[encoding.BomLength..]);
    }

    private bool TryResolve(string name, out VfsPath path)
    {
        path = _folder;
        if (name.StartsWith('/') || name.Contains('\0') ||
            (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')) return false;
        foreach (var part in name.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part != "..")
            {
                path = path.Combine(part);
                continue;
            }
            if (path == _folder) return false;
            path = path.Parent!.Value;
        }
        return path != _folder;
    }

    private async Task<(VerifyStatus Status, long Size)> StatWithoutLinksAsync(IFileSystemProvider provider,
        VfsPath path, CancellationToken ct)
    {
        var relative = path.Path[(_folder.IsRoot ? 1 : _folder.Path.Length + 1)..].Split('/');
        var current = _folder;
        for (var i = 0; i < relative.Length; i++)
        {
            current = current.Combine(relative[i]);
            FileEntry? entry;
            try
            {
                entry = await provider.GetEntryAsync(current, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is VfsNotFoundException or FileNotFoundException or DirectoryNotFoundException)
            {
                return (VerifyStatus.Missing, 0);
            }
            if (entry is null) return (VerifyStatus.Missing, 0);
            if (entry.Kind == EntryKind.Symlink) return (VerifyStatus.OutsideFolder, 0);
            if (i == relative.Length - 1)
                return entry.Kind == EntryKind.File ? (VerifyStatus.Ok, entry.Size) : (VerifyStatus.Unreadable, 0);
            if (entry.Kind != EntryKind.Directory) return (VerifyStatus.Missing, 0);
        }
        return (VerifyStatus.Missing, 0);
    }
}
