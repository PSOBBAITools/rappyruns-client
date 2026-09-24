using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace RappyRuns.Tests.PinShare;

/// <summary>
/// A local stand-in for the Pin Share relay server: a TcpListener that does
/// the WebSocket handshake by hand (no http.sys URL ACL needed) and hands the
/// stream to <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>.
/// Records every text message received; tests push state snapshots back.
/// </summary>
internal sealed class FakeRelayServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<WebSocket> _clients = [];
    private readonly object _lock = new();

    public FakeRelayServer()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    public string Url => $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

    public ConcurrentQueue<string> Received { get; } = new();

    public int Connections { get; private set; }

    public async Task BroadcastAsync(string text)
    {
        WebSocket[] clients;
        lock (_lock) clients = [.. _clients.Where(c => c.State == WebSocketState.Open)];
        foreach (var client in clients)
            await client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>Drops every connection abruptly (the relay must reconnect).</summary>
    public void DropAll()
    {
        lock (_lock)
        {
            foreach (var client in _clients) client.Abort();
            _clients.Clear();
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Serve(tcp);
        }
    }

    private async Task Serve(TcpClient tcp)
    {
        var stream = tcp.GetStream();
        var request = new StringBuilder();
        var one = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0) return;
            request.Append((char)one[0]);
        }
        var key = request.ToString().Split("\r\n")
            .First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                       + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        lock (_lock)
        {
            _clients.Add(socket);
            Connections++;
        }
        var buffer = new byte[65536];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                Received.Enqueue(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        }
        catch (Exception)
        {
            // Dropped.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        DropAll();
    }
}
