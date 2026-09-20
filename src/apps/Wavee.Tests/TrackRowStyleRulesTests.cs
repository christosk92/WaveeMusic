// ── Wavee.Tests/TrackRowStyleRulesTests.cs — the lane table, the skin + density ladders, the sort cycle, relief ───────
//
// Wave 4.5's gate for `Track.Lane` and `Track.TableRules` (Entities/Track.Rules.cs), ported VERBATIM from 0.2.9's
// TrackRowStyleRulesTests (374 lines). Only the names moved: DetailTrackTableRules → Track.TableRules, TrackLane →
// Track.Lane, TrackTableLanes → Track.Lanes, DetailTrackSort → Track.SortSpec, WaveeSize → Design.Size,
// DetailLayoutBreakpoints → Detail.Breakpoints. The last three facts pin the relief APPLICATION (`LanesOf` /
// `ApplyRelief`), which 0.2.9 kept private inside the renderer (DetailTracks.cs:626-659) and 0.3 promotes to CORE so
// the header, the rows and the shimmer cannot disagree about which lanes are up.
//
// Pure: no scope, no engine.

using FluentGpu.Foundation;
using Xunit;
using RowMetrics = Wavee.Track.RowMetrics;
using TableRules = Wavee.Track.TableRules;

namespace Wavee.Tests;

public sealed class TrackRowStyleRulesTests
{
    [Fact]
    public void Modern_UsesArtworkPreferenceAndKeepsArtistInTitle()
    {
        var shown = TableRules.IdentityColumns(
            classic: false, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 0);
        var hidden = TableRules.IdentityColumns(
            classic: false, showArtThumb: true, artworkHidden: true, showTrackArtist: true, tier: 0);

        Assert.True(shown.Thumb);
        Assert.False(shown.Artist);
        Assert.True(shown.ArtistInTitle);
        Assert.False(hidden.Thumb);
    }

    [Fact]
    public void Classic_ProjectsPlaylistAlbumAndCompilationIdentityColumns()
    {
        var playlist = TableRules.IdentityColumns(
            classic: true, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 0);
        var album = TableRules.IdentityColumns(
            classic: true, showArtThumb: false, artworkHidden: false, showTrackArtist: false, tier: 0);
        var compilation = TableRules.IdentityColumns(
            classic: true, showArtThumb: false, artworkHidden: false, showTrackArtist: true, tier: 0);

        Assert.Equal(new Track.IdentityColumns(Thumb: false, Artist: true, ArtistInTitle: false), playlist);
        Assert.Equal(new Track.IdentityColumns(Thumb: false, Artist: false, ArtistInTitle: false), album);
        Assert.Equal(new Track.IdentityColumns(Thumb: false, Artist: true, ArtistInTitle: false), compilation);
    }

