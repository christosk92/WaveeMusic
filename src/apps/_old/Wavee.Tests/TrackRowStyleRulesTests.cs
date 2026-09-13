using System.Collections.Generic;
using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

public sealed class TrackRowStyleRulesTests
{
    [Fact]
    public void Modern_UsesArtworkPreferenceAndKeepsArtistInTitle()
    {
        var shown = DetailTrackTableRules.IdentityColumns(
            classic: false, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 0);
        var hidden = DetailTrackTableRules.IdentityColumns(
            classic: false, showArtThumb: true, artworkHidden: true, showTrackArtist: true, tier: 0);

        Assert.True(shown.Thumb);
        Assert.False(shown.Artist);
        Assert.True(shown.ArtistInTitle);
        Assert.False(hidden.Thumb);
    }

    [Fact]
    public void Classic_ProjectsPlaylistAlbumAndCompilationIdentityColumns()
    {
        var playlist = DetailTrackTableRules.IdentityColumns(
            classic: true, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 0);
        var album = DetailTrackTableRules.IdentityColumns(
            classic: true, showArtThumb: false, artworkHidden: false, showTrackArtist: false, tier: 0);
        var compilation = DetailTrackTableRules.IdentityColumns(
            classic: true, showArtThumb: false, artworkHidden: false, showTrackArtist: true, tier: 0);

        Assert.Equal(new TrackIdentityColumns(Thumb: false, Artist: true, ArtistInTitle: false), playlist);
        Assert.Equal(new TrackIdentityColumns(Thumb: false, Artist: false, ArtistInTitle: false), album);
        Assert.Equal(new TrackIdentityColumns(Thumb: false, Artist: true, ArtistInTitle: false), compilation);
    }

    [Fact]
    public void Classic_ArtistSurvivesTierThreeAndFoldsAtTierFour()
    {
        var medium = DetailTrackTableRules.IdentityColumns(
            classic: true, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 3);
        var narrow = DetailTrackTableRules.IdentityColumns(
            classic: true, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 4);

        Assert.True(medium.Artist);
        Assert.False(medium.ArtistInTitle);
        Assert.False(narrow.Artist);
        Assert.True(narrow.ArtistInTitle);
    }

    [Theory]
    [InlineData(0, 36f)]
    [InlineData(1, 40f)]
    [InlineData(2, 44f)]
    [InlineData(3, 48f)]
    public void Classic_UsesTightIndependentDensityLadder(int density, float expected)
    {
        Assert.Equal(expected, DetailTrackTableRules.RowHeightFor(density, classic: true));
        Assert.Equal(32f, DetailTrackTableRules.HeaderHeightFor(classic: true));
    }

    // ── row-size artwork (issue B) ───────────────────────────────────────────────────────────────────────────────────
    // "Row size → Comfortable" used to make rows 64 DIP tall while the cover stayed pinned to the fixed 32-DIP
    // TrackRow.ThumbSize — a small square floating in a tall row. ArtSizeFor is the one place that ladder lives; these
    // three tests pin the ladder itself, the row/art breathing-room invariant it is built to hold, and the Settings
    // density preview's contract to never show a different art size than the real row does.

    [Theory]
    [InlineData(0, 40f)]
    [InlineData(1, 48f)]
    [InlineData(2, 56f)]
    [InlineData(3, 64f)]
    public void Modern_RowHeightLadder(int density, float expected)
        => Assert.Equal(expected, DetailTrackTableRules.RowHeightFor(density, classic: false));

    /// <summary>The bug this fixes was a CONSTANT art size under a GROWING row. Two invariants pin the fix: the art
    /// never outgrows the row it sits in (at least 8 DIP of combined breathing room — the row keeps reading as a row,
    /// not a wall-to-wall thumbnail), and it never shrinks as density rises. Checked against the Modern row ladder for
    /// BOTH skins, because Classic never actually shows a Thumb column at all (<see cref="DetailTrackTableRules.IdentityColumns"/>
    /// forces <c>Thumb: false</c> whenever <c>classic</c> is true) — the Modern row is the one real geometry an art
    /// value is ever measured against.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArtSizeFor_GrowsMonotonically_AndLeavesBreathingRoom(bool classic)
    {
        float prevArt = 0f;
        for (int density = 0; density <= 3; density++)
        {
            float row = DetailTrackTableRules.RowHeightFor(density, classic: false);
            float art = DetailTrackTableRules.ArtSizeFor(density, classic);

            Assert.True(art is WaveeSize.Thumb32 or WaveeSize.Thumb40 or WaveeSize.Thumb48,
                $"density {density} classic={classic}: {art} is not on the 32/40/48 thumbnail ladder");
            Assert.True(art <= row - 8f,
                $"density {density} classic={classic}: art {art} leaves no breathing room in row {row}");
            Assert.True(art >= prevArt,
                $"density {density} classic={classic}: art shrank from {prevArt} to {art} as density rose");
            prevArt = art;
        }
    }

