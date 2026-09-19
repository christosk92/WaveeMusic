// ── Wavee.Tests/AlbumPageRulesTests.cs — the album page's predicates (ch 05 §8 rows 4-16) ────────────────────────────
//
// NEW in 0.3. ch 05 §8 lists these as UNTESTED in 0.2.9 (05-album.md:1220-1221: `SeedTrack`, `TopTrack`,
// `HasTrailingSections`, `shortRelease`, plus `Breakdown`, `AlbumSubtitle`/`VersionLabel` and the face pile's overflow) —
// private statics inside `DetailTrailing`, `DetailTracks` and `ArtistFacePile`. Each fact states 0.2.9's decision over
// `Album.PageRules`; the ONE deliberate change is `FaceOverflow` (ch 05 §0.5, 05-album.md:44-47), asserted as the fix.

using FluentGpu.Localization;
using Wavee;
using Xunit;
using Rules = Wavee.Album.PageRules;

namespace Wavee.Tests;

public class AlbumPageRulesTests
{
    // ── shortRelease (DetailTrailing.cs:65) ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AlbumKind.Single, 0, true)]        // a single is short whatever its row count says
    [InlineData(AlbumKind.Single, 7, true)]
    [InlineData(AlbumKind.Album, 1, true)]
    [InlineData(AlbumKind.Album, 2, true)]
    [InlineData(AlbumKind.EP, 3, false)]
    [InlineData(AlbumKind.Album, 0, false)]        // no rows yet is not "short" — the tracks is > 0 arm
    public void IsShortRelease_IsASingleOrOneToTwoRows(AlbumKind kind, int tracks, bool expected)
        => Assert.Equal(expected, Rules.IsShortRelease(kind, tracks));

    // ── SongCount (the watch-video card's and Detail.Identity's meta line, 2026-09-16) ──────────────────────────────
    //
    // A count of 0 is "not known yet", never "no songs": the row-wide convention every producer follows by withholding
    // `AlbumFields.TrackCount` rather than claiming 0. The realised members are the fallback — the same rows the facts
    // tile and the tracklist count, so the three surfaces can never disagree ("0 songs" beside "1 Song").

    [Fact]
    public void SongCount_AKnownCountWins()
    {
        Assert.Equal(12, Rules.SongCount(12, 12));
        Assert.Equal(12, Rules.SongCount(12, 3));     // the members may be a partial page; the known total stands
    }

    [Fact]
    public void SongCount_AKnownZero_IsUnknown_AndFallsBackToTheMembers()
        => Assert.Equal(1, Rules.SongCount(0, 1));

    [Fact]
    public void SongCount_NothingKnown_NoMembers_IsZero()
        => Assert.Equal(0, Rules.SongCount(0, 0));

    // ── HasReleasePanel (DetailTrailing.cs:200-201) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void HasReleasePanel_FactsOrVersions()
    {
        Assert.False(Rules.HasReleasePanel(Album.ReleaseFacts.Empty, 0));
        Assert.True(Rules.HasReleasePanel(Album.ReleaseFacts.Empty, 1));       // the versions-only panel (parity 52)
        var labelOnly = Album.ReleaseFacts.Empty with { Label = "BLØF" };
        Assert.True(Rules.HasReleasePanel(labelOnly, 0));
    }

    // ── the trailing reserve and the unresolvable prerelease (DetailTrailing.cs:54-60, 87-88; DetailPage.cs:405-411) ──

