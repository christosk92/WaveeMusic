using System.Buffers.Binary;

namespace Wavee.Backend.Spotify;

// ── Mercury request/reply framing (the payload of an AP Mercury packet) ──────────────────────────────────────────────
// [seq_len: u16 BE][seq][flags: u8][part_count: u16 BE][(part_len: u16 BE)(part)]... — the librespot mercury shape. Part 0
// is a protobuf Header (uri/method/status) on the real wire; here parts are opaque bytes so the FRAMING is testable on its
// own (the Header protobuf is the network-dependent layer). Rides ApCodec packets via ③ Transport.RequestAsync(ApMercury).
public sealed record MercuryMessage(byte[] Sequence, byte Flags, IReadOnlyList<byte[]> Parts)
{
    public byte[] Encode()
    {
        if (Sequence.Length > ushort.MaxValue || Parts.Count > ushort.MaxValue)
            throw new InvalidOperationException("Mercury sequence/part-count exceeds the u16 wire limit");
        int size = 2 + Sequence.Length + 1 + 2;
        foreach (var p in Parts)
        {
            if (p.Length > ushort.MaxValue) throw new InvalidOperationException("Mercury part exceeds the u16 wire limit");
            size += 2 + p.Length;
        }
        var buf = new byte[size];
        int o = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), (ushort)Sequence.Length); o += 2;
        Sequence.CopyTo(buf.AsSpan(o)); o += Sequence.Length;
        buf[o++] = Flags;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), (ushort)Parts.Count); o += 2;
        foreach (var p in Parts)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(o), (ushort)p.Length); o += 2;
            p.CopyTo(buf.AsSpan(o)); o += p.Length;
        }
        return buf;
    }

    public static MercuryMessage Decode(ReadOnlySpan<byte> data)
    {
        int o = 0;
        int seqLen = BinaryPrimitives.ReadUInt16BigEndian(data[o..]); o += 2;
        var seq = data.Slice(o, seqLen).ToArray(); o += seqLen;
        byte flags = data[o++];
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[o..]); o += 2;
        var parts = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16BigEndian(data[o..]); o += 2;
            parts.Add(data.Slice(o, len).ToArray()); o += len;
        }
        return new MercuryMessage(seq, flags, parts);
    }
}
