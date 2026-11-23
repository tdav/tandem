using System.Text.Json;

namespace Tandem.Core.Settings;

/// <summary>
/// Keeps <see cref="AppState"/> in <c>state.json</c> inside <paramref name="directory"/>.
/// <see cref="Load"/> never throws: a missing, unreadable or damaged file yields <see cref="AppState.Default"/>,
/// and a damaged one is kept aside as <c>state.json.bad</c>. <see cref="Save"/> replaces the file atomically.
/// Calls on one instance are serialised, so it may be used from several threads.
/// </summary>
public sealed class JsonStateStore(string directory)
{
    private const string FileName = "state.json";

    private readonly Lock _gate = new();
    private readonly string _file = Path.Combine(directory, FileName);
    private readonly string _badFile = Path.Combine(directory, FileName + ".bad");
    private readonly string _tempFile = Path.Combine(directory, "." + FileName + ".tandem-part");

    public AppState Load(string homeDirectory)
    {
        lock (_gate)
        {
            var text = SettingsFile.ReadText(_file);
            if (text is null) return AppState.Default(homeDirectory);

            var state = Parse(text);
            if (state is not null) return state;

            SettingsFile.SetAside(_file, _badFile);
            return AppState.Default(homeDirectory);
        }
    }

    /// <exception cref="IOException">The directory or file cannot be written; the previous file is left intact.</exception>
    public void Save(AppState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, SettingsJsonContext.Format.AppState);
        lock (_gate) SettingsFile.WriteAtomically(directory, _file, _tempFile, bytes, "application state");
    }

    /// <summary>The parsed state, or null when the text is not a complete state this version understands.</summary>
    private static AppState? Parse(string text)
    {
        AppState? state;
        try
        {
            state = JsonSerializer.Deserialize<AppState>(text, SettingsJsonContext.Format.AppState);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }

        if (state is null || state.Version is < 1 or > AppState.CurrentVersion) return null;
        if (!IsValid(state.Left) || !IsValid(state.Right) || state.View is null) return null;

        var favorites = state.Favorites ?? [];
        if (favorites.Any(f => f is null)) return null;
        return state with { Favorites = favorites };
    }

    private static bool IsValid(PanelState? panel) =>
        panel is { Tabs.Count: > 0 } &&
        panel.ActiveTab >= 0 && panel.ActiveTab < panel.Tabs.Count &&
        panel.Tabs.All(t => t is not null && !string.IsNullOrEmpty(t.ProviderId) && !string.IsNullOrEmpty(t.Path));
}

/// <summary>Reading, setting aside and atomically replacing the app's own JSON files (<c>state.json</c>,
/// <c>settings.json</c>).</summary>
internal static class SettingsFile
{
    /// <summary>The file's text; null when it does not exist or cannot be read.</summary>
    public static string? ReadText(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Moves a file that cannot be used to <paramref name="badFile"/>, replacing an older one, if it can.</summary>
    public static void SetAside(string file, string badFile)
    {
        try
        {
            File.Move(file, badFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keeping the bad copy is a courtesy; the next Save replaces the file anyway.
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="tempFile"/> in <paramref name="directory"/>
    /// (created when missing), flushed to disk, and renames it over <paramref name="file"/>.</summary>
    /// <param name="what">What the file holds, for the message of the exception.</param>
    /// <exception cref="IOException">The directory or file cannot be written; the previous file is left intact.</exception>
    public static void WriteAtomically(string directory, string file, string tempFile, byte[] bytes, string what)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempFile, file, overwrite: true);
        }
        catch (IOException)
        {
            TryDelete(tempFile);
            throw;
        }
        catch (UnauthorizedAccessException e)
        {
            TryDelete(tempFile);
            throw new IOException($"Cannot save {what} to '{file}'.", e);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do: the stray temp file is overwritten by the next Save.
        }
    }
}
