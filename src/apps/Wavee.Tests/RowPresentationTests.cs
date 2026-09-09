using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

// The virtualized track row's per-slot VALUE (Operation ultra-fast, P5 slice 1 — see
// docs/plans/wavee/operation-ultra-fast-app-progress.md). RowPresentation is a plain C# `readonly record struct`, so
// its equality is field-by-field by construction; these tests pin the two static factories (Empty/Skeleton) and the
// "an activity-only republish is silent" contract a later slice's gated Memo<RowPresentation> depends on.
public sealed class RowPresentationTests
{
    static Track Song(string id, string title) =>
        new(id, "spotify:track:" + id, title, [], new("album", "spotify:album:album", "Album"), 123000, false, null);

    [Fact]
    public void EmptyIsOutOfRangeSkeletonWithNoTrack()
    {
        var e = RowPresentation.Empty;
        Assert.Equal("", e.Track.Uri);
        Assert.Equal(0, e.DisplayIndex);
        Assert.True(e.IsSkeleton);
        Assert.False(e.IsExpanded);
        Assert.Equal(TrackTitleState.Loading, e.TitleState);
        Assert.Null(e.AddedBy);
        Assert.False(e.HasVideo);
        // Go must be safely invokable (it is what a template's Invoke() handler resolves against a still-Empty slot).
        e.Go("spotify:artist:x", null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(1493)]
    public void SkeletonCarriesTheGivenDisplayPositionAndStaysASkeleton(int displayIndex)
    {
        var s = RowPresentation.Skeleton(displayIndex);
        Assert.Equal(displayIndex, s.DisplayIndex);
        Assert.True(s.IsSkeleton);
        // Skeleton(i) is Empty with only DisplayIndex changed — every other field matches Empty exactly.
        Assert.Equal(RowPresentation.Empty with { DisplayIndex = displayIndex }, s);
    }

    [Fact]
    public void SkeletonAtZeroEqualsEmpty()
        // DisplayIndex 0 is Empty's own default, so Skeleton(0) is Empty by value — a bound source's fallback and a
        // freshly-skeletoned first row are indistinguishable, which is exactly what a recycled slot needs.
        => Assert.Equal(RowPresentation.Empty, RowPresentation.Skeleton(0));

    [Fact]
    public void ValueEqualityHoldsForTwoIdenticallyProjectedRows()
    {
        var track = Song("a", "A");
        var state = new TrackRow.State(IsNow: true, IsPlaying: true, IsBuffering: false, IsTop: false, Saved: true);
        var owner = new Owner("u1", "Someone", null);
        RowPresentation Build() => new(
            track, 3, state, MarqueeDisabled: false, ShowTrackArtist: true, ShowListMetadata: false,
            Go: (uri, ctx) => { }, AddedBy: owner, TitleState: TrackTitleState.Ready, HasVideo: true,
            PlaysState: TrackFactState.Present, IsSkeleton: false, IsExpanded: true);

        var a = Build();
        var b = Build();
        // Two independently built values over the SAME inputs compare equal — this is what "an activity-only
        // republish is silent" rests on: a gated Memo<RowPresentation> (a later slice) never fires downstream when
        // the resolved row is equal to the previous one, even though it is a freshly allocated record each time.
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ChangingOnlyActivityFieldsStillMovesEquality_ButUnrelatedFieldsDoNot()
    {
        var track = Song("a", "A");
        var state = new TrackRow.State(false, false, false, false, false);
        var baseline = new RowPresentation(
            track, 0, state, false, false, false, static (_, _) => { }, null,
            TrackTitleState.Ready, false, TrackFactState.Present, false, false);

        // A field this test's "activity" does not touch (DisplayIndex) staying put keeps the values equal.
        var same = baseline with { };
        Assert.Equal(baseline, same);

        // A field a real activity transition WOULD move (State.IsPlaying) breaks equality — proving the struct's
        // equality is genuinely field-sensitive, not a no-op.
        var playing = baseline with { State = state with { IsPlaying = true } };
        Assert.NotEqual(baseline, playing);
    }

    [Fact]
    public void SkeletonPositionsAreDistinctValues()
        => Assert.NotEqual(RowPresentation.Skeleton(1), RowPresentation.Skeleton(2));
}
