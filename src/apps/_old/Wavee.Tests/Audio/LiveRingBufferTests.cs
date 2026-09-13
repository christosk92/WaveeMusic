using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

/// <summary>The live ring's two asymmetric contracts: writes never block (they drop the oldest), reads DO block
/// (returning 0 would read as end-of-stream at the decode edge and end the station).</summary>
public class LiveRingBufferTests
{
    static byte[] Ramp(int n, int seed = 0)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(seed + i);
        return b;
    }

    [Fact]
    public void Read_ReturnsWrittenBytes_InOrder()
    {
        using var ring = new LiveRingBuffer(4096);
        ring.Write(Ramp(10));
        var dst = new byte[10];
        Assert.Equal(10, ring.Read(dst));
        Assert.Equal(Ramp(10), dst);
        Assert.Equal(0, ring.Available);
    }

    [Fact]
    public void Read_WrapsAroundTheRing()
    {
        using var ring = new LiveRingBuffer(1024);
        ring.Write(Ramp(1000));
        var drain = new byte[900];
        Assert.Equal(900, ring.Read(drain));
        ring.Write(Ramp(800, seed: 7));      // wraps past the physical end
        var rest = new byte[900];
        int n = ring.Read(rest);
        Assert.Equal(900, n);
        // 100 bytes of the first ramp, then the second ramp.
        Assert.Equal(unchecked((byte)900), rest[0]);
        Assert.Equal((byte)7, rest[100]);
        Assert.Equal(unchecked((byte)(7 + 799)), rest[899]);
    }

    [Fact]
    public void Write_NeverBlocks_AndDropsOldestOnOverrun()
    {
        using var ring = new LiveRingBuffer(1024);
        ring.Write(Ramp(1024, seed: 1));
        Assert.Equal(0, ring.TotalDropped);
        ring.Write(Ramp(100, seed: 200));
        Assert.Equal(100, ring.TotalDropped);
        Assert.Equal(1024, ring.Available);

        var dst = new byte[1024];
        ring.Read(dst);
        // The first 100 bytes are gone; the buffer now starts at ramp byte 100 and ends with the newest write.
        Assert.Equal((byte)(1 + 100), dst[0]);
        Assert.Equal(unchecked((byte)(200 + 99)), dst[1023]);
    }

    [Fact]
    public void Write_LargerThanCapacity_KeepsOnlyTheTail()
    {
        using var ring = new LiveRingBuffer(1024);
        ring.Write(Ramp(4096));
        Assert.Equal(1024, ring.Available);
        Assert.Equal(4096 - 1024, ring.TotalDropped);
        var dst = new byte[1024];
        ring.Read(dst);
        Assert.Equal(unchecked((byte)(4096 - 1024)), dst[0]);
    }

    [Fact]
    public void Read_BlocksUntilDataArrives()
    {
        using var ring = new LiveRingBuffer(4096);
        var reader = Task.Run(() =>
        {
            var dst = new byte[8];
            return ring.Read(dst);
        });
        Assert.False(reader.Wait(150), "the read must block while the ring is empty");
        ring.Write(Ramp(8));
        Assert.True(reader.Wait(5000));
        Assert.Equal(8, reader.Result);
    }

    [Fact]
    public void Read_HonoursMinAvailable_AsPrefill()
    {
        using var ring = new LiveRingBuffer(8192);
        var reader = Task.Run(() =>
        {
            var dst = new byte[4096];
            return ring.Read(dst, minAvailable: 2048);
        });
        ring.Write(Ramp(1000));
        Assert.False(reader.Wait(150), "1000 bytes is under the 2048 prefill");
        ring.Write(Ramp(1100, seed: 50));
        Assert.True(reader.Wait(5000));
        Assert.Equal(2100, reader.Result);
    }

    [Fact]
    public void Complete_WithoutError_DrainsThenReturnsZero()
    {
        using var ring = new LiveRingBuffer(4096);
        ring.Write(Ramp(5));
        ring.Complete();
        var dst = new byte[64];
        Assert.Equal(5, ring.Read(dst));
        Assert.Equal(0, ring.Read(dst));
    }

    [Fact]
    public void Complete_WithError_DrainsThenThrowsIt()
    {
        using var ring = new LiveRingBuffer(4096);
        ring.Write(Ramp(5));
        var boom = new InvalidOperationException("dropped");
        ring.Complete(boom);
        var dst = new byte[64];
        Assert.Equal(5, ring.Read(dst));
        var thrown = Assert.Throws<InvalidOperationException>(() => ring.Read(dst));
        Assert.Same(boom, thrown);
    }

    [Fact]
    public void Complete_WakesABlockedReader()
    {
        using var ring = new LiveRingBuffer(4096);
        var reader = Task.Run(() => ring.Read(new byte[16]));
        Assert.False(reader.Wait(120));
        ring.Complete();
        Assert.True(reader.Wait(5000));
        Assert.Equal(0, reader.Result);
    }

    [Fact]
    public void Dispose_WakesABlockedReader_WithObjectDisposed()
    {
        var ring = new LiveRingBuffer(4096);
        var reader = Task.Run(() =>
        {
            try { ring.Read(new byte[16]); return "read"; }
            catch (ObjectDisposedException) { return "disposed"; }
        });
        Assert.False(reader.Wait(120));
        ring.Dispose();
        Assert.True(reader.Wait(5000));
        Assert.Equal("disposed", reader.Result);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var ring = new LiveRingBuffer(2048);
        ring.Dispose();
        ring.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ring.Read(new byte[4]));
    }

    [Fact]
    public void Peek_DoesNotConsume()
    {
        using var ring = new LiveRingBuffer(4096);
        ring.Write(Ramp(32));
        var peek = new byte[16];
        Assert.Equal(16, ring.Peek(peek));
        Assert.Equal(32, ring.Available);
        Assert.Equal(Ramp(16), peek);
    }

    [Fact]
    public void WaitForAvailable_TimesOutWithoutData()
    {
        using var ring = new LiveRingBuffer(4096);
        Assert.False(ring.WaitForAvailable(64, timeoutMs: 60));
        ring.Write(Ramp(64));
        Assert.True(ring.WaitForAvailable(64, timeoutMs: 60));
    }
}