    [Fact]
    public void Classic_ArtistSurvivesTierThreeAndFoldsAtTierFour()
    {
        var medium = TableRules.IdentityColumns(
            classic: true, showArtThumb: true, artworkHidden: false, showTrackArtist: true, tier: 3);
        var narrow = TableRules.IdentityColumns(
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
        Assert.Equal(expected, TableRules.RowHeightFor(density, classic: true));
        Assert.Equal(32f, TableRules.HeaderHeightFor(classic: true));
    }

    // ── row-size artwork (issue B) ───────────────────────────────────────────────────────────────────────────────────
    // "Row size → Comfortable" used to make rows 64 DIP tall while the cover stayed pinned to the fixed 32-DIP thumb — a
    // small square floating in a tall row. ArtSizeFor is the one place that ladder lives.

    [Theory]
    [InlineData(0, 40f)]
    [InlineData(1, 48f)]
    [InlineData(2, 56f)]
    [InlineData(3, 64f)]
    public void Modern_RowHeightLadder(int density, float expected)
        => Assert.Equal(expected, TableRules.RowHeightFor(density, classic: false));

    /// <summary>The bug was a CONSTANT art size under a GROWING row. The art never outgrows the row (≥ 8 DIP of combined
    /// breathing room) and never shrinks as density rises — checked against the Modern row for BOTH skins, because
    /// Classic never shows a Thumb lane at all.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArtSizeFor_GrowsMonotonically_AndLeavesBreathingRoom(bool classic)
    {
        float prevArt = 0f;
        for (int density = 0; density <= 3; density++)
        {
            float row = TableRules.RowHeightFor(density, classic: false);
            float art = TableRules.ArtSizeFor(density, classic);

            Assert.True(art is Design.Size.Thumb32 or Design.Size.Thumb40 or Design.Size.Thumb48,
                $"density {density} classic={classic}: {art} is not on the 32/40/48 thumbnail ladder");
            Assert.True(art <= row - 8f,
                $"density {density} classic={classic}: art {art} leaves no breathing room in row {row}");
            Assert.True(art >= prevArt,
                $"density {density} classic={classic}: art shrank from {prevArt} to {art} as density rose");
            prevArt = art;
        }
    }

    /// <summary>The Settings density miniature draws its art tile as <c>RowMetrics.ArtSizeFor(density) * PreviewScale</c>,
    /// and that is a bare forward to <c>ArtSizeFor(density, classic: false)</c> — so pinning this formula pins what the
    /// preview paints.</summary>
    [Theory]
    [InlineData(0, 8f)]
    [InlineData(1, 8f)]
    [InlineData(2, 10f)]
    [InlineData(3, 12f)]
    public void Preview_MirrorsTrackRow(int density, float expectedEdge)
        => Assert.Equal(expectedEdge, TableRules.ArtSizeFor(density, classic: false) * TableRules.PreviewScale);

    /// <summary>Classic folds VIDEO into the Title line and keeps one trailing command lane — but it keeps the disclosure
    /// chevron, exactly like Modern: what the drawer opens is a property of the TRACK.</summary>
    [Fact]
    public void Classic_FoldsMediaChromeIntoTitleButKeepsTheDisclosureChevron()
    {
        var classic = TableRules.TrailingColumns(classic: true, hasVideo: true, showVersions: true, tier: 0);
        var modern = TableRules.TrailingColumns(classic: false, hasVideo: true, showVersions: true, tier: 0);

        Assert.Equal(new Track.TrailingColumns(Video: false, Actions: true, Expand: true), classic);
        Assert.Equal(new Track.TrailingColumns(Video: true, Actions: false, Expand: true), modern);
        // Classic gets no film LANE (asserted above), so the inline glyph is its only indicator and is NOT dropped
        // by width any more: a sidebar plus a now-playing rail reach tier 4, and a Classic playlist there used to
        // show no video indicator at all while the artist chart beside it still did.
        Assert.True(TableRules.ShowClassicInlineVideo(true, hasVideo: true, tier: 3));
        Assert.True(TableRules.ShowClassicInlineVideo(true, hasVideo: true, tier: 4));
        Assert.True(TableRules.ShowClassicInlineVideo(true, hasVideo: true, tier: 6));
        Assert.False(TableRules.ShowClassicInlineVideo(true, hasVideo: false, tier: 0));
        Assert.False(TableRules.ShowClassicInlineVideo(false, hasVideo: true, tier: 0));
    }

    /// <summary>The trailing lane is ONE width. Modern still TRADES the "…" lane for the film lane on a release that
    /// carries a video — the cell changes, so the row shows a film glyph at rest and the "…" on hover — but while the two
    /// lanes were 28 and 40 that trade also MOVED every column to its left: the duration clock and its header sat 12 DIP
    /// apart on two albums selected one after the other in the library pane. Pinned on the width TRACKS, because that
    /// array is what the column header, the rows and the shimmer all lay themselves out from.</summary>
    [Fact]
    public void TrailingLane_IsOneWidthWhetherOrNotTheReleaseCarriesAVideo()
    {
        var film = TableRules.TrailingColumns(classic: false, hasVideo: true, showVersions: false, tier: 1);
        var dots = TableRules.TrailingColumns(classic: false, hasVideo: false, showVersions: false, tier: 1);

        Assert.Equal(new Track.TrailingColumns(Video: true, Actions: false, Expand: false), film);
        Assert.Equal(new Track.TrailingColumns(Video: false, Actions: true, Expand: false), dots);
        Assert.Equal(Track.Lane.Actions, Track.Lane.Video);

        // The embedded album arm, both ways round (Track.Table.Chrome.EmbeddedColumns' lanes).
        var withFilm = new Track.ColumnSet(
            Album: false, By: false, Date: false, Video: film.Video, Plays: false, Heart: true, Thumb: false,
            Actions: film.Actions, Tier: 1);
        var withDots = withFilm with { Video = dots.Video, Actions = dots.Actions };
        var a = Track.TracksFor(in withFilm, Design.Size.Thumb32);
        var b = Track.TracksFor(in withDots, Design.Size.Thumb32);

        Assert.Equal(b.Length, a.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.Equal(b[i], a[i]);
    }

    // ── the TRAILING ♥ lane (the library reader's row) ──────────────────────────────────────────────────────────────
    //
    // The reader's row is `28 (number) | 1fr (title) | 32 (heart) | 52 (duration)`: the same heart, moved to the RIGHT
    // and sat immediately before the duration. Two facts hold it there — the lane's POSITION (a heart after the clock
    // reads as chrome, and one lane out of place shifts every column after it) and its exclusivity with the leading ♥.

    /// <summary>The reader's four tracks, in order, from the lane table — not from four literals beside it.</summary>
    [Fact]
    public void TracksFor_HeartTrailing_PutsTheHeartLaneImmediatelyBeforeTheDuration()
    {
        var reader = new Track.ColumnSet(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: false,
                                         Thumb: false, Actions: false, HeartTrailing: true);
        var tracks = Track.TracksFor(in reader, Design.Size.Thumb32);

        Assert.Equal(4, tracks.Length);
        Assert.Equal(TrackSize.Px(Track.Lane.Num), tracks[0]);
        Assert.Equal(TrackSize.Star(Track.Lane.TitleStar), tracks[1]);
        Assert.Equal(TrackSize.Px(Track.Lane.HeartTrailing), tracks[2]);
        Assert.Equal(TrackSize.Px(Track.Lane.Duration), tracks[3]);

        // The prototype's literal row, so a lane silently re-sized is a failure here and not a visual regression.
        Assert.Equal(28f, Track.Lane.Num);
        Assert.Equal(32f, Track.Lane.HeartTrailing);
        Assert.Equal(52f, Track.Lane.Duration);
    }

    /// <summary>It still sits between the last fact and the clock when the whole trailing cluster is up: Tempo/Plays end
    /// left of it, and Video / "…" / the chevron stay AFTER the duration. Spelled as the WHOLE sequence, because several
    /// lanes share a width (art 32 = the trailing heart, Plays 52 = the duration) and an index search for one of them
    /// would find the other.</summary>
    [Fact]
    public void TracksFor_HeartTrailing_SitsBetweenTheLastFactAndTheClock()
    {
        var set = new Track.ColumnSet(Album: false, By: false, Date: false, Video: false, Plays: true, Heart: false,
                                      Thumb: true, Actions: true, Tier: 0, Tempo: true, Expand: true, HeartTrailing: true);
        var tracks = Track.TracksFor(in set, Design.Size.Thumb32);

        Assert.Equal(
            new[]
            {
                TrackSize.Px(Track.Lane.Num), TrackSize.Px(Design.Size.Thumb32), TrackSize.Star(Track.Lane.TitleStar),
                TrackSize.Px(Track.Lane.Plays), TrackSize.Px(Track.Lane.Tempo),
                TrackSize.Px(Track.Lane.HeartTrailing), TrackSize.Px(Track.Lane.Duration),
                TrackSize.Px(Track.Lane.Actions), TrackSize.Px(Track.Lane.Expand),
            },
            tracks);
    }

    /// <summary>Heart and HeartTrailing are mutually exclusive, and a set that asks for both NORMALIZES to the leading
    /// lane — one gate (<see cref="RowMetrics.ShowHeartTrailing"/>) that the width tracks, the cell count and the cell
    /// all read, so the three can never disagree about how many lanes there are.</summary>
    [Fact]
    public void HeartTrailing_WithTheLeadingHeart_NormalizesToTheLeadingLane()
    {
        var both = new Track.ColumnSet(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: true,
                                       Thumb: false, Actions: false, HeartTrailing: true);
        var leadingOnly = both with { HeartTrailing = false };
        var trailingOnly = both with { Heart = false };

        Assert.False(RowMetrics.ShowHeartTrailing(in both));
        Assert.True(RowMetrics.ShowHeartTrailing(in trailingOnly));

        var tracks = Track.TracksFor(in both, Design.Size.Thumb32);
        var lead = Track.TracksFor(in leadingOnly, Design.Size.Thumb32);
        Assert.Equal(lead.Length, tracks.Length);
        for (int i = 0; i < tracks.Length; i++) Assert.Equal(lead[i], tracks[i]);
        // # · ♥(leading, 28) · Title* · duration — the trailing 32 is nowhere in it.
        Assert.Equal(TrackSize.Px(Track.Lane.Heart), tracks[1]);
        Assert.DoesNotContain(TrackSize.Px(Track.Lane.HeartTrailing), tracks);
    }

    /// <summary>The trailing lane forks the width-track CACHE: the key is the whole ColumnSet, so the reader's set and
    /// the same set without the heart never share one array (the header/rows "same instance" rule cuts both ways).</summary>
    [Fact]
    public void TracksFor_HeartTrailing_IsPartOfTheCacheKey()
    {
        var hearted = new Track.ColumnSet(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: false,
                                          Thumb: false, Actions: false, HeartTrailing: true);
        var bare = hearted with { HeartTrailing = false };

        Assert.Same(Track.TracksFor(in hearted, 32f), Track.TracksFor(in hearted, 32f));
        Assert.NotSame(Track.TracksFor(in hearted, 32f), Track.TracksFor(in bare, 32f));
        Assert.Equal(3, Track.TracksFor(in bare, 32f).Length);
    }

    /// <summary>The chevron lane follows the "…" lane's width gate in BOTH skins: present down to tier 5, gone at tier 6.
    /// A surface that does not offer versions never gets one.</summary>
    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 5, true)]
    [InlineData(true, 6, false)]
    [InlineData(false, 0, false)]
    public void ExpandLane_FollowsTheTrailingWidthGateInBothSkins(bool showVersions, int tier, bool expected)
    {
        Assert.Equal(expected, TableRules.TrailingColumns(
            classic: true, hasVideo: true, showVersions: showVersions, tier: tier).Expand);
        Assert.Equal(expected, TableRules.TrailingColumns(
            classic: false, hasVideo: true, showVersions: showVersions, tier: tier).Expand);
    }

