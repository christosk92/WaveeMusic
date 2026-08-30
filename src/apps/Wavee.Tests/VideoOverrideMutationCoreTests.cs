using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class VideoOverrideMutationCoreTests
{
    const string KeyA = "local:video:aaaa";
    const string KeyB = "local:video:bbbb";

    [Fact]
    public void Attach_DoesNotClearHasVideoLatch_ClearsDeadOnly()
    {
        var p = VideoOverrideMutationCore.Plan(OverrideMutationKind.Attach,
            isCurrentPlayable: true, videoAlreadyActive: false,
            previousSourceKey: null, nextSourceKey: KeyA);

        Assert.False(p.ClearHasVideoLatch);
        Assert.True(p.ClearDeadVideoLatch);
        Assert.True(p.CommitHasVideoUpgrade);
        Assert.False(p.ForceReloadIfVideo);
        Assert.True(p.RevealSurfaceIfCurrent);
    }

    [Fact]
    public void Remove_ClearsHasVideoLatch()
    {
        var p = VideoOverrideMutationCore.Plan(OverrideMutationKind.Remove,
            isCurrentPlayable: true, videoAlreadyActive: true,
            previousSourceKey: KeyA, nextSourceKey: null);

        Assert.True(p.ClearHasVideoLatch);
        Assert.True(p.ClearDeadVideoLatch);
        Assert.False(p.ForceReloadIfVideo);
        Assert.False(p.RevealSurfaceIfCurrent);
    }

    [Fact]
    public void Replace_SameKey_NoForceReload()
    {
        var p = VideoOverrideMutationCore.Plan(OverrideMutationKind.Replace,
            isCurrentPlayable: true, videoAlreadyActive: true,
            previousSourceKey: KeyA, nextSourceKey: KeyA);

        Assert.False(p.ClearHasVideoLatch);
        Assert.False(p.ForceReloadIfVideo);
    }

    [Fact]
    public void Replace_KeyChanged_WhileVideoActive_ForceReload()
    {
        var p = VideoOverrideMutationCore.Plan(OverrideMutationKind.Replace,
            isCurrentPlayable: true, videoAlreadyActive: true,
            previousSourceKey: KeyA, nextSourceKey: KeyB);

        Assert.False(p.ClearHasVideoLatch);
        Assert.True(p.ForceReloadIfVideo);
        Assert.True(p.RevealSurfaceIfCurrent);   // reveal is gated by CanReveal (alreadyActive → no OpenAt)
    }

    [Fact]
    public void Attach_WhileAudio_NoForceReload()
    {
        var p = VideoOverrideMutationCore.Plan(OverrideMutationKind.Attach,
            isCurrentPlayable: true, videoAlreadyActive: false,
            previousSourceKey: null, nextSourceKey: KeyA);

        Assert.False(p.ForceReloadIfVideo);   // availability edge alone does Audio→Video
    }

    [Fact]
    public void IsRealTrackBoundary_NullNext_IsFalse()
    {
        Assert.False(VideoOverrideMutationCore.IsRealTrackBoundary("spotify:track:a", null));
        Assert.False(VideoOverrideMutationCore.IsRealTrackBoundary("spotify:track:a", ""));
        Assert.False(VideoOverrideMutationCore.IsRealTrackBoundary(null, "spotify:track:a"));
        Assert.False(VideoOverrideMutationCore.IsRealTrackBoundary("spotify:track:a", "spotify:track:a"));
        Assert.True(VideoOverrideMutationCore.IsRealTrackBoundary("spotify:track:a", "spotify:track:b"));
    }

    [Fact]
    public void CanReveal_RequiresHasVideoCommitted()
    {
        Assert.False(VideoOverrideMutationCore.CanReveal(isCurrent: true, hasVideoCommitted: false, alreadyActive: false));
        Assert.False(VideoOverrideMutationCore.CanReveal(isCurrent: false, hasVideoCommitted: true, alreadyActive: false));
        Assert.False(VideoOverrideMutationCore.CanReveal(isCurrent: true, hasVideoCommitted: true, alreadyActive: true));
        Assert.True(VideoOverrideMutationCore.CanReveal(isCurrent: true, hasVideoCommitted: true, alreadyActive: false));
    }

    [Fact]
    public void MountStage_WhenPlayerNonNull_EvenIfSourceNull()
    {
        Assert.True(VideoSurfaceMount.ShouldMountPlayerStage(playerPresent: true));
        Assert.False(VideoSurfaceMount.ShouldMountPlayerStage(playerPresent: false));
    }

    [Fact]
    public void AttachPolicy_PreservesGlitchSuppression()
    {
        // After an attach plan (no ClearFor has), a transient has=false / null uri must still be suppressed.
        string? latched = null;
        Assert.True(HasVideoLatch.Apply(true, "spotify:track:a", ref latched));
        var plan = VideoOverrideMutationCore.Plan(OverrideMutationKind.Attach,
            isCurrentPlayable: true, videoAlreadyActive: false, null, KeyA);
        if (plan.ClearHasVideoLatch) HasVideoLatch.ClearFor("spotify:track:a", ref latched);

        Assert.True(HasVideoLatch.Apply(false, null, ref latched));
        Assert.Equal("spotify:track:a", latched);
    }

    [Fact]
    public void PushStateBoundary_NullFlicker_DoesNotClearLatch()
    {
        string? latched = null;
        Assert.True(HasVideoLatch.Apply(true, "spotify:track:a", ref latched));

        // Mid-push null: NOT a real boundary → latch stays; Apply(false, null) still suppresses.
        Assert.False(VideoOverrideMutationCore.IsRealTrackBoundary("spotify:track:a", null));
        Assert.True(HasVideoLatch.Apply(false, null, ref latched));

        // Real boundary a→b: clear latches (bridge does this), then b can start clean.
        Assert.True(VideoOverrideMutationCore.IsRealTrackBoundary("spotify:track:a", "spotify:track:b"));
        HasVideoLatch.ClearFor("spotify:track:a", ref latched);
        Assert.Null(latched);
    }

    [Fact]
    public void RemovePolicy_AllowsTrueToFalse()
    {
        string? latched = null;
        Assert.True(HasVideoLatch.Apply(true, "spotify:track:a", ref latched));
        var plan = VideoOverrideMutationCore.Plan(OverrideMutationKind.Remove,
            isCurrentPlayable: true, videoAlreadyActive: true, KeyA, null);
        if (plan.ClearHasVideoLatch) HasVideoLatch.ClearFor("spotify:track:a", ref latched);

        Assert.False(HasVideoLatch.Apply(false, "spotify:track:a", ref latched));
    }
}
