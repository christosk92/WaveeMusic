using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Wavee.Backend.Spotify;

// ── LIFTED protocol mechanic (a fixed spec — must match librespot's shannon crate bit-for-bit) ───────────────────────
// The Shannon stream cipher (Rose, Qualcomm) is the AP-channel packet codec: after the handshake, every packet is
// Shannon-encrypted with a per-packet 4-byte big-endian nonce + a 4-byte MAC. This is a *protocol fact*, not architecture
// — so it is lifted faithfully and pinned by the librespot test vectors (see CryptoTests). It lives inside ③ Transport's
// AP channel, not as a standalone subsystem.
public sealed class ShannonCipher
{
    const int N = 16;
    const int FOLD = N;
    const uint INITKONST = 0x6996c53a;
    const int KEYP = 13;

    readonly uint[] _R = new uint[N];
    readonly uint[] _CRC = new uint[N];
    readonly uint[] _initR = new uint[N];
    uint _konst, _sbuf, _mbuf;
    int _nbuf;

    public ShannonCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentException("Shannon key must be exactly 32 bytes", nameof(key));
        InitState();
        LoadKey(key, key.Length);
        GenKonst();
        SaveState();
        _nbuf = 0;
    }

    public void NonceU32(uint nonce)
    {
        Span<byte> nonceBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(nonceBytes, nonce);
        ReloadState();
        _konst = INITKONST;
        LoadKey(nonceBytes, 4);
        GenKonst();
        _nbuf = 0;
        _mbuf = 0;
    }

    public void Encrypt(Span<byte> buffer) => EncryptInternal(buffer);
    public void Decrypt(Span<byte> buffer) => DecryptInternal(buffer);

    public void Finish(Span<byte> mac)
    {
        if (mac.Length != 4) throw new ArgumentException("MAC must be exactly 4 bytes", nameof(mac));
        if (_nbuf != 0) MacFunc(_mbuf);
        Cycle();
        _R[KEYP] ^= INITKONST ^ (uint)(_nbuf << 3);
        _nbuf = 0;
        for (int i = 0; i < N; i++) _R[i] ^= _CRC[i];
        Diffuse();
        Cycle();
        BinaryPrimitives.WriteUInt32LittleEndian(mac, _sbuf);
    }

    public void CheckMac(ReadOnlySpan<byte> receivedMac)
    {
        if (receivedMac.Length != 4) throw new ArgumentException("MAC must be exactly 4 bytes", nameof(receivedMac));
        Span<byte> computed = stackalloc byte[4];
        Finish(computed);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(receivedMac, computed))   // constant-time compare
            throw new InvalidDataException("Shannon MAC verification failed");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint SBox1(uint w) { w ^= RotL(w, 5) | RotL(w, 7); w ^= RotL(w, 19) | RotL(w, 22); return w; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint SBox2(uint w) { w ^= RotL(w, 7) | RotL(w, 22); w ^= RotL(w, 5) | RotL(w, 19); return w; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint RotL(uint v, int c) => (v << c) | (v >> (32 - c));

    void Cycle()
    {
        uint t = _R[12] ^ _R[13] ^ _konst;
        t = SBox1(t) ^ RotL(_R[0], 1);
        for (int i = 1; i < N; i++) _R[i - 1] = _R[i];
        _R[N - 1] = t;
        t = SBox2(_R[2] ^ _R[15]);
        _R[0] ^= t;
        _sbuf = t ^ _R[8] ^ _R[12];
    }

    void CrcFunc(uint input)
    {
        uint t = _CRC[0] ^ _CRC[2] ^ _CRC[15] ^ input;
        for (int j = 1; j < N; j++) _CRC[j - 1] = _CRC[j];
        _CRC[N - 1] = t;
    }

    void MacFunc(uint input) { CrcFunc(input); _R[KEYP] ^= input; }

    void InitState() { _R[0] = 1; _R[1] = 1; for (int i = 2; i < N; i++) _R[i] = _R[i - 1] + _R[i - 2]; _konst = INITKONST; }
    void SaveState() => Array.Copy(_R, _initR, N);
    void ReloadState() => Array.Copy(_initR, _R, N);
    void GenKonst() => _konst = _R[0];
    void Diffuse() { for (int i = 0; i < FOLD; i++) Cycle(); }

    void LoadKey(ReadOnlySpan<byte> key, int keylen)
    {
        int i = 0;
        while (i < (keylen & ~0x3)) { uint k = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i)); _R[KEYP] ^= k; Cycle(); i += 4; }
        if (i < keylen)
        {
            Span<byte> xtra = stackalloc byte[4];
            xtra.Clear();
            int j = 0;
            while (i < keylen) xtra[j++] = key[i++];
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(xtra);
            _R[KEYP] ^= k; Cycle();
        }
        _R[KEYP] ^= (uint)keylen; Cycle();
        Array.Copy(_R, _CRC, N);
        Diffuse();
        for (i = 0; i < N; i++) _R[i] ^= _CRC[i];
    }

    void EncryptInternal(Span<byte> buffer)
    {
        int offset = 0, remaining = buffer.Length;
        while (_nbuf != 0 && remaining > 0)
        {
            _mbuf ^= (uint)buffer[offset] << (32 - _nbuf);
            buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);
            offset++; _nbuf -= 8; remaining--;
        }
        if (_nbuf == 0 && offset > 0) { MacFunc(_mbuf); _mbuf = 0; }
        while (remaining >= 4)
        {
            Cycle();
            uint pt = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
            MacFunc(pt);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset, 4), pt ^ _sbuf);
            offset += 4; remaining -= 4;
        }
        if (remaining > 0)
        {
            Cycle(); _mbuf = 0; _nbuf = 32;
            while (remaining > 0)
            {
                _mbuf ^= (uint)buffer[offset] << (32 - _nbuf);
                buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);
                offset++; _nbuf -= 8; remaining--;
            }
        }
    }

    void DecryptInternal(Span<byte> buffer)
    {
        int offset = 0, remaining = buffer.Length;
        while (_nbuf != 0 && remaining > 0)
        {
            buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);
            _mbuf ^= (uint)buffer[offset] << (32 - _nbuf);
            offset++; _nbuf -= 8; remaining--;
        }
        if (_nbuf == 0 && offset > 0) { MacFunc(_mbuf); _mbuf = 0; }
        while (remaining >= 4)
        {
            Cycle();
            uint ct = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
            uint pt = ct ^ _sbuf;
            MacFunc(pt);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset, 4), pt);
            offset += 4; remaining -= 4;
        }
        if (remaining > 0)
        {
            Cycle(); _mbuf = 0; _nbuf = 32;
            while (remaining > 0)
            {
                buffer[offset] ^= (byte)((_sbuf >> (32 - _nbuf)) & 0xFF);
                _mbuf ^= (uint)buffer[offset] << (32 - _nbuf);
                offset++; _nbuf -= 8; remaining--;
            }
        }
    }
}