    /// <summary>WaveePicker.DensityRows draws its wireframe's art tile as
    /// <c>TrackRow.ArtSizeFor(density) * DetailTrackTableRules.PreviewScale</c> — and <c>TrackRow.ArtSizeFor</c> is a
    /// bare forward to <c>ArtSizeFor(density, classic: false)</c> with no logic of its own, so pinning THIS formula
    /// pins exactly what the preview paints without mounting the (engine-hosted) picker.</summary>
    [Theory]
    [InlineData(0, 8f)]
    [InlineData(1, 8f)]
    [InlineData(2, 10f)]
    [InlineData(3, 12f)]
    public void Preview_MirrorsTrackRow(int density, float expectedEdge)
        => Assert.Equal(expectedEdge,
            DetailTrackTableRules.ArtSizeFor(density, classic: false) * DetailTrackTableRules.PreviewScale);

    /// <summary>Classic folds VIDEO into the Title line and keeps one trailing command lane — but it keeps the
    /// disclosure chevron, exactly like Modern. What the drawer opens (the track's facts, then its versions) is a
    /// property of the TRACK, so the affordance cannot be skin-specific.</summary>
    [Fact]
    public void Classic_FoldsMediaChromeIntoTitleButKeepsTheDisclosureChevron()
    {
        var classic = DetailTrackTableRules.TrailingColumns(
            classic: true, hasVideo: true, showVersions: true, tier: 0);
        var modern = DetailTrackTableRules.TrailingColumns(
            classic: false, hasVideo: true, showVersions: true, tier: 0);

        Assert.Equal(new TrackTrailingColumns(Video: false, Actions: true, Expand: true), classic);
        Assert.Equal(new TrackTrailingColumns(Video: true, Actions: false, Expand: true), modern);
        Assert.True(DetailTrackTableRules.ShowClassicInlineVideo(true, hasVideo: true, tier: 3));
        Assert.False(DetailTrackTableRules.ShowClassicInlineVideo(true, hasVideo: true, tier: 4));
    }

