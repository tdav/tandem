using Tandem.App.ViewModels.Editing;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.App.ViewModels.Net;

public enum UploadOutcome { Uploaded, ChangedAtSource, Failed }

/// <summary>A private copy of a file, with its source baseline for later upload.</summary>
public sealed class RemoteEdit
{
    private readonly ProviderRegistry _providers;
    private readonly TempFileStore _temp;
    private (long Size, DateTimeOffset Modified)? _expected;
    private int _released;

    private RemoteEdit(ProviderRegistry providers, TempFileStore temp, VfsPath source, VfsPath copy, FileEntry entry)
    {
        _providers = providers;
        _temp = temp;
        Source = source;
        LocalCopy = copy;
        _expected = Stamp(entry);
    }

    public VfsPath Source { get; }
    public VfsPath LocalCopy { get; }

    public static async Task<RemoteEdit> DownloadAsync(ProviderRegistry providers, TempFileStore temp, VfsPath file,
        long maxBytes, CancellationToken ct)
    {
        var source = providers.For(file);
        var target = await EditorViewModel.ResolveTargetAsync(providers, file, ct).ConfigureAwait(false);
        var entry = await source.GetEntryAsync(target, ct).ConfigureAwait(false)
            ?? throw new VfsNotFoundException(target.Path);
        if (entry.Kind != EntryKind.File) throw new VfsAccessDeniedException(target.Path);
        if (entry.Size > maxBytes) throw new EditorRefusedException(EditorRefusal.TooLarge);

        var localCopy = VfsPath.Local(temp.NewFile(file.Name));
        var local = providers.For(localCopy);
        var edit = new RemoteEdit(providers, temp, target, localCopy, entry);
        try
        {
            await EnsureDirectoryAsync(local, localCopy.Parent!.Value, ct).ConfigureAwait(false);
            await using (var input = await source.OpenReadAsync(target, ct).ConfigureAwait(false))
            await using (var output = await local.OpenWriteAsync(localCopy, ct).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    copied += read;
                    if (copied > maxBytes) throw new EditorRefusedException(EditorRefusal.TooLarge);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            if (Stamp(await source.GetEntryAsync(target, ct).ConfigureAwait(false)) != edit._expected)
                throw new VfsException(target.Path);
            return edit;
        }
        catch
        {
            await edit.ReleaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<(UploadOutcome Outcome, Exception? Error)> UploadAsync(bool overwriteChanged, CancellationToken ct)
    {
        var source = _providers.For(Source);
        var local = _providers.For(LocalCopy);
        try
        {
            await using var batch = await BatchScope.BeginAsync(_providers, [Source], ct).ConfigureAwait(false);
            var current = await source.GetEntryAsync(Source, ct).ConfigureAwait(false);
            var baseline = Stamp(current);
            if (!overwriteChanged && (baseline is null || baseline != _expected))
                return (UploadOutcome.ChangedAtSource, null);
            if (current is not null && current.Kind != EntryKind.File)
                return (UploadOutcome.Failed, new VfsAccessDeniedException(Source.Path));
            await SafeReplace.WriteAsync(source, Source, current is not null, async part =>
            {
                if (Stamp(await source.GetEntryAsync(Source, ct).ConfigureAwait(false)) != baseline)
                    throw new SourceChangedException();
                await using (var input = await local.OpenReadAsync(LocalCopy, ct).ConfigureAwait(false))
                await using (var output = await source.OpenWriteAsync(part, ct).ConfigureAwait(false))
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                if (source.Capabilities.HasFlag(ProviderCapabilities.UnixPermissions) && current?.Mode is { } mode)
                    await source.SetModeAsync(part, mode, ct).ConfigureAwait(false);
                if (Stamp(await source.GetEntryAsync(Source, ct).ConfigureAwait(false)) != baseline)
                    throw new SourceChangedException();
                ct.ThrowIfCancellationRequested();
            }, ct).ConfigureAwait(false);
            await batch.CommitAsync(ct).ConfigureAwait(false);
            _expected = null;
            try { _expected = Stamp(await source.GetEntryAsync(Source, ct).ConfigureAwait(false)); }
            catch (IOException) { /* The upload committed; ask before a later overwrite without a current baseline. */ }
            return (UploadOutcome.Uploaded, null);
        }
        catch (SourceChangedException)
        {
            return (UploadOutcome.ChangedAtSource, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return (UploadOutcome.Failed, error);
        }
    }

    public void Release() => ReleaseAsync().GetAwaiter().GetResult();

    public async Task ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        try
        {
            var local = _providers.For(LocalCopy);
            var folder = LocalCopy.Parent!.Value;
            if (await local.GetEntryAsync(folder, CancellationToken.None).ConfigureAwait(false) is not null)
                await local.DeleteAsync(folder, recursive: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        try { _temp.Release(LocalCopy.ToOsPath()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static async Task EnsureDirectoryAsync(IFileSystemProvider provider, VfsPath folder, CancellationToken ct)
    {
        if (folder.IsRoot || await provider.GetEntryAsync(folder, ct).ConfigureAwait(false) is not null) return;
        await EnsureDirectoryAsync(provider, folder.Parent!.Value, ct).ConfigureAwait(false);
        try { await provider.CreateDirectoryAsync(folder, ct).ConfigureAwait(false); }
        catch (VfsAlreadyExistsException) { /* A concurrent download created the shared parent. */ }
    }

    private static (long Size, DateTimeOffset Modified)? Stamp(FileEntry? file) =>
        file is { } entry ? (entry.Size, entry.Modified) : null;

    private sealed class SourceChangedException : IOException;
}
