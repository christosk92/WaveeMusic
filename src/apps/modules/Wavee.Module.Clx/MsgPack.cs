using System.Buffers.Binary;
using System.Text;

namespace Wavee.Module.Clx;

/// <summary>
/// The few MessagePack shapes a CLX datagram needs, written into one reused buffer: a packet-type byte, then ONE flat map of
/// string keys to float64 / float32 / uint8 / string values. No package, no reflection (the module is NativeAOT).
/// Big-endian per the MessagePack spec.
/// </summary>
internal sealed class MsgPackWriter
{
    private readonly byte[] _buffer = new byte[1024];
    private int _length;

    public ReadOnlyMemory<byte> Written => _buffer.AsMemory(0, _length);

    /// <summary>Start a datagram: the CLX packet type, then a map of <paramref name="entries"/> pairs.</summary>
    public void Begin(byte packetType, int entries)
    {
        _length = 0;
        Byte(packetType);
        if (entries <= 15) Byte((byte)(0x80 | entries));
        else { Byte(0xde); BinaryPrimitives.WriteUInt16BigEndian(Take(2), (ushort)entries); }
    }

    public void Float64(string key, double value)
    {
        Str(key);
        Byte(0xcb);
        BinaryPrimitives.WriteDoubleBigEndian(Take(8), value);
    }

    public void Float32(string key, float value)
    {
        Str(key);
        Byte(0xca);
        BinaryPrimitives.WriteSingleBigEndian(Take(4), value);
    }

    public void UInt8(string key, byte value)
    {
        Str(key);
        if (value < 0x80) Byte(value);                      // positive fixint
        else { Byte(0xcc); Byte(value); }
    }

    public void String(string key, string? value)
    {
        Str(key);
        Str(value ?? "");
    }

    private void Str(string s)
    {
        int n = Encoding.UTF8.GetByteCount(s);
        if (n > 255) { s = s[..Math.Min(s.Length, 120)]; n = Encoding.UTF8.GetByteCount(s); }   // a title is never the datagram
        if (n <= 31) Byte((byte)(0xa0 | n));
        else { Byte(0xd9); Byte((byte)n); }
        Encoding.UTF8.GetBytes(s, Take(n));
    }

    private void Byte(byte b) => _buffer[_length++] = b;

    private Span<byte> Take(int n)
    {
        Span<byte> span = _buffer.AsSpan(_length, n);
        _length += n;
        return span;
    }
}
