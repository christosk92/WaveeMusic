using System;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Audio;

// The prepared-next decision table (docs/plans/wavee/gapless-findings.md fix design §1/§5). These are the rules that used
// to live inline in PlaybackController.SchedulePreparedNext, where the only way to check them was to play music.
public class PreparedNextPolicyTests
{
    static Track T(string uri, string? source = null) =>
        new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri, Array.Empty<ArtistRef>(),
            new AlbumRef("", "spotify:album:al", "Al"), 200_000, false, null, Source: source);

    static QueueEntry E(ulong id, string uri, string? source = null) =>
        new(new QueueItemId(id), "e" + id, T(uri, source), QueueBucket.NextUp, QueueProvider.Context, false);

    static QueueEntry Music(ulong id) => E(id, "spotify:track:t" + id);
    static QueueEntry Episode(ulong id) => E(id, "spotify:episode:e" + id);

    static PreparedNextPolicy.PrepareDecision Decide(
        QueueEntry? current, QueueEntry? next,
        PlayableKind currentKind = PlayableKind.Audio, PlayableKind nextKind = PlayableKind.Audio,
        bool nextMayPrepare = true, RepeatMode repeat = RepeatMode.Off)
        => PreparedNextPolicy.Decide(currentKind, current, next, nextKind, nextMayPrepare, repeat);

    // ── what gets prepared, and whether the boundary may overlap ─────────────────────────────────────────────────────

    [Fact]
    public void LinearAudioBoundary_PreparesAndAllowsOverlap()
    {
        var d = Decide(Music(1), Music(2));

        Assert.True(d.Prepare);
        Assert.True(d.AllowOverlap);        // music → music: the gapless/crossfade join is legal
        Assert.NotNull(d.Signature);
    }

    [Fact]
    public void NoNext_PreparesNothing_AndCancelsAnyPriorToken()
    {
        var d = Decide(Music(1), null);

        Assert.False(d.Prepare);
        Assert.Null(d.Signature);           // a null signature is the cancel signal
    }

    [Fact]
    public void NoCurrent_PreparesNothing()
    {
        Assert.False(Decide(null, Music(2)).Prepare);
    }

    [Fact]
    public void VideoOnEitherSide_PreparesNothing()
    {
        Assert.False(Decide(Music(1), Music(2), nextKind: PlayableKind.Video).Prepare);
        Assert.False(Decide(Music(1), Music(2), currentKind: PlayableKind.Video).Prepare);
    }

    [Fact]
    public void AGatedNext_PreparesNothing()
    {
        // The provider seam can refuse a prepare (no key, unplayable, local file rules) — the policy must honour it.
        Assert.False(Decide(Music(1), Music(2), nextMayPrepare: false).Prepare);
    }

    [Fact]
    public void RepeatTrack_StillPrepares_ButRefusesOverlap()
    {
        // Repeat-one re-enters the same track: preparing is useful, overlapping it with itself is not.
        var d = Decide(Music(1), Music(2), repeat: RepeatMode.Track);

        Assert.True(d.Prepare);
        Assert.False(d.AllowOverlap);
    }

    [Fact]
    public void SpokenContent_PreparesButNeverOverlaps()
    {
        Assert.True(Decide(Episode(1), Episode(2)).Prepare);
        Assert.False(Decide(Episode(1), Episode(2)).AllowOverlap);
        Assert.False(Decide(Music(1), Episode(2)).AllowOverlap);      // one spoken side is enough to refuse
        Assert.False(Decide(Episode(1), Music(2)).AllowOverlap);
        Assert.False(Decide(E(1, "spotify:track:x", source: "podcast-feed"), Music(2)).AllowOverlap);
    }

    // ── the dedupe signature ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Signature_IsStableForTheSamePair_SoARescheduleIsANoOp()
    {
        Assert.Equal(Decide(Music(1), Music(2)).Signature, Decide(Music(1), Music(2)).Signature);
    }

    [Fact]
    public void Signature_TracksThePairAndTheOverlapDecision()
    {
        string? linear = Decide(Music(1), Music(2)).Signature;

        Assert.NotEqual(linear, Decide(Music(1), Music(3)).Signature);                          // next changed
        Assert.NotEqual(linear, Decide(Music(9), Music(2)).Signature);                          // current changed
        Assert.NotEqual(linear, Decide(Music(1), Music(2), repeat: RepeatMode.Track).Signature); // overlap changed
    }

    // ── the ending-soon window (the remaining-ms re-arm) ─────────────────────────────────────────────────────────────

    [Fact]
    public void EndingSoonMargin_IsOverlapPlusTheWorstCasePrimeBudget()
    {
        Assert.Equal(PreparedNextPolicy.WorstCasePrimeMs, PreparedNextPolicy.EndingSoonMarginMs(300_000, 0));
        Assert.Equal(PreparedNextPolicy.WorstCasePrimeMs + 5_000, PreparedNextPolicy.EndingSoonMarginMs(300_000, 5_000));
    }

    [Fact]
    public void AShortTrackSpendsItsWholeLengthAsThePrepareBudget()
    {
        // A 4 s interlude is shorter than the margin: clamping to the duration keeps the window meaningful instead of
        // "ending soon" being true before the track starts.
        Assert.Equal(4_000, PreparedNextPolicy.EndingSoonMarginMs(4_000, 0));
        Assert.True(PreparedNextPolicy.IsEndingSoon(4_000, positionMs: 0, overlapMs: 0));
    }

    [Fact]
    public void IsEndingSoon_OpensExactlyAtTheMargin()
    {
        const long dur = 200_000;
        int margin = PreparedNextPolicy.WorstCasePrimeMs;

        Assert.False(PreparedNextPolicy.IsEndingSoon(dur, dur - margin - 1, 0));
        Assert.True(PreparedNextPolicy.IsEndingSoon(dur, dur - margin, 0));
        Assert.True(PreparedNextPolicy.IsEndingSoon(dur, dur, 0));
    }

    [Fact]
    public void AnUnknownDuration_NeverClaimsToBeEndingSoon()
    {
        // A live/streaming source with no duration must not trigger an endless prepare storm.
        Assert.False(PreparedNextPolicy.IsEndingSoon(0, 10_000, 0));
        Assert.False(PreparedNextPolicy.SeekRequiresRearm(0, 10_000, 0));
    }

    [Fact]
    public void ASeekIntoTheTail_RearmsThePrepare_AndASeekToTheHeadDoesNot()
    {
        // The bug this closes: scrubbing to the last few seconds left the next track unprepared, so the boundary fell
        // back to a full reload — an audible gap exactly where the user was listening for one.
        const long dur = 200_000;

        Assert.True(PreparedNextPolicy.SeekRequiresRearm(dur, seekToMs: dur - 2_000, overlapMs: 0));
        Assert.False(PreparedNextPolicy.SeekRequiresRearm(dur, seekToMs: 1_000, overlapMs: 0));
    }

    [Fact]
    public void ALongCrossfadeOpensTheWindowEarlier()
    {
        const long dur = 200_000;
        long remainingAt12s = dur - 12_000;

        Assert.False(PreparedNextPolicy.IsEndingSoon(dur, remainingAt12s, overlapMs: 0));       // 8 s margin: not yet
        Assert.True(PreparedNextPolicy.IsEndingSoon(dur, remainingAt12s, overlapMs: 6_000));    // 14 s margin: open
    }
}
