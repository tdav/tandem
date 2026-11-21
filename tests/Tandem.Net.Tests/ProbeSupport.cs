using System.Net;
using System.Net.Sockets;

namespace Tandem.Net.Tests;

/// <summary>Loopback helpers for the library probes: nothing here listens or connects outside 127.0.0.1.</summary>
internal static class ProbeSupport
{
    public static string KeyPath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "keys", name);

    /// <summary>A port on 127.0.0.1 that nothing listens on: the system hands it out, and it is closed again.</summary>
    public static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>A listener on 127.0.0.1 (system port) that accepts each connection and either closes it at once or holds
/// it open without saying anything.</summary>
internal sealed class LoopbackListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<Socket> _held = [];
    private readonly CancellationTokenSource _stop = new();

    public LoopbackListener(bool hangUp)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync(hangUp);
    }

    public int Port { get; }

    private async Task AcceptAsync(bool hangUp)
    {
        try
        {
            while (true)
            {
                var socket = await _listener.AcceptSocketAsync(_stop.Token);
                if (hangUp) socket.Close();
                else lock (_held) _held.Add(socket);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_held)
            foreach (var socket in _held) socket.Dispose();
        _stop.Dispose();
    }
}
