using System;
using System.IO;
using Wavee.Backend.Audio;
using Wavee.Core;
using Wavee.Sdk.Streams;
using Xunit;

namespace Wavee.Tests.Audio;

/// <summary>The two engine outcomes a live session has to RE-READ, pinned away from WASAPI and sockets: "Ended" means
/// dropped (retry this playable) rather than finished (advance the queue), and a Playing tick during a reconnect has to
/// keep carrying the recovery kind or the projection clears it on the very next tick.</summary>
public class LiveSessionRulesTests
{
    [Fact]
    public void ALiveEnded_IsReportedAsADrop()
    {
        Assert.True(LiveSessionRules.ShouldReportDropInsteadOfEnded(isLive: true, alreadyReported: false));
    }

    [Fact]
    public void ADropIsReportedOnlyOnce()
    {
        Assert.False(LiveSessionRules.ShouldReportDropInsteadOfEnded(isLive: true, alreadyReported: true));
    }

    [Fact]
    public void AFiniteTrackKeepsTheOrdinaryEndedPath()
    {
        Assert.False(LiveSessionRules.ShouldReportDropInsteadOfEnded(isLive: false, alreadyReported: false));
        Assert.False(LiveSessionRules.ShouldReportDropInsteadOfEnded(isLive: false, alreadyReported: true));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void OnlyALiveReconnectEmitsTheRecoveringTick(bool isLive, bool recovering, bool expected)
    {
        Assert.Equal(expected, LiveSessionRules.ShouldEmitRecoveringTick(isLive, recovering));
    }

    [Fact]
    public void TheRecoveringTickCarriesNetwork()
    {
        Assert.Equal(PlaybackRecoveryKind.Network, LiveSessionRules.TickRecoveryKind(isLive: true, recovering: true));
        Assert.Equal(PlaybackRecoveryKind.None, LiveSessionRules.TickRecoveryKind(isLive: true, recovering: false));
        Assert.Equal(PlaybackRecoveryKind.None, LiveSessionRules.TickRecoveryKind(isLive: false, recovering: true));
    }

    [Theory]
    [InlineData(AudioNetworkRecoveryStage.Started, true)]
    [InlineData(AudioNetworkRecoveryStage.Attempt, true)]
    [InlineData(AudioNetworkRecoveryStage.Recovered, false)]
    [InlineData(AudioNetworkRecoveryStage.Exhausted, false)]
    [InlineData(AudioNetworkRecoveryStage.Cancelled, false)]
    public void RecoveringIsArmedByStartedAndAttempt(AudioNetworkRecoveryStage stage, bool recovering)
    {
        Assert.Equal(recovering, LiveSessionRules.IsRecovering(stage));
    }

    [Theory]
    [InlineData(AudioNetworkRecoveryStage.Exhausted, true)]
    [InlineData(AudioNetworkRecoveryStage.Started, false)]
    [InlineData(AudioNetworkRecoveryStage.Recovered, false)]
    public void OnlyExhaustionIsTerminal(AudioNetworkRecoveryStage stage, bool terminal)
    {
        Assert.Equal(terminal, LiveSessionRules.IsTerminal(stage));
    }

    [Fact]
    public void DropReason_PrefersTheTypedFetchFailure()
    {
        var typed = new AudioRangeFetchException(StreamFailureReason.Restricted, "station", 0, 0, 3, 1000, null);
        Assert.Equal(AudioKeyFailureReason.Restricted, LiveSessionRules.DropReason(typed));
    }

    [Fact]
    public void DropReason_DefaultsToNetwork()
    {
        Assert.Equal(AudioKeyFailureReason.Network, LiveSessionRules.DropReason(null));
        Assert.Equal(AudioKeyFailureReason.Network, LiveSessionRules.DropReason(new IOException("socket died")));
    }

    [Fact]
    public void ALiveSessionDeclaresNoDuration()
    {
        // The duration is what arms ending-soon / gapless-join / prepared-next; a live session must arm none of them
        // even if a resolver optimistically reported one.
        Assert.Equal(0, LiveSessionRules.SessionDurationMs(isLive: true, declaredMs: 240_000));
        Assert.Equal(240_000, LiveSessionRules.SessionDurationMs(isLive: false, declaredMs: 240_000));
    }
}
