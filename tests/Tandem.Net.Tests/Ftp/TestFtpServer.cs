using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tandem.Net.Tests.Ftp;

/// <summary>A loopback-only FTP fixture backed by a test-owned temporary directory.</summary>
internal sealed class TestFtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly List<Task> _handlers = [];
    private readonly Task _accept;
    private readonly string _root;
    private readonly bool _mlsd;
    private readonly Encoding _encoding;
    private long _dropNextDataAfterBytes = -1;

    public TestFtpServer(string root, bool mlsd = true, Encoding? encoding = null)
    {
        _root = Path.GetFullPath(root);
        _mlsd = mlsd;
        _encoding = encoding ?? new UTF8Encoding(false);
        _listener.Start();
        _accept = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public IReadOnlyList<string> Commands => _commands.ToArray();
    public int? StoreCompletionCode { get; set; }
    public bool HangAfterLogin { get; set; }
    public bool Mfmt { get; set; } = true;
    public bool RenameOverwrites { get; set; } = true;

    public void DropNextDataAfterBytes(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Interlocked.Exchange(ref _dropNextDataAfterBytes, bytes);
    }

    public void DropAllConnections()
    {
        foreach (var client in _clients.Keys) client.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.TryAdd(client, 0);
                _handlers.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        TcpListener? data = null;
        var cwd = "/";
        string? renameFrom = null;
        string? username = null;
        var loggedIn = false;
        var ct = _stop.Token;
        try
        {
            await using var connection = client.GetStream();
            using var reader = new StreamReader(connection, _encoding, false, leaveOpen: true);
            await using var writer = new StreamWriter(connection, _encoding, leaveOpen: true)
            { NewLine = "\r\n", AutoFlush = true };
            Task Reply(string message) => writer.WriteLineAsync(message.AsMemory(), ct);
            await Reply("220 Tandem test FTP");
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var split = line.IndexOf(' ');
                var command = (split < 0 ? line : line[..split]).ToUpperInvariant();
                var argument = split < 0 ? "" : line[(split + 1)..];
                _commands.Enqueue(command == "PASS" ? command : line);
                if (loggedIn && HangAfterLogin && command != "QUIT")
                    await Task.Delay(Timeout.Infinite, ct);

                if (!loggedIn && command is not ("USER" or "PASS" or "AUTH" or "FEAT" or "QUIT"))
                {
                    await Reply("530 Login required");
                    continue;
                }

                try
                {
                    switch (command)
                    {
                        case "USER":
                            username = argument;
                            loggedIn = false;
                            await Reply("331 Password required");
                            break;
                        case "PASS":
                            loggedIn = (username, argument) is ("user", "test-password") or ("anonymous", "");
                            await Reply(loggedIn ? "230 Logged in" : "530 Login incorrect");
                            break;
                        case "SYST": await Reply("215 UNIX Type: L8"); break;
                        case "FEAT":
                            await Reply("211-Features");
                            if (_encoding.CodePage == 65001) await Reply(" UTF8");
                            if (_mlsd)
                            {
                                await Reply(" MLST type*;size*;modify*;");
                                await Reply(" MLSD");
                            }
                            if (Mfmt) await Reply(" MFMT");
                            await Reply(" SIZE");
                            await Reply(" MDTM");
                            await Reply("211 End");
                            break;
                        case "OPTS": await Reply("200 Options accepted"); break;
                        case "PWD": await Reply($"257 \"{cwd}\""); break;
                        case "CWD":
                        case "CDUP":
                            var directory = Resolve(cwd, command == "CDUP" ? ".." : argument);
                            if (!Directory.Exists(directory)) { await Reply("550 No such directory"); break; }
                            var relative = Path.GetRelativePath(_root, directory);
                            cwd = relative == "." ? "/" : "/" + relative;
                            await Reply("250 Directory changed");
                            break;
                        case "TYPE": await Reply(argument == "I" ? "200 Binary" : "504 Binary only"); break;
                        case "PASV":
                        case "EPSV":
                            data?.Stop();
                            data = new TcpListener(IPAddress.Loopback, 0);
                            data.Start();
                            var port = ((IPEndPoint)data.LocalEndpoint).Port;
                            await Reply(command == "EPSV" ? $"229 Entering Extended Passive Mode (|||{port}|)" :
                                $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                            break;
                        case "LIST":
                        case "MLSD":
                        case "RETR":
                        case "STOR":
                            if (data is null) { await Reply("425 Passive connection required"); break; }
                            if (command == "MLSD" && !_mlsd) { await Reply("502 MLSD unavailable"); break; }
                            var transferPath = Resolve(cwd, argument.StartsWith("-a ", StringComparison.Ordinal)
                                ? argument[3..] : argument == "-a" ? "" : argument);
                            if ((command is "LIST" or "MLSD") && !Directory.Exists(transferPath) ||
                                command == "RETR" && !File.Exists(transferPath) ||
                                command == "STOR" && !Directory.Exists(Path.GetDirectoryName(transferPath)))
                            {
                                await Reply("550 No such file or directory");
                                break;
                            }
                            await Reply("150 Opening binary data connection");
                            var cutoff = command is "STOR" or "RETR"
                                ? Interlocked.Exchange(ref _dropNextDataAfterBytes, -1) : -1;
                            bool dropped;
                            using (var channel = await data.AcceptTcpClientAsync(ct))
                            {
                                await using var stream = channel.GetStream();
                                dropped = await TransferAsync(command, transferPath, stream, cutoff, ct);
                                if (dropped) channel.Client.Close(0);
                            }
                            data.Stop();
                            data = null;
                            var code = dropped ? 426 :
                                command == "STOR" ? StoreCompletionCode ?? 226 : 226;
                            await Reply(code == 226 ? "226 Transfer complete" : $"{code} Transfer failed");
                            break;
                        case "MLST":
                            var item = Resolve(cwd, argument);
                            if (!_mlsd) { await Reply("502 MLST unavailable"); break; }
                            if (!File.Exists(item) && !Directory.Exists(item)) { await Reply("550 No such file"); break; }
                            await Reply("250-Object");
                            await Reply(" " + Facts(item, argument));
                            await Reply("250 End");
                            break;
                        case "SIZE":
                            var sized = new FileInfo(Resolve(cwd, argument));
                            await Reply(sized.Exists ? $"213 {sized.Length}" : "550 No such file");
                            break;
                        case "MDTM":
                            var dated = Resolve(cwd, argument);
                            await Reply(File.Exists(dated) || Directory.Exists(dated)
                                ? $"213 {Stamp(File.GetLastWriteTimeUtc(dated))}" : "550 No such file");
                            break;
                        case "MFMT":
                            if (!Mfmt) { await Reply("502 MFMT unavailable"); break; }
                            var timeEnd = argument.IndexOf(' ');
                            var timestamp = DateTime.ParseExact(argument[..timeEnd], "yyyyMMddHHmmss",
                                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                            var update = Resolve(cwd, argument[(timeEnd + 1)..]);
                            if (!File.Exists(update) && !Directory.Exists(update)) { await Reply("550 No such file"); break; }
                            File.SetLastWriteTimeUtc(update, timestamp);
                            await Reply("213 Modified");
                            break;
                        case "DELE":
                            var remove = Resolve(cwd, argument);
                            if (!File.Exists(remove)) { await Reply("550 No such file"); break; }
                            File.Delete(remove);
                            await Reply("250 Deleted");
                            break;
                        case "MKD":
                            var create = Resolve(cwd, argument);
                            if (!Directory.Exists(Path.GetDirectoryName(create))) { await Reply("550 No such directory"); break; }
                            Directory.CreateDirectory(create);
                            await Reply("257 Directory created");
                            break;
                        case "RMD":
                            Directory.Delete(Resolve(cwd, argument));
                            await Reply("250 Removed");
                            break;
                        case "RNFR":
                            renameFrom = Resolve(cwd, argument);
                            if (!File.Exists(renameFrom) && !Directory.Exists(renameFrom))
                            {
                                renameFrom = null;
                                await Reply("550 No such file");
                                break;
                            }
                            await Reply("350 Ready for destination");
                            break;
                        case "RNTO":
                            if (renameFrom is null) { await Reply("503 RNFR required"); break; }
                            var source = renameFrom;
                            renameFrom = null;
                            var target = Resolve(cwd, argument);
                            if (Directory.Exists(source)) Directory.Move(source, target);
                            else File.Move(source, target, RenameOverwrites);
                            await Reply("250 Renamed");
                            break;
                        case "REST": await Reply(argument == "0" ? "350 Restart at zero" : "504 Offset unsupported"); break;
                        case "NOOP": await Reply("200 OK"); break;
                        case "QUIT": await Reply("221 Bye"); return;
                        default: await Reply("502 Command not implemented"); break;
                    }
                }
                catch (DirectoryNotFoundException) { await Reply("550 No such directory"); }
                catch (FileNotFoundException) { await Reply("550 No such file"); }
                catch (UnauthorizedAccessException) { await Reply("550 Permission denied"); }
                catch (IOException) { await Reply("550 Requested action unavailable"); }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
        finally
        {
            data?.Stop();
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    private async Task<bool> TransferAsync(string command, string path, NetworkStream stream, long cutoff,
        CancellationToken ct)
    {
        if (command == "STOR")
        {
            await using var file = File.Create(path);
            return await CopyAsync(stream, file, cutoff, ct);
        }
        else if (command == "RETR")
        {
            await using var file = File.OpenRead(path);
            return await CopyAsync(file, stream, cutoff, ct);
        }
        else
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(path))
            {
                var name = Path.GetFileName(item);
                var info = new FileInfo(item);
                var mode = Directory.Exists(item) ? "drwxr-xr-x" : "-rw-r--r--";
                var length = Directory.Exists(item) ? 0 : info.Length;
                var time = info.LastWriteTimeUtc.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
                var line = command == "MLSD" ? Facts(item, name) : $"{mode} 1 owner group {length} {time} {name}";
                await stream.WriteAsync(_encoding.GetBytes(line + "\r\n"), ct);
            }
            return false;
        }
    }

    private static async Task<bool> CopyAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        if (limit < 0)
        {
            await input.CopyToAsync(output, ct);
            return false;
        }
        var buffer = new byte[8192];
        var remaining = limit;
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
            if (count == 0) return false;
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            remaining -= count;
        }
        return true;
    }

    private string Resolve(string cwd, string argument)
    {
        var relative = argument.StartsWith('/') ? argument.TrimStart('/') : cwd.TrimStart('/') + "/" + argument;
        var full = Path.GetFullPath(Path.Combine(_root, relative.TrimStart('/')));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException();
        // The fixture does not serve symlinks pointing outside its storage.
        for (var parent = full; parent != _root; parent = Path.GetDirectoryName(parent)!)
            if (new FileInfo(parent).LinkTarget is not null || new DirectoryInfo(parent).LinkTarget is not null)
                throw new UnauthorizedAccessException();
        return full;
    }

    private static string Facts(string path, string name)
    {
        var directory = Directory.Exists(path);
        var length = directory ? 0 : new FileInfo(path).Length;
        return $"type={(directory ? "dir" : "file")};size={length};modify={Stamp(File.GetLastWriteTimeUtc(path))}; {name}";
    }

    private static string Stamp(DateTime time) => time.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _accept;
        _listener.Stop();
        DropAllConnections();
        await Task.WhenAll(_handlers);
        _stop.Dispose();
    }
}