    // One skeleton from mount, one swap (ch 05 §0.10). The demand and the tracklist are unconditional; About the artist,
    // Featured on and the watch-video verdict — the sections directly under the rows — hold the reserve until all are
    // ready or the deadline (PageRules.TrailingDeadlineMs) passes, so they reveal WITH the band instead of popping in
    // one after another. The below-the-fold relations (merch, more-by, similar, related artists) are not inputs at all.
    [Theory]
    [InlineData(false, EdgeState.Complete, true, true, true, true, false, true)]     // not demanded: reserved whatever is ready
    [InlineData(true, EdgeState.Unknown, true, true, true, true, false, true)]       // tracklist unanswered: reserved
    [InlineData(true, EdgeState.Complete, true, true, true, true, false, false)]     // every section ready: swap
    [InlineData(true, EdgeState.Complete, true, false, true, true, false, true)]     // About only: still reserved
    [InlineData(true, EdgeState.Complete, false, true, true, true, false, true)]     // Featured on only: still reserved
    [InlineData(true, EdgeState.Complete, true, true, true, false, false, true)]     // video verdict out: still reserved
    [InlineData(true, EdgeState.Complete, true, true, false, true, false, true)]     // Fans also like out: still reserved
    [InlineData(true, EdgeState.Complete, false, false, false, false, true, false)]   // nothing ready, but the deadline passed: swap
    [InlineData(true, EdgeState.Complete, true, true, true, false, true, false)]     // the deadline releases an unresolved video
    [InlineData(true, EdgeState.Unknown, false, false, false, false, true, true)]     // the deadline never overrides the tracklist
    public void TrailingReserve_HoldsForTheTracklist_ThenTheSectionsUnderTheRows_UntilTheDeadline(
        bool demanded, EdgeState tracklist, bool aboutReady, bool featuredReady, bool fansReady, bool videoReady, bool deadlinePassed, bool expected)
        => Assert.Equal(expected, Rules.TrailingReserved(demanded, tracklist, aboutReady, featuredReady, fansReady, videoReady, deadlinePassed));

    [Fact]
    public void TrailingReserve_AFailedTracklistOrRelationIsAnswered_NotOut()
    {
        // Failed counts as answered on every input (fail-soft, W12): a failed tracklist ask releases the reserve like a
        // complete one, and the host derives featuredReady as "not Unknown" — so a failed recommendations edge is ready.
        Assert.False(Rules.TrailingReserved(demanded: true, EdgeState.Failed, aboutReady: true, featuredReady: true, fansReady: true, videoReady: true,
                                            deadlinePassed: false));
        Assert.Equal(400f, Rules.TrailingDeadlineMs);
        Assert.True(Rules.InFlight(asked: true, EdgeState.Unknown));
        Assert.False(Rules.InFlight(asked: false, EdgeState.Unknown));          // never asked is not in flight
        Assert.False(Rules.InFlight(asked: true, EdgeState.Failed));            // failed: fail-soft, not out
        Assert.False(Rules.InFlight(asked: true, EdgeState.Complete));
    }

    // ── the skeleton's shape (defect 4 of the 2026-09-16 recording: a fixed three-section reserve collapsed on reveal) ──

    [Fact]
    public void SkeletonShape_ASingle_ReservesNoRowsBlock()
    {
        var s = Rules.SkeletonShape(knowsKind: true, AlbumKind.Single, knownTrackCount: 0);
        Assert.Equal(new Rules.TrailingShape(About: true, Fans: true, RowBlocks: 0), s);
        Assert.False(s.IsEmpty);
    }

    [Fact]
    public void SkeletonShape_AnAlbum_ReservesOneRowsBlock()
        => Assert.Equal(new Rules.TrailingShape(true, true, 1), Rules.SkeletonShape(true, AlbumKind.Album, 12));

    [Fact]
    public void SkeletonShape_OneOrTwoTracks_IsShort_WhateverTheKind()
    {
        Assert.Equal(0, Rules.SkeletonShape(true, AlbumKind.Album, 1).RowBlocks);
        Assert.Equal(0, Rules.SkeletonShape(true, AlbumKind.EP, 2).RowBlocks);
        Assert.Equal(1, Rules.SkeletonShape(true, AlbumKind.EP, 3).RowBlocks);
    }

    [Fact]
    public void SkeletonShape_UnknownKind_ReadsAsAnAlbum_AndReservesAbout()
    {
        var s = Rules.SkeletonShape(knowsKind: false, AlbumKind.Single, knownTrackCount: 0);
        Assert.Equal(new Rules.TrailingShape(About: true, Fans: true, RowBlocks: 1), s);
    }

    [Fact]
    public void SkeletonShape_AlwaysReservesAboutAndFans()
    {
        var s = Rules.SkeletonShape(knowsKind: true, AlbumKind.Album, knownTrackCount: 10);
        Assert.True(s.About);
        Assert.True(s.Fans);
        Assert.Equal(1, s.RowBlocks);
    }

