using System.Text;

namespace Tandem.Core.Viewing;

/// <summary>
/// A text encoding the user can pick, identified by a stable lowercase <paramref name="Id"/>.
/// <paramref name="Encoding"/> never throws on bad input: undecodable bytes become U+FFFD and
/// unencodable characters become a replacement character. It writes no byte order mark of its own.
/// </summary>
public sealed record TextEncodingInfo(string Id, Encoding Encoding);

/// <summary>
/// The encodings offered for viewing, editing and searching text. Touching this class registers the
/// legacy code pages (<see cref="CodePagesEncodingProvider"/>) for the whole process, so Core and its
/// users get Windows-1251, KOI8-R, CP866 and the rest without any startup code.
/// </summary>
public static class TextEncodings
{
    static TextEncodings()
    {
        // Idempotent: the runtime ignores a provider instance that is already registered.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Utf8 = new("utf-8", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
        Utf16LE = new("utf-16le", new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false));
        Utf16BE = new("utf-16be", new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false));
        Windows1251 = CodePage("windows-1251", 1251);
        All =
        [
            Utf8,
            Utf16LE,
            Utf16BE,
            Windows1251,
            CodePage("windows-1252", 1252),
            CodePage("koi8-r", 20866),
            CodePage("cp866", 866),
            CodePage("x-mac-cyrillic", 10007),
        ];
    }

    public static TextEncodingInfo Utf8 { get; }
    public static TextEncodingInfo Utf16LE { get; }
    public static TextEncodingInfo Utf16BE { get; }
    public static TextEncodingInfo Windows1251 { get; }

    /// <summary>In the order offered for manual choice.</summary>
    public static IReadOnlyList<TextEncodingInfo> All { get; }

    /// <summary>The listed encoding whose id equals <paramref name="id"/> ignoring case, or null.</summary>
    public static TextEncodingInfo? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        foreach (var info in All)
            if (string.Equals(info.Id, id, StringComparison.OrdinalIgnoreCase))
                return info;
        return null;
    }

    // DecoderFallback.ReplacementFallback would put '?'; the documented replacement is U+FFFD.
    private static TextEncodingInfo CodePage(string id, int codePage) =>
        new(id, Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("\uFFFD")));
}
