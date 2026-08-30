using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The per-kind rung tables (design §1.2). These predicates replace ≥6 unshared "is it cold?" copies
/// (IsAlbumOpenReady, IsAlbumComplete, the four-clause artist gate, both NowPlayingReady copies, ArtistStatsCache's
/// freshness gate, LibrarySync's "unnamed ⇒ cold") — so every rung boundary is pinned here once.</summary>
public class HydrationLevelsTests
{
    static Image Art => new("https://i.scdn.co/image/abc", 300, 300);
    static ArtistRef Named => new("a1", "spotify:artist:a1", "Artist One");
    static AlbumRef NamedAlbum => new("al1", "spotify:album:al1", "Album One");

    // ── Track ────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Track Thin(string uri = "spotify:track:t1")
        => new("t1", uri, uri, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);

    static Track Open(Track t) => t with
    {
        Title = "Song",
        Artists = new[] { Named },
        Album = NamedAlbum,
        DurationMs = 210_000,
        Image = Art,
    };

    [Fact]
    public void Track_Null_IsNone() => Assert.Equal(HydrationLevel.None, HydrationLevels.Of((Track?)null));

    [Fact]
    public void Track_PlaceholderTitle_IsNone()   // title == uri is the synthetic seed every thin writer stamps
        => Assert.Equal(HydrationLevel.None, HydrationLevels.Of(Thin()));