    /// <summary>The chevron lane follows the "…" lane's width gate in BOTH skins: present down to tier 5, gone at
    /// the ultra-compact tier 6 where the table keeps no trailing lane at all. A surface that does not offer versions
    /// (search, artist Popular) never gets one.</summary>
    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 5, true)]
    [InlineData(true, 6, false)]
    [InlineData(false, 0, false)]
    public void ExpandLane_FollowsTheTrailingWidthGateInBothSkins(bool showVersions, int tier, bool expected)
    {
        Assert.Equal(expected, DetailTrackTableRules.TrailingColumns(
            classic: true, hasVideo: true, showVersions: showVersions, tier: tier).Expand);
        Assert.Equal(expected, DetailTrackTableRules.TrailingColumns(
            classic: false, hasVideo: true, showVersions: showVersions, tier: tier).Expand);
    }

    /// <summary>The context-menu "Track details" verb is a FALLBACK for a missing affordance, not a duplicate of a
    /// visible one: it appears only when the row wants a drawer and the chevron lane is absent — in either
    /// skin.</summary>
    [Theory]
    [InlineData(true, false, true, true)]    // wants a drawer, no lane, a music track -> the verb
    [InlineData(true, true, true, false)]    // the chevron is on screen -> no duplicate
    [InlineData(true, false, false, false)]  // an episode carries no drawer
    [InlineData(false, false, true, false)]  // the surface does not offer versions at all
    public void VersionsMenu_IsTheFallbackForAMissingChevronLane(
        bool versions, bool expandLane, bool single, bool expected)
    {
        Assert.Equal(expected, DetailTrackTableRules.ShowVersionsMenuItem(versions, expandLane, single));
    }

    [Fact]
    public void DedicatedArtistColumn_SplitsTitleAndArtistSortCycles()
    {
        var titleAsc = DetailTrackTableRules.NextSort(DetailTrackSort.Default, SortColumn.Title, artistColumn: true);
        var titleDesc = DetailTrackTableRules.NextSort(titleAsc, SortColumn.Title, artistColumn: true);
        var titleDefault = DetailTrackTableRules.NextSort(titleDesc, SortColumn.Title, artistColumn: true);
        var artistAsc = DetailTrackTableRules.NextSort(DetailTrackSort.Default, SortColumn.Artist, artistColumn: true);
        var artistDesc = DetailTrackTableRules.NextSort(artistAsc, SortColumn.Artist, artistColumn: true);
        var artistDefault = DetailTrackTableRules.NextSort(artistDesc, SortColumn.Artist, artistColumn: true);

        Assert.Equal(new DetailTrackSort(SortColumn.Title, false), titleAsc);
        Assert.Equal(new DetailTrackSort(SortColumn.Title, true), titleDesc);
        Assert.Equal(DetailTrackSort.Default, titleDefault);
        Assert.Equal(new DetailTrackSort(SortColumn.Artist, false), artistAsc);
        Assert.Equal(new DetailTrackSort(SortColumn.Artist, true), artistDesc);
        Assert.Equal(DetailTrackSort.Default, artistDefault);
        Assert.False(DetailTrackTableRules.HeaderActive(SortColumn.Title, SortColumn.Artist, artistColumn: true));
        Assert.True(DetailTrackTableRules.HeaderActive(SortColumn.Artist, SortColumn.Artist, artistColumn: true));
    }

    [Fact]
    public void FoldedArtist_RestoresLegacyTitleArtistCycleAndOwnership()
    {
        var titleAsc = DetailTrackTableRules.NextSort(DetailTrackSort.Default, SortColumn.Title, artistColumn: false);
        var titleDesc = DetailTrackTableRules.NextSort(titleAsc, SortColumn.Title, artistColumn: false);
        var artistAsc = DetailTrackTableRules.NextSort(titleDesc, SortColumn.Title, artistColumn: false);
        var artistDesc = DetailTrackTableRules.NextSort(artistAsc, SortColumn.Title, artistColumn: false);
        var reset = DetailTrackTableRules.NextSort(artistDesc, SortColumn.Title, artistColumn: false);

        Assert.Equal(new DetailTrackSort(SortColumn.Title, false), titleAsc);
        Assert.Equal(new DetailTrackSort(SortColumn.Title, true), titleDesc);
        Assert.Equal(new DetailTrackSort(SortColumn.Artist, false), artistAsc);
        Assert.Equal(new DetailTrackSort(SortColumn.Artist, true), artistDesc);
        Assert.Equal(DetailTrackSort.Default, reset);
        Assert.True(DetailTrackTableRules.HeaderActive(SortColumn.Title, SortColumn.Artist, artistColumn: false));
    }

    // ── the identity-first relief ladder ─────────────────────────────────────────────────────────────────────────────
    // The bug these pin: lane presence used to be a function of the pane's TOTAL width (the tier ladder) and never of
    // what the STAR tracks had left after the fixed lanes took theirs, so the trailing lanes had absolute priority and
    // Title/Artist absorbed every DIP of pressure. The user's report is the scenario in
    // Relief_UserScenario_ClassicLikedAt650_YieldsPlaysAndKeepsTitleAndArtistReadable below.

    /// <summary>The user's report: a Classic Liked Songs table with the Queue pane open, ≈650 DIP wide. The tier ladder
    /// admits ♥ · Artist · Date added · Plays · BPM·Key · duration · "…" at that width (tier 2) and the identity lanes
    /// paid for all of them. Relief gives up exactly ONE lane — Plays, the weakest fact — and that is enough to hand
    /// Title and Artist their floors.</summary>
    [Fact]
    public void Relief_UserScenario_ClassicLikedAt650_YieldsPlaysAndKeepsTitleAndArtistReadable()
    {
        var lanes = ClassicLikedTierTwo;

        Assert.Equal(706f, DetailTrackTableRules.MinWidthFor(in lanes, colGap: 12f, padX: 16f));
        Assert.Equal(1, DetailTrackTableRules.NominalReliefFor(in lanes, 650f, 12f, 16f));

        var relieved = DetailTrackTableRules.Relieve(in lanes, 1);
        Assert.False(relieved.Plays);
        // …and ONLY Plays: the BPM·key and Date-added lanes the user could still read stay.
        Assert.True(relieved.Tempo);
        Assert.True(relieved.Date);
        Assert.True(relieved.Artist);
        Assert.True(relieved.Heart);
        Assert.True(relieved.Actions);
        // 642 is exactly what the relieved set needs, so at 650 the star pool clears the floors with 8 DIP to spare.
        Assert.Equal(642f, DetailTrackTableRules.MinWidthFor(in relieved, 12f, 16f));
    }

    /// <summary>The head of every row is TWO lanes side by side — the # ↔ play transport and the ♥ —
    /// and they are ONE number. At 36 against 28 the transport lane read as a gutter with a number lost in it: wider
    /// than the affordance next to it, for content (three digits, or a 24-DIP play glyph) that never needed the room.
    /// The header's caret reservation is symmetric INSIDE that lane, so the "#" label keeps the middle and never
    /// shifts when the sort indicator turns on.</summary>
    [Fact]
    public void LeadingCluster_TransportLaneMatchesTheHeartLane()
    {
        Assert.Equal(28f, TrackLane.Num);
        Assert.Equal(TrackLane.Heart, TrackLane.Num);
        // Both caret slots fit inside the lane and still leave the label a readable middle.
        Assert.True(TrackLane.NumCaretSlot * 2f < TrackLane.Num);
        Assert.Equal(10f, TrackLane.Num - TrackLane.NumCaretSlot * 2f);
    }

    /// <summary>The identity floors are ratio-consistent with the star weights, which is what makes "the pool equals the
    /// sum of the floors" hand every identity lane exactly its floor rather than starving one to fund another.</summary>
    [Fact]
    public void Relief_IdentityFloorsMatchTheStarWeights()
    {
        Assert.Equal(TrackLane.ArtistStar, TrackLane.ArtistFloor / TrackLane.TitleFloor);
        Assert.Equal(TrackLane.AlbumStar, TrackLane.AlbumFloor / TrackLane.TitleFloor);
    }

    /// <summary>A wide layout is untouched BY CONSTRUCTION — the ladder returns step 0 whenever the table already
    /// clears the floors, so nothing about the full-width table is special-cased.</summary>
    [Theory]
    [InlineData(952f, 0)]   // exactly what the full Classic lane set needs
    [InlineData(1200f, 0)]
    [InlineData(2000f, 0)]
    [InlineData(951f, 1)]   // one DIP short → the weakest fact goes, nothing else
    public void Relief_FullClassicSet_IsUntouchedOnceItClearsTheFloors(float available, int expected)
        => Assert.Equal(expected, DetailTrackTableRules.NominalReliefFor(in ClassicFull, available, 12f, 16f));

    /// <summary>The yield order, pinned step by step: Plays → BPM·key → Added by → Date added → Album → Artist →
    /// art thumb → ♥.</summary>
    [Theory]
    [InlineData(0, "heart,thumb,artist,album,by,date,plays,tempo")]
    [InlineData(1, "heart,thumb,artist,album,by,date,tempo")]
    [InlineData(2, "heart,thumb,artist,album,by,date")]
    [InlineData(3, "heart,thumb,artist,album,date")]
    [InlineData(4, "heart,thumb,artist,album")]
    [InlineData(5, "heart,thumb,artist")]
    [InlineData(6, "heart,thumb")]
    [InlineData(7, "heart")]
    [InlineData(8, "")]
    public void Relief_YieldsLanesCheapestFactFirst(int step, string expected)
    {
        var all = new DetailTrackTableRules.TrackTableLanes(
            Heart: true, Thumb: true, Artist: true, Album: true, By: true, Date: true, Plays: true, Tempo: true,
            Video: true, Actions: true, Expand: true);
        var l = DetailTrackTableRules.Relieve(in all, step);
        var kept = new List<string>(8);
        if (l.Heart) kept.Add("heart");
        if (l.Thumb) kept.Add("thumb");
        if (l.Artist) kept.Add("artist");
        if (l.Album) kept.Add("album");
        if (l.By) kept.Add("by");
        if (l.Date) kept.Add("date");
        if (l.Plays) kept.Add("plays");
        if (l.Tempo) kept.Add("tempo");
        Assert.Equal(expected, string.Join(",", kept));
        // The row's transport, its title, its length and its verbs are never on the table.
        Assert.True(l.Video);
        Assert.True(l.Actions);
        Assert.True(l.Expand);
    }

    /// <summary>Exhausted relief is not an error: the star tracks collapse exactly as they did before, which is the
    /// only honest answer left below # + a Title floor + duration.</summary>
    [Fact]
    public void Relief_SaturatesAtMaxReliefOnAnUnservableWidth()
    {
        Assert.Equal(DetailTrackTableRules.MaxRelief,
            DetailTrackTableRules.NominalReliefFor(in ClassicFull, 120f, 12f, 16f));
        // A width of 0 is "not measured yet", never "relieve everything".
        Assert.Equal(0, DetailTrackTableRules.NominalReliefFor(in ClassicFull, 0f, 12f, 16f));
    }

    /// <summary>Hysteresis: a lane goes the moment the floor is breached, and comes back only once the width clears its
    /// threshold by the band — so dragging a window edge across 650 never flickers Plays in and out.</summary>
    [Fact]
    public void Relief_YieldsImmediatelyAndReAdmitsOnlyWithMargin()
    {
        var l = ClassicLikedTierTwo;
        // Narrowing past the step-1 threshold (642) takes BPM·key at once.
        Assert.Equal(2, DetailTrackTableRules.ReliefFor(in l, 641f, 12f, 16f, prev: 1));
        // Widening back to 642 does NOT hand it straight back…
        Assert.Equal(2, DetailTrackTableRules.ReliefFor(in l, 642f, 12f, 16f, prev: 2));
        Assert.Equal(2, DetailTrackTableRules.ReliefFor(in l, 665f, 12f, 16f, prev: 2));
        // …only once the width has re-earned the whole band.
        Assert.Equal(1, DetailTrackTableRules.ReliefFor(in l, 666f, 12f, 16f, prev: 2));
        Assert.Equal(DetailLayoutBreakpoints.TierHysteresisDip, DetailTrackTableRules.ReliefHysteresisDip);
    }

    /// <summary>Pre-measure and first-measure, on the same grammar as the tier ladder: an unmeasured width holds the
    /// previous step, and the FIRST real measurement is authoritative with no hysteresis.</summary>
    [Fact]
    public void Relief_HoldsOnAnUnmeasuredWidthAndTakesTheFirstMeasureOutright()
    {
        var l = ClassicLikedTierTwo;
        Assert.Equal(3, DetailTrackTableRules.ReliefFor(in l, 0f, 12f, 16f, prev: 3));
        Assert.Equal(1, DetailTrackTableRules.ReliefFor(in l, 650f, 12f, 16f, prev: 3, initialized: false));
    }

    /// <summary>The modern (artist-in-subline) table has one identity star instead of two, so it clears the floor at a
    /// narrower width than Classic — and it yields the same weakest fact first when it does not.</summary>
    [Fact]
    public void Relief_ModernLikedTable_ClearsTheFloorEarlierThanClassic()
    {
        var modern = new DetailTrackTableRules.TrackTableLanes(
            Heart: true, Thumb: true, Artist: false, Album: false, By: false, Date: true, Plays: true, Tempo: true,
            Video: false, Actions: true, Expand: false);
        Assert.Equal(648f, DetailTrackTableRules.MinWidthFor(in modern, 12f, 16f));
        Assert.Equal(0, DetailTrackTableRules.NominalReliefFor(in modern, 648f, 12f, 16f));
        Assert.Equal(1, DetailTrackTableRules.NominalReliefFor(in modern, 647f, 12f, 16f));
    }

    /// <summary>Relief is monotone in width: narrower never yields fewer lanes. That is what lets the renderer treat
    /// the step as a plain subtractive pass over the tier's own column set.</summary>
    [Fact]
    public void Relief_IsMonotoneInWidth()
    {
        int prev = DetailTrackTableRules.MaxRelief + 1;
        for (float w = 200f; w <= 1400f; w += 7f)
        {
            int step = DetailTrackTableRules.NominalReliefFor(in ClassicFull, w, 12f, 16f);
            Assert.True(step <= prev, $"relief grew while widening at {w}");
            prev = step;
        }
        Assert.Equal(0, prev);
    }

    /// <summary>Classic Liked Songs at tier 2 with the Queue pane open — the user's exact column set: ♥ · Title ·
    /// Artist · Date added · Plays · BPM·key · duration · "…" (no art thumb in Classic, no Album/Added-by at tier 2).</summary>
    static readonly DetailTrackTableRules.TrackTableLanes ClassicLikedTierTwo = new(
        Heart: true, Thumb: false, Artist: true, Album: false, By: false, Date: true, Plays: true, Tempo: true,
        Video: false, Actions: true, Expand: false);

    /// <summary>Every optional Classic lane at once — the widest table the app builds.</summary>
    static readonly DetailTrackTableRules.TrackTableLanes ClassicFull = new(
        Heart: true, Thumb: false, Artist: true, Album: true, By: true, Date: true, Plays: true, Tempo: true,
        Video: false, Actions: true, Expand: false);
}
