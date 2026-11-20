using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.Tests.Views;
using Tandem.App.ViewModels.Viewing;
using Tandem.Core.Tests.Viewing;
using Tandem.Core.Viewing;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;

namespace Tandem.App.Tests.ViewModels;

/// <summary>The built-in viewer's model: text in the detected or chosen encoding, hex, images, paging by rows, and
/// files of any size or from any source read a little at a time.</summary>
[Collection(LanguageCollection.Name)]
public sealed class ViewerViewModelTests : LanguageTestBase, IDisposable
{
    /// <summary>A 1×1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly TempFileStore _temp =
        new(Path.Combine(Path.GetTempPath(), "tandem-viewer-tests", Guid.NewGuid().ToString("N")));

    private readonly FakeImageDecoder _images = new();

    public new void Dispose()
    {
        _temp.Dispose();
        Directory.Delete(_temp.Directory, recursive: true);
        base.Dispose();
    }

    private static Encoding CodePage(int number) => TextEncodings.All.Single(e => e.Encoding.CodePage == number).Encoding;

    private static (ProviderRegistry Providers, VfsPath File) InMemory(string name, byte[] content)
    {
        var fs = new InMemoryFileSystem("mem");
        fs.AddFile("/dir/" + name, content);
        var providers = new ProviderRegistry();
        providers.Register(fs);
        return (providers, new VfsPath("mem", "/dir/" + name));
    }

    private static ProviderRegistry Registry(StreamProvider provider)
    {
        var providers = new ProviderRegistry();
        providers.Register(provider);
        return providers;
    }

    private Task<ViewerViewModel> OpenAsync(ProviderRegistry providers, VfsPath file, CancellationToken ct = default) =>
        ViewerViewModel.OpenAsync(providers, _temp, file, ct, _images);

    private Task<ViewerViewModel> OpenAsync(string name, byte[] content)
    {
        var (providers, file) = InMemory(name, content);
        return OpenAsync(providers, file);
    }

