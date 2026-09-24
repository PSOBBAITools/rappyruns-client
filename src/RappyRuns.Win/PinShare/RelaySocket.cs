using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace RappyRuns.Win.PinShare;

/// <summary>
/// The relay server connection: <see cref="ClientWebSocket"/> with the
/// semantics of the Lisp client's WinHTTP WebSocket (websocket-win32.lisp,
/// spec core.md §18). One thread may wait in <see cref="ReceiveAsync"/>
/// while another sends; <see cref="Close"/> from any thread is idempotent
/// and makes a pending receive return null. Pings are answered by the
/// runtime, and its own keep-alive pings (30 s) turn a dead connection into
/// a receive failure, so receives have no timeout.
/// </summary>
public sealed class RelaySocket : IDisposable
{
    /// <summary>Upper bound on one reassembled message. State snapshots are a few KB; anything near this is a broken or hostile peer.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Bounds a send: the relay sends from the thread that owes the addon a
    /// heartbeat every second.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Bounds the whole handshake: a host that accepts TCP but stalls TLS or the upgrade must fail, not hang.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private const int ReceiveChunk = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _closed;

    private RelaySocket(ClientWebSocket socket) => _socket = socket;

    /// <summary>
    /// Opens <paramref name="url"/> (ws:// or wss://). Throws on a transport
    /// failure or a refused upgrade; the message is what the relay shows
    /// after "connect failed: ".
    /// </summary>
    public static async Task<RelaySocket> ConnectAsync(string url, string userAgent = "ephinea-ta-client", CancellationToken cancellationToken = default)
    {
        if (!url.StartsWith("ws://", StringComparison.Ordinal) && !url.StartsWith("wss://", StringComparison.Ordinal))
            throw new ArgumentException($"Bad URL: {url}");
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.KeepAliveInterval = KeepAlive;
            socket.Options.KeepAliveTimeout = KeepAlive;
            socket.Options.SetRequestHeader("User-Agent", userAgent);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("WebSocket connect timed out");
            }
            return new RelaySocket(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Sends <paramref name="text"/> as one UTF-8 text message, serialized
    /// with other sends and with <see cref="Close"/>. Throws when the socket
    /// is closed or the send fails / exceeds <see cref="SendTimeout"/>.
    /// </summary>
    public void SendText(string text)
    {
        if (!_sendLock.Wait(SendTimeout)) throw new TimeoutException("WebSocket send timed out");
        try
        {
            if (IsClosed) throw new WebSocketException("WebSocket is closed");
            using var timeout = new CancellationTokenSource(SendTimeout);
            _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, timeout.Token)
                .GetAwaiter().GetResult();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Waits for one whole text message; null when the peer closed, the
    /// connection died, the message grew past <see cref="MaxMessageBytes"/>
    /// or <see cref="Close"/> cancelled the wait. Binary messages and text
    /// that is not valid UTF-8 are skipped: one bad message must not end the
    /// caller's receive loop.
    /// </summary>
    public async Task<string?> ReceiveAsync()
    {
        var chunk = ArrayPool<byte>.Shared.Rent(ReceiveChunk);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(chunk, 0, ReceiveChunk), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException or IOException or InvalidOperationException)
                {
                    return null;
                }
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (message.Length + result.Count > MaxMessageBytes) return null;
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    // Binary: not ours; drop what was gathered and wait for the next one.
                    message.SetLength(0);
                    continue;
                }
                message.Write(chunk, 0, result.Count);
                if (!result.EndOfMessage) continue;
                try
                {
                    return StrictUtf8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
                catch (DecoderFallbackException)
                {
                    message.SetLength(0);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>
    /// Closes from any thread, exactly once: a best-effort close frame (1000),
    /// then abort - which is what actually makes a pending receive return.
    /// Never waits for the peer's close (a receive blocked on another thread
    /// would swallow it).
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        if (_sendLock.Wait(SendTimeout))
        {
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).GetAwaiter().GetResult();
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or InvalidOperationException)
            {
                // Best effort.
            }
            finally
            {
                _sendLock.Release();
            }
        }
        _socket.Abort();
        _socket.Dispose();
    }

    public void Dispose() => Close();
}