    /// <summary>"Track details" is a FALLBACK for a missing affordance, not a duplicate of a visible one.</summary>
    [Theory]
    [InlineData(true, false, true, true)]    // wants a drawer, no lane, a music track -> the verb
    [InlineData(true, true, true, false)]    // the chevron is on screen -> no duplicate
    [InlineData(true, false, false, false)]  // an episode carries no drawer
    [InlineData(false, false, true, false)]  // the surface does not offer versions at all
    public void VersionsMenu_IsTheFallbackForAMissingChevronLane(
        bool versions, bool expandLane, bool single, bool expected)
    {
        Assert.Equal(expected, TableRules.ShowVersionsMenuItem(versions, expandLane, single));
    }

    [Fact]
    public void DedicatedArtistColumn_SplitsTitleAndArtistSortCycles()
    {
        var titleAsc = TableRules.NextSort(Track.SortSpec.Default, Track.SortColumn.Title, artistColumn: true);
        var titleDesc = TableRules.NextSort(titleAsc, Track.SortColumn.Title, artistColumn: true);
        var titleDefault = TableRules.NextSort(titleDesc, Track.SortColumn.Title, artistColumn: true);
        var artistAsc = TableRules.NextSort(Track.SortSpec.Default, Track.SortColumn.Artist, artistColumn: true);
        var artistDesc = TableRules.NextSort(artistAsc, Track.SortColumn.Artist, artistColumn: true);
        var artistDefault = TableRules.NextSort(artistDesc, Track.SortColumn.Artist, artistColumn: true);

        Assert.Equal(new Track.SortSpec(Track.SortColumn.Title, false), titleAsc);
        Assert.Equal(new Track.SortSpec(Track.SortColumn.Title, true), titleDesc);
        Assert.Equal(Track.SortSpec.Default, titleDefault);
        Assert.Equal(new Track.SortSpec(Track.SortColumn.Artist, false), artistAsc);
        Assert.Equal(new Track.SortSpec(Track.SortColumn.Artist, true), artistDesc);
        Assert.Equal(Track.SortSpec.Default, artistDefault);
        Assert.False(TableRules.HeaderActive(Track.SortColumn.Title, Track.SortColumn.Artist, artistColumn: true));
        Assert.True(TableRules.HeaderActive(Track.SortColumn.Artist, Track.SortColumn.Artist, artistColumn: true));
    }

