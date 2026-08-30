using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Wavee.Backend.Spotify;

// ── LIFTED handshake mechanics (the AP connection crypto) — pure + unit-testable without a live server ────────────────
// The AP handshake: client sends ClientHello (DH gc) → server APResponse (DH gs + RSA sig) → both derive Shannon keys via
// HMAC-SHA1 over the accumulated handshake bytes → packets are Shannon-framed. The DH key-exchange, the key derivation,
// and the ApCodec are protocol facts (lifted faithfully); the live TCP socket + the ClientHello/APResponse protobuf are
// the remaining real-network step. ③ Transport's SpotifyApChannel composes these.

/// <summary>768-bit Diffie-Hellman (RFC 2409 / Oakley Group 1 prime, generator 2) — Spotify's handshake key exchange.</summary>
public sealed class DiffieHellman
{
    static ReadOnlySpan<byte> Prime =>
    [
        0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xc9,0x0f,0xda,0xa2,0x21,0x68,0xc2,
        0x34,0xc4,0xc6,0x62,0x8b,0x80,0xdc,0x1c,0xd1,0x29,0x02,0x4e,0x08,0x8a,0x67,
        0xcc,0x74,0x02,0x0b,0xbe,0xa6,0x3b,0x13,0x9b,0x22,0x51,0x4a,0x08,0x79,0x8e,
        0x34,0x04,0xdd,0xef,0x95,0x19,0xb3,0xcd,0x3a,0x43,0x1b,0x30,0x2b,0x0a,0x6d,
        0xf2,0x5f,0x14,0x37,0x4f,0xe1,0x35,0x6d,0x6d,0x51,0xc2,0x45,0xe4,0x85,0xb5,
        0x76,0x62,0x5e,0x7e,0xc6,0xf4,0x4c,0x42,0xe9,0xa6,0x3a,0x36,0x20,0xff,0xff,
        0xff,0xff,0xff,0xff,0xff,0xff,
    ];
    const int Generator = 2;
    const int PrivateKeySize = 95;

    readonly BigInteger _priv;
    public byte[] PublicKey { get; }

    DiffieHellman(BigInteger priv, byte[] pub) { _priv = priv; PublicKey = pub; }

    public static DiffieHellman Generate()
    {
        Span<byte> p = stackalloc byte[PrivateKeySize];
        RandomNumberGenerator.Fill(p);
        var priv = new BigInteger(p, isUnsigned: true, isBigEndian: false);
        var prime = new BigInteger(Prime, isUnsigned: true, isBigEndian: true);
        var pub = BigInteger.ModPow(Generator, priv, prime);
        return new DiffieHellman(priv, pub.ToByteArray(isUnsigned: true, isBigEndian: true));
    }

    /// <summary>Shared secret = remotePublic^private mod p, LEFT-PADDED to the 96-byte modulus length. BigInteger.ToByteArray
    /// drops leading zero bytes, so ~1/256 secrets would otherwise be 95 bytes and derive different keys than the server
    /// (RFC 2631 §2.1.2 mandates a fixed-width ZZ). Padding keeps key derivation deterministic.</summary>
    public byte[] SharedSecret(ReadOnlySpan<byte> remotePublicKey)
    {
        if (remotePublicKey.IsEmpty) throw new ArgumentException("remote public key empty", nameof(remotePublicKey));
        var prime = new BigInteger(Prime, isUnsigned: true, isBigEndian: true);
        var remote = new BigInteger(remotePublicKey, isUnsigned: true, isBigEndian: true);
        var raw = BigInteger.ModPow(remote, _priv, prime).ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == 96) return raw;
        var padded = new byte[96];
        raw.CopyTo(padded, 96 - raw.Length);   // left-pad with leading zeros to the modulus width
        return padded;
    }
}

