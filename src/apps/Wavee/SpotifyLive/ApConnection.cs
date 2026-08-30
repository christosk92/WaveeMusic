using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// ── Stage F — the persistent AP channel (audio-key fetch over the long-lived Shannon socket) ──────────────────────────
// ONE AP connection serves both login AND audio keys: the login handshake's socket+codec are ADOPTED here (no second
// handshake). The read-pump answers pings + routes the 0x0d/0x0e audio-key replies to the internal dispatcher; 0x0c
// requests go out via RequestAudioKeyAsync. (A standalone ConnectAsync also exists for opening a dedicated channel.)
public sealed class ApConnection : IDisposable
{
    /// <summary>Idle read timeout for the persistent channel — longer than the AP's ping interval so idle reads survive.</summary>
    public static readonly TimeSpan IdleReadTimeout = TimeSpan.FromSeconds(90);

    const byte CmdPing = 0x04, CmdPong = 0x49, CmdPongAck = 0x4a, CmdAesKeyRequest = 0x0c, CmdAesKey = 0x0d,
        CmdAesKeyError = 0x0e, CmdLocalePreamble = 0x0f, CmdPreferredLocale = 0x74;
    static readonly byte[] PongPayload = new byte[4];   // librespot answers Ping with Pong(0x00000000), not the echoed payload

    readonly IDuplexStream _stream;
    readonly ApCodec _codec;
    readonly AudioKeyDispatcher _keys = new();
    readonly WaveeLogger _log;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();

    ApConnection(IDuplexStream stream, ApCodec codec, WaveeLogger log)
    {
        _stream = stream;
        _codec = codec;
        _log = log;
    }

    /// <summary>Adopt an already-handshaken AP socket + its negotiated codec (e.g. the login socket) as the persistent
    /// channel, and start the read-pump. The returned connection owns the socket lifetime.</summary>
    public static ApConnection Adopt(IDuplexStream stream, ApCodec codec, WaveeLogger log)
    {
        var c = new ApConnection(stream, codec, log);
        _ = Task.Run(() => c.PumpAsync(c._cts.Token));
        log.Info("AP channel adopted (login socket reused) — audio-key path ready");
        return c;
    }

    /// <summary>Open a DEDICATED AP socket + handshake (used only when not reusing the login socket).</summary>
    public static async Task<ApConnection> ConnectAsync(string apHost, int apPort, Credential cred, string deviceId,
        WaveeLogger log, CancellationToken ct)
    {
        var tcp = await TcpDuplexStream.ConnectAsync(apHost, apPort, ct, IdleReadTimeout).ConfigureAwait(false);
        try
        {
            var (_, codec) = await SpotifyConnection.HandshakeRetainAsync(tcp, cred, deviceId, ct).ConfigureAwait(false);
            return Adopt(tcp, codec, log);
        }
        catch { tcp.Dispose(); throw; }
    }

    /// <summary>Fetch the 16-byte AES key for a file via the 0x0c/0x0d exchange (5 s timeout).</summary>
    public async Task<byte[]> RequestAudioKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid, CancellationToken ct = default)
    {
        var (body, keyTask) = _keys.Begin(fileId.Span, trackGid.Span);
        await SendAsync(CmdAesKeyRequest, body, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return await keyTask.WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Publishes Spotify's two-letter preferred locale on the retained AP session. Spotify expects the 0x0f
    /// random preamble immediately before the 0x74 key/value packet.</summary>
    public async Task PublishPreferredLocaleAsync(string language, CancellationToken ct = default)
    {
        language = SpotifyHeaders.NormalizeLanguage(language);
        var preamble = new byte[20];
        RandomNumberGenerator.Fill(preamble);
        await SendAsync(CmdLocalePreamble, preamble, ct).ConfigureAwait(false);

        byte[] locale = Encoding.ASCII.GetBytes(language);
        var payload = new byte[5 + 16 + locale.Length];
        payload[2] = 0x10;
        payload[4] = 0x02;
        Encoding.ASCII.GetBytes("preferred-locale", payload.AsSpan(5, 16));
        locale.CopyTo(payload, 21);
        await SendAsync(CmdPreferredLocale, payload, ct).ConfigureAwait(false);
    }

    async Task PumpAsync(CancellationToken ct)
    {
        var header = new byte[3];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _stream.ReadExactAsync(header, ct).ConfigureAwait(false);
                var (cmd, len) = _codec.BeginDecode(header);
                var rest = new byte[len + 4];
                await _stream.ReadExactAsync(rest, ct).ConfigureAwait(false);
                var payload = _codec.EndDecode(rest.AsSpan(0, len), rest.AsSpan(len, 4));
                switch (cmd)
                {
                    case CmdAesKey: _keys.OnAesKey(payload); break;
                    case CmdAesKeyError: _keys.OnAesKeyError(payload); break;
                    case CmdPing: await SendAsync(CmdPong, PongPayload, ct).ConfigureAwait(false); break;   // keepalive: server Ping -> Pong(0x00000000)
                    case CmdPongAck: break;   // the server's PongAck for our Pong — nothing to do
                    // Mercury (0xb2-0xb6), country/product trailers, legacy packets: ignored for now.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* disposed */ }
        catch (Exception ex)
        {
            _log.Info("AP channel dropped: " + ex.Message);
            _keys.FailAll(ex);   // pending key waiters fail → callers retry / fall back
        }
    }

    async Task SendAsync(byte cmd, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _stream.WriteAsync(_codec.Encode(cmd, payload.Span), ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { (_stream as IDisposable)?.Dispose(); } catch { }
        _sendLock.Dispose();
        _cts.Dispose();
    }
}

// IAudioKeySource over the persistent AP channel + an in-memory key cache. The connection is fetched via a delegate so the
// session owner can (re)establish it without re-creating this source.
public sealed class LiveAudioKeySource : IAudioKeySource
{
    readonly Func<ApConnection?> _connection;
    readonly Dictionary<string, byte[]> _cache = new();
    readonly object _cacheGate = new();

    public LiveAudioKeySource(Func<ApConnection?> connection) => _connection = connection;

    public async Task<ReadOnlyMemory<byte>> GetKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid, CancellationToken ct = default)
    {
        string hex = Convert.ToHexStringLower(fileId.Span);
        lock (_cacheGate) if (_cache.TryGetValue(hex, out var cached)) return cached;

        var conn = _connection() ?? throw new InvalidOperationException("AP channel not connected");
        var key = await conn.RequestAudioKeyAsync(fileId, trackGid, ct).ConfigureAwait(false);
        lock (_cacheGate) _cache[hex] = key;
        return key;
    }
}
