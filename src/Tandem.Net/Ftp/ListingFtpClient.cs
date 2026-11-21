using FluentFTP;
using FluentFTP.Exceptions;
using FluentFTP.Helpers;

namespace Tandem.Net.Ftp;

/// <summary>Reads directory data without treating an FTP permission or transfer failure as an empty directory.</summary>
internal sealed class ListingFtpClient(string host, string user, string password, int port, FtpConfig config)
    : AsyncFtpClient(host, user, password, port, config)
{
    public async Task<FtpListItem[]> ListStrictlyAsync(string directory, CancellationToken ct)
    {
        var machine = HasFeature(FtpCapability.MLST);
        await SetDataTypeAsync(FtpDataType.Binary, ct).ConfigureAwait(false);
        var lines = new List<string>();
        await using (var data = await OpenDataStreamAsync((machine ? "MLSD " : "LIST -a ") + directory, 0, ct)
            .ConfigureAwait(false))
        {
            data.CommandStatus = new FtpReply();
            using var reader = new StreamReader(data, Encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
        }

        var result = await GetReply(ct).ConfigureAwait(false);
        if (!result.Success || !result.Code.StartsWith('2')) throw new FtpCommandException(result);
        if (lines.Count == 0) return [];

        var parser = new FtpListParser(this);
        parser.Init(ServerOS, Config.ListingParser);
        var entries = new List<FtpListItem>();
        foreach (var line in lines)
        {
            if (!machine && line.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) continue;
            ct.ThrowIfCancellationRequested();
            var file = parser.ParseSingleLine(directory, line, Capabilities, machine);
            if (file is null) throw new FtpException("Unrecognized FTP directory listing entry.");
            entries.Add(file);
        }
        return entries.ToArray();
    }
}