    [Fact]
    public void FoldedArtist_RestoresLegacyTitleArtistCycleAndOwnership()
    {
        var titleAsc = TableRules.NextSort(Track.SortSpec.Default, Track.SortColumn.Title, artistColumn: false);
        var titleDesc = TableRules.NextSort(titleAsc, Track.SortColumn.Title, artistColumn: false);
        var artistAsc = TableRules.NextSort(titleDesc, Track.SortColumn.Title, artistColumn: false);
        var artistDesc = TableRules.NextSort(artistAsc, Track.SortColumn.Title, artistColumn: false);
        var reset = TableRules.NextSort(artistDesc, Track.SortColumn.Title, artistColumn: false);

        Assert.Equal(new Track.SortSpec(Track.SortColumn.Title, false), titleAsc);
        Assert.Equal(new Track.SortSpec(Track.SortColumn.Title, true), titleDesc);
        Assert.Equal(new Track.SortSpec(Track.SortColumn.Artist, false), artistAsc);
        Assert.Equal(new Track.SortSpec(Track.SortColumn.Artist, true), artistDesc);
        Assert.Equal(Track.SortSpec.Default, reset);
        Assert.True(TableRules.HeaderActive(Track.SortColumn.Title, Track.SortColumn.Artist, artistColumn: false));
    }