    [Fact]
    public void SkeletonHeight_GrowsWithEachSection_AndAnAlbumOutreservesASingle()
    {
        float none = Rules.SkeletonHeight(default);
        float fans = Rules.SkeletonHeight(new Rules.TrailingShape(false, true, 0));
        float single = Rules.SkeletonHeight(Rules.SkeletonShape(true, AlbumKind.Single, 0));
        float album = Rules.SkeletonHeight(Rules.SkeletonShape(true, AlbumKind.Album, 12));
        Assert.Equal(0f, none);
        Assert.True(fans > none);
        Assert.True(single > fans);
        Assert.True(album > single);
        // The rows block is the whole difference between the two shapes.
        float rows = Rules.SkelSectionPadV + Rules.SkelHeaderH + Rules.SkelSectionGap
                   + Rules.SkelRows * Rules.SkelRowH + (Rules.SkelRows - 1) * Rules.SkelRowGap;
        Assert.Equal(rows, album - single);
    }

    [Fact]
    public void SkeletonHeight_IsTheSumOfTheSectionsDrawn()
    {
        float section = Rules.SkelSectionPadV + Rules.SkelHeaderH + Rules.SkelSectionGap;
        Assert.Equal(section + Rules.SkelAboutH, Rules.SkeletonHeight(new Rules.TrailingShape(true, false, 0)));
        Assert.Equal(section + Rules.SkelChipH, Rules.SkeletonHeight(new Rules.TrailingShape(false, true, 0)));
        Assert.Equal(2 * (section + Rules.SkelRows * Rules.SkelRowH + (Rules.SkelRows - 1) * Rules.SkelRowGap),
                     Rules.SkeletonHeight(new Rules.TrailingShape(false, false, 2)));
    }

    // No members, nothing to wait for. (A full album no longer short-circuits: see AlbumVideoSectionTests — the
    // section is not a short-release privilege any more, so its verdict is not either.)
    [Fact]
    public void VideoDecided_NoMembers_IsDecided()
        => Assert.True(Rules.VideoDecided(ReadOnlySpan<Track>.Empty));

    [Fact]
    public void UnresolvedPreRelease_OnlyWhenNothingNamedIt_TheAskFailed_AndNoTitleLanded()
    {
        Assert.True(Rules.IsUnresolvedPreRelease(resolved: false, EdgeState.Failed, knowsTitle: false));
        Assert.False(Rules.IsUnresolvedPreRelease(resolved: true, EdgeState.Failed, knowsTitle: false));
        Assert.False(Rules.IsUnresolvedPreRelease(resolved: false, EdgeState.Unknown, knowsTitle: false)); // still asking
        Assert.False(Rules.IsUnresolvedPreRelease(resolved: false, EdgeState.Failed, knowsTitle: true));
    }

    // ── HasTrailingSections (DetailTrailing.cs:146-153) ─────────────────────────────────────────────────────────────

    [Fact]
    public void HasTrailingSections_EveryArm()
    {
        Assert.False(Rules.HasTrailingSections(false, false, 0, 0, 0, 0, 0, 0));
        Assert.True(Rules.HasTrailingSections(true, false, 0, 0, 0, 0, 0, 0));    // the music-video section, at ANY length
        Assert.True(Rules.HasTrailingSections(false, true, 0, 0, 0, 0, 0, 0));    // about the artist
        Assert.True(Rules.HasTrailingSections(false, false, 1, 0, 0, 0, 0, 0));   // fans
        Assert.True(Rules.HasTrailingSections(false, false, 0, 1, 0, 0, 0, 0));   // featured on
        Assert.True(Rules.HasTrailingSections(false, false, 0, 0, 1, 0, 0, 0));   // merch
        Assert.True(Rules.HasTrailingSections(false, false, 0, 0, 0, 1, 0, 0));   // similar
        Assert.True(Rules.HasTrailingSections(false, false, 0, 0, 0, 0, 6, 1));   // more by, titled by the lead
        Assert.False(Rules.HasTrailingSections(false, false, 0, 0, 0, 0, 6, 0));  // more by with nobody to name it
    }

    [Fact]
    public void Caps_AreThePagesNumbers()
    {
        Assert.Equal(5, Rules.StackCap);        // TrailingStack.Cap
        Assert.Equal(8, Rules.FansCap);         // FansRow's Take(8)
        Assert.Equal(24, Rules.SimilarLimit);   // GetSimilarAlbumsAsync(seed, 24)
    }

    // ── Subtitle / VersionLabel / KindBadge (DetailTrailing.cs:304-316, 492-495) ───────────────────────────────────

