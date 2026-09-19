using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend;

// ── Stage F — audio-key fetch over the persistent AP channel (the 0x0c/0x0d exchange) ─────────────────────────────────
// The key path is: AesKeyRequest (raw AP packet cmd 0x0c) = file_id(20) ++ track_gid(16) ++ seq(u32 BE) ++ 0x00 0x00;
// the AP replies AesKey (0x0d) = seq(u32) ++ key(16) or AesKeyError (0x0e) = seq(u32) ++ code(u16). The correlation engine
// here is proto-free + socket-free (unit-tested); SpotifyLive's ApConnection owns the socket and feeds it the responses.

/// <summary>Produces the 16-byte AES key for a track's audio file (needed by the deferred audio host to decrypt).</summary>
public interface IAudioKeySource
{
    Task<ReadOnlyMemory<byte>> GetKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid, CancellationToken ct = default);
}

public interface IPlayPlayNativeSeedSource
{
    ReadOnlyMemory<byte> GetNativeCdnSeed(string fileIdHex);
}

public interface IPlayPlayCdnDecryptorFactory
{
    Wavee.Backend.Audio.CdnDecryptor? CreateCdnDecryptor(ReadOnlyMemory<byte> nativeCdnSeed);
}

/// <summary>Headless stub: a 16-byte zero key (the silent host ignores it).</summary>
public sealed class StubAudioKeySource : IAudioKeySource
{
    public Task<ReadOnlyMemory<byte>> GetKeyAsync(ReadOnlyMemory<byte> fileId, ReadOnlyMemory<byte> trackGid, CancellationToken ct = default)
        => Task.FromResult<ReadOnlyMemory<byte>>(new byte[16]);
}

/// <summary>The 0x0c/0x0d correlation engine: allocate a sequence + pending TCS, build the request packet body, and
/// complete/fail the matching waiter when the AP pushes 0x0d/0x0e. Socket-free + proto-free → unit-tested.</summary>
public sealed class AudioKeyDispatcher
{
    readonly object _gate = new();
    readonly Dictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
    uint _seq;

    /// <summary>Register a request and return its packet body (the caller sends it over the AP socket) + the key task.</summary>
    public (byte[] Body, Task<byte[]> Key) Begin(ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> trackGid)
    {
        uint seq;
        TaskCompletionSource<byte[]> tcs;
        lock (_gate)
        {
            seq = _seq++;
            tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[seq] = tcs;
        }
        return (BuildBody(fileId, trackGid, seq), tcs.Task);
    }

    /// <summary>AesKey (0x0d): seq(u32 BE) ++ key(16).</summary>
    public void OnAesKey(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 20) return;
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var key = payload.Slice(4, 16).ToArray();
        TaskCompletionSource<byte[]>? tcs;
        lock (_gate) { _pending.Remove(seq, out tcs); }
        tcs?.TrySetResult(key);
    }

    /// <summary>AesKeyError (0x0e): seq(u32 BE) ++ code(u16).</summary>
    public void OnAesKeyError(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return;
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(payload);
        int code = payload.Length >= 6 ? BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2)) : -1;
        TaskCompletionSource<byte[]>? tcs;
        lock (_gate) { _pending.Remove(seq, out tcs); }
        tcs?.TrySetException(new InvalidOperationException($"audio key error (code {code})"));
    }

    /// <summary>Fail all pending waiters (on AP disconnect) so callers can retry on the next connection.</summary>
    public void FailAll(Exception ex)
    {
        List<TaskCompletionSource<byte[]>> all;
        lock (_gate) { all = new List<TaskCompletionSource<byte[]>>(_pending.Values); _pending.Clear(); }
        foreach (var t in all) t.TrySetException(ex);
    }

    public static byte[] BuildBody(ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> trackGid, uint seq)
    {
        var body = new byte[fileId.Length + trackGid.Length + 6];
        fileId.CopyTo(body);
        trackGid.CopyTo(body.AsSpan(fileId.Length));
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(fileId.Length + trackGid.Length), seq);
        // trailing 0x00 0x00 (already zero from allocation)
        return body;
    }
}