    // ── the identity-first relief ladder ─────────────────────────────────────────────────────────────────────────────
    // Lane presence used to be a function of the pane's TOTAL width and never of what the STAR tracks had left, so
    // Title/Artist absorbed every DIP of pressure. The user's report is the first fact below.

    /// <summary>A Classic Liked Songs table with the Queue pane open, ≈650 DIP. Relief gives up exactly ONE lane — Plays,
    /// the weakest fact — and that is enough to hand Title and Artist their floors.</summary>
    [Fact]
    public void Relief_UserScenario_ClassicLikedAt650_YieldsPlaysAndKeepsTitleAndArtistReadable()
    {
        var lanes = ClassicLikedTierTwo;

        Assert.Equal(706f, TableRules.MinWidthFor(in lanes, colGap: 12f, padX: 16f));
        Assert.Equal(1, TableRules.NominalReliefFor(in lanes, 650f, 12f, 16f));

        var relieved = TableRules.Relieve(in lanes, 1);
        Assert.False(relieved.Plays);
        // …and ONLY Plays: the BPM·key and Date-added lanes the user could still read stay.
        Assert.True(relieved.Tempo);
        Assert.True(relieved.Date);
        Assert.True(relieved.Artist);
        Assert.True(relieved.Heart);
        Assert.True(relieved.Actions);
        // 642 is exactly what the relieved set needs, so at 650 the star pool clears the floors with 8 DIP to spare.
        Assert.Equal(642f, TableRules.MinWidthFor(in relieved, 12f, 16f));
    }

    /// <summary>The # ↔ play transport and the ♥ are ONE number, and the header's caret reservation is symmetric INSIDE
    /// the lane, so the "#" keeps the middle when the sort indicator turns on.</summary>
    [Fact]
    public void LeadingCluster_TransportLaneMatchesTheHeartLane()
    {
        Assert.Equal(28f, Track.Lane.Num);
        Assert.Equal(Track.Lane.Heart, Track.Lane.Num);
        Assert.True(Track.Lane.NumCaretSlot * 2f < Track.Lane.Num);
        Assert.Equal(10f, Track.Lane.Num - Track.Lane.NumCaretSlot * 2f);
    }

    /// <summary>Ratio-consistent floors: "the pool equals the sum of the floors" hands every identity lane its floor.</summary>
    [Fact]
    public void Relief_IdentityFloorsMatchTheStarWeights()
    {
        Assert.Equal(Track.Lane.ArtistStar, Track.Lane.ArtistFloor / Track.Lane.TitleFloor);
        Assert.Equal(Track.Lane.AlbumStar, Track.Lane.AlbumFloor / Track.Lane.TitleFloor);
    }

