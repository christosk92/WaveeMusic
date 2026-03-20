using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;

namespace Wavee.Core.Mercury;

/// <summary>
/// Response from a Mercury request.
/// </summary>
public sealed record MercuryResponse(string Uri, int StatusCode, IReadOnlyList<byte[]> Payload);

/// <summary>
/// Manages Mercury protocol requests over the AP TCP connection.
/// Mercury is Spotify's internal request/response protocol for accessing
/// resources like keymaster tokens, metadata, and subscriptions.
/// </summary>
public sealed class MercuryManager
{
    private readonly ISession _session;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<ulong, MercuryPendingRequest> _pending = new();
    private ulong _sequence;

    public MercuryManager(ISession session, ILogger? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger;
    }

    /// <summary>
    /// Sends a Mercury GET request and awaits the response.
    /// </summary>
    public Task<MercuryResponse> GetAsync(string uri, CancellationToken ct = default)
        => SendRequestAsync("GET", uri, null, ct);

    /// <summary>
    /// Sends a Mercury SEND (POST) request and awaits the response.
    /// </summary>
    public Task<MercuryResponse> SendAsync(string uri, byte[]? payload = null, CancellationToken ct = default)
        => SendRequestAsync("SEND", uri, payload, ct);

    /// <summary>
    /// Dispatches an incoming Mercury packet from the session dispatcher.
    /// </summary>
    public void DispatchPacket(byte command, ReadOnlySpan<byte> payload)
    {
        try
        {
            ParseResponse(payload);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to dispatch Mercury packet (cmd=0x{Cmd:X2})", command);
        }
    }

    private async Task<MercuryResponse> SendRequestAsync(
        string method, string uri, byte[]? payload, CancellationToken ct)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var pending = new MercuryPendingRequest();

        if (!_pending.TryAdd(seq, pending))
            throw new InvalidOperationException($"Mercury sequence {seq} already pending");

        try
        {
            var packet = BuildRequestPacket(seq, method, uri, payload);
            await _session.SendAsync(PacketType.MercuryReq, packet, ct);

            _logger?.LogDebug("Mercury {Method} sent: seq={Seq}, uri={Uri}", method, seq, uri);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                return await pending.Tcs.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"Mercury request timeout: {uri}");
            }
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
    }

    private static byte[] BuildRequestPacket(ulong seq, string method, string uri, byte[]? payload)
    {
        // Build protobuf header
        var header = new Protocol.Header
        {
            Uri = uri,
            Method = method
        };
        var headerBytes = header.ToByteArray();

        using var ms = new MemoryStream();

        // Sequence: 2-byte length + 8-byte value (big-endian)
        var seqBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqBytes, seq);
        WriteBigEndianUInt16(ms, (ushort)seqBytes.Length);
        ms.Write(seqBytes);

        // Flags: 0x01 = FINAL (single packet request)
        ms.WriteByte(0x01);

        // Part count
        var partCount = (ushort)(payload != null ? 2 : 1);
        WriteBigEndianUInt16(ms, partCount);

        // Part 1: header
        WriteBigEndianUInt16(ms, (ushort)headerBytes.Length);
        ms.Write(headerBytes);

        // Part 2: payload (if present)
        if (payload != null)
        {
            WriteBigEndianUInt16(ms, (ushort)payload.Length);
            ms.Write(payload);
        }

        return ms.ToArray();
    }

    private void ParseResponse(ReadOnlySpan<byte> data)
    {
        var offset = 0;

        // Read sequence
        var seqLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));
        offset += 2;
        var seqBytes = data.Slice(offset, seqLen);
        offset += seqLen;

        // Parse sequence as ulong (8 bytes big-endian)
        ulong seq = 0;
        if (seqLen == 8)
            seq = BinaryPrimitives.ReadUInt64BigEndian(seqBytes);
        else if (seqLen == 4)
            seq = BinaryPrimitives.ReadUInt32BigEndian(seqBytes);
        else if (seqLen > 0)
        {
            // Variable length: pad to 8 bytes
            Span<byte> padded = stackalloc byte[8];
            seqBytes.CopyTo(padded.Slice(8 - seqLen));
            seq = BinaryPrimitives.ReadUInt64BigEndian(padded);
        }

        // Read flags
        var flags = data[offset++];

        // Read part count
        var partCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));
        offset += 2;

        // Look up pending request
        if (!_pending.TryGetValue(seq, out var pending))
        {
            _logger?.LogDebug("Mercury response for unknown seq={Seq} (possibly event)", seq);
            return;
        }

        // Read parts
        for (int i = 0; i < partCount; i++)
        {
            if (offset + 2 > data.Length) break;
            var partSize = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));
            offset += 2;

            if (offset + partSize > data.Length) break;
            pending.Parts.Add(data.Slice(offset, partSize).ToArray());
            offset += partSize;
        }

        // Check if final (0x01 flag)
        if ((flags & 0x01) != 0)
        {
            CompleteRequest(seq, pending);
        }
    }

    private void CompleteRequest(ulong seq, MercuryPendingRequest pending)
    {
        if (pending.Parts.Count == 0)
        {
            pending.Tcs.TrySetException(new InvalidOperationException("Empty Mercury response"));
            return;
        }

        // First part is the protobuf Header
        var headerPart = pending.Parts[0];
        var header = Protocol.Header.Parser.ParseFrom(headerPart);

        var response = new MercuryResponse(
            header.Uri ?? "",
            header.HasStatusCode ? header.StatusCode : 200,
            pending.Parts.Skip(1).ToList());

        _logger?.LogDebug("Mercury response: seq={Seq}, uri={Uri}, status={Status}, parts={Parts}",
            seq, response.Uri, response.StatusCode, response.Payload.Count);

        if (response.StatusCode >= 400)
        {
            pending.Tcs.TrySetException(new InvalidOperationException(
                $"Mercury error {response.StatusCode}: {response.Uri}"));
        }
        else
        {
            pending.Tcs.TrySetResult(response);
        }
    }

    private static void WriteBigEndianUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }

    /// <summary>
    /// Cancels all pending requests. Called on reconnection.
    /// </summary>
    public void Reset()
    {
        foreach (var kvp in _pending)
        {
            kvp.Value.Tcs.TrySetCanceled();
        }
        _pending.Clear();
        _sequence = 0;
    }

    private sealed class MercuryPendingRequest
    {
        public TaskCompletionSource<MercuryResponse> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Parts { get; } = [];
    }
}
