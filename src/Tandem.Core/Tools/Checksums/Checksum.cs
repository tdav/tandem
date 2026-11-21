using System.Buffers;
using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace Tandem.Core.Tools.Checksums;

public enum ChecksumAlgorithm { Crc32, Md5, Sha1, Sha256 }

public static class Checksum
{
    public static string Extension(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => "sfv",
        ChecksumAlgorithm.Md5 => "md5",
        ChecksumAlgorithm.Sha1 => "sha1",
        ChecksumAlgorithm.Sha256 => "sha256",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    public static ChecksumAlgorithm? FromFileName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".sfv" => ChecksumAlgorithm.Crc32,
        ".md5" => ChecksumAlgorithm.Md5,
        ".sha1" => ChecksumAlgorithm.Sha1,
        ".sha256" => ChecksumAlgorithm.Sha256,
        _ => null,
    };

    internal static int HexLength(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => 8,
        ChecksumAlgorithm.Md5 => 32,
        ChecksumAlgorithm.Sha1 => 40,
        ChecksumAlgorithm.Sha256 => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    public static async Task<string> ComputeAsync(Stream stream, ChecksumAlgorithm algorithm, IProgress<long>? bytes,
        Func<CancellationToken, ValueTask>? pause, CancellationToken ct)
    {
        const int blockSize = 1024 * 1024;
        var crc = algorithm == ChecksumAlgorithm.Crc32 ? new Crc32() : null;
        using var hash = algorithm switch
        {
            ChecksumAlgorithm.Crc32 => null,
            ChecksumAlgorithm.Md5 => IncrementalHash.CreateHash(HashAlgorithmName.MD5),
            ChecksumAlgorithm.Sha1 => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
            ChecksumAlgorithm.Sha256 => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
        var buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (pause is not null) await pause(ct).ConfigureAwait(false);
                var size = await stream.ReadAsync(buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false);
                if (size == 0) break;
                if (crc is not null) crc.Append(buffer.AsSpan(0, size));
                else hash!.AppendData(buffer, 0, size);
                bytes?.Report(size);
            }
            return crc is null
                ? Convert.ToHexStringLower(hash!.GetHashAndReset())
                : crc.GetCurrentHashAsUInt32().ToString("x8", CultureInfo.InvariantCulture);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
