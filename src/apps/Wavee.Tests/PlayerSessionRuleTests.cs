// ── Wavee.Tests/PlayerSessionRuleTests.cs — which play mints, adopts or keeps the player session ──────────────────
//
// `Playback.PlayerSession` is the pure rule behind put-state's session_id and a stream report's player_session_id,
// pinned from the official client's captures: a local play mints (even in the same context), an advance keeps, a remote
// play adopts what it offers, a transfer mints. Ids here are hand-made.

using System;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class PlayerSessionRuleTests
{
    static readonly UInt128 Current = 1111;
    static readonly UInt128 Offered = 2222;
    static readonly UInt128 Minted = 3333;

    static UInt128 Next(UInt128 current, Playback.SessionCause cause, UInt128 offered = default)
        => Playback.PlayerSession.NextSession(current, cause, offered, static () => Minted);

    [Fact]
    public void ALocalPlayMintsANewSessionEvenInTheSameContext()
        => Assert.Equal(Minted, Next(Current, Playback.SessionCause.LocalPlay));

    [Fact]
    public void AnAdvanceKeepsTheSession()
    {
        Assert.Equal(Current, Next(Current, Playback.SessionCause.Advance));
        Assert.Equal(Minted, Next(UInt128.Zero, Playback.SessionCause.Advance));      // none yet: mint one
    }

    [Fact]
    public void ARemotePlayAdoptsTheOfferedSessionId()
        => Assert.Equal(Offered, Next(Current, Playback.SessionCause.RemotePlay, Offered));

    [Fact]
    public void ARemotePlayWithoutAnOfferMints()
        => Assert.Equal(Minted, Next(Current, Playback.SessionCause.RemotePlay));

    [Fact]
    public void ATransferMintsANewSession()
        => Assert.Equal(Minted, Next(Current, Playback.SessionCause.Transfer, Offered));

    [Fact]
    public void AMintedIdIs22Base62Chars()
    {
        string text = Playback.PlayerSession.TelemetryText(Minted, video: false);
        Assert.Equal(22, text.Length);
        foreach (char c in text) Assert.True(char.IsAsciiLetterOrDigit(c));
    }

    [Theory]
    [InlineData(false, Playback.ClaimCause.UserPlay, false, Playback.SessionCause.Advance)]       // not a Play: a skip, a join
    [InlineData(true, Playback.ClaimCause.UserPlay, false, Playback.SessionCause.LocalPlay)]
    [InlineData(true, Playback.ClaimCause.InboundPlay, false, Playback.SessionCause.RemotePlay)]
    [InlineData(true, Playback.ClaimCause.InboundTransfer, false, Playback.SessionCause.Transfer)]
    [InlineData(true, Playback.ClaimCause.NobodyTransferToSelf, false, Playback.SessionCause.Transfer)]
    [InlineData(true, Playback.ClaimCause.UserPlay, true, Playback.SessionCause.Transfer)]        // a takeover of the mirrored row
    [InlineData(true, Playback.ClaimCause.InboundSkip, false, Playback.SessionCause.Advance)]
    public void ThePlayMapsToItsCause(bool isPlay, Playback.ClaimCause claim, bool takeover, Playback.SessionCause expected)
        => Assert.Equal(expected, Playback.PlayerSession.CauseOf(isPlay, claim, takeover));
}
