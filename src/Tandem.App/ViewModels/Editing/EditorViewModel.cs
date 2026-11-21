using System.Text;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels.Net;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Viewing;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Editing;

/// <summary>Why a file is not opened in the editor.</summary>
public enum EditorRefusal
{
    /// <summary>Larger than <see cref="EditorViewModel.MaxFileBytes"/>.</summary>
    TooLarge,

    /// <summary>Its first bytes look like binary data, not text.</summary>
    Binary,
}

/// <summary>The file is not opened in the editor, for <see cref="Reason"/>; the viewer (F3) still shows it.</summary>
public sealed class EditorRefusedException(EditorRefusal reason)
    : Exception($"The file is not opened in the editor: {reason}.")
{
    public EditorRefusal Reason { get; } = reason;
}

/// <summary>
/// One file open in the built-in editor: its text as an AvaloniaEdit document, the encoding it was read in, and saving
/// it back the same way.
/// </summary>
/// <remarks>
/// <para>Created (by <see cref="OpenAsync"/>) and used on the UI thread; reading, decoding, encoding and writing run
/// off it. A symbolic link is followed to the file it really leads to (on the same provider; on the local disk as the
/// system resolves it): that file is read and
/// written, so the link stays a link; <see cref="File"/> and <see cref="Title"/> keep the path that was opened.</para>
/// <para>The text is decoded with the encoding <see cref="TextEncodingDetector"/> guesses, without the byte order
/// mark; saving encodes it with the same encoding and puts the same mark back, so a file saved unchanged keeps its
/// bytes. Line endings are whatever the document holds: the file's own, and the editor inserts the line's own ending
/// on Enter.</para>
/// <para>Nothing is ever changed silently. A save asks (<see cref="ConfirmSave"/>; no answerer means no) when the file
/// changed on disk since it was read or saved, when its bytes do not survive decoding (see <see cref="Notice"/>),
/// and when the text has characters its encoding cannot hold (then UTF-8 is offered; once a save in it succeeds, it
/// is kept for later saves).</para>
/// <para>Saving writes through the provider's batch (<see cref="BatchScope"/>) and <see cref="SafeReplace"/>: the new
/// text goes to a part file, which gets the original's Unix mode (where the provider has modes) and, on the local
/// disk, its extended attributes, ACL and flags (<see cref="INativeCopy.CopyFileMetadataAsync"/>), and only then
/// replaces the original; if any step fails, the original stays as it was.</para>
/// </remarks>
public sealed partial class EditorViewModel : ObservableObject
{
    /// <summary>The largest file the editor opens: 32 MiB.</summary>
    public const long MaxFileBytes = 32 * 1024 * 1024;

    private const int MaxLinkHops = 40;

    private readonly ProviderRegistry _providers;
    private readonly INativeCopy? _nativeCopy;
    private readonly string _displayPath;
    private readonly VfsPath _target;
    private readonly VfsPath _writeTarget;
    private readonly RemoteEdit? _remote;
    private readonly UnixFileMode? _openedMode;
    private byte[] _bom;
    private bool _roundTrips;
    private (long Size, DateTimeOffset Modified) _onDisk;
    private Task<bool>? _saving;
    private Task<bool>? _uploading;
    private int _changes;