    /// <summary>A wide layout is untouched BY CONSTRUCTION — step 0 whenever the table already clears the floors.</summary>
    [Theory]
    [InlineData(952f, 0)]   // exactly what the full Classic lane set needs
    [InlineData(1200f, 0)]
    [InlineData(2000f, 0)]
    [InlineData(951f, 1)]   // one DIP short → the weakest fact goes, nothing else
    public void Relief_FullClassicSet_IsUntouchedOnceItClearsTheFloors(float available, int expected)
        => Assert.Equal(expected, TableRules.NominalReliefFor(in ClassicFull, available, 12f, 16f));

    /// <summary>The yield order, step by step: Plays → BPM·key → Added by → Date added → Album → Artist → art → ♥.</summary>
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
        var all = new Track.Lanes(
            Heart: true, Thumb: true, Artist: true, Album: true, By: true, Date: true, Plays: true, Tempo: true,
            Video: true, Actions: true, Expand: true);
        var l = TableRules.Relieve(in all, step);
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

    /// <summary>Exhausted relief is not an error; and a width of 0 is "not measured yet", never "relieve everything".</summary>
    [Fact]
    public void Relief_SaturatesAtMaxReliefOnAnUnservableWidth()
    {
        Assert.Equal(TableRules.MaxRelief, TableRules.NominalReliefFor(in ClassicFull, 120f, 12f, 16f));
        Assert.Equal(0, TableRules.NominalReliefFor(in ClassicFull, 0f, 12f, 16f));
    }

    /// <summary>A lane goes the moment the floor is breached and comes back only once the width clears its threshold by
    /// the band — dragging a window edge across 650 never flickers Plays in and out.</summary>
    [Fact]
    public void Relief_YieldsImmediatelyAndReAdmitsOnlyWithMargin()
    {
        var l = ClassicLikedTierTwo;
        // Narrowing past the step-1 threshold (642) takes BPM·key at once.
        Assert.Equal(2, TableRules.ReliefFor(in l, 641f, 12f, 16f, prev: 1));
        // Widening back to 642 does NOT hand it straight back…
        Assert.Equal(2, TableRules.ReliefFor(in l, 642f, 12f, 16f, prev: 2));
        Assert.Equal(2, TableRules.ReliefFor(in l, 665f, 12f, 16f, prev: 2));
        // …only once the width has re-earned the whole band.
        Assert.Equal(1, TableRules.ReliefFor(in l, 666f, 12f, 16f, prev: 2));
        Assert.Equal(Detail.Breakpoints.TierHysteresisDip, TableRules.ReliefHysteresisDip);
    }

    /// <summary>An unmeasured width holds the previous step; the FIRST real measurement is authoritative.</summary>
    [Fact]
    public void Relief_HoldsOnAnUnmeasuredWidthAndTakesTheFirstMeasureOutright()
    {
        var l = ClassicLikedTierTwo;
        Assert.Equal(3, TableRules.ReliefFor(in l, 0f, 12f, 16f, prev: 3));
        Assert.Equal(1, TableRules.ReliefFor(in l, 650f, 12f, 16f, prev: 3, initialized: false));
    }

    /// <summary>The Modern (artist-in-subline) table has one identity star instead of two, so it clears the floor at a
    /// narrower width than Classic — and yields the same weakest fact first when it does not.</summary>
    [Fact]
    public void Relief_ModernLikedTable_ClearsTheFloorEarlierThanClassic()
    {
        var modern = new Track.Lanes(
            Heart: true, Thumb: true, Artist: false, Album: false, By: false, Date: true, Plays: true, Tempo: true,
            Video: false, Actions: true, Expand: false);
        Assert.Equal(648f, TableRules.MinWidthFor(in modern, 12f, 16f));
        Assert.Equal(0, TableRules.NominalReliefFor(in modern, 648f, 12f, 16f));
        Assert.Equal(1, TableRules.NominalReliefFor(in modern, 647f, 12f, 16f));
    }

