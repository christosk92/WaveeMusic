using Wavee.Backend.Spotify;
using Xunit;

namespace Wavee.Tests;

// Lifted protocol mechanics — pinned to fixed spec vectors. These prove the "real Spotify" crypto matches librespot
// bit-for-bit (Shannon) and the RFC (PKCE), independent of any network.
public class ShannonCipherTests
{
    // librespot shannon crate test key
    static readonly byte[] Key =
    [
        0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07, 0x08,0x09,0x0a,0x0b,0x0c,0x0d,0x0e,0x0f,
        0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17, 0x18,0x19,0x1a,0x1b,0x1c,0x1d,0x1e,0x1f
    ];

    static (byte[] enc, byte[] mac) Run(uint nonce, byte[] plain)
    {
        var c = new ShannonCipher(Key);
        c.NonceU32(nonce);
        var enc = (byte[])plain.Clone();
        c.Encrypt(enc);
        var mac = new byte[4];
        c.Finish(mac);
        return (enc, mac);
    }

    [Fact]
    public void Vector1_Nonce0()
    {
        var (enc, mac) = Run(0, [0x01, 0x02, 0x03, 0x04]);
        Assert.Equal(new byte[] { 0xcb, 0x7f, 0xea, 0x2f }, enc);
        Assert.Equal(new byte[] { 0x80, 0x3a, 0x07, 0x7f }, mac);
    }

    [Fact]
    public void Vector2_Nonce1()
    {
        var (enc, mac) = Run(1, [0x01, 0x02, 0x03, 0x04]);
        Assert.Equal(new byte[] { 0xba, 0x95, 0x25, 0xab }, enc);
        Assert.Equal(new byte[] { 0xae, 0x02, 0xa2, 0xc0 }, mac);
    }

    [Fact]
    public void Vector3_EmptyData_MacOnly()
    {
        var (enc, mac) = Run(0, []);
        Assert.Empty(enc);
        Assert.Equal(new byte[] { 0x0a, 0xab, 0x57, 0x02 }, mac);
    }

    [Fact]
    public void Vector4_NonWordAligned_HelloWorld()
    {
        var (enc, mac) = Run(0, [0x48, 0x65, 0x6c, 0x6c, 0x6f, 0x2c, 0x20, 0x57, 0x6f, 0x72, 0x6c, 0x64, 0x21]);
        Assert.Equal(new byte[] { 0x82, 0x18, 0x85, 0x47, 0x57, 0x85, 0xea, 0x2e, 0xae, 0x01, 0x7e, 0xfe, 0xbe }, enc);
        Assert.Equal(new byte[] { 0x81, 0x49, 0x3f, 0x7e }, mac);
    }

    [Fact]
    public void Vector5_ApCodecStylePacket()
    {
        var (enc, mac) = Run(0, [0x42, 0x00, 0x04, 0xaa, 0xbb, 0xcc, 0xdd]);
        Assert.Equal(new byte[] { 0x88, 0x7d, 0xed, 0x81, 0x9f, 0x11, 0xf0 }, enc);
        Assert.Equal(new byte[] { 0x27, 0xa1, 0xfc, 0x02 }, mac);
    }

    [Theory]
    [InlineData(0u, new byte[] { 0xd8, 0x49, 0xbf, 0x53 }, new byte[] { 0xb2, 0x3d, 0xaf, 0x43 })]
    [InlineData(1u, new byte[] { 0xa9, 0xa3, 0x70, 0xd7 }, new byte[] { 0x1f, 0xb6, 0xf1, 0x9b })]
    [InlineData(2u, new byte[] { 0xdd, 0x3f, 0x13, 0x8e }, new byte[] { 0xf0, 0x78, 0x5c, 0x24 })]
    public void Vector6_SequentialNonces(uint nonce, byte[] expEnc, byte[] expMac)
    {
        var (enc, mac) = Run(nonce, [0x12, 0x34, 0x56, 0x78]);
        Assert.Equal(expEnc, enc);
        Assert.Equal(expMac, mac);
    }

