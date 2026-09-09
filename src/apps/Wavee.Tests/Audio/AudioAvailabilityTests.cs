using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

public sealed class AudioAvailabilityTests
{
    [Fact]
    public async Task CancellationInterruptsDecoderWaitWithoutReturningEof()
    {
        using var http = new HttpClient();
        using var source = SpotifyAudioStream.CreateHeadOnly(http, ReadOnlyMemory<byte>.Empty, 0);
        using var cancellation = new CancellationTokenSource();
        using var view = new PrefetchingReadStream(source, 0, cancellation.Token);
        Task<int> read = Task.Run(() => view.Read(new byte[16]));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AvailabilityPublishedBetweenReadAndWaitIsNotLost()
    {
        var available = new AudioDataAvailability();
        long observed = available.Version;
        available.Pulse();
        await Task.Run(() => available.Wait(observed, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AbortedBodyWakesDecoderAndReportsFailure()
    {
        using var http = new HttpClient();
        using var source = SpotifyAudioStream.CreateHeadOnly(http, ReadOnlyMemory<byte>.Empty, 0);
        using var view = new PrefetchingReadStream(source, 0);
        Task<int> read = Task.Run(() => view.Read(new byte[16]));
        source.AbortBody(new IOException("body failed"));
        var error = await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("body failed", error.Message);
    }

    [Fact]
    public void LiveRingDistinguishesTemporaryMissFromDrainedEof()
    {
        using var ring = new LiveRingBuffer(1024);
        Span<byte> bytes = stackalloc byte[8];
        Assert.Equal(0, ring.TryRead(bytes, out bool wouldBlock));
        Assert.True(wouldBlock);
        ring.Write(new byte[] { 1, 2, 3 });
        Assert.Equal(3, ring.TryRead(bytes, out wouldBlock));
        Assert.False(wouldBlock);
        ring.Complete();
        Assert.Equal(0, ring.TryRead(bytes, out wouldBlock));
        Assert.False(wouldBlock);
    }

    [Fact]
    public async Task LiveRingCancellationWakesAvailabilityWait()
    {
        using var ring = new LiveRingBuffer(1024);
        using var cancellation = new CancellationTokenSource();
        long version = ring.DataVersion;
        Task wait = Task.Run(() => ring.WaitForData(version, cancellation.Token));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
