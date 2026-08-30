using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// Stage F — the proto-free audio-key correlation engine (0x0c request body shape, 0x0d/0x0e response routing).
public class ConnectAudioKeyTests
{
    static byte[] Key0d(uint seq, byte[] key16)
    {
        var p = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(p, seq);
        key16.CopyTo(p.AsSpan(4));
        return p;
    }

    [Fact]
    public async Task RequestBodyShape_AndCompletesOnAesKey()
    {
        var d = new AudioKeyDispatcher();
        var fileId = new byte[20]; for (int i = 0; i < 20; i++) fileId[i] = (byte)i;
        var gid = new byte[16]; for (int i = 0; i < 16; i++) gid[i] = (byte)(100 + i);

        var (body, task) = d.Begin(fileId, gid);
        Assert.Equal(42, body.Length);                 // 20 + 16 + 4 + 2
        Assert.Equal(fileId, body[0..20]);
        Assert.Equal(gid, body[20..36]);
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(36, 4));
        Assert.Equal(0u, seq);
        Assert.Equal(0, body[40]); Assert.Equal(0, body[41]);

        var key = new byte[16]; for (int i = 0; i < 16; i++) key[i] = (byte)(200 + i);
        d.OnAesKey(Key0d(seq, key));
        Assert.Equal(key, await task);
    }

    [Fact]
    public async Task SeqIncrements_AndErrorFailsTheRightWaiter()
    {
        var d = new AudioKeyDispatcher();
        var (b1, t1) = d.Begin(new byte[20], new byte[16]);
        var (b2, t2) = d.Begin(new byte[20], new byte[16]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(b1.AsSpan(36, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(b2.AsSpan(36, 4)));

        var err = new byte[6]; BinaryPrimitives.WriteUInt32BigEndian(err, 0u); err[4] = 0; err[5] = 2;
        d.OnAesKeyError(err);
        await Assert.ThrowsAnyAsync<Exception>(async () => await t1);

        d.OnAesKey(Key0d(1u, new byte[16]));
        Assert.Equal(16, (await t2).Length);
    }

    [Fact]
    public async Task FailAll_FailsPendingWaiters()
    {
        var d = new AudioKeyDispatcher();
        var (_, t) = d.Begin(new byte[20], new byte[16]);
        d.FailAll(new InvalidOperationException("ap dropped"));
        await Assert.ThrowsAnyAsync<Exception>(async () => await t);
    }

    [Fact]
    public async Task StubAudioKeySource_Returns16Zeroes()
    {
        var key = await new StubAudioKeySource().GetKeyAsync(new byte[20], new byte[16]);
        Assert.Equal(16, key.Length);
        Assert.True(key.ToArray().AsSpan().IndexOfAnyExcept((byte)0) < 0);   // all zero
    }
}
