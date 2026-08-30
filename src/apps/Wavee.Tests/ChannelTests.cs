using Wavee.Backend.Spotify;
using Xunit;

namespace Wavee.Tests;

public class SpotifyApChannelTests
{
    static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    static async Task<(SpotifyApChannel client, SpotifyApChannel server)> HandshakeAsync()
    {
        var (a, b) = InMemoryDuplex.Pair();
        var ct = Timeout;
        var clientTask = SpotifyApChannel.ConnectClientAsync(a, ct);
        var serverTask = SpotifyApChannel.AcceptServerAsync(b, ct);
        await Task.WhenAll(clientTask, serverTask);
        return (clientTask.Result, serverTask.Result);
    }

    [Fact]
    public async Task FullHandshake_ThenClientToServer_EncryptedPacket_RoundTrips()
    {
        var (client, server) = await HandshakeAsync();   // DH exchange + HMAC-SHA1 key derivation over the duplex

        byte[] payload = [0xde, 0xad, 0xbe, 0xef, 0x01, 0x02];
        await client.SendAsync(0x04, payload, Timeout);
        var (cmd, got) = await server.ReceiveAsync(Timeout);

        Assert.Equal(0x04, cmd);
        Assert.Equal(payload, got);   // the derived keys + Shannon codec line up end-to-end
    }

    [Fact]
    public async Task FullHandshake_ServerToClient_AlsoRoundTrips()
    {
        var (client, server) = await HandshakeAsync();
        byte[] payload = [1, 2, 3];
        await server.SendAsync(0x09, payload, Timeout);
        var (cmd, got) = await client.ReceiveAsync(Timeout);
        Assert.Equal(0x09, cmd);
        Assert.Equal(payload, got);
    }

    [Fact]
    public async Task FullHandshake_SequencedPackets_StayInSyncAcrossNonces()
    {
        var (client, server) = await HandshakeAsync();
        for (int i = 0; i < 8; i++)
        {
            byte[] p = [(byte)i, (byte)(i * 2), (byte)(i * 3)];
            await client.SendAsync((byte)(0x20 + i), p, Timeout);
            var (cmd, got) = await server.ReceiveAsync(Timeout);
            Assert.Equal((byte)(0x20 + i), cmd);
            Assert.Equal(p, got);   // per-packet nonce increments stay synchronized
        }
    }
}

public class MercuryTests
{
    [Fact]
    public void Encode_Decode_RoundTrip()
    {
        var msg = new MercuryMessage(
            Sequence: [0, 0, 0, 0, 0, 0, 0, 7],
            Flags: 1,
            Parts: [[0xaa, 0xbb], [], [0x01, 0x02, 0x03, 0x04, 0x05]]);

        var round = MercuryMessage.Decode(msg.Encode());

        Assert.Equal(msg.Sequence, round.Sequence);
        Assert.Equal(msg.Flags, round.Flags);
        Assert.Equal(3, round.Parts.Count);
        Assert.Equal(new byte[] { 0xaa, 0xbb }, round.Parts[0]);
        Assert.Empty(round.Parts[1]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, round.Parts[2]);
    }

    [Fact]
    public void Decode_NoParts()
    {
        var msg = new MercuryMessage([1, 2], 0, []);
        var round = MercuryMessage.Decode(msg.Encode());
        Assert.Empty(round.Parts);
        Assert.Equal(new byte[] { 1, 2 }, round.Sequence);
    }
}