    private EditorViewModel(ProviderRegistry providers, INativeCopy? nativeCopy, VfsPath file, string displayPath,
        Loaded loaded, bool isReadOnly, RemoteEdit? remote = null)
    {
        _providers = providers;
        _nativeCopy = nativeCopy;
        _displayPath = displayPath;
        _remote = remote;
        _target = remote?.Source ?? loaded.Target;
        _writeTarget = loaded.Target;
        _openedMode = loaded.Mode;
        _bom = loaded.Bom;
        _roundTrips = loaded.RoundTrips;
        _onDisk = (loaded.Size, loaded.Modified);
        File = file;
        Encoding = loaded.Encoding;
        IsReadOnly = isReadOnly;
        Notice = _roundTrips ? null : Loc.Format("Editor_LossyNotice", loaded.Encoding.Id);
        Document = new TextDocument(loaded.Text);
        Document.UndoStack.ClearAll();
        Document.Changed += (_, _) => _changes++;
        Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UndoStack.IsOriginalFile)) return;
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(Title));
        };
    }

    /// <summary>Reads and decodes <paramref name="file"/> off the UI thread and returns its editor. Call on the UI
    /// thread.</summary>
    /// <param name="nativeCopy">Gives a local file's new content the original's metadata on save; none is copied
    /// when null.</param>
    /// <exception cref="EditorRefusedException">The file is too large or looks binary.</exception>
    /// <exception cref="IOException">The file cannot be read (<see cref="VfsNotFoundException"/> when it is gone,
    /// or a link leads nowhere).</exception>
    public static async Task<EditorViewModel> OpenAsync(ProviderRegistry providers, VfsPath file,
        CancellationToken ct, INativeCopy? nativeCopy = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var provider = providers.For(file);
        var displayPath = providers.DisplayPath(file);
        var loaded = await Task.Run(() => LoadAsync(provider, file, ct), ct);
        return new EditorViewModel(providers, nativeCopy, file, displayPath, loaded,
            !provider.Capabilities.HasFlag(ProviderCapabilities.Write));
    }

    public static async Task<EditorViewModel> OpenRemoteAsync(ProviderRegistry providers, TempFileStore temp,
        VfsPath file, CancellationToken ct)
    {
        var remote = await Task.Run(() => RemoteEdit.DownloadAsync(providers, temp, file, MaxFileBytes, ct), ct);
        try
        {
            var loaded = await Task.Run(() => LoadAsync(providers.For(remote.LocalCopy), remote.LocalCopy, ct), ct);
            return new EditorViewModel(providers, null, file, providers.DisplayPath(file), loaded,
                !providers.For(file).Capabilities.HasFlag(ProviderCapabilities.Write), remote);
        }
        catch
        {
            await Task.Run(remote.ReleaseAsync);
            throw;
        }
    }

    /// <summary>The file opened (a link stays the link's path).</summary>
    public VfsPath File { get; }

    /// <summary>The edited file with links resolved. For a temporary copy this is its original source path.</summary>
    public VfsPath Target => _target;

    /// <summary>The file <paramref name="file"/> really is on its provider, as the editor reads and writes it: every
    /// link followed, by the provider itself where it can (<see cref="ILinkResolver"/>: on the local disk
    /// "/tmp/x" gives "/private/tmp/x"). Runs off the UI thread.</summary>
    /// <exception cref="IOException">The file cannot be reached (<see cref="VfsNotFoundException"/> when it, or a
    /// link's target, does not exist).</exception>
    public static async Task<VfsPath> ResolveTargetAsync(ProviderRegistry providers, VfsPath file,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var provider = providers.For(file);
        var (target, _) = await Task.Run(() => ResolveAsync(provider, file, ct), ct);
        return target;
    }

    /// <summary>The file's path as the user reads it, with " *" while there are unsaved changes.</summary>
    public string Title
    {
        get
        {
            var title = HasPendingUpload ? Loc.Format("RemoteEdit_NotUploaded", _displayPath) : _displayPath;
            return IsModified ? title + " *" : title;
        }
    }

    public bool IsRemoteCopy => _remote is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial bool HasPendingUpload { get; private set; }

    /// <summary>The text; edit it through the editor control (or directly).</summary>
    public TextDocument Document { get; }

    /// <summary>The encoding the file was read in and is saved in; UTF-8 once the user chose it for text the
    /// original encoding cannot hold.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBom))]
    public partial TextEncodingInfo Encoding { get; private set; }

    /// <summary>The file starts with a byte order mark, which saving keeps.</summary>
    public bool HasBom => _bom.Length > 0;

    /// <summary>The file's provider cannot write: the text cannot be changed or saved.</summary>
    public bool IsReadOnly { get; }

    /// <summary>The text differs from what was read or last saved (undoing back to it counts as unchanged).</summary>
    public bool IsModified => !Document.UndoStack.IsOriginalFile;

    /// <summary>Why the last save failed, in words; null after a successful one.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>A lasting remark on the file, in words: its bytes do not survive decoding in its encoding, so saving
    /// would change them (a save asks first); or, after a save, that the file's tags and other attributes could not
    /// be kept. Null otherwise.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>Answers a question a save asks before it writes: true to go on. While null every question is answered
    /// no, and nothing is written.</summary>
    public Func<EditorSaveQuestion, Task<bool>>? ConfirmSave { get; set; }

    /// <summary>Writes the text back to the file. True when saved: the text is then unmodified, unless it changed
    /// while the save ran. False when the save failed (<see cref="Error"/> says why), a question was answered no,
    /// or the file is read-only; the original file is then unchanged and the text stays modified. A save asked for
    /// while one runs waits for it and gives its result.</summary>
    public async Task<bool> SaveAsync()
    {
        if (_saving is { } running) return await running;
        var saving = SaveCoreAsync();
        _saving = saving;
        try
        {
            return await saving;
        }
        finally
        {
            _saving = null;
        }
    }

    private async Task<bool> SaveCoreAsync()
    {
        if (IsReadOnly)
        {
            Error = Loc.Get("Editor_ReadOnly");
            return false;
        }
        if (_uploading is { } upload) await upload;
        if (_remote is not null && HasPendingUpload && !IsModified)
            return !await ConfirmAsync(EditorSaveQuestion.UploadToSource) || await UploadAsync();
        var changes = _changes;
        string? notice;
        var snapshot = Document.CreateSnapshot();
        var (providers, target, nativeCopy) = (_providers, _writeTarget, _nativeCopy);
        try
        {
            var provider = providers.For(target);
            var current = await Task.Run(() => provider.GetEntryAsync(target, CancellationToken.None));
            if (current is not null && (current.Size, current.Modified) != _onDisk &&
                !await ConfirmAsync(EditorSaveQuestion.OverwriteChangedFile))
                return false;
            var replaceUnsavable = !_roundTrips;
            if (replaceUnsavable && !await ConfirmAsync(EditorSaveQuestion.ReplaceUnsavableBytes)) return false;

            var (encoding, bom) = (Encoding, _bom);
            var content = await Task.Run(() => Encode(snapshot, encoding, bom, replaceUnsavable));
            if (content is null)
            {
                if (!await ConfirmAsync(EditorSaveQuestion.SaveAsUtf8)) return false;
                (encoding, bom) = (TextEncodings.Utf8, []);
                content = await Task.Run(() => Encode(snapshot, encoding, bom, replaceUnsavable));
                if (content is null)
                {
                    Error = Loc.Format("Editor_SaveFailed", File.Name, Loc.Get("OperationFailed_Other"));
                    return false;
                }
            }

            var mode = provider.Capabilities.HasFlag(ProviderCapabilities.UnixPermissions)
                ? current?.Mode ?? _openedMode
                : null;
            var (saved, metadataFailure) = await Task.Run(() => WriteAsync(providers, target, content, mode,
                current is not null, nativeCopy));
            if (saved is not null) _onDisk = (saved.Size, saved.Modified);
            // Only a written file takes the encoding it was written in (UTF-8, when the user chose it).
            _bom = bom;
            Encoding = encoding;
            notice = metadataFailure is null ? null : Loc.Format("Editor_MetadataNotKept", metadataFailure.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = Loc.Format("Editor_SaveFailed", File.Name, OperationTexts.ForFailure(e));
            return false;
        }
        Error = null;
        _roundTrips = true;
        Notice = notice;
        if (changes == _changes) Document.UndoStack.MarkAsOriginalFile();
        if (_remote is not null)
        {
            HasPendingUpload = true;
            if (await ConfirmAsync(EditorSaveQuestion.UploadToSource)) return await UploadAsync();
        }
        return true;
    }

    public async Task<bool> UploadAsync()
    {
        if (_uploading is { } running) return await running;
        if (_remote is not { } remote || !HasPendingUpload) return true;
        _uploading = UploadCoreAsync(remote);
        try { return await _uploading; }
        finally { _uploading = null; }
    }

    private async Task<bool> UploadCoreAsync(RemoteEdit remote)
    {
        var result = await Task.Run(() => remote.UploadAsync(false, CancellationToken.None));
        if (result.Outcome == UploadOutcome.ChangedAtSource)
        {
            if (!await ConfirmAsync(EditorSaveQuestion.OverwriteChangedAtSource)) return false;
            result = await Task.Run(() => remote.UploadAsync(true, CancellationToken.None));
        }
        if (result.Outcome == UploadOutcome.Uploaded)
        {
            HasPendingUpload = false;
            Error = null;
            return true;
        }
        var detail = result.Error is { } error ? OperationTexts.ForFailure(error) : Loc.Get("RemoteEdit_Changed");
        Error = Loc.Format("RemoteEdit_UploadFailed", File.Name, detail);
        return false;
    }

    internal async Task WaitForSaveAsync()
    {
        if (_saving is { } saving) await saving;
        if (_uploading is { } uploading) await uploading;
    }

    internal Task ReleaseCopyAsync() => _remote is { } remote ? Task.Run(remote.ReleaseAsync) : Task.CompletedTask;

    private Task<bool> ConfirmAsync(EditorSaveQuestion question) =>
        ConfirmSave is { } confirm ? confirm(question) : Task.FromResult(false);

    /// <summary>What a file read gives the editor: the file it led to, its decoded text and how to write it back.</summary>
    private sealed record Loaded(VfsPath Target, string Text, TextEncodingInfo Encoding, byte[] Bom,
        UnixFileMode? Mode, long Size, DateTimeOffset Modified, bool RoundTrips);

    /// <summary>Follows links, reads the whole file (refusing one over <see cref="MaxFileBytes"/> by its size or while
    /// reading it), detects the encoding, decodes, and checks that encoding the text again gives the same
    /// bytes.</summary>
    private static async Task<Loaded> LoadAsync(IFileSystemProvider provider, VfsPath file, CancellationToken ct)
    {
        var (target, entry) = await ResolveAsync(provider, file, ct).ConfigureAwait(false);
        if (entry.Size > MaxFileBytes) throw new EditorRefusedException(EditorRefusal.TooLarge);
        byte[] content;
        await using (var stream = await provider.OpenReadAsync(target, ct).ConfigureAwait(false))
        {
            using var buffered = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (buffered.Length + read > MaxFileBytes) throw new EditorRefusedException(EditorRefusal.TooLarge);
                buffered.Write(buffer, 0, read);
            }
            content = buffered.ToArray();
        }

        var sampleLength = Math.Min(content.Length, TextEncodingDetector.SampleSize);
        var guess = TextEncodingDetector.Detect(content.AsSpan(0, sampleLength),
            sampleIsWholeFile: content.Length <= TextEncodingDetector.SampleSize);
        if (guess.LooksBinary) throw new EditorRefusedException(EditorRefusal.Binary);
        var encoding = guess.Encoding.Encoding;
        var body = content.AsSpan(guess.BomLength);
        var text = encoding.GetString(body);
        var roundTrips = encoding.GetBytes(text).AsSpan().SequenceEqual(body);
        return new Loaded(target, text, guess.Encoding, content[..guess.BomLength], entry.Mode, entry.Size,
            entry.Modified, roundTrips);
    }

    /// <summary>The file <paramref name="file"/> leads to on its own provider, through any symbolic links, with its
    /// entry: resolved by the provider itself where it can (<see cref="ILinkResolver"/>: the local disk, where ".."
    /// in a link is relative to the real folder), otherwise by following each link's target as path text.</summary>
    /// <exception cref="VfsNotFoundException">The file, or a link's target, does not exist.</exception>
    private static async Task<(VfsPath Target, FileEntry Entry)> ResolveAsync(IFileSystemProvider provider,
        VfsPath file, CancellationToken ct)
    {
        if (provider is ILinkResolver resolver)
        {
            var resolved = await resolver.ResolveLinksAsync(file, ct).ConfigureAwait(false);
            var entry = await provider.GetEntryAsync(resolved, ct).ConfigureAwait(false);
            return entry is { Kind: not EntryKind.Symlink }
                ? (resolved, entry)
                : throw new VfsNotFoundException($"'{file.Path}' does not exist or its link leads nowhere.");
        }
        var path = file;
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            var entry = await provider.GetEntryAsync(path, ct).ConfigureAwait(false)
                        ?? throw new VfsNotFoundException($"'{path.Path}' does not exist.");
            if (entry.Kind != EntryKind.Symlink) return (path, entry);
            path = LinkDestination(path, entry.LinkTarget)
                   ?? throw new VfsNotFoundException($"The link '{path.Path}' leads nowhere.");
        }
        throw new VfsException($"Too many levels of symbolic links at '{file.Path}'.");
    }

    private static VfsPath? LinkDestination(VfsPath link, string? target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        try
        {
            return new VfsPath(link.ProviderId,
                target.StartsWith('/') ? target : link.Parent!.Value.Path + "/" + target);
        }
        catch (ArgumentException)
        {
            return null; // the target climbs above the root
        }
    }

    /// <summary>The bytes to write: the mark, then the text in <paramref name="encoding"/>; null when the text has a
    /// character the encoding cannot hold. With <paramref name="replaceUnsavable"/> (the user agreed), the
    /// replacement characters decoding put in are written as the encoding writes them, or as '?'.</summary>
    private static byte[]? Encode(ITextSource snapshot, TextEncodingInfo encoding, byte[] bom, bool replaceUnsavable)
    {
        var strict = (System.Text.Encoding)encoding.Encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        var text = snapshot.Text;
        try
        {
            if (replaceUnsavable && !CanEncode(strict, "�")) text = text.Replace('�', '?');
            return [.. bom, .. strict.GetBytes(text)];
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    private static bool CanEncode(System.Text.Encoding strict, string text)
    {
        try
        {
            strict.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>One batch on the file's provider, the safe replace inside it, then the commit; the batch is disposed
    /// (discarding what was not committed) whatever happens. The part file gets <paramref name="mode"/>, and on the
    /// local disk the original's metadata, before it replaces the original; that copy is best effort (a volume may
    /// not support extended attributes): its failure does not stop the save and is returned. Also returns the file's
    /// entry once written (null if it cannot be seen yet).</summary>
    private static async Task<(FileEntry? Saved, Exception? MetadataFailure)> WriteAsync(ProviderRegistry providers, VfsPath target, byte[] content,
        UnixFileMode? mode, bool exists, INativeCopy? nativeCopy)
    {
        var ct = CancellationToken.None;
        var provider = providers.For(target);
        Exception? metadataFailure = null;
        await using var batch = await BatchScope.BeginAsync(providers, [target], ct).ConfigureAwait(false);
        // A file deleted meanwhile is written anew rather than failing on the missing original.
        await SafeReplace.WriteAsync(provider, target, replace: exists, async part =>
        {
            await using (var stream = await provider.OpenWriteAsync(part, ct).ConfigureAwait(false))
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
            if (mode is { } m) await provider.SetModeAsync(part, m, ct).ConfigureAwait(false);
            if (exists && nativeCopy is not null && target.ProviderId == VfsPath.LocalProviderId)
            {
                try
                {
                    await nativeCopy.CopyFileMetadataAsync(target.Path, part.Path, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    metadataFailure = e;
                }
            }
        }).ConfigureAwait(false);
        await batch.CommitAsync(ct).ConfigureAwait(false);
        return (await provider.GetEntryAsync(target, ct).ConfigureAwait(false), metadataFailure);
    }
}
