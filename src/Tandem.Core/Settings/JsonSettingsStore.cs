using System.Text.Json;

namespace Tandem.Core.Settings;

/// <summary>
/// Keeps <see cref="AppSettings"/> in <c>settings.json</c> inside <paramref name="directory"/>, by the rules of
/// <see cref="JsonStateStore"/>. <see cref="Load"/> never throws: a missing or unreadable file yields
/// <see cref="AppSettings.Default"/>; a file that is not JSON, has a <see cref="AppSettings.Version"/> outside
/// <c>1…<see cref="AppSettings.CurrentVersion"/></c> or names an unknown enumeration value is kept aside as
/// <c>settings.json.bad</c> and yields the defaults; anything else is repaired by <see cref="AppSettings.Normalized"/>.
/// <see cref="Save"/> replaces the file atomically. Calls on one instance are serialised, so it may be used from
/// several threads.
/// </summary>
public sealed class JsonSettingsStore(string directory)
{
    private const string FileName = "settings.json";

    private readonly Lock _gate = new();
    private readonly string _file = Path.Combine(directory, FileName);
    private readonly string _badFile = Path.Combine(directory, FileName + ".bad");
    private readonly string _tempFile = Path.Combine(directory, "." + FileName + ".tandem-part");

    public AppSettings Load()
    {
        lock (_gate)
        {
            var text = SettingsFile.ReadText(_file);
            if (text is null) return AppSettings.Default;

            var settings = Parse(text);
            if (settings is not null) return settings;

            SettingsFile.SetAside(_file, _badFile);
            return AppSettings.Default;
        }
    }

    /// <exception cref="IOException">The directory or file cannot be written; the previous file is left intact.</exception>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Format.AppSettings);
        lock (_gate) SettingsFile.WriteAtomically(directory, _file, _tempFile, bytes, "settings");
    }

    /// <summary>The settings read and repaired, or null when the text is not settings this version understands.</summary>
    private static AppSettings? Parse(string text)
    {
        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(text, SettingsJsonContext.Format.AppSettings);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }

        if (settings is null || settings.Version is < 1 or > AppSettings.CurrentVersion) return null;
        return settings.Normalized();
    }
}
