using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Operations;
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
/// Created (by <see cref="OpenAsync"/>) and used on the UI thread; reading and writing the file run off it. The text
/// is decoded with the encoding <see cref="TextEncodingDetector"/> guesses, without the byte order mark; saving
/// encodes it with the same encoding and puts the same mark back, so a file saved unchanged keeps its bytes. Line
/// endings are whatever the document holds: the file's own, and the editor inserts the line's own ending on Enter.
/// Saving writes through the provider's batch (<see cref="BatchScope"/>) and <see cref="SafeReplace"/>: the new text
/// goes to a part file, which gets the original's Unix mode (where the provider has modes) and only then replaces the
/// original; if any step fails, the original stays as it was.
/// </remarks>
public sealed partial class EditorViewModel : ObservableObject
{
    /// <summary>The largest file the editor opens: 32 MiB.</summary>
    public const long MaxFileBytes = 32 * 1024 * 1024;

    private readonly ProviderRegistry _providers;
    private readonly string _displayPath;
    private readonly byte[] _bom;
    private readonly UnixFileMode? _openedMode;
    private Task<bool>? _saving;
    private int _changes;

    private EditorViewModel(ProviderRegistry providers, VfsPath file, string displayPath, string text,
        TextEncodingInfo encoding, byte[] bom, UnixFileMode? mode, bool isReadOnly)
    {
        _providers = providers;
        _displayPath = displayPath;
        _bom = bom;
        _openedMode = mode;
        File = file;
        Encoding = encoding;
        IsReadOnly = isReadOnly;
        Document = new TextDocument(text);
        Document.UndoStack.ClearAll();
        Document.Changed += (_, _) => _changes++;
        Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(UndoStack.IsOriginalFile)) return;
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(Title));
        };
    }

    /// <summary>Reads <paramref name="file"/> off the UI thread and returns its editor. Call on the UI thread.</summary>
    /// <exception cref="EditorRefusedException">The file is too large or looks binary.</exception>
    /// <exception cref="IOException">The file cannot be read (<see cref="VfsNotFoundException"/> when it is
    /// gone).</exception>
    public static async Task<EditorViewModel> OpenAsync(ProviderRegistry providers, VfsPath file,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var provider = providers.For(file);
        var displayPath = providers.DisplayPath(file);
        var (content, mode) = await Task.Run(() => ReadAsync(provider, file, ct), ct);

        var sampleLength = Math.Min(content.Length, TextEncodingDetector.SampleSize);
        var guess = TextEncodingDetector.Detect(content.AsSpan(0, sampleLength),
            sampleIsWholeFile: content.Length <= TextEncodingDetector.SampleSize);
        if (guess.LooksBinary) throw new EditorRefusedException(EditorRefusal.Binary);
        var text = guess.Encoding.Encoding.GetString(content, guess.BomLength, content.Length - guess.BomLength);
        return new EditorViewModel(providers, file, displayPath, text, guess.Encoding, content[..guess.BomLength], mode,
            !provider.Capabilities.HasFlag(ProviderCapabilities.Write));
    }

    /// <summary>The file being edited.</summary>
    public VfsPath File { get; }

    /// <summary>The file's path as the user reads it, with " *" while there are unsaved changes.</summary>
    public string Title => IsModified ? _displayPath + " *" : _displayPath;

    /// <summary>The text; edit it through the editor control (or directly).</summary>
    public TextDocument Document { get; }

    /// <summary>The encoding the file was read in and is saved in.</summary>
    public TextEncodingInfo Encoding { get; }

    /// <summary>The file starts with a byte order mark, which saving keeps.</summary>
    public bool HasBom => _bom.Length > 0;

    /// <summary>The file's provider cannot write: the text cannot be changed or saved.</summary>
    public bool IsReadOnly { get; }

    /// <summary>The text differs from what was read or last saved (undoing back to it counts as unchanged).</summary>
    public bool IsModified => !Document.UndoStack.IsOriginalFile;

    /// <summary>Why the last save failed, in words; null after a successful one.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Writes the text back to the file. True when saved: the text is then unmodified, unless it changed
    /// while the save ran. False when the save failed (<see cref="Error"/> says why) or the file is read-only; the
    /// original file is then unchanged and the text stays modified. A save asked for while one runs waits for it and
    /// gives its result.</summary>
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
        var changes = _changes;
        byte[] content = [.. _bom, .. Encoding.Encoding.GetBytes(Document.Text)];
        var (providers, file, mode) = (_providers, File, _openedMode);
        try
        {
            await Task.Run(() => WriteAsync(providers, file, content, mode));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = Loc.Format("Editor_SaveFailed", File.Name, OperationTexts.ForFailure(e));
            return false;
        }
        Error = null;
        if (changes == _changes) Document.UndoStack.MarkAsOriginalFile();
        return true;
    }

    /// <summary>Reads the whole file, refusing one over <see cref="MaxFileBytes"/> before reading it (by its size) or
    /// while reading it (a size that was not reported or changed).</summary>
    private static async Task<(byte[] Content, UnixFileMode? Mode)> ReadAsync(IFileSystemProvider provider,
        VfsPath file, CancellationToken ct)
    {
        var entry = await provider.GetEntryAsync(file, ct).ConfigureAwait(false)
                    ?? throw new VfsNotFoundException($"'{file.Path}' does not exist.");
        if (entry.Kind == EntryKind.File && entry.Size > MaxFileBytes)
            throw new EditorRefusedException(EditorRefusal.TooLarge);
        await using var stream = await provider.OpenReadAsync(file, ct).ConfigureAwait(false);
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (content.Length + read > MaxFileBytes) throw new EditorRefusedException(EditorRefusal.TooLarge);
            content.Write(buffer, 0, read);
        }
        return (content.ToArray(), entry.Mode);
    }

    /// <summary>One batch on the file's provider, the safe replace inside it, then the commit; the batch is disposed
    /// (discarding what was not committed) whatever happens. The part file gets the file's current mode, or the one
    /// it had when opened, when the provider has modes.</summary>
    private static async Task WriteAsync(ProviderRegistry providers, VfsPath file, byte[] content,
        UnixFileMode? openedMode)
    {
        var ct = CancellationToken.None;
        var provider = providers.For(file);
        await using var batch = await BatchScope.BeginAsync(providers, [file], ct).ConfigureAwait(false);
        var existing = await provider.GetEntryAsync(file, ct).ConfigureAwait(false);
        var mode = provider.Capabilities.HasFlag(ProviderCapabilities.UnixPermissions)
            ? existing?.Mode ?? openedMode
            : null;
        // A file deleted meanwhile is written anew rather than failing on the missing original.
        await SafeReplace.WriteAsync(provider, file, replace: existing is not null, async part =>
        {
            await using (var stream = await provider.OpenWriteAsync(part, ct).ConfigureAwait(false))
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
            if (mode is { } m) await provider.SetModeAsync(part, m, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await batch.CommitAsync(ct).ConfigureAwait(false);
    }
}