    [Fact]
    public void Subtitle_IsArtist_ThenYear_ThenTheKindBadge()
    {
        Assert.Equal("Daft Punk", Rules.Subtitle("Daft Punk", 2013, AlbumKind.Album));
        Assert.Equal("2013", Rules.Subtitle(null, 2013, AlbumKind.Album));
        Assert.Equal("2013", Rules.Subtitle("", 2013, AlbumKind.Single));
        Assert.Equal(Loc.Get(Strings.Detail.Badge.Album), Rules.Subtitle(null, 0, AlbumKind.Album));
    }

    [Fact]
    public void VersionLabel_IsNameYearKind_AndDropsAnUnknownYear()
    {
        Assert.Equal("Random Access Memories · 2013 · " + Loc.Get(Strings.Detail.Badge.Album),
            Rules.VersionLabel("Random Access Memories", 2013, AlbumKind.Album));
        Assert.Equal("Get Lucky · " + Loc.Get(Strings.Detail.Badge.Single), Rules.VersionLabel("Get Lucky", 0, AlbumKind.Single));
    }

    [Theory]
    [InlineData(AlbumKind.Album, Strings.Detail.Badge.Album)]
    [InlineData(AlbumKind.EP, Strings.Detail.Badge.Ep)]
    [InlineData(AlbumKind.Single, Strings.Detail.Badge.Single)]
    [InlineData(AlbumKind.Compilation, Strings.Detail.Badge.Compilation)]
    public void KindBadge_IsTheReleaseBadge(AlbumKind kind, string key)
        => Assert.Equal(Loc.Get(key), Rules.KindBadge(kind));

    // ── the face pile's +N (ArtistFacePile.cs:53-55, 109-116) ───────────────────────────────────────────────────────

    [Fact]
    public void FaceOverflow_CountsTheTrackOnlyContributorsBeyondTheBilledSet()
    {
        // 2 billed (both drawn) + 3 track-only: +3 — 0.2.9's `all − billed` when every billed face is drawn.
        Assert.Equal(3, Rules.FaceOverflow(billed: 2, allDistinct: 5, drawn: 2));
        Assert.Equal(0, Rules.FaceOverflow(billed: 3, allDistinct: 3, drawn: 3));
    }

    [Fact]
    public void FaceOverflow_CountsTheBilledFacesItHid_TheFix()
    {
        // ch 05 §0.5 (05-album.md:44-47): an album billed to 6 drew 4 faces while 0.2.9 subtracted all 6, so the +N
        // counted neither hidden artist (8 − 6 = 2). 0.3 counts everything not drawn.
        Assert.Equal(4, Rules.FaceOverflow(billed: 6, allDistinct: 8, drawn: 4));
    }

    [Fact]
    public void FaceOverflow_NoBilledArtist_ShowsTheFirstFourOfAll()
    {
        Assert.Equal(3, Rules.FaceOverflow(billed: 0, allDistinct: 7, drawn: 4));
        Assert.Equal(0, Rules.FaceOverflow(billed: 0, allDistinct: 3, drawn: 3));
        Assert.Equal(0, Rules.FaceOverflow(billed: 0, allDistinct: 0, drawn: 0));
    }

    // ── HasCustomVideo (DetailTrailing.cs:345-351) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HasCustomVideo_IsAnyRowWithAnOverride()
    {
        Track[] rows = [new Track(3), new Track(4)];
        Assert.False(Rules.HasCustomVideo(rows, static _ => false));
        Assert.True(Rules.HasCustomVideo(rows, static t => t.Slot == 4));
        Assert.False(Rules.HasCustomVideo(ReadOnlySpan<Track>.Empty, static _ => true));
    }

    // ── RowFold (W2-A2, the value gates: a host's UseComputed value carries a fold over the rows it paints) ─────────

    [Fact]
    public void RowFold_IsAFunctionOfTheWords_AndOfTheirOrder()
    {
        ulong ab = RowFold.Add(RowFold.Add(RowFold.Seed, 1), 2u);
        Assert.Equal(ab, RowFold.Add(RowFold.Add(RowFold.Seed, 1), 2u));           // the same words: the same fold
        Assert.NotEqual(ab, RowFold.Add(RowFold.Add(RowFold.Seed, 2), 1u));        // reordered rows paint differently
        Assert.NotEqual(ab, RowFold.Add(RowFold.Add(RowFold.Seed, 1), 3u));        // one version bump moves the fold
        Assert.NotEqual(RowFold.Seed, RowFold.Add(RowFold.Seed, 0u));              // a zero word is still a row
    }

