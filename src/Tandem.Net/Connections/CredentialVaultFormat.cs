using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tandem.Net.Connections;

internal static partial class CredentialVaultFormat
{
    private static ReadOnlySpan<byte> Magic => "TANDVLT1"u8;

    internal const int SaltSize = 16;
    private const int SaltOffset = 8;
    private const int NonceOffset = SaltOffset + SaltSize;
    private const int NonceSize = 12;
    private const int TagOffset = NonceOffset + NonceSize;
    private const int TagSize = 16;
    private const int PayloadOffset = TagOffset + TagSize;
    internal const int MaxFileBytes = 32 * 1024 * 1024;
    private const int MaxPayloadBytes = MaxFileBytes - PayloadOffset;
    private const int MaxEncodedKeyBytes = (CredentialVault.MaxKeyBytes + 2) / 3 * 4;
    private static readonly UTF8Encoding PasswordEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly VaultJsonContext Format = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter<NetProtocol>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<NetAuth>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new PrivateKeyConverter(),
        },
    });

    private sealed record Payload(VaultEntry[] Entries);

    [JsonSerializable(typeof(Payload))]
    [JsonSerializable(typeof(byte[]))]
    private sealed partial class VaultJsonContext : JsonSerializerContext;

    internal static void ValidateFileLength(long length)
    {
        if (length is <= PayloadOffset or > MaxFileBytes)
            throw new CredentialVaultException("The vault has an invalid file size.");
    }

    internal static byte[] ReadSalt(ReadOnlySpan<byte> data)
    {
        if (!data[..SaltOffset].SequenceEqual(Magic))
            throw new CredentialVaultException("The vault header is invalid.");
        return data.Slice(SaltOffset, SaltSize).ToArray();
    }

    internal static byte[] DeriveKey(string password, byte[] salt)
    {
        var utf8 = PasswordEncoding.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(utf8, salt, 600_000, HashAlgorithmName.SHA256, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    internal static byte[] Encrypt(VaultEntry[] entries, byte[] key, byte[] salt)
    {
        ValidateEntries(entries);
        using var plaintext = new PayloadStream();
        JsonSerializer.Serialize(plaintext, new Payload(entries), Format.Payload);
        var data = new byte[PayloadOffset + (int)plaintext.Length];
        Magic.CopyTo(data);
        salt.CopyTo(data, SaltOffset);
        var nonce = data.AsSpan(NonceOffset, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var cipher = new AesGcm(key, TagSize);
        cipher.Encrypt(nonce, plaintext.GetBuffer().AsSpan(0, (int)plaintext.Length), data.AsSpan(PayloadOffset),
            data.AsSpan(TagOffset, TagSize), data.AsSpan(0, TagOffset));
        return data;
    }

    internal static VaultEntry[] Decrypt(byte[] data, byte[] key)
    {
        var plaintext = new byte[data.Length - PayloadOffset];
        var entries = new List<VaultEntry>();
        var ids = new HashSet<Guid>();
        var success = false;
        try
        {
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(data.AsSpan(NonceOffset, NonceSize), data.AsSpan(PayloadOffset),
                data.AsSpan(TagOffset, TagSize), plaintext, data.AsSpan(0, TagOffset));

            using var json = JsonDocument.Parse(plaintext, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 8 });
            var root = json.RootElement;
            if (root.GetPropertyCount() != 1) throw InvalidPayload();
            foreach (var element in root.GetProperty("entries").EnumerateArray())
            {
                if (element.GetPropertyCount() != 4) throw InvalidPayload();
                var profile = ReadProfile(element.GetProperty("profile"));
                if (!ids.Add(profile.Id)) throw InvalidPayload();

                var password = element.GetProperty("password").GetString();
                var passphrase = element.GetProperty("passphrase").GetString();
                var privateKey = element.GetProperty("privateKey").Deserialize(Format.ByteArray);
                entries.Add(new VaultEntry(profile, password, privateKey, passphrase));
            }

            var result = entries.ToArray();
            success = true;
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw InvalidPayload();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (!success) CredentialVault.ClearEntries(entries);
        }
    }

    internal static void ValidateEntries(IReadOnlyList<VaultEntry> entries)
    {
        var ids = new HashSet<Guid>();
        long minimumLength = 0;
        foreach (var entry in entries)
        {
            ValidateEntry(entry);
            if (!ids.Add(entry.Profile.Id)) throw InvalidPayload();

            // Reject oversized collections before copying keys or creating a serialization buffer.
            var profile = entry.Profile;
            minimumLength += 54L + profile.Name.Length + profile.Host.Length + profile.User.Length +
                profile.InitialPath.Length + (profile.PrivateKeyPath?.Length ?? 0) + (profile.NameEncoding?.Length ?? 0) +
                (entry.Password?.Length ?? 0) + (entry.Passphrase?.Length ?? 0) +
                ((entry.PrivateKey?.Length ?? 0) + 2) / 3 * 4;
            if (minimumLength > MaxPayloadBytes) throw TooLarge();
        }
    }

    internal static void ValidateEntry(VaultEntry entry)
    {
        if (entry is null) throw InvalidPayload();
        ValidateProfile(entry.Profile);
        if (entry.PrivateKey is { Length: > CredentialVault.MaxKeyBytes })
            throw new CredentialVaultException("A saved private key is too large.");
    }

    private static ConnectionProfile ReadProfile(JsonElement element)
    {
        if (element.GetPropertyCount() != 11) throw InvalidPayload();
        var profile = element.Deserialize(Format.ConnectionProfile);
        ValidateProfile(profile);

        // The framework enum converter accepts case-insensitive names. V1 defines exact spellings.
        if (!element.GetProperty("protocol").ValueEquals(JsonNamingPolicy.CamelCase.ConvertName(profile.Protocol.ToString())) ||
            !element.GetProperty("auth").ValueEquals(JsonNamingPolicy.CamelCase.ConvertName(profile.Auth.ToString())))
            throw InvalidPayload();
        return profile;
    }

    private static void ValidateProfile([NotNull] ConnectionProfile? profile)
    {
        if (profile is null || profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Host) ||
            profile.Name is null || profile.User is null || profile.InitialPath is null || profile.Port is < 1 or > 65535 ||
            !Enum.IsDefined(profile.Protocol) || !Enum.IsDefined(profile.Auth))
            throw InvalidPayload();
    }

    private static CredentialVaultException InvalidPayload() => new("The vault contains an invalid connection record.");

    private static CredentialVaultException TooLarge() => new("The vault exceeds the supported file size.");

    private sealed class PrivateKeyConverter : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw InvalidPayload();
            if (!reader.ValueIsEscaped) return Decode(reader.ValueSpan);

            var buffer = ArrayPool<byte>.Shared.Rent(MaxEncodedKeyBytes);
            try
            {
                var length = reader.CopyString(buffer.AsSpan(0, MaxEncodedKeyBytes));
                return Decode(buffer.AsSpan(0, length));
            }
            catch (ArgumentException)
            {
                throw InvalidPayload();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private static byte[] Decode(ReadOnlySpan<byte> encoded)
        {
            if (encoded.Length > MaxEncodedKeyBytes ||
                !Base64.IsValid(encoded, out var length) || length > CredentialVault.MaxKeyBytes)
                throw InvalidPayload();

            var key = new byte[length];
            Base64.DecodeFromUtf8(encoded, key, out _, out _);
            return key;
        }

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
            writer.WriteBase64StringValue(value);
    }

    // Bound the plaintext buffer even when JSON escaping expands the input. Clear each backing array after use.
    private sealed class PayloadStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var end = Position + buffer.Length;
            if (end > MaxPayloadBytes) throw TooLarge();
            if (end > Capacity)
            {
                var previous = GetBuffer();
                Capacity = Math.Min(MaxPayloadBytes, Math.Max((int)end, Math.Max(4096, Capacity * 2)));
                CryptographicOperations.ZeroMemory(previous);
            }

            if (end > Length) SetLength(end);
            buffer.CopyTo(GetBuffer().AsSpan((int)Position));
            Position = end;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CryptographicOperations.ZeroMemory(GetBuffer());
            base.Dispose(disposing);
        }
    }
}
