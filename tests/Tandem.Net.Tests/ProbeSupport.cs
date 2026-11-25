using System.Net;
using System.Net.Sockets;

namespace Tandem.Net.Tests;

/// <summary>Loopback helpers for the library probes: nothing here listens or connects outside 127.0.0.1.</summary>
internal static class ProbeSupport
{
    public static string KeyPath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "keys", name);

    /// <summary>A port on 127.0.0.1 that refuses connections: the system hands one out, it is closed again, and a
    /// connect to it is seen refused before it is returned (up to five fresh ports, against another process taking it
    /// in between). A socket bound without listening does not do: on macOS a connect to it hangs instead of being
    /// refused.</summary>
    public static int ClosedPort()
    {
        for (var attempt = 0; ; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (IsRefused(port)) return port;
            if (attempt == 4) throw new InvalidOperationException("No closed port found on 127.0.0.1.");
        }
    }

    private static bool IsRefused(int port)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Windows retries a refused loopback connect for about two seconds before it reports the refusal.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 6 : 2));
        try
        {
            probe.ConnectAsync(IPAddress.Loopback, port, timeout.Token).AsTask().GetAwaiter().GetResult();
            return false;
        }
        catch (SocketException e)
        {
            return e.SocketErrorCode == SocketError.ConnectionRefused;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
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
