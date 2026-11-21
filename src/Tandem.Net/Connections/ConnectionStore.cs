using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tandem.Net.Connections;

/// <summary>Persists nonsecret connection profile fields, replacing the file atomically.</summary>
public sealed class ConnectionStore(string directory)
{
    public const string FileName = "connections.json";

    private readonly Lock _gate = new();
    private readonly ConnectionFile<Document> _file = new(directory, FileName);

    public IReadOnlyList<ConnectionProfile> Load()
    {
        lock (_gate)
        {
            var document = _file.Load(value => value is { Version: 1, Profiles: not null });
            if (document is null) return [];

            return document.Profiles.Where(IsValid).DistinctBy(profile => profile.Id)
                .Select(profile => profile.Normalized()).ToArray();
        }
    }

    public void Save(IReadOnlyList<ConnectionProfile> profiles)
    {
        lock (_gate) _file.Save(new Document(1, profiles));
    }

    private static bool IsValid(ConnectionProfile profile) => profile is not null &&
        profile.Id != Guid.Empty && !string.IsNullOrWhiteSpace(profile.Host) &&
        profile.Name is not null && profile.User is not null &&
        Enum.IsDefined(profile.Protocol) && Enum.IsDefined(profile.Auth);

    private sealed record Document(int Version, IReadOnlyList<ConnectionProfile> Profiles);
}

/// <summary>Reads and atomically replaces the application-owned connection and trust files.</summary>
internal sealed class ConnectionFile<T>(string directory, string fileName) where T : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private readonly string _path = Path.Combine(directory, fileName);

    public T? Load(Func<T, bool> validate)
    {
        try
        {
            using var file = File.OpenRead(_path);
            if (JsonSerializer.Deserialize<T>(file, Options) is { } data && validate(data)) return data;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            // Preserve malformed JSON for diagnosis below.
        }

        try
        {
            File.Move(_path, _path + ".bad", overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A read-only settings directory cannot set aside a bad file.
        }
        return null;
    }

    public void Save(T document)
    {
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tandem-part");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, document, Options);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Keep the original serialization or filesystem error.
            }
        }
    }
}
