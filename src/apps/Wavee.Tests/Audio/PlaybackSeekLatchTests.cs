using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Audio;

public sealed class PlaybackSeekLatchTests
{
    [Fact]
    public void PositionAndUnrelatedAcknowledgementCannotReleaseTarget()
    {
        var latch = new PlaybackSeekLatch();
        long revision = latch.Begin(60_000);
        var id = new PlaybackCommandId(1, 4);
        latch.Accept(revision, new(id));
        latch.Observe(new(new(1, 5), 60_000, 60_000, PlaybackOperationStatus.Applied));
        Assert.Equal(60_000, latch.TargetMs);
        latch.Observe(new(id, 60_000, 59_991, PlaybackOperationStatus.Applied));
        Assert.Null(latch.TargetMs);
    }

    [Fact]
    public void AppliedEventCanArriveBeforeAcceptanceContinuation()
    {
        var latch = new PlaybackSeekLatch();
        long revision = latch.Begin(90_000);
        var id = new PlaybackCommandId(7, 10);
        latch.Observe(new(id, 90_000, 90_000, PlaybackOperationStatus.Applied));
        latch.Accept(revision, new(id));
        Assert.Null(latch.TargetMs);
    }

    [Fact]
    public void OlderRequestCannotClearNewerTarget()
    {
        var latch = new PlaybackSeekLatch();
        long old = latch.Begin(1_000);
        long latest = latch.Begin(2_000);
        latch.Accept(old, new(new(1, 1)));
        latch.Reject(old);
        Assert.Equal(2_000, latch.TargetMs);
        latch.Accept(latest, new(new(1, 2)));
        latch.Observe(new(new(1, 1), 1_000, 1_000, PlaybackOperationStatus.Applied));
        Assert.Equal(2_000, latch.TargetMs);
        latch.Observe(new(new(1, 2), 2_000, null, PlaybackOperationStatus.Failed));
        Assert.Null(latch.TargetMs);
    }

    [Fact]
    public void TrackChangeInvalidatesOutstandingAcceptance()
    {
        var latch = new PlaybackSeekLatch();
        long revision = latch.Begin(1_000);
        latch.Clear();
        latch.Accept(revision, new(new(1, 1)));
        Assert.Null(latch.TargetMs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANewItemGenerationReleasesTheTargetEvenWhenUriIsUnchanged(bool acceptanceFirst)
    {
        var latch = new PlaybackSeekLatch();
        long revision = latch.Begin(25_000);
        var receipt = new PlaybackCommandReceipt(new(3, 9));
        if (acceptanceFirst) latch.Accept(revision, receipt);
        latch.Observe(null, itemGeneration: 4);
        if (!acceptanceFirst) latch.Accept(revision, receipt);
        Assert.Null(latch.TargetMs);
    }
}