    [Fact]
    public void RowStamp_MovesOnAnyField()
    {
        var s = new RowStamp(5, 10, 3, Source: 1);
        Assert.False(s.Moved(new RowStamp(5, 10, 3, Source: 1)));
        Assert.True(s.Moved(new RowStamp(6, 10, 3, Source: 1)));
        Assert.True(s.Moved(new RowStamp(5, 11, 3, Source: 1)));
        Assert.True(s.Moved(new RowStamp(5, 10, 4, Source: 1)));
        Assert.True(s.Moved(new RowStamp(5, 10, 3, Source: 2)));                   // the fans row's artist/track flip
        Assert.True(default(RowStamp).Moved(s));                                    // Slot 0 (Table.None) never matches a row
    }
}

/// <summary>The rules that read play counts and artists off the tables.</summary>
[Collection(EntitiesCollection.Name)]
public class AlbumPageRulesTableTests
{
    static Track Played(string uri, uint plays)
    {
        int slot = Entities.Current.Tracks.Slot(uri.AsSpan());
        Entities.Current.Tracks.PlayCount[slot] = plays;
        return new Track(slot);
    }

    // ── SeedTrack (DetailTrailing.cs:334-341) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void SeedTrackIndex_IsTheHighestPlayCount_TiesKeepTheEarlier_ElseRowZero()
    {
        TestScope.Fresh();
        Assert.Equal(-1, Rules.SeedTrackIndex(ReadOnlySpan<Track>.Empty));
        Assert.Equal(1, Rules.SeedTrackIndex([Played("spotify:track:s0", 10), Played("spotify:track:s1", 90), Played("spotify:track:s2", 90)]));
        Assert.Equal(0, Rules.SeedTrackIndex([Played("spotify:track:n0", 0), Played("spotify:track:n1", 0)]));   // no plays: track 0
    }

    // ── TopTrack (DetailTracks.cs:399-406) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TopTrackIndex_IsArgmaxWhenAnyRowHasPlays_ElseNone()
    {
        TestScope.Fresh();
        Assert.Equal(-1, Rules.TopTrackIndex([Played("spotify:track:z0", 0), Played("spotify:track:z1", 0)]));
        Assert.Equal(2, Rules.TopTrackIndex([Played("spotify:track:t0", 5), Played("spotify:track:t1", 0), Played("spotify:track:t2", 9)]));
    }

    [Fact]
    public void TopTrackIndex_AgreesWithTheStarDerivedAtCommit()
    {
        TestScope.Fresh();
        Track[] rows = [Played("spotify:track:a0", 900), Played("spotify:track:a1", 4_200), Played("spotify:track:a2", 4_200)];
        int album = Entities.Current.Albums.Slot("spotify:album:agree".AsSpan());
        Entities.Current.Edges.AlbumTracks.ReplaceRun(album, [rows[0].Slot, rows[1].Slot, rows[2].Slot], default);
        Album.DeriveTopTrack(album);

        Assert.Equal(rows[Rules.TopTrackIndex(rows)].Slot, Entities.Current.Albums.TopTrackSlot[album]);
    }

    // ── AllDistinctArtists (ArtistFacePile.cs:204-220) ──────────────────────────────────────────────────────────────

    [Fact]
    public void DistinctArtists_BilledFirst_ThenTrackOnlyInRowOrder_NoDuplicates()
    {
        TestScope.Fresh();
        var e = Entities.Current.Edges;
        var artists = Entities.Current.Artists;
        int lead = artists.Slot("spotify:artist:lead".AsSpan());
        int feat = artists.Slot("spotify:artist:feat".AsSpan());
        int guest = artists.Slot("spotify:artist:guest".AsSpan());
        int album = Entities.Current.Albums.Slot("spotify:album:pile".AsSpan());
        int t0 = Entities.Current.Tracks.Slot("spotify:track:p0".AsSpan());
        int t1 = Entities.Current.Tracks.Slot("spotify:track:p1".AsSpan());
        e.AlbumArtists.ReplaceRun(album, [lead], default);
        e.AlbumTracks.ReplaceRun(album, [t0, t1], default);
        e.TrackArtists.ReplaceRun(t0, [lead, guest], default);
        e.TrackArtists.ReplaceRun(t1, [feat, guest, lead], default);

        Span<int> into = stackalloc int[8];
        int n = Rules.DistinctArtists(new Album(album), into);
        Assert.Equal(new[] { lead, guest, feat }, into[..n].ToArray());
        Assert.Equal(2, Rules.FaceOverflow(billed: 1, allDistinct: n, drawn: 1));
    }