    [Fact]
    public void Track_Named_IsIdentity()
        => Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Thin() with { Title = "Song" }));

    [Fact]
    public void Track_NowPlayingReadyPlusDuration_IsRich()   // Rich ≡ Open for a playable, so Of reports the higher name
        => Assert.Equal(HydrationLevel.Rich, HydrationLevels.Of(Open(Thin())));

    [Theory]
    [InlineData("image")]
    [InlineData("duration")]
    [InlineData("artist")]
    [InlineData("album")]
    public void Track_MissingOneOpenClause_FallsBackToIdentity(string missing)
    {
        var t = Open(Thin());
        t = missing switch
        {
            "image" => t with { Image = null },
            "duration" => t with { DurationMs = 0 },
            "artist" => t with { Artists = new[] { new ArtistRef("a1", "spotify:artist:a1", "") } },
            _ => t with { Album = new AlbumRef("al1", "spotify:album:al1", "") },
        };
        Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(t));
    }

    [Fact]
    public void Track_WithAvailabilityVerdict_IsFull()   // only getTrack/TrackV4 files one
        => Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(Open(Thin()) with { Availability = Availability.Playable }));

    // ── Episode ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static Episode Ep(string? show = "The Show", Image? img = null, long dur = 0, string? desc = null)
        => new("e1", "spotify:episode:e1", "Ep 1", show ?? "", img, dur, DateTimeOffset.UnixEpoch, desc);

    [Fact]
    public void Episode_Null_IsNone() => Assert.Equal(HydrationLevel.None, HydrationLevels.Of((Episode?)null));

    [Fact]
    public void Episode_TitleOnly_IsIdentity() => Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Ep()));

    [Fact]
    public void Episode_ShowNameImageDuration_IsRich()
        => Assert.Equal(HydrationLevel.Rich, HydrationLevels.Of(Ep(img: Art, dur: 1000)));

    [Fact]
    public void Episode_WithDescription_IsFull()
        => Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(Ep(img: Art, dur: 1000, desc: "About this episode")));

    [Fact]
    public void Episode_NoShowName_StaysIdentity()
        => Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Ep(show: "", img: Art, dur: 1000)));

    // ── Album ────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Track AlbumRow(int i, string title = "Row")
        => new($"t{i}", $"spotify:track:t{i}", title, new[] { Named }, NamedAlbum, 1000, false, Art);

    static Album Alb(AlbumHydrationLevel h, IReadOnlyList<Track>? tracks, string? copyright = null, string? released = null)
        => new("al1", "spotify:album:al1", "Album One", Art, new[] { Named }, 2020, tracks?.Count ?? 0,
               tracks, Copyright: copyright, ReleaseDate: released, Hydration: h);

    [Fact]
    public void Album_Null_IsNone() => Assert.Equal(HydrationLevel.None, HydrationLevels.Of((Album?)null));

    [Fact]
    public void Album_SummaryHeader_IsIdentity()
        => Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Alb(AlbumHydrationLevel.Summary, null)));

    [Fact]
    public void Album_TracksButUnnamedRow_IsIdentity()   // the old HasUnnamedTrack gate — a disc row still owed a TrackV4
    {
        var rows = new[] { AlbumRow(1), AlbumRow(2, "spotify:track:t2") };
        Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Alb(AlbumHydrationLevel.Tracks, rows)));
    }

    [Fact]
    public void Album_TracksAllNamed_IsOpen()   // = the old IsAlbumOpenReady
        => Assert.Equal(HydrationLevel.Open, HydrationLevels.Of(Alb(AlbumHydrationLevel.Tracks, new[] { AlbumRow(1) })));

    [Theory]
    [InlineData("© 2020 Label", null)]
    [InlineData(null, "2020-01-01")]
    public void Album_WithPublishing183_IsRich(string? copyright, string? released)
        => Assert.Equal(HydrationLevel.Rich,
            HydrationLevels.Of(Alb(AlbumHydrationLevel.Tracks, new[] { AlbumRow(1) }, copyright, released)));

    [Fact]
    public void Album_FullEnvelope_IsFull()
        => Assert.Equal(HydrationLevel.Full,
            HydrationLevels.Of(Alb(AlbumHydrationLevel.Full, new[] { AlbumRow(1) }, "© 2020 Label")));

    // THE ordering regression (finding 10). Plenty of releases carry no publishing facet at all — no (c)/(p) line and
    // no release date. The rungs used to be tested Rich-first, so such an album came back OPEN even holding a complete
    // getAlbum envelope: DetailTrailing asks for Full, could never see its own answer, and re-ran getAlbum every
    // AlbumFullTtl forever. Full is "we have the envelope", full stop.
    [Fact]
    public void Album_FullEnvelopeWithNoPublishingFacet_IsStillFull()
        => Assert.Equal(HydrationLevel.Full,
            HydrationLevels.Of(Alb(AlbumHydrationLevel.Full, new[] { AlbumRow(1) }, copyright: null, released: null)));

    // …and the other half of the restructure: the envelope alone is enough for Rich, without a (c)/(p) line.
    [Fact]
    public void Album_FullEnvelope_SatisfiesRichToo()
        => Assert.True(HydrationLevels.Of(Alb(AlbumHydrationLevel.Full, new[] { AlbumRow(1) })) >= HydrationLevel.Rich);

    [Fact]
    public void Album_RestoredThinFullIsDemotedToRich()
    {
        // CachedStore caps the PERSISTED Hydration at Tracks (the thin split), so a restored album reads back as Rich
        // and the below-the-fold surface re-asks getAlbum inside its 10-minute cache. Deliberate — design §1.2.
        var restored = Alb(AlbumHydrationLevel.Tracks, new[] { AlbumRow(1) }, "© 2020 Label", "2020-01-01");
        Assert.Equal(HydrationLevel.Rich, HydrationLevels.Of(restored));
    }

    // ── Artist ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static Album Disc(string name = "Release") => new("d1", "spotify:album:d1", name, Art, new[] { Named }, 2020, 1);

    static Artist Art0(IReadOnlyList<Album>? top = null, int albumsTotal = 0,
                       IReadOnlyList<Track>? topTracks = null, Album? latest = null)
        => new("a1", "spotify:artist:a1", "Artist One", Art, top,
               AlbumsTotal: albumsTotal, TopTracks: topTracks, LatestRelease: latest);

    [Fact]
    public void Artist_Null_IsNone() => Assert.Equal(HydrationLevel.None, HydrationLevels.Of((Artist?)null));

    [Fact]
    public void Artist_NameOnly_IsIdentity() => Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Art0()));

    [Fact]
    public void Artist_UnnamedDiscographyStub_IsIdentity()
        => Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Art0(new[] { Disc("") })));

    [Fact]
    public void Artist_FacetTotalExceedsHeld_IsIdentity()   // pages are still missing — the discography is not assembled
        => Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Art0(new[] { Disc() }, albumsTotal: 40)));

    [Fact]
    public void Artist_AssembledDiscography_IsOpen()
        => Assert.Equal(HydrationLevel.Open, HydrationLevels.Of(Art0(new[] { Disc() }, albumsTotal: 1)));

    [Fact]
    public void Artist_WithOverview_IsRich()
        => Assert.Equal(HydrationLevel.Rich,
            HydrationLevels.Of(Art0(new[] { Disc() }, 1, new[] { AlbumRow(1) }, latest: Disc())));

    [Fact]
    public void Artist_WithExtendedChart_IsFull()
    {
        // The COUNT is the "already extended" gate: more top tracks than the overview seeds ⇒ the chart landed.
        var chart = new List<Track>();
        for (int i = 0; i <= ArtistPopularTracks.OverviewSeedCap; i++) chart.Add(AlbumRow(i));
        Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(Art0(new[] { Disc() }, 1, chart, latest: Disc())));
    }

    [Fact]
    public void Artist_OverviewWithoutReleasesColumn_StaysOpen()
        => Assert.Equal(HydrationLevel.Open, HydrationLevels.Of(Art0(new[] { Disc() }, 1, new[] { AlbumRow(1) })));

    // ── Playlist / Show / Owner ──────────────────────────────────────────────────────────────────────────────────────

    static Playlist Pl(string name = "My List")
        => new("p1", "spotify:playlist:p1", name, null, "me", Art, 0);

    [Fact]
    public void Playlist_Null_IsNone() => Assert.Equal(HydrationLevel.None, HydrationLevels.Of((Playlist?)null, true));

    [Fact]
    public void Playlist_HeaderOnly_IsIdentity() => Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Pl(), false));

    [Fact]
    public void Playlist_WithMembership_IsFull()   // Open ≡ Rich ≡ Full for a playlist — LibrarySync owns its freshness
        => Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(Pl(), true));

    static Show Sh(string name = "The Show") => new("s1", "spotify:show:s1", name, "Publisher", Art);

    [Fact]
    public void Show_Null_IsNone() => Assert.Equal(HydrationLevel.None, HydrationLevels.Of((Show?)null, true, 0, 0));

    [Fact]
    public void Show_NoMembership_IsIdentity()
        => Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Sh(), false, 0, 0));

    [Fact]
    public void Show_PartialFirstPage_IsIdentity()
        => Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Sh(), true, 299, 1000));

    [Fact]
    public void Show_FirstPageResident_IsRich()   // Open ≡ Rich
        => Assert.Equal(HydrationLevel.Rich, HydrationLevels.Of(Sh(), true, HydrationLevels.ShowOpenPage, 1000));

    [Fact]
    public void Show_SmallShowFullyResident_IsFull()
        => Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(Sh(), true, 12, 12));

    [Fact]
    public void Show_AllPagesResident_IsFull()
        => Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(Sh(), true, 1000, 1000));

    /// <summary>The Open rung is about the HEAD — the page the show actually renders — and Full is about the whole
    /// list. Feeding one combined count let episodes resident anywhere (a Liked-Episodes sweep, a playlist carrying
    /// this show's episodes) carry the head threshold on their own, so the show reported paintable with holes at the
    /// top — and, being reported as satisfied, those rows were then never fetched.</summary>
    [Fact]
    public void Show_TailResident_DoesNotSatisfyTheHead()
    {
        // 400 episodes resident somewhere in a 1000-member show, but only 5 of them in the first page.
        Assert.Equal(HydrationLevel.Identity, HydrationLevels.Of(Sh(), true, 5, 1000, 400));
        // The head really being resident is Open/Rich, whatever the tail has done.
        Assert.Equal(HydrationLevel.Rich, HydrationLevels.Of(Sh(), true, HydrationLevels.ShowOpenPage, 1000, 400));
        // And Full still reads the TOTAL, not the head (a head count can never reach a 1000-member list).
        Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(Sh(), true, HydrationLevels.ShowOpenPage, 1000, 1000));
    }

    [Fact]
    public void Owner_NameIsEveryRung()
    {
        Assert.Equal(HydrationLevel.None, HydrationLevels.Of((Owner?)null));
        Assert.Equal(HydrationLevel.None, HydrationLevels.Of(new Owner("u1", "", null)));
        Assert.Equal(HydrationLevel.Full, HydrationLevels.Of(new Owner("u1", "Bob", null)));
    }

    // ── Row-gap primitives ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("spotify:track:t1", true)]   // title == uri: the synthetic placeholder
    [InlineData("Real Title", false)]
    public void TitleMissing_TreatsThePlaceholderAsAbsent(string? title, bool missing)
        => Assert.Equal(missing, HydrationLevels.TitleMissing(title, "spotify:track:t1"));

    [Fact]
    public void TrackUnnamed_CoversArtistsWithoutNames()
    {
        Assert.False(HydrationLevels.TrackUnnamed(AlbumRow(1)));
        Assert.True(HydrationLevels.TrackUnnamed(AlbumRow(1) with { Title = "spotify:track:t1" }));
        Assert.True(HydrationLevels.TrackUnnamed(AlbumRow(1) with
        {
            Artists = new[] { new ArtistRef("a1", "spotify:artist:a1", "") },
        }));
        // A ref with no URI is a plain local/synthetic credit, not a fetchable gap.
        Assert.False(HydrationLevels.TrackUnnamed(AlbumRow(1) with { Artists = new[] { new ArtistRef("", "", "") } }));
    }

    [Fact]
    public void RefNeedsName_IsUriWithoutName()
    {
        Assert.True(HydrationLevels.RefNeedsName(new AlbumRef("al1", "spotify:album:al1", "")));
        Assert.False(HydrationLevels.RefNeedsName(new AlbumRef("al1", "spotify:album:al1", "Album")));
        Assert.False(HydrationLevels.RefNeedsName(new AlbumRef("", "", "")));
    }

    [Fact]
    public void Rungs_AreOrdered()   // every ladder compares with >=, so the numeric order is load-bearing
    {
        Assert.True(HydrationLevel.None < HydrationLevel.Identity);
        Assert.True(HydrationLevel.Identity < HydrationLevel.Open);
        Assert.True(HydrationLevel.Open < HydrationLevel.Rich);
        Assert.True(HydrationLevel.Rich < HydrationLevel.Full);
    }
}
