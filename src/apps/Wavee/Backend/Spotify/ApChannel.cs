using System.Buffers.Binary;
using System.Threading.Channels;

namespace Wavee.Backend.Spotify;

// ── The encrypted AP channel — assembles DiffieHellman + ApKeyDerivation + ApCodec over a duplex byte stream ──────────
// This is ③ Transport's AP channel. The socket is behind an IDuplexStream seam so the full handshake + encrypted exchange
// is unit-testable end-to-end against a simulated peer (no TCP). NOTE: the handshake's outer framing is faithful
// (ClientHello [0x00,0x04][size][payload]; APResponse [size][payload]); the *payload* here is simplified to the raw DH
// public key — the real wire wraps it in the ClientHello/APResponse protobuf. The DH exchange, the HMAC-SHA1 key
// derivation, and the Shannon packet codec are the real, tested mechanics; the live socket + protobuf is the last step.

public interface IDuplexStream
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);          // ValueTask: no wrapper Task alloc on synchronous completion
    ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken ct);
}

/// <summary>A connected in-memory duplex pair (a↔b) for testing the channel without a socket.</summary>
public sealed class InMemoryDuplex : IDuplexStream
{
    readonly ChannelWriter<byte[]> _out;
    readonly ChannelReader<byte[]> _in;
    byte[] _leftover = Array.Empty<byte>();
    int _leftoverPos;

    InMemoryDuplex(ChannelWriter<byte[]> outw, ChannelReader<byte[]> inr) { _out = outw; _in = inr; }

    public static (IDuplexStream A, IDuplexStream B) Pair()
    {
        var ab = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var ba = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        return (new InMemoryDuplex(ab.Writer, ba.Reader), new InMemoryDuplex(ba.Writer, ab.Reader));
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        _out.TryWrite(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public async ValueTask ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            if (_leftoverPos >= _leftover.Length) { _leftover = await _in.ReadAsync(ct).ConfigureAwait(false); _leftoverPos = 0; }
            int take = Math.Min(buffer.Length - filled, _leftover.Length - _leftoverPos);
            _leftover.AsSpan(_leftoverPos, take).CopyTo(buffer.Span.Slice(filled, take));
            _leftoverPos += take;
            filled += take;
        }
    }
}

public sealed class SpotifyApChannel
{
    readonly IDuplexStream _stream;
    readonly ApCodec _codec;

    SpotifyApChannel(IDuplexStream stream, ApCodec codec) { _stream = stream; _codec = codec; }

    public static async Task<SpotifyApChannel> ConnectClientAsync(IDuplexStream stream, CancellationToken ct = default)
    {
        var dh = DiffieHellman.Generate();
        var acc = new List<byte>();

        var hello = FrameHello(dh.PublicKey);
        acc.AddRange(hello);
        await stream.WriteAsync(hello, ct).ConfigureAwait(false);

        var sizeBuf = new byte[4];
        await stream.ReadExactAsync(sizeBuf, ct).ConfigureAwait(false);
        acc.AddRange(sizeBuf);
        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(sizeBuf);
        var serverPub = new byte[size - 4];
        await stream.ReadExactAsync(serverPub, ct).ConfigureAwait(false);
        acc.AddRange(serverPub);

        var keys = ApKeyDerivation.Derive(dh.SharedSecret(serverPub), acc.ToArray());
        return new SpotifyApChannel(stream, new ApCodec(keys.SendKey, keys.ReceiveKey));
    }

    public static async Task<SpotifyApChannel> AcceptServerAsync(IDuplexStream stream, CancellationToken ct = default)
    {
        var acc = new List<byte>();

        var pre = new byte[2];
        await stream.ReadExactAsync(pre, ct).ConfigureAwait(false);
        acc.AddRange(pre);
        var sizeBuf = new byte[4];
        await stream.ReadExactAsync(sizeBuf, ct).ConfigureAwait(false);
        acc.AddRange(sizeBuf);
        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(sizeBuf);
        var clientPub = new byte[size - 6];
        await stream.ReadExactAsync(clientPub, ct).ConfigureAwait(false);
        acc.AddRange(clientPub);

        var dh = DiffieHellman.Generate();
        var resp = FrameResponse(dh.PublicKey);
        acc.AddRange(resp);
        await stream.WriteAsync(resp, ct).ConfigureAwait(false);

        var keys = ApKeyDerivation.Derive(dh.SharedSecret(clientPub), acc.ToArray());
        // server mirrors the client: its send-key is the client's receive-key and vice versa.
        return new SpotifyApChannel(stream, new ApCodec(keys.ReceiveKey, keys.SendKey));
    }

    public ValueTask SendAsync(byte cmd, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => _stream.WriteAsync(_codec.Encode(cmd, payload.Span), ct);

    public async Task<(byte Cmd, byte[] Payload)> ReceiveAsync(CancellationToken ct = default)
    {
        var header = new byte[3];
        await _stream.ReadExactAsync(header, ct).ConfigureAwait(false);
        var (cmd, len) = _codec.BeginDecode(header);
        var rest = new byte[len + 4];
        await _stream.ReadExactAsync(rest, ct).ConfigureAwait(false);
        var payload = _codec.EndDecode(rest.AsSpan(0, len), rest.AsSpan(len, 4));
        return (cmd, payload);
    }

    static byte[] FrameHello(ReadOnlySpan<byte> payload)
    {
        var f = new byte[2 + 4 + payload.Length];
        f[0] = 0x00; f[1] = 0x04;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(2), (uint)(2 + 4 + payload.Length));
        payload.CopyTo(f.AsSpan(6));
        return f;
    }

    static byte[] FrameResponse(ReadOnlySpan<byte> payload)
    {
        var f = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(0), (uint)(4 + payload.Length));
        payload.CopyTo(f.AsSpan(4));
        return f;
    }
}