    /// <summary>Monotone in width: narrower never yields fewer lanes, so the renderer can treat the step as a plain
    /// subtractive pass over the tier's own column set.</summary>
    [Fact]
    public void Relief_IsMonotoneInWidth()
    {
        int prev = TableRules.MaxRelief + 1;
        for (float w = 200f; w <= 1400f; w += 7f)
        {
            int step = TableRules.NominalReliefFor(in ClassicFull, w, 12f, 16f);
            Assert.True(step <= prev, $"relief grew while widening at {w}");
            prev = step;
        }
        Assert.Equal(0, prev);
    }

    // ── the relief APPLICATION (DetailTracks.cs:626-659, now CORE) ──────────────────────────────────────────────────

    /// <summary>The ladder measures the Tempo lane the grid will actually BUILD — through the tier gate — so a tier-4 set
    /// that wants BPM·Key but cannot show it is not charged 80 DIP it never paints.</summary>
    [Fact]
    public void LanesOf_MeasuresTempoThroughTheTierGate()
    {
        var wide = new Track.ColumnSet(Album: false, By: false, Date: true, Video: false, Plays: true, Heart: true,
                                       Thumb: true, Tier: 3, Tempo: true);
        var narrow = wide with { Tier = 4 };

        Assert.True(TableRules.LanesOf(in wide).Tempo);
        Assert.False(TableRules.LanesOf(in narrow).Tempo);
        Assert.Equal(wide.Plays, TableRules.LanesOf(in wide).Plays);
    }

    /// <summary>Relief only ever REMOVES what the tier admitted, and step 0 hands back the very same set.</summary>
    [Fact]
    public void ApplyRelief_RemovesLanesInTheLadderOrderAndStepZeroIsIdentity()
    {
        var set = new Track.ColumnSet(Album: true, By: true, Date: true, Video: false, Plays: true, Heart: true,
                                      Thumb: true, Tier: 0, Tempo: true);

        Assert.Equal(set, TableRules.ApplyRelief(in set, 0));

        var three = TableRules.ApplyRelief(in set, 3);
        Assert.False(three.Plays);
        Assert.False(three.Tempo);
        Assert.False(three.By);
        Assert.True(three.Date);
        Assert.True(three.Album);
        Assert.True(three.Heart);
        Assert.True(three.Actions);                                   // the trailing lane never yields
    }

    /// <summary>Tempo is cleared only where the lane was actually UP. The flag also means "this surface wants BPM·Key";
    /// clearing it where the tier had already hidden the lane would fork the width-track cache for no visible change.</summary>
    [Fact]
    public void ApplyRelief_ClearsTheTempoWishOnlyWhereTheLaneWasShown()
    {
        var shown = new Track.ColumnSet(Album: false, By: false, Date: false, Video: false, Plays: true, Heart: true,
                                        Thumb: false, Tier: 2, Tempo: true);
        var hiddenByTier = shown with { Tier = 4 };

        Assert.False(TableRules.ApplyRelief(in shown, 2).Tempo);
        Assert.True(TableRules.ApplyRelief(in hiddenByTier, 2).Tempo);
        Assert.False(TableRules.ApplyRelief(in hiddenByTier, 2).Plays);
    }

    /// <summary>The shimmer holds through the FIRST PAGE of a Partial edge, not just through Unknown: a two-row first
    /// page must not swap the shimmer for a near-empty list that fills row by row. Unknown always shimmers; Partial
    /// shimmers until <see cref="TableRules.FirstPageRows"/> rows are in, or every row of a shorter list (total clamped
    /// to ≥ count, so an undeclared total cannot strand it); Complete and Failed never shimmer.</summary>
    [Theory]
    [InlineData(EdgeState.Unknown, 0, 0, true)]
    [InlineData(EdgeState.Partial, 3, 50, true)]
    [InlineData(EdgeState.Partial, 12, 50, false)]
    [InlineData(EdgeState.Partial, 5, 5, false)]
    [InlineData(EdgeState.Complete, 0, 0, false)]
    [InlineData(EdgeState.Failed, 0, 0, false)]
    public void RowsPending_HoldsTheShimmerThroughTheFirstPage(EdgeState state, int count, int total, bool pending)
    {
        Assert.Equal(pending, TableRules.RowsPending(state, count, total));
    }

