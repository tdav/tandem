using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tandem.Net.Connections;

[JsonSerializable(typeof(ConnectionStore.Document), TypeInfoPropertyName = "Connections")]
[JsonSerializable(typeof(TrustStore.Document), TypeInfoPropertyName = "Trust")]
internal sealed partial class ConnectionJsonContext : JsonSerializerContext
{
    internal static ConnectionJsonContext Format { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<NetProtocol>(allowIntegerValues: false),
            new JsonStringEnumConverter<NetAuth>(allowIntegerValues: false),
        },
    });
}
