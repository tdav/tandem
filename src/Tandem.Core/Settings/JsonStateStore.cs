using System.Text.Json;
using System.Text.Json.Serialization;

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

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly Lock _gate = new();
    private readonly string _file = Path.Combine(directory, FileName);
    private readonly string _badFile = Path.Combine(directory, FileName + ".bad");
    private readonly string _tempFile = Path.Combine(directory, "." + FileName + ".tandem-part");

    public AppState Load(string homeDirectory)
    {
        lock (_gate)
        {
            string text;
            try
            {
                if (!File.Exists(_file)) return AppState.Default(homeDirectory);
                text = File.ReadAllText(_file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return AppState.Default(homeDirectory);
            }

            var state = Parse(text);
            if (state is not null) return state;

            SetAsideBadFile();
            return AppState.Default(homeDirectory);
        }
    }

    /// <exception cref="IOException">The directory or file cannot be written; the previous file is left intact.</exception>
    public void Save(AppState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(_tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(_tempFile, _file, overwrite: true);
            }
            catch (IOException)
            {
                TryDelete(_tempFile);
                throw;
            }
            catch (UnauthorizedAccessException e)
            {
                TryDelete(_tempFile);
                throw new IOException($"Cannot save application state to '{_file}'.", e);
            }
        }
    }

    /// <summary>The parsed state, or null when the text is not a complete state this version understands.</summary>
    private static AppState? Parse(string text)
    {
        AppState? state;
        try
        {
            state = JsonSerializer.Deserialize<AppState>(text, Options);
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

    private void SetAsideBadFile()
    {
        try
        {
            File.Move(_file, _badFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keeping the bad copy is a courtesy; the next Save replaces the file anyway.
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