/// <summary>Verifies the AP server's RSA-SHA1 signature over its DH public key `gs`. The AP socket is RAW TCP — this
/// signature (against Spotify's well-known 2048-bit server key, exponent 65537) is the ONLY thing authenticating the peer.
/// Verifying it BEFORE deriving keys is what stops an active MITM from negotiating the channel and reading the login packet
/// (which carries the OAuth token / reusable credential). Key + scheme are protocol facts, lifted faithfully. Fails closed.</summary>
public static class ApSignature
{
    static ReadOnlySpan<byte> ServerKey =>
    [
        0xac,0xe0,0x46,0x0b,0xff,0xc2,0x30,0xaf,0xf4,0x6b,0xfe,0xc3,0xbf,0xbf,0x86,0x3d,
        0xa1,0x91,0xc6,0xcc,0x33,0x6c,0x93,0xa1,0x4f,0xb3,0xb0,0x16,0x12,0xac,0xac,0x6a,
        0xf1,0x80,0xe7,0xf6,0x14,0xd9,0x42,0x9d,0xbe,0x2e,0x34,0x66,0x43,0xe3,0x62,0xd2,
        0x32,0x7a,0x1a,0x0d,0x92,0x3b,0xae,0xdd,0x14,0x02,0xb1,0x81,0x55,0x05,0x61,0x04,
        0xd5,0x2c,0x96,0xa4,0x4c,0x1e,0xcc,0x02,0x4a,0xd4,0xb2,0x0c,0x00,0x1f,0x17,0xed,
        0xc2,0x2f,0xc4,0x35,0x21,0xc8,0xf0,0xcb,0xae,0xd2,0xad,0xd7,0x2b,0x0f,0x9d,0xb3,
        0xc5,0x32,0x1a,0x2a,0xfe,0x59,0xf3,0x5a,0x0d,0xac,0x68,0xf1,0xfa,0x62,0x1e,0xfb,
        0x2c,0x8d,0x0c,0xb7,0x39,0x2d,0x92,0x47,0xe3,0xd7,0x35,0x1a,0x6d,0xbd,0x24,0xc2,
        0xae,0x25,0x5b,0x88,0xff,0xab,0x73,0x29,0x8a,0x0b,0xcc,0xcd,0x0c,0x58,0x67,0x31,
        0x89,0xe8,0xbd,0x34,0x80,0x78,0x4a,0x5f,0xc9,0x6b,0x89,0x9d,0x95,0x6b,0xfc,0x86,
        0xd7,0x4f,0x33,0xa6,0x78,0x17,0x96,0xc9,0xc3,0x2d,0x0d,0x32,0xa5,0xab,0xcd,0x05,
        0x27,0xe2,0xf7,0x10,0xa3,0x96,0x13,0xc4,0x2f,0x99,0xc0,0x27,0xbf,0xed,0x04,0x9c,
        0x3c,0x27,0x58,0x04,0xb6,0xb2,0x19,0xf9,0xc1,0x2f,0x02,0xe9,0x48,0x63,0xec,0xa1,
        0xb6,0x42,0xa0,0x9d,0x48,0x25,0xf8,0xb3,0x9d,0xd0,0xe8,0x6a,0xf9,0x48,0x4d,0xa1,
        0xc2,0xba,0x86,0x30,0x42,0xea,0x9d,0xb3,0x08,0x6c,0x19,0x0e,0x48,0xb3,0x9d,0x66,
        0xeb,0x00,0x06,0xa2,0x5a,0xee,0xa1,0x1b,0x13,0x87,0x3c,0xd7,0x19,0xe6,0x55,0xbd,
    ];

    public static bool Verify(ReadOnlySpan<byte> gs, byte[] gsSignature)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = ServerKey.ToArray(), Exponent = [0x01, 0x00, 0x01] });
            return rsa.VerifyData(gs.ToArray(), gsSignature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException) { return false; }   // malformed signature → invalid (fail closed)
    }
}

public readonly record struct ApKeys(byte[] Challenge, byte[] SendKey, byte[] ReceiveKey);

