using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Viewing;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Viewing;

/// <summary>How the viewer shows the file.</summary>
public enum ViewerMode
{
    /// <summary>Rows of text in <see cref="ViewerViewModel.Encoding"/>.</summary>
    Text,

    /// <summary>Rows of 16 bytes: offset, bytes, ASCII.</summary>
    Hex,

    /// <summary>The picture; offered only when the file decoded as one.</summary>
    Image,
}

/// <summary>
/// One file open in the built-in viewer (F3) or the quick-view panel: a page of text rows or hex rows from any place in
/// a file of any size, or the file's picture.
/// </summary>
/// <remarks>
/// <para>Created (by <see cref="OpenAsync"/>) and used on the UI thread; every read runs off it. The file's stream
/// stays open until <see cref="DisposeAsync"/>. A source that cannot seek (a packed entry) is first copied, from the
/// stream already opened, to the <see cref="TempFileStore"/>; the copy is read and released on dispose.</para>
/// <para>Nothing is read beyond what a page needs: positions are byte offsets, rows come from
/// <see cref="PagedTextReader"/> and <see cref="HexDump"/>, so opening, jumping to the end or to any offset reads a few
/// hundred kilobytes whatever the file's size. <see cref="Offset"/> is the first row shown; a page never starts past
/// the start of the last full page.</para>
/// <para>Page changes run one after another in the order asked for; the async methods complete when theirs is shown.
/// A read that fails shows <see cref="Error"/> and keeps the page. Of several jumps (<see cref="ScrollToOffsetAsync"/>)
/// waiting their turn, as while a scroll bar is dragged, only the last is read.</para>
/// <para>The first mode: <see cref="ViewerMode.Image"/> for a picture by its extension
/// (<c>png jpg jpeg gif bmp webp ico</c>) of at most <see cref="MaxImageBytes"/> that decodes; otherwise the first
/// 64 KiB decide (<see cref="TextEncodingDetector"/>): <see cref="ViewerMode.Hex"/> when they look binary (a UTF-16
/// guess without a byte order mark is checked again, see <see cref="IsUnlikelyUtf16"/>), else
/// <see cref="ViewerMode.Text"/> in the guessed encoding. A picture that does not decode shows as hex; a larger
/// picture file is not decoded and goes by its first 64 KiB.</para>
/// </remarks>
public sealed partial class ViewerViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>The largest picture file decoded: 512 MiB.</summary>
    public const long MaxImageBytes = 512L * 1024 * 1024;

    /// <summary>Pictures wider or taller are decoded scaled down to fit this many pixels.</summary>
    public const int MaxImageWidth = 4096;

    /// <summary>Rows per page until the view says how many fit.</summary>
    public const int DefaultPageLines = 40;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"];

    private const ConfigureAwaitOptions OnThisThreadIgnoringFailure =
        ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext;

    private readonly Stream _stream;
    private readonly TempFileStore _temp;
    private readonly string? _copy;
    private readonly EncodingGuess _guess;
    private readonly CancellationTokenSource _life = new();
    private PagedTextReader _reader;
    private Task _tail = Task.CompletedTask;
    private ViewerMode _mode;
    private TextEncodingInfo _encoding;
    private int _pageLines = DefaultPageLines;
    private int _jumps;
    private bool _disposed;

    private ViewerViewModel(string title, Loaded loaded, TempFileStore temp)
    {
        Title = title;
        _stream = loaded.Stream;
        _copy = loaded.Copy;
        _temp = temp;
        _guess = loaded.Guess;
        Length = loaded.Length;
        Image = loaded.Image;
        Modes = loaded.Image is null ? [ViewerMode.Text, ViewerMode.Hex] : [ViewerMode.Text, ViewerMode.Hex, ViewerMode.Image];
        _mode = loaded.Image is not null ? ViewerMode.Image
            : loaded.LooksBinary ? ViewerMode.Hex
            : ViewerMode.Text;
        _encoding = loaded.Guess.Encoding;
        _reader = new PagedTextReader(_stream, _encoding, loaded.Guess.BomLength);
        Lines = [];
        StatusText = Status();
    }

    /// <summary>Opens <paramref name="file"/> off the UI thread, decides the first mode and reads the first page.
    /// Call on the UI thread.</summary>
    /// <param name="images">Decodes pictures; <see cref="SkiaImageDecoder"/> when null.</param>
    /// <exception cref="IOException">The file cannot be read (<see cref="VfsNotFoundException"/> when it is
    /// gone).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; nothing is left open or in
    /// the temporary files.</exception>
    public static async Task<ViewerViewModel> OpenAsync(ProviderRegistry providers, TempFileStore temp, VfsPath file,
        CancellationToken ct, IImageDecoder? images = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(temp);
        var provider = providers.For(file);
        var title = providers.DisplayPath(file);
        var decoder = images ?? SkiaImageDecoder.Instance;
        var loaded = await Task.Run(() => LoadAsync(provider, temp, file, decoder, ct), CancellationToken.None);
        var viewer = new ViewerViewModel(title, loaded, temp);
        await viewer.GoToStartAsync();
        if (ct.IsCancellationRequested)
        {
            await viewer.DisposeAsync();
            ct.ThrowIfCancellationRequested();
        }
        return viewer;
    }

    /// <summary>The file's path as the user reads it.</summary>
    public string Title { get; }

    /// <summary>The file's size in bytes, as it was when opened.</summary>
    public long Length { get; }

    /// <summary>The modes this file can be shown in: <see cref="ViewerMode.Image"/> only when it decoded as a
    /// picture.</summary>
    public IReadOnlyList<ViewerMode> Modes { get; }

    /// <summary>How the file is shown. A mode not in <see cref="Modes"/> is ignored. Switching between text and hex
    /// keeps the place.</summary>
    public ViewerMode Mode
    {
        get => _mode;
        set
        {
            if (value == _mode || !Modes.Contains(value) || !SetProperty(ref _mode, value)) return;
            StatusText = Status();
            Track(Enqueue(ct => ShowAsync(() => AlignAsync(Offset, ct), ct)));
        }
    }

    /// <summary>The encodings text can be read in (<see cref="TextEncodings.All"/>).</summary>
    public IReadOnlyList<TextEncodingInfo> Encodings => TextEncodings.All;

    /// <summary>The encoding of the text rows: the guessed one at first. Changing it reads the page again from the
    /// same place (moved to a row start of the new encoding).</summary>
    public TextEncodingInfo Encoding
    {
        get => _encoding;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == _encoding) return;
            SetProperty(ref _encoding, value);
            // The guessed byte order mark belongs to the guessed encoding; in any other, those bytes are text.
            _reader = new PagedTextReader(_stream, value, value == _guess.Encoding ? _guess.BomLength : 0);
            StatusText = Status();
            Track(Enqueue(ct => _mode == ViewerMode.Text ? ShowAsync(() => AlignAsync(Offset, ct), ct) : Task.CompletedTask));
        }
    }

    /// <summary>Long text rows wrap in the view instead of scrolling sideways.</summary>
    [ObservableProperty]
    public partial bool WrapLines { get; set; }

    /// <summary>The rows of the page shown: text rows or hex rows; empty for a picture.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Lines { get; private set; }

    /// <summary>The byte offset of the first row shown.</summary>
    [ObservableProperty]
    public partial long Offset { get; private set; }

    /// <summary>The byte offset just past the page shown: the next page's first row.</summary>
    [ObservableProperty]
    public partial long PageEnd { get; private set; }

    /// <summary>How many rows a page holds; the view sets it from its height. At least 1.</summary>
    public int PageLines
    {
        get => _pageLines;
        set
        {
            value = Math.Max(1, value);
            if (!SetProperty(ref _pageLines, value)) return;
            Track(Enqueue(ct => ShowAsync(() => Task.FromResult(Offset), ct)));
        }
    }

    /// <summary>The picture, when the file decoded as one; disposed with the viewer.</summary>
    public Bitmap? Image { get; }

    /// <summary>How far the page reaches into the file (a picture: all of it), its size, and the encoding in text
    /// mode.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; }

    /// <summary>Why the last read failed, in words; null once a read succeeds.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Moves the page <paramref name="delta"/> rows down (up when negative), no further than the first or the
    /// last full page.</summary>
    public Task ScrollLinesAsync(int delta) =>
        delta == 0 ? IdleAsync() : Enqueue(ct => ShowAsync(() => LinesFromAsync(delta, ct), ct));

    /// <summary>Shows the row holding <paramref name="offset"/> at the top, or the last full page when that is
    /// nearer the end.</summary>
    public Task ScrollToOffsetAsync(long offset)
    {
        var jump = ++_jumps;
        return Enqueue(ct => jump == _jumps ? ShowAsync(() => AlignAsync(offset, ct), ct) : Task.CompletedTask);
    }

    public Task GoToStartAsync() => Enqueue(ct => ShowAsync(() => Task.FromResult(0L), ct));

    public Task GoToEndAsync() => Enqueue(ct => ShowAsync(() => EndTopAsync(ct), ct));

    /// <summary>Completes once every page change asked for so far is shown.</summary>
    public Task IdleAsync() => _tail.IsCompleted ? Task.CompletedTask : Enqueue(_ => Task.CompletedTask);

    /// <summary>Stops a page still being read, closes the file and releases its temporary copy and the picture.
    /// Call on the UI thread.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _life.Cancel();
        await _tail.ConfigureAwait(OnThisThreadIgnoringFailure);
        var (stream, temp, copy) = (_stream, _temp, _copy);
        await Task.Run(async () =>
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            if (copy is not null) Release(temp, copy);
        });
        Image?.Dispose();
        _life.Dispose();
    }

    // ---- Pages -------------------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="change"/> after the changes asked for before it. A read error shows in
    /// <see cref="Error"/>; after dispose nothing runs.</summary>
    private Task Enqueue(Func<CancellationToken, Task> change)
    {
        if (_disposed) return Task.CompletedTask;
        var task = RunAfterAsync(_tail, change);
        _tail = task;
        return task;
    }

    private async Task RunAfterAsync(Task previous, Func<CancellationToken, Task> change)
    {
        await previous.ConfigureAwait(OnThisThreadIgnoringFailure);
        if (_disposed) return;
        var ct = _life.Token;
        try
        {
            await change(ct);
            Error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // closed meanwhile
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = Loc.Format("Viewer_ReadFailed", OperationTexts.ForFailure(e));
        }
    }

    /// <summary>A change started from a property setter: what it throws unexpectedly goes to the UI thread's
    /// unhandled-exception handler (the app logs and shows it).</summary>
    private static async void Track(Task change) => await change;

    /// <summary>Reads the page whose first row starts where <paramref name="top"/> says, in the current mode, and
    /// shows it. A picture has no rows: nothing is read.</summary>
    private async Task ShowAsync(Func<Task<long>> top, CancellationToken ct)
    {
        if (_mode == ViewerMode.Image) return;
        var start = await top();
        var (reader, stream, rows) = (_reader, _stream, _pageLines);
        if (_mode == ViewerMode.Text)
        {
            var page = await Task.Run(() => reader.ReadPageAsync(start, rows, ct), ct);
            ct.ThrowIfCancellationRequested();
            (Lines, Offset, PageEnd) = (page.Lines.Select(l => l.Text).ToArray(), page.StartOffset, page.EndOffset);
        }
        else
        {
            var hex = await Task.Run(() => HexDump.ReadRowsAsync(stream, start, rows, ct), ct);
            ct.ThrowIfCancellationRequested();
            (Lines, Offset, PageEnd) = (hex, start, Math.Min(Length, start + (long)hex.Count * HexDump.BytesPerRow));
        }
        StatusText = Status();
    }

    /// <summary>The page start <paramref name="delta"/> rows from the current one, clamped to the first and last full
    /// page (or kept, when the page is past the last full one already).</summary>
    private async Task<long> LinesFromAsync(int delta, CancellationToken ct)
    {
        var (reader, offset) = (_reader, Offset);
        if (_mode == ViewerMode.Hex)
        {
            var moved = offset + (long)delta * HexDump.BytesPerRow;
            return Math.Clamp(moved, 0, Math.Max(HexEndTop(), offset));
        }
        if (delta < 0) return await Task.Run(() => reader.StepBackAsync(offset, -delta, ct), ct);
        var page = await Task.Run(() => reader.ReadPageAsync(offset, delta, ct), ct);
        return Math.Min(page.EndOffset, Math.Max(await EndTopAsync(ct), offset));
    }

    /// <summary><paramref name="offset"/> moved to the start of its row, no further than the last full page.</summary>
    private async Task<long> AlignAsync(long offset, CancellationToken ct)
    {
        offset = Math.Clamp(offset, 0, Length);
        if (_mode == ViewerMode.Hex) return Math.Min(offset - offset % HexDump.BytesPerRow, HexEndTop());
        var end = await EndTopAsync(ct);
        if (offset >= end) return end;
        var reader = _reader;
        return await Task.Run(() => reader.LineStartAsync(offset, ct), ct);
    }

    /// <summary>The start of the last full page in the current mode.</summary>
    private async Task<long> EndTopAsync(CancellationToken ct)
    {
        if (_mode != ViewerMode.Text) return HexEndTop();
        var (reader, length, rows) = (_reader, Length, _pageLines);
        return await Task.Run(() => reader.StepBackAsync(length, rows, ct), ct);
    }

    private long HexEndTop()
    {
        var rows = (Length + HexDump.BytesPerRow - 1) / HexDump.BytesPerRow;
        return Math.Max(0, rows - _pageLines) * HexDump.BytesPerRow;
    }

    private string Status()
    {
        var percent = Length == 0 || _mode == ViewerMode.Image ? 100 : (int)(PageEnd * 100 / Length);
        var size = SizeFormatter.Format(Length);
        return _mode == ViewerMode.Text
            ? Loc.Format("Viewer_StatusWithEncoding", percent, size, _encoding.Id)
            : Loc.Format("Viewer_Status", percent, size);
    }

    // ---- Opening -----------------------------------------------------------------------------------------

    /// <summary>What opening gives the viewer: the seekable stream to read (of the file, or of its temporary
    /// <paramref name="Copy"/>), the encoding guess, whether the file is shown as hex at first, and the picture, if
    /// any.</summary>
    private sealed record Loaded(Stream Stream, string? Copy, long Length, EncodingGuess Guess, bool LooksBinary,
        Bitmap? Image);

    private static async Task<Loaded> LoadAsync(IFileSystemProvider provider, TempFileStore temp, VfsPath file,
        IImageDecoder images, CancellationToken ct)
    {
        Stream? stream = null;
        string? copy = null;
        Bitmap? image = null;
        try
        {
            stream = await provider.OpenReadAsync(file, ct).ConfigureAwait(false);
            if (!stream.CanSeek)
            {
                // The stream already open is copied, so the source is read once.
                copy = temp.NewFile(file.Name);
                await CopyAsync(stream, copy, ct).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                stream = null;
                stream = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            }
            var length = stream.Length;

            var sample = new byte[(int)Math.Min(length, TextEncodingDetector.SampleSize)];
            stream.Position = 0;
            var read = 0;
            while (read < sample.Length)
            {
                var n = await stream.ReadAsync(sample.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            var wholeFile = length <= TextEncodingDetector.SampleSize;
            var guess = TextEncodingDetector.Detect(sample.AsSpan(0, read), wholeFile);
            var looksBinary = guess.LooksBinary || IsUnlikelyUtf16(guess, sample.AsSpan(0, read), wholeFile);

            var decodable = ImageExtensions.Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase)
                            && length <= MaxImageBytes;
            if (decodable)
            {
                ct.ThrowIfCancellationRequested();
                stream.Position = 0;
                image = TryDecode(images, stream);
                // A picture by its name that does not decode is shown as bytes.
                looksBinary |= image is null;
            }
            ct.ThrowIfCancellationRequested();
            return new Loaded(stream, copy, length, guess, looksBinary, image);
        }
        catch
        {
            image?.Dispose();
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            if (copy is not null) Release(temp, copy);
            throw;
        }
    }

    private static async Task CopyAsync(Stream source, string path, CancellationToken ct)
    {
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, useAsync: true);
        await source.CopyToAsync(destination, 81920, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a UTF-16 guess made without a byte order mark is unlikely to be text. The detector makes it from the
    /// share of zero bytes at odd or even positions alone, which arrays of small 16-bit numbers (tables, sound
    /// samples) share with text, and then does not look for binary data. So the sample is decoded here, judged like
    /// the 8-bit check: binary when control characters other than tab, line feed, form feed, carriage return and
    /// escape make up more than <see cref="TextEncodingDetector.BinaryControlShare"/> of the units. A lone surrogate
    /// is binary at once; a high surrogate at the very end of a sample cut from a longer file is not counted.
    /// </summary>
    private static bool IsUnlikelyUtf16(EncodingGuess guess, ReadOnlySpan<byte> sample, bool wholeFile)
    {
        if (guess.BomLength != 0 || guess.LooksBinary) return false;
        bool bigEndian;
        if (guess.Encoding == TextEncodings.Utf16LE) bigEndian = false;
        else if (guess.Encoding == TextEncodings.Utf16BE) bigEndian = true;
        else return false;

        var units = sample.Length / 2;
        if (units == 0) return false;
        var controls = 0;
        for (var i = 0; i < units; i++)
        {
            var unit = Unit(sample, i, bigEndian);
            if (unit < 0x20)
            {
                if (unit is not ('\t' or '\n' or '\f' or '\r' or '\u001b')) controls++;
            }
            else if (char.IsHighSurrogate(unit))
            {
                if (i + 1 < units)
                {
                    if (!char.IsLowSurrogate(Unit(sample, i + 1, bigEndian))) return true;
                    i++;
                }
                else if (wholeFile)
                {
                    return true;
                }
            }
            else if (char.IsLowSurrogate(unit))
            {
                return true;
            }
        }
        return (double)controls / units > TextEncodingDetector.BinaryControlShare;
    }

    private static char Unit(ReadOnlySpan<byte> bytes, int index, bool bigEndian) =>
        (char)(bigEndian
            ? bytes[2 * index] << 8 | bytes[2 * index + 1]
            : bytes[2 * index + 1] << 8 | bytes[2 * index]);

    /// <summary>The picture, or null when the bytes do not decode. A decoder fails with whatever exception its codec
    /// throws for damaged or foreign data, so every failure counts as "not a picture" here.</summary>
    private static Bitmap? TryDecode(IImageDecoder images, Stream stream)
    {
        try
        {
            return images.Decode(stream, MaxImageWidth);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Removes the temporary copy; one that cannot be removed goes with the store's next clear.</summary>
    private static void Release(TempFileStore temp, string copy)
    {
        try
        {
            temp.Release(copy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // TempFileStore.Clear removes it at exit or the next start
        }
    }
}