    [Fact]
    public void RowsPending_FirstPageIsTwelveRows()
    {
        Assert.Equal(12, TableRules.FirstPageRows);
        Assert.True(TableRules.RowsPending(EdgeState.Partial, 11, 500));
        Assert.False(TableRules.RowsPending(EdgeState.Partial, 12, 500));
        // An undeclared total (total < count is impossible by contract, but the clamp makes it harmless anyway).
        Assert.False(TableRules.RowsPending(EdgeState.Partial, 7, 0));
    }

    /// <summary>The whole gate: a Complete edge whose first page does not know its face still shimmers (the 2026-09-17
    /// recording — row ids landed, the rows' own fetch had not), releases when the hot rows settle, and a Failed edge
    /// never waits on rows it does not have.</summary>
    [Theory]
    [InlineData(EdgeState.Complete, 1494, 1494, false, true)]
    [InlineData(EdgeState.Complete, 1494, 1494, true, false)]
    [InlineData(EdgeState.Partial, 12, 500, false, true)]
    [InlineData(EdgeState.Partial, 12, 500, true, false)]
    [InlineData(EdgeState.Partial, 3, 500, true, true)]
    [InlineData(EdgeState.Unknown, 0, 0, true, true)]
    [InlineData(EdgeState.Failed, 0, 0, false, false)]
    [InlineData(EdgeState.Complete, 0, 0, true, false)]
    public void RowsPending_WaitsForTheHotRowsToSettle(EdgeState state, int count, int total, bool hotSettled, bool pending)
    {
        Assert.Equal(pending, TableRules.RowsPending(state, count, total, hotSettled));
    }

    [Fact]
    public void HotRows_IsTheFirstPageOrTheWholeShortList()
    {
        Assert.Equal(TableRules.FirstPageRows, TableRules.HotRows(1494));
        Assert.Equal(5, TableRules.HotRows(5));
        Assert.Equal(0, TableRules.HotRows(0));
        Assert.Equal(0, TableRules.HotRows(-3));
    }

    /// <summary>A hot row is loading while an ask is out, and while NOBODY has asked or answered yet (the flush between
    /// the edge landing and the page's demand effect). It settles on its face, or on an answer with nothing in flight —
    /// the disk saying no, a batch that did not name it, a terminal failure after the disk probe.</summary>
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, false)]
    public void RowUnsettled_LoadingWhileAskedOrVirgin_SettledOnFaceOrAnswer(bool knowsFace, bool inFlight, bool answered, bool unsettled)
    {
        Assert.Equal(unsettled, TableRules.RowUnsettled(knowsFace, inFlight, answered));
    }

    [Fact]
    public void RowHasData_IsAValidRowThatKnowsItsFace()
    {
        Assert.True(TableRules.RowHasData(isValid: true, knowsFace: true));
        Assert.False(TableRules.RowHasData(isValid: true, knowsFace: false));
        Assert.False(TableRules.RowHasData(isValid: false, knowsFace: true));
    }

    /// <summary>The row's paint set is Identity WITHOUT Artists: a disk-restored row withholds that bit until the credits
    /// edge re-lands, and a warm open must reveal at once.</summary>
    [Fact]
    public void Face_IsIdentityWithoutArtists()
    {
        Assert.Equal(TrackFields.Identity & ~TrackFields.Artists, TrackFields.Face);
        Assert.Equal(TrackFields.Title | TrackFields.Album | TrackFields.Duration | TrackFields.Explicit | TrackFields.Image, TrackFields.Face);
    }

    /// <summary>Classic Liked Songs at tier 2 with the Queue pane open — ♥ · Title · Artist · Date added · Plays · BPM·key
    /// · duration · "…".</summary>
    static readonly Track.Lanes ClassicLikedTierTwo = new(
        Heart: true, Thumb: false, Artist: true, Album: false, By: false, Date: true, Plays: true, Tempo: true,
        Video: false, Actions: true, Expand: false);

    /// <summary>Every optional Classic lane at once — the widest table the app builds.</summary>
    static readonly Track.Lanes ClassicFull = new(
        Heart: true, Thumb: false, Artist: true, Album: true, By: true, Date: true, Plays: true, Tempo: true,
        Video: false, Actions: true, Expand: false);
}
