using System.Text.Json;
using System.Text.Json.Serialization;
using Tandem.Core.Listing;

namespace Tandem.Core.Settings;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppState))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
    internal static SettingsJsonContext Format { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<ThemeChoice>(allowIntegerValues: false),
            new JsonStringEnumConverter<ColumnId>(allowIntegerValues: false),
            new JsonStringEnumConverter<TerminalChoice>(allowIntegerValues: false),
            new JsonStringEnumConverter<SortColumn>(allowIntegerValues: false),
            new JsonStringEnumConverter<ListMode>(allowIntegerValues: false),
        },
    });
}
