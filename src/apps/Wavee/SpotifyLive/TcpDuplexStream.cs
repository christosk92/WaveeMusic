using System.IO;
using System.Net.Sockets;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// The real socket behind the backend's IDuplexStream seam — a TCP connection to a Spotify access point. The backend's
// SpotifyApChannel/ApCodec are socket-agnostic; this is the live wire.
public sealed class TcpDuplexStream : IDuplexStream, IDisposable
{
    // per-op backstop: a half-open socket can't hang forever. The login handshake uses 30s (request/response); the
    // persistent AP channel (Stage F) passes a longer idle timeout so an idle read survives between the AP's ping packets.
    readonly TimeSpan _opTimeout;

    readonly Socket _socket;
    readonly NetworkStream _stream;

    TcpDuplexStream(Socket socket, TimeSpan opTimeout)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _opTimeout = opTimeout;
    }

    public static async Task<TcpDuplexStream> ConnectAsync(string host, int port, CancellationToken ct, TimeSpan? opTimeout = null)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var timeout = opTimeout ?? TimeSpan.FromSeconds(30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));   // connect always bounded at 30s
        await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        return new TcpDuplexStream(socket, timeout);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opTimeout);
        await _stream.WriteAsync(data, cts.Token).ConfigureAwait(false);
        await _stream.FlushAsync(cts.Token).ConfigureAwait(false);
    }

    public async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_opTimeout);   // even under CancellationToken.None, a stalled read times out
            int n = await _stream.ReadAsync(buffer.Slice(filled), cts.Token).ConfigureAwait(false);
            if (n == 0) throw new IOException("connection closed by peer");
            filled += n;
        }
    }

    public void Dispose() { _stream.Dispose(); _socket.Dispose(); }
}