    [Fact]
    public void DistinctArtists_StopsAtTheSpan()
    {
        TestScope.Fresh();
        var e = Entities.Current.Edges;
        int album = Entities.Current.Albums.Slot("spotify:album:cap".AsSpan());
        int a = Entities.Current.Artists.Slot("spotify:artist:c0".AsSpan());
        int b = Entities.Current.Artists.Slot("spotify:artist:c1".AsSpan());
        e.AlbumArtists.ReplaceRun(album, [a, b], default);
        Span<int> one = stackalloc int[1];
        Assert.Equal(1, Rules.DistinctArtists(new Album(album), one));
        Assert.Equal(a, one[0]);
    }

    // ── VideoDecided (the music-video section's reserve — at ANY release length since 2026-09-17) ───────────────────

    static Track Member(string uri, bool videoKnown)
    {
        var tracks = Entities.Current.Tracks;
        int slot = tracks.Slot(uri.AsSpan());
        if (videoKnown) tracks.Known[slot] |= (uint)TrackFields.Video;
        return new Track(slot);
    }

    [Fact]
    public void VideoDecided_WaitsUntilEveryMemberKnowsItsVideoGroup()
    {
        TestScope.Fresh();
        Track[] undecided = [Member("spotify:track:v0", true), Member("spotify:track:v1", false)];
        Assert.False(Rules.VideoDecided(undecided));
        Track[] decided = [Member("spotify:track:v2", true), Member("spotify:track:v3", true)];
        Assert.True(Rules.VideoDecided(decided));
        Assert.True(Rules.VideoDecided(ReadOnlySpan<Track>.Empty));   // no members: nothing to wait for
    }

    // ── the similar-albums seed the planner resolves ────────────────────────────────────────────────────────────────

    [Fact]
    public void SimilarSeedUri_IsTheSeedTracksUri_OrNullBeforeTheTracklist()
    {
        TestScope.Fresh();
        int album = Entities.Current.Albums.Slot("spotify:album:seeded".AsSpan());
        Assert.Null(Album.SimilarSeedUri(album));
        Track[] rows = [Played("spotify:track:seed0", 3), Played("spotify:track:seed1", 30)];
        Entities.Current.Edges.AlbumTracks.ReplaceRun(album, [rows[0].Slot, rows[1].Slot], default);
        Assert.Equal("spotify:track:seed1", Album.SimilarSeedUri(album));
        Assert.Null(Album.SimilarSeedUri(0));
    }

    // ── the library pane header's height budget (Entities/Album.UI.cs, 2026-09-18) ───────────────────────────────────
    //
    // The header used to be content-height, so an album whose title wrapped to two lines was ~9 DIP taller than the one
    // selected before it and every row under it moved. It states one height now — and a stated height is only honest
    // while what goes in it fits, which is what these two facts are.

    /// <summary>The block states the cover plus its own padding, so the cover is never the thing that clips.</summary>
    [Fact]
    public void PaneHeader_StatesTheCoverPlusItsOwnPadding()
    {
        Assert.Equal(160f, Album.PaneHeaderHeight);
        Assert.Equal(Album.PaneCover, Album.PaneHeaderTextBudget);
        Assert.True(Album.PaneHeaderHeight > Album.PaneCover, "the cover would touch the block's edges");
    }

    /// <summary>A WRAPPED title fits the budget with room to spare — the fact that keeps the tracklist still. It is the
    /// title's own metrics that buy it: at the prototype's 34-DIP line two lines alone overran the cover by a DIP.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]   // clamped to the two lines the title actually paints
    public void PaneHeader_TextColumnFitsTheBudgetAtEveryTitleLength(int lines)
    {
        Assert.True(Album.PaneHeaderTextHeight(lines) <= Album.PaneHeaderTextBudget,
            $"{lines}-line title: {Album.PaneHeaderTextHeight(lines)} does not fit {Album.PaneHeaderTextBudget}");
        Assert.Equal(Album.PaneHeaderTextHeight(Album.PaneTitleMaxLines), Album.PaneHeaderTextHeight(9));
        // A two-line title costs exactly one more line than a one-line one: nothing else in the column moves with it.
        Assert.Equal(Album.PaneTitleLine, Album.PaneHeaderTextHeight(2) - Album.PaneHeaderTextHeight(1));
    }
}