    [Fact]
    public void Vector7_LargeData_First32AndMac()
    {
        var plain = new byte[100];
        for (int i = 0; i < 100; i++) plain[i] = (byte)(i & 0xFF);
        var (enc, mac) = Run(0, plain);
        Assert.Equal(new byte[]
        {
            0xca,0x7c,0xeb,0x28,0x6e,0x95,0x17,0xed, 0xc8,0x3c,0xd2,0x8b,0x9a,0xe9,0x2a,0x5c,
            0x25,0x3e,0x58,0x9a,0x57,0x3d,0x30,0x58, 0x79,0x41,0x8d,0xac,0x83,0x28,0xf1,0xa3
        }, enc[..32]);
        Assert.Equal(new byte[] { 0x05, 0x88, 0x75, 0x07 }, mac);
    }

    [Fact]
    public void RoundTrip_DecryptReturnsPlaintext()
    {
        byte[] original = [0x01, 0x02, 0x03, 0x04];
        var (enc, encMac) = Run(0, original);

        var d = new ShannonCipher(Key);
        d.NonceU32(0);
        var dec = (byte[])enc.Clone();
        d.Decrypt(dec);
        var decMac = new byte[4];
        d.Finish(decMac);

        Assert.Equal(original, dec);
        Assert.Equal(encMac, decMac);
    }

    [Fact]
    public void CheckMac_Valid_DoesNotThrow()
    {
        var c = new ShannonCipher(Key);
        c.NonceU32(0);
        var enc = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        c.Encrypt(enc);
        var ex = Record.Exception(() => c.CheckMac([0x80, 0x3a, 0x07, 0x7f]));
        Assert.Null(ex);
    }

    [Fact]
    public void CheckMac_Invalid_Throws()
    {
        var c = new ShannonCipher(Key);
        c.NonceU32(0);
        var enc = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        c.Encrypt(enc);
        Assert.Throws<InvalidDataException>(() => c.CheckMac([0, 0, 0, 0]));
    }
}

public class PkceTests
{
    [Fact]
    public void Challenge_MatchesRfc7636AppendixBVector()
    {
        // RFC 7636 Appendix B ground-truth vector.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
        Assert.Equal(expected, Pkce.Challenge(verifier));
    }

    [Fact]
    public void Verifier_HasLegalLengthAndCharset()
    {
        var v = Pkce.NewVerifier(64);
        Assert.Equal(64, v.Length);
        Assert.All(v, ch => Assert.Contains(ch, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~"));
    }

    [Fact]
    public void Challenge_IsBase64Url_NoPadding()
    {
        var c = Pkce.Challenge(Pkce.NewVerifier());
        Assert.DoesNotContain('=', c);
        Assert.DoesNotContain('+', c);
        Assert.DoesNotContain('/', c);
    }
}

public class Base62Tests
{
    [Fact]
    public void RoundTrip_16Bytes()
    {
        byte[] gid =
        [
            0xde,0xad,0xbe,0xef,0x00,0x11,0x22,0x33, 0x44,0x55,0x66,0x77,0x88,0x99,0xaa,0xbb
        ];
        string id = Base62.Encode(gid);
        Assert.Equal(22, id.Length);
        Assert.Equal(gid, Base62.Decode(id));
    }

    [Fact]
    public void Encode_Zero_IsAllZeros()
    {
        Assert.Equal(new string('0', 22), Base62.Encode(new byte[16]));
    }

    [Fact]
    public void RoundTrip_Random()
    {
        var rnd = new Random(1234);
        for (int n = 0; n < 50; n++)
        {
            var gid = new byte[16];
            rnd.NextBytes(gid);
            Assert.Equal(gid, Base62.Decode(Base62.Encode(gid)));
        }
    }
}