    /// <summary>"line 001\n" … "line {count}\n": 9 bytes each.</summary>
    private static byte[] NumberedLines(int count) =>
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(1, count).Select(i => $"line {i:D3}\n")));

    private IReadOnlyList<string> TempEntries() => Directory.EnumerateFileSystemEntries(_temp.Directory).ToArray();

    [AvaloniaFact]
    public async Task Text_file_opens_as_text_in_detected_encoding()
    {
        await using var viewer = await OpenAsync("hello.txt", CodePage(1251).GetBytes("Привет\nмир\n"));

        Assert.Equal(ViewerMode.Text, viewer.Mode);
        Assert.Equal(TextEncodings.Windows1251, viewer.Encoding);
        Assert.Equal(TextEncodings.All, viewer.Encodings);
        Assert.Equal(["Привет", "мир"], viewer.Lines);
        Assert.Equal(0, viewer.Offset);
        Assert.Equal(11, viewer.Length);
        Assert.Equal("/dir/hello.txt", viewer.Title);
        Assert.Contains("windows-1251", viewer.StatusText);
        Assert.Equal([ViewerMode.Text, ViewerMode.Hex], viewer.Modes);
    }

    [AvaloniaFact]
    public async Task Changing_encoding_redecodes_the_same_place()
    {
        // The first 64 KiB are ASCII, so the file is guessed as UTF-8; its end is DOS Cyrillic.
        var ascii = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ascii text\n", 7000)));
        var content = ascii.Concat(Enumerable.Range(0, 8000).SelectMany(_ => CodePage(866).GetBytes("Привет из DOS\n")))
            .ToArray();
        Assert.True(ascii.Length > TextEncodingDetector.SampleSize && ascii.Length < content.Length / 2);
        await using var viewer = await OpenAsync("dos.txt", content);
        viewer.PageLines = 5;
        Assert.Equal(TextEncodings.Utf8, viewer.Encoding);
        await viewer.ScrollToOffsetAsync(content.Length / 2);
        var place = viewer.Offset;
        Assert.All(viewer.Lines, line => Assert.Contains('�', line));

        viewer.Encoding = TextEncodings.All.Single(e => e.Id == "cp866");
        await viewer.IdleAsync();

        Assert.Equal(place, viewer.Offset);
        Assert.Equal(Enumerable.Repeat("Привет из DOS", 5), viewer.Lines);
        Assert.Contains("cp866", viewer.StatusText);
    }

    [AvaloniaFact]
    public async Task Binary_file_opens_in_hex_with_offset_bytes_and_ascii()
    {
        var content = Enumerable.Range(0, 256).Select(i => (byte)i).Concat("Tail"u8.ToArray()).ToArray();

        await using var viewer = await OpenAsync("data.bin", content);

        Assert.Equal(ViewerMode.Hex, viewer.Mode);
        Assert.Equal(17, viewer.Lines.Count);
        Assert.Equal("00000000  00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  |................|", viewer.Lines[0]);
        Assert.Equal("00000040  40 41 42 43 44 45 46 47  48 49 4A 4B 4C 4D 4E 4F  |@ABCDEFGHIJKLMNO|", viewer.Lines[4]);
        Assert.Equal("00000100  54 61 69 6C                                       |Tail|", viewer.Lines[16]);

        viewer.Mode = ViewerMode.Text;
        await viewer.IdleAsync();
        Assert.Equal(ViewerMode.Text, viewer.Mode);
        Assert.NotEmpty(viewer.Lines);
    }

    [AvaloniaFact]
    public async Task Scroll_moves_by_lines_and_clamps_at_both_ends()
    {
        await using var viewer = await OpenAsync("lines.txt", NumberedLines(100));
        viewer.PageLines = 10;
        await viewer.IdleAsync();

        await viewer.ScrollLinesAsync(3);
        Assert.Equal("line 004", viewer.Lines[0]);
        Assert.Equal(3 * 9, viewer.Offset);

        await viewer.ScrollLinesAsync(-10);
        Assert.Equal("line 001", viewer.Lines[0]);
        Assert.Equal(0, viewer.Offset);

        await viewer.ScrollLinesAsync(1000);
        Assert.Equal(Enumerable.Range(91, 10).Select(i => $"line {i:D3}"), viewer.Lines);
        await viewer.ScrollLinesAsync(1);
        Assert.Equal("line 091", viewer.Lines[0]);

        // Hex keeps the place (its row) and scrolls by 16-byte rows, also clamped.
        await viewer.ScrollLinesAsync(-50);
        Assert.Equal("line 041", viewer.Lines[0]);
        viewer.Mode = ViewerMode.Hex;
        await viewer.IdleAsync();
        Assert.Equal(40 * 9 / 16 * 16, viewer.Offset);
        viewer.Mode = ViewerMode.Text;
        await viewer.IdleAsync();
        Assert.Equal("line 040", viewer.Lines[0]); // the row holding byte 352
        viewer.Mode = ViewerMode.Hex;
        await viewer.ScrollLinesAsync(1000);
        Assert.Equal((57 - 10) * 16, viewer.Offset); // 900 bytes: 57 rows
        Assert.Equal(10, viewer.Lines.Count);
        await viewer.ScrollLinesAsync(-1000);
        Assert.Equal(0, viewer.Offset);

        viewer.Mode = ViewerMode.Text;
        await viewer.ScrollToOffsetAsync(5 * 9 + 4); // inside line 6
        Assert.Equal("line 006", viewer.Lines[0]);
    }

    [AvaloniaFact]
    public async Task End_shows_the_last_page_and_Start_the_first()
    {
        await using var viewer = await OpenAsync("lines.txt", NumberedLines(100));
        viewer.PageLines = 10;

        await viewer.GoToEndAsync();
        Assert.Equal(Enumerable.Range(91, 10).Select(i => $"line {i:D3}"), viewer.Lines);
        Assert.StartsWith("100%", viewer.StatusText);

        await viewer.GoToStartAsync();
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"line {i:D3}"), viewer.Lines);
        Assert.Equal(0, viewer.Offset);
        Assert.StartsWith("10%", viewer.StatusText);
    }

    [AvaloniaFact]
    public async Task Png_opens_as_image_and_can_switch_to_hex()
    {
        await using var viewer = await OpenAsync("pixel.png", Png);

        Assert.Equal(ViewerMode.Image, viewer.Mode);
        Assert.NotNull(viewer.Image);
        Assert.Equal([ViewerMode.Text, ViewerMode.Hex, ViewerMode.Image], viewer.Modes);
        Assert.Equal([(ViewerViewModel.MaxImageWidth, false)], _images.Calls); // off the UI thread
        Assert.StartsWith("100%", viewer.StatusText);

        viewer.Mode = ViewerMode.Hex;
        await viewer.IdleAsync();
        Assert.StartsWith("00000000  89 50 4E 47 0D 0A 1A 0A", viewer.Lines[0]);
        viewer.Mode = ViewerMode.Image;
        Assert.Equal(ViewerMode.Image, viewer.Mode);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Small_16_bit_numbers_without_a_byte_order_mark_open_as_hex(bool bigEndian)
    {
        // Every high byte is zero and the low bytes are not: the detector's UTF-16 pattern, but the units are
        // control characters.
        var content = Enumerable.Range(0, 1000)
            .SelectMany(i => bigEndian ? new byte[] { 0, (byte)(1 + i % 30) } : [(byte)(1 + i % 30), 0]).ToArray();
        Assert.Equal(bigEndian ? TextEncodings.Utf16BE : TextEncodings.Utf16LE,
            TextEncodingDetector.Detect(content, sampleIsWholeFile: true).Encoding);

        await using var viewer = await OpenAsync("samples.raw", content);

        Assert.Equal(ViewerMode.Hex, viewer.Mode);
    }

    [AvaloniaTheory]
    [InlineData("Привет, мир\tэто\r\n")]
    [InlineData("Hello, world: a line long enough for one emoji \U0001F600\n")]
    public async Task Utf16_text_without_a_byte_order_mark_still_opens_as_text(string line)
    {
        var content = TextEncodings.Utf16LE.Encoding.GetBytes(string.Concat(Enumerable.Repeat(line, 20)));

        await using var viewer = await OpenAsync("utf16.txt", content);

        Assert.Equal(ViewerMode.Text, viewer.Mode);
        Assert.Equal(TextEncodings.Utf16LE, viewer.Encoding);
        Assert.Equal(line.TrimEnd('\n', '\r'), viewer.Lines[0]);
    }

    [AvaloniaFact]
    public async Task Lone_surrogate_in_a_utf16_guess_opens_as_hex()
    {
        var units = Enumerable.Repeat('я', 40).Concat(['\uDC00']).Concat(Enumerable.Repeat('я', 40)).ToArray();
        var content = units.SelectMany(c => new[] { (byte)c, (byte)(c >> 8) }).ToArray();

        await using var viewer = await OpenAsync("odd.txt", content);

        Assert.Equal(ViewerMode.Hex, viewer.Mode);
    }

    [AvaloniaFact]
    public async Task Picture_file_too_large_to_decode_goes_by_its_bytes()
    {
        const long length = ViewerViewModel.MaxImageBytes + 1;
        var provider = new StreamProvider();
        var file = provider.Add("huge.png", length,
            () => new SparseStream(length, p => p % 64 == 63 ? (byte)'\n' : (byte)'a'));

        await using var viewer = await OpenAsync(Registry(provider), file);

        Assert.Equal(ViewerMode.Text, viewer.Mode);
        Assert.Empty(_images.Calls);
    }

    [AvaloniaFact]
    public async Task Picture_decoded_before_a_cancel_is_disposed()
    {
        using var cts = new CancellationTokenSource();
        _images.AfterDecode = cts.Cancel;
        var (providers, file) = InMemory("pixel.png", Png);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenAsync(providers, file, cts.Token));

        Assert.Single(_images.Calls);
        Assert.Equal(1, _images.Disposed);
    }

    [AvaloniaFact]
    public async Task Broken_image_falls_back_to_hex()
    {
        await using var viewer = await OpenAsync("broken.png", "not a picture"u8.ToArray());

        Assert.Equal(ViewerMode.Hex, viewer.Mode);
        Assert.Null(viewer.Image);
        Assert.Equal([ViewerMode.Text, ViewerMode.Hex], viewer.Modes);
        Assert.Single(_images.Calls);
        viewer.Mode = ViewerMode.Image; // not offered: ignored
        Assert.Equal(ViewerMode.Hex, viewer.Mode);
    }

    [AvaloniaFact]
    public async Task Non_seekable_source_is_copied_to_temp_once_and_released_on_dispose()
    {
        var provider = new StreamProvider();
        var content = NumberedLines(100);
        var file = provider.Add("packed.txt", content.Length, () => new TestStream(new MemoryStream(content), false));

        var viewer = await OpenAsync(Registry(provider), file);
        Assert.Equal("line 001", viewer.Lines[0]);
        await viewer.GoToEndAsync();
        await viewer.ScrollLinesAsync(-5);
        Assert.Equal("line 056", viewer.Lines[0]);
        var copy = Assert.Single(TempEntries());
        Assert.Equal(["packed.txt"], Directory.EnumerateFiles(copy).Select(Path.GetFileName));
        Assert.True(((TestStream)Assert.Single(provider.Opened)).IsDisposed); // opened once, copied, read from the copy

        await viewer.DisposeAsync();

        Assert.Empty(TempEntries());
        Assert.All(provider.Opened, s => Assert.True(((TestStream)s).IsDisposed));
    }

    [AvaloniaFact]
    public async Task Viewer_closed_while_copying_its_source_leaves_no_temp_file()
    {
        var provider = new StreamProvider();
        var gate = new TaskCompletionSource();
        var content = NumberedLines(100);
        var file = provider.Add("packed.txt", content.Length,
            () => new TestStream(new MemoryStream(content), false) { Gate = gate });
        using var cts = new CancellationTokenSource();

        var opening = OpenAsync(Registry(provider), file, cts.Token);
        await TestApp.WaitUntil(() => provider.Opened.Count == 1);
        await ((TestStream)provider.Opened[0]).ReadWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(TempEntries());
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Empty(TempEntries());
        Assert.All(provider.Opened, s => Assert.True(((TestStream)s).IsDisposed));
    }

    [AvaloniaFact]
    public async Task Viewer_closed_while_a_page_loads_closes_the_stream()
    {
        var provider = new StreamProvider();
        var stream = new TestStream(new MemoryStream(NumberedLines(100)), true);
        var file = provider.Add("slow.txt", stream.Length, () => stream);
        var viewer = await OpenAsync(Registry(provider), file);
        stream.Gate = new TaskCompletionSource();

        var scrolling = viewer.ScrollLinesAsync(1);
        await stream.ReadWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await scrolling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stream.IsDisposed);
        Assert.Equal("line 001", viewer.Lines[0]);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eight_GiB_file_opens_and_jumps_to_end_reading_under_two_mebibytes(bool singleLine)
    {
        const long length = 8L * 1024 * 1024 * 1024;
        var sparse = new SparseStream(length, singleLine
            ? p => (byte)('a' + p % 26)
            : p => p % 64 == 63 ? (byte)'\n' : (byte)('a' + p % 26));
        var provider = new StreamProvider();
        var file = provider.Add("big.txt", length, () => sparse);

        await using var viewer = await OpenAsync(Registry(provider), file);
        Assert.Equal(ViewerMode.Text, viewer.Mode);
        Assert.Equal(length, viewer.Length);
        Assert.Equal(ViewerViewModel.DefaultPageLines, viewer.Lines.Count);
        await viewer.GoToEndAsync();

        Assert.True(sparse.BytesRead < 2 * 1024 * 1024, $"read {sparse.BytesRead} bytes");
        Assert.Equal(ViewerViewModel.DefaultPageLines, viewer.Lines.Count);
        Assert.StartsWith("100%", viewer.StatusText);
        if (!singleLine) Assert.Equal(length - 64 * ViewerViewModel.DefaultPageLines, viewer.Offset);

        sparse.ResetCounters();
        await viewer.ScrollToOffsetAsync(length / 2);
        await viewer.ScrollLinesAsync(-3);
        viewer.Mode = ViewerMode.Hex;
        await viewer.IdleAsync();
        await viewer.GoToEndAsync();
        Assert.True(sparse.BytesRead < 2 * 1024 * 1024, $"read {sparse.BytesRead} bytes");
        Assert.True(sparse.MaxRequestedCount < 1024 * 1024);
        Assert.Equal(length - 16 * ViewerViewModel.DefaultPageLines, viewer.Offset);
    }

    [AvaloniaFact]
    public async Task Of_jumps_waiting_their_turn_only_the_last_is_read()
    {
        const long length = 8L * 1024 * 1024 * 1024;
        var sparse = new SparseStream(length, p => p % 64 == 63 ? (byte)'\n' : (byte)'x');
        var stream = new TestStream(sparse, true);
        var provider = new StreamProvider();
        var file = provider.Add("big.txt", length, () => stream);
        await using var viewer = await OpenAsync(Registry(provider), file);
        stream.Gate = new TaskCompletionSource();
        var scrolling = viewer.ScrollLinesAsync(1);
        await stream.ReadWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var jumps = Enumerable.Range(1, 20).Select(i => viewer.ScrollToOffsetAsync(length / 21 * i)).ToArray();
        sparse.ResetCounters();
        stream.Gate.SetResult();
        await Task.WhenAll([scrolling, .. jumps]);

        Assert.Equal(length / 21 * 20 / 64 * 64, viewer.Offset);
        Assert.True(sparse.BytesRead < 1024 * 1024, $"read {sparse.BytesRead} bytes"); // one scroll and one jump
    }

    [AvaloniaFact]
    public async Task Dispose_closes_the_stream()
    {
        var provider = new StreamProvider();
        var stream = new TestStream(new MemoryStream(Png), true);
        var file = provider.Add("pixel.png", Png.Length, () => stream);
        var viewer = await OpenAsync(Registry(provider), file);
        Assert.False(stream.IsDisposed);

        await viewer.DisposeAsync();

        Assert.True(stream.IsDisposed);
        Assert.Empty(TempEntries());
        Assert.Equal(1, _images.Disposed);
        await viewer.DisposeAsync(); // again: nothing more
    }

    [AvaloniaFact]
    public async Task A_failed_read_is_shown_in_words_and_keeps_the_page()
    {
        var provider = new StreamProvider();
        var stream = new TestStream(new MemoryStream(NumberedLines(100)), true);
        var file = provider.Add("flaky.txt", stream.Length, () => stream);
        await using var viewer = await OpenAsync(Registry(provider), file);
        stream.Failure = new IOException("Input/output error");

        await viewer.ScrollLinesAsync(5);

        Assert.Equal(Loc.Format("Viewer_ReadFailed", OperationTexts.ForFailure(new IOException("Input/output error"))), viewer.Error);
        Assert.Equal("line 001", viewer.Lines[0]);

        stream.Failure = null;
        await viewer.ScrollLinesAsync(5);
        Assert.Null(viewer.Error);
        Assert.Equal("line 006", viewer.Lines[0]);
    }

    [AvaloniaFact]
    public async Task Unreadable_file_fails_to_open()
    {
        var fs = new InMemoryFileSystem("mem");
        fs.AddFile("/gone.txt", "x");
        fs.FailOn((op, _) => op == "OpenRead", () => new IOException("device not configured"));
        var providers = new ProviderRegistry();
        providers.Register(fs);

        await Assert.ThrowsAsync<IOException>(() => OpenAsync(providers, new VfsPath("mem", "/gone.txt")));
        Assert.Empty(TempEntries());
    }

    [AvaloniaFact]
    public void Skia_decoder_reads_a_picture_and_refuses_other_bytes()
    {
        using (var png = new MemoryStream(Png))
        using (var bitmap = SkiaImageDecoder.Instance.Decode(png, ViewerViewModel.MaxImageWidth))
            Assert.NotNull(bitmap);
        using var text = new MemoryStream("not a picture"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => SkiaImageDecoder.Instance.Decode(text, ViewerViewModel.MaxImageWidth));
    }

    /// <summary>Accepts only bytes that start with the PNG signature, so a broken picture can fail as the real decoder
    /// does (the headless platform decodes anything). Records each call: the size asked for and whether it ran on the
    /// UI thread.</summary>
    private sealed class FakeImageDecoder : IImageDecoder
    {
        public List<(int MaxSize, bool OnUiThread)> Calls { get; } = [];
        public int Disposed;

        /// <summary>Runs after a successful decode, before it returns.</summary>
        public Action? AfterDecode { get; set; }

        public Bitmap Decode(Stream stream, int maxSize)
        {
            lock (Calls) Calls.Add((maxSize, Dispatcher.UIThread.CheckAccess()));
            var signature = new byte[8];
            stream.ReadExactly(signature);
            if (!signature.AsSpan().SequenceEqual(Png.AsSpan(0, 8))) throw new InvalidDataException("Not a PNG.");
            stream.Position = 0;
            var bitmap = new CountingBitmap(stream, this);
            AfterDecode?.Invoke();
            return bitmap;
        }

        private sealed class CountingBitmap(Stream stream, FakeImageDecoder owner) : Bitmap(stream)
        {
            public override void Dispose()
            {
                Interlocked.Increment(ref owner.Disposed);
                base.Dispose();
            }
        }
    }
}
