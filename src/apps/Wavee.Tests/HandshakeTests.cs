using Wavee.Backend.Spotify;
using Xunit;

namespace Wavee.Tests;

public class DiffieHellmanTests
{
    [Fact]
    public void SharedSecret_IsSymmetric()
    {
        var a = DiffieHellman.Generate();
        var b = DiffieHellman.Generate();
        Assert.Equal(a.SharedSecret(b.PublicKey), b.SharedSecret(a.PublicKey));   // the DH property
    }

    [Fact]
    public void PublicKey_IsAbout96Bytes()
    {
        var k = DiffieHellman.Generate();
        Assert.InRange(k.PublicKey.Length, 90, 96);   // 768-bit modulus
    }

    [Fact]
    public void DifferentKeyPairs_ProduceDifferentSecrets()
    {
        var a = DiffieHellman.Generate();
        var b = DiffieHellman.Generate();
        var c = DiffieHellman.Generate();
        Assert.NotEqual(a.SharedSecret(b.PublicKey), a.SharedSecret(c.PublicKey));
    }
}

public class ApKeyDerivationTests
{
    [Fact]
    public void Derive_IsDeterministic_AndCorrectLengths()
    {
        byte[] secret = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] acc = [10, 20, 30, 40, 50];

        var k1 = ApKeyDerivation.Derive(secret, acc);
        var k2 = ApKeyDerivation.Derive(secret, acc);

        Assert.Equal(20, k1.Challenge.Length);
        Assert.Equal(32, k1.SendKey.Length);
        Assert.Equal(32, k1.ReceiveKey.Length);
        Assert.Equal(k1.SendKey, k2.SendKey);          // deterministic
        Assert.Equal(k1.Challenge, k2.Challenge);
        Assert.NotEqual(k1.SendKey, k1.ReceiveKey);    // send != receive
    }

    [Fact]
    public void Derive_DifferentSecret_DifferentKeys()
    {
        byte[] acc = [10, 20, 30];
        var a = ApKeyDerivation.Derive([1, 1, 1, 1], acc);
        var b = ApKeyDerivation.Derive([2, 2, 2, 2], acc);
        Assert.NotEqual(a.SendKey, b.SendKey);
    }
}

public class ApCodecTests
{
    // client.send == server.recv (k1); server.send == client.recv (k2) — the real directional keying.
    static (ApCodec client, ApCodec server) Pair()
    {
        var k1 = new byte[32]; new Random(1).NextBytes(k1);
        var k2 = new byte[32]; new Random(2).NextBytes(k2);
        return (new ApCodec(k1, k2), new ApCodec(k2, k1));
    }

    [Fact]
    public void RoundTrip_ClientToServer()
    {
        var (client, server) = Pair();
        byte[] payload = [0xaa, 0xbb, 0xcc, 0xdd, 0xee];
        var frame = client.Encode(0x04, payload);
        var (cmd, got) = server.Decode(frame);
        Assert.Equal(0x04, cmd);
        Assert.Equal(payload, got);
    }

    [Fact]
    public void RoundTrip_ServerToClient_AndEmptyPayload()
    {
        var (client, server) = Pair();
        var frame = server.Encode(0x09, []);
        var (cmd, got) = client.Decode(frame);
        Assert.Equal(0x09, cmd);
        Assert.Empty(got);
    }

    [Fact]
    public void NonceIncrements_SamePacketEncodesDifferently()
    {
        var (client, _) = Pair();
        byte[] payload = [1, 2, 3, 4];
        var f1 = client.Encode(0x04, payload);
        var f2 = client.Encode(0x04, payload);
        Assert.NotEqual(f1, f2);   // nonce advanced ⇒ different keystream
    }

    [Fact]
    public void TamperedFrame_FailsMacCheck()
    {
        var (client, server) = Pair();
        var frame = client.Encode(0x04, [1, 2, 3, 4]);
        frame[5] ^= 0xFF;   // flip a payload byte
        Assert.ThrowsAny<Exception>(() => server.Decode(frame));
    }

    [Fact]
    public void SequencedPackets_RoundTripInOrder()
    {
        var (client, server) = Pair();
        for (int i = 0; i < 5; i++)
        {
            byte[] payload = [(byte)i, (byte)(i + 1)];
            var (cmd, got) = server.Decode(client.Encode((byte)(0x10 + i), payload));
            Assert.Equal((byte)(0x10 + i), cmd);
            Assert.Equal(payload, got);
        }
    }
}