/// <summary>Derives the challenge + send/receive Shannon keys from the DH shared secret and the accumulated handshake
/// bytes (HMAC-SHA1, 5 iterations → 100 bytes; challenge = HMAC(data[0..20], packets); send = data[20..52]; recv = data[52..84]).</summary>
public static class ApKeyDerivation
{
    public static ApKeys Derive(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> accumulator)
    {
        var secret = sharedSecret.ToArray();
        var packets = accumulator.ToArray();
        var data = new byte[100];
        for (int i = 1; i <= 5; i++)
        {
            using var hmac = new HMACSHA1(secret);
            hmac.TransformBlock(packets, 0, packets.Length, null, 0);
            hmac.TransformFinalBlock([(byte)i], 0, 1);
            hmac.Hash!.CopyTo(data.AsSpan((i - 1) * 20, 20));
        }
        byte[] challenge;
        using (var h = new HMACSHA1(data[..20])) challenge = h.ComputeHash(packets);
        return new ApKeys(challenge, data[20..52], data[52..84]);
    }
}

/// <summary>The Shannon AP packet codec: a frame is [cmd:1][len:2 BE][payload:len] Shannon-encrypted with a per-direction
/// nonce that increments per packet, followed by a 4-byte MAC. Encode/decode are directional (client send-key == server
/// receive-key). Decode mirrors the real reader (decrypt the 3-byte header to learn len, then the payload, then verify MAC).</summary>
public sealed class ApCodec
{
    readonly ShannonCipher _send;
    readonly ShannonCipher _recv;
    uint _sendNonce;
    uint _recvNonce;

    public ApCodec(ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> receiveKey)
    {
        _send = new ShannonCipher(sendKey);
        _recv = new ShannonCipher(receiveKey);
    }

    public byte[] Encode(byte cmd, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue) throw new ArgumentException("payload too large");
        var frame = new byte[3 + payload.Length + 4];   // one allocation: [cmd|len|payload|mac] (was 3 arrays + 3 copies)
        frame[0] = cmd;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(3));

        _send.NonceU32(_sendNonce++);
        _send.Encrypt(frame.AsSpan(0, 3 + payload.Length));   // encrypt header+payload in place
        _send.Finish(frame.AsSpan(3 + payload.Length, 4));    // MAC straight into the trailing 4 bytes
        return frame;
    }

    public (byte Cmd, byte[] Payload) Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 7) throw new InvalidDataException("frame too short");
        _recv.NonceU32(_recvNonce++);

        var header = frame[..3].ToArray();
        _recv.Decrypt(header);
        byte cmd = header[0];
        int len = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        if (frame.Length != 3 + len + 4) throw new InvalidDataException("frame length mismatch");

        var payload = frame.Slice(3, len).ToArray();
        _recv.Decrypt(payload);
        _recv.CheckMac(frame.Slice(3 + len, 4));   // CheckMac takes ReadOnlySpan — no .ToArray(); throws on tamper
        return (cmd, payload);
    }

    // ── streaming decode for a live stream (read the 3-byte header to learn len, then the payload+MAC) ──
    public (byte Cmd, int Len) BeginDecode(ReadOnlySpan<byte> encHeader3)
    {
        _recv.NonceU32(_recvNonce++);
        Span<byte> h = stackalloc byte[3];   // was a per-packet heap byte[3]
        encHeader3[..3].CopyTo(h);
        _recv.Decrypt(h);
        return (h[0], BinaryPrimitives.ReadUInt16BigEndian(h[1..]));
    }

    public byte[] EndDecode(ReadOnlySpan<byte> encPayload, ReadOnlySpan<byte> mac)
    {
        var p = encPayload.ToArray();
        _recv.Decrypt(p);
        _recv.CheckMac(mac);   // mac is already a ReadOnlySpan — no .ToArray()
        return p;
    }
}
