namespace Tandem.Core.Themes;

/// <summary>A theme the user may choose: the built-in one, or a file of the themes folder. A file that cannot be read
/// is listed with its <see cref="Error"/>, so the user sees why it is not applied.</summary>
public sealed record ThemeInfo(string Id, string Name, string? Path, string? Error);

/// <summary>
/// The themes there are: the built-in "Stone" and every <c>*.json</c> of <see cref="Directory"/> (the app's
/// <c>Themes</c> folder beside <c>settings.json</c>). A file's id is its name without ".json"; a file named like the
/// built-in theme replaces it. Read anew on every call, so a theme dropped into the folder shows up without a restart.
/// </summary>
public sealed class ThemeCatalog(string directory)
{
    /// <summary>The themes folder; it need not exist.</summary>
    public string Directory { get; } = directory;

    /// <summary>The folder <c>Themes</c> under the app's state folder.</summary>
    public static string DirectoryIn(string stateDirectory) => System.IO.Path.Combine(stateDirectory, "Themes");

    /// <summary>The built-in theme first, then the files by name.</summary>
    public IReadOnlyList<ThemeInfo> List()
    {
        var files = new List<ThemeInfo>();
        foreach (var path in Files())
        {
            var id = IdOf(path);
            try
            {
                var theme = ThemeParser.Parse(File.ReadAllText(path), id, path);
                files.Add(new ThemeInfo(id, theme.Name, path, null));
            }
            catch (Exception e) when (e is ThemeFormatException or IOException or UnauthorizedAccessException)
            {
                files.Add(new ThemeInfo(id, id, path, e.Message));
            }
        }
        var builtIn = files.Any(f => f.Id == BuiltInThemes.StoneId && f.Error is null)
            ? []
            : new[] { new ThemeInfo(BuiltInThemes.StoneId, BuiltInThemes.Stone.Name, null, null) };
        return builtIn.Concat(files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    /// <summary>The theme <paramref name="id"/> with the built-in tokens it lacks; the built-in theme when the id is
    /// null, unknown or its file cannot be read (then <paramref name="error"/> says why).</summary>
    public ThemeDefinition Load(string? id, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(id)) return BuiltInThemes.Stone;
        var path = Files().FirstOrDefault(p => string.Equals(IdOf(p), id, StringComparison.OrdinalIgnoreCase));
        if (path is null)
        {
            if (!string.Equals(id, BuiltInThemes.StoneId, StringComparison.OrdinalIgnoreCase))
                error = $"No theme \"{id}\" in {Directory}.";
            return BuiltInThemes.Stone;
        }
        try
        {
            return ThemeParser.Parse(File.ReadAllText(path), IdOf(path), path).WithFallback(BuiltInThemes.Stone);
        }
        catch (Exception e) when (e is ThemeFormatException or IOException or UnauthorizedAccessException)
        {
            error = $"{System.IO.Path.GetFileName(path)}: {e.Message}";
            return BuiltInThemes.Stone;
        }
    }

    private IEnumerable<string> Files()
    {
        try
        {
            return System.IO.Directory.Exists(Directory)
                ? System.IO.Directory.EnumerateFiles(Directory, "*.json").ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string IdOf(string path) => System.IO.Path.GetFileNameWithoutExtension(path);
}
