using System.Text;

namespace Tandem.Core.Themes;

/// <summary>
/// The theme files that ship with the app (Themes/Bundled/*.json, embedded) and are copied into the user's themes
/// folder on start, so they show up in the theme list and can be edited there like any other theme file. A file the
/// folder already has is never overwritten: the user's edits (or a file of their own by that name) win.
/// </summary>
public static class BundledThemes
{
    private const string Prefix = "Tandem.Core.Themes.Bundled.";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly Lazy<IReadOnlyList<string>> Names = new(() => typeof(BundledThemes).Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(n => n[Prefix.Length..])
        .Order(StringComparer.Ordinal)
        .ToList());

    /// <summary>The file names of the bundled themes ("npp-monokai.json"…), by name.</summary>
    public static IReadOnlyList<string> FileNames => Names.Value;

    /// <summary>The text of the bundled theme file <paramref name="fileName"/>.</summary>
    /// <exception cref="ArgumentException">There is no bundled theme of that name.</exception>
    public static string Read(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        using var stream = typeof(BundledThemes).Assembly.GetManifestResourceStream(Prefix + fileName)
                           ?? throw new ArgumentException($"No bundled theme \"{fileName}\".", nameof(fileName));
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Copies every bundled theme the folder <paramref name="directory"/> lacks into it (UTF-8 without BOM), creating
    /// the folder if need be. A file of the same name is left as it is. A file that cannot be written (or a folder
    /// that cannot be created) is skipped, so a read-only or locked folder never stops the app from starting.
    /// </summary>
    /// <returns>The file names copied.</returns>
    public static IReadOnlyList<string> Install(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var copied = new List<string>();
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return copied;
        }

        foreach (var fileName in FileNames)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path)) continue;
            if (TryCreate(path, Read(fileName))) copied.Add(fileName);
        }
        return copied;
    }

    /// <summary>Writes <paramref name="text"/> to a new file <paramref name="path"/>; false when the file already
    /// exists (created meanwhile) or cannot be written, a half-written file removed again.</summary>
    private static bool TryCreate(string path, string text)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // already there, or not writable
        }

        try
        {
            using (stream)
            {
                var bytes = Utf8NoBom.GetBytes(text);
                stream.Write(bytes);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException)
            {
                // left half-written; the theme list shows it with its parse error
            }
            return false;
        }
    }
}
