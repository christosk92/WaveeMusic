using System;
using System.Collections.Generic;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

// The bound track row's span-index → entity layout (Operation ultra-fast, P5 slice 3 — see
// docs/plans/wavee/operation-ultra-fast-app-progress.md). RowSpanLayout (Features/Detail/RowHandlers.cs) is the ONE
// place TrackRowTemplate's span-BUILDING code (Meta/ArtistCell, which fill a SpanBuffer with TextSpan(IsLink: ...))
// and its click-index-to-ROUTE resolver (GoToMetaSpan/ArtistCell's OnSpanClick) both read — so they can never
// disagree about what clicking span N means. The actual route STRING (RichText.RouteForUri) is FluentGpu-bound and
// out of reach here (same "Wavee.Tests doesn't reference Wavee.dll" constraint TrackRowTemplateTests documents); what
// is tested is the part that decides which entity (if any) span N refers to — the substantive, otherwise-untestable
// decision the eager TrackRow.ArtistLinks/MetadataLine made inline per-render and this bound path now shares.
public sealed class RowSpanLayoutTests
{
    static ArtistRef Artist(string n) => new(n, "spotify:artist:" + n, n);

    static Track Song(IReadOnlyList<ArtistRef> artists, AlbumRef? album = null, string uri = "spotify:track:a") =>
        new("a", uri, "Title", artists, album ?? new("album", "spotify:album:album", "Album"), 200_000, false, null);

    static RowPresentation Row(Track t, bool showArtist, bool showListMeta) =>
        RowPresentation.Empty with { Track = t, ShowTrackArtist = showArtist, ShowListMetadata = showListMeta };

    [Fact]
    public void ArtistsOfNoArtistsIsEmpty()
        => Assert.Empty(RowSpanLayout.Artists(Array.Empty<ArtistRef>()));

    [Fact]
    public void ArtistsSingleArtistIsOneSlotNoSeparator()
    {
        var slots = RowSpanLayout.Artists([Artist("Rex")]);
        Assert.Single(slots);
        Assert.Equal(MetaSpanKind.Artist, slots[0].Kind);
        Assert.Equal("Rex", slots[0].Name);
    }

    [Fact]
    public void ArtistsMultipleInterleavesSeparatorsAtOddIndices()
    {
        var slots = RowSpanLayout.Artists([Artist("A"), Artist("B"), Artist("C")]);
        // A , B , C -> 5 slots: Artist, Separator, Artist, Separator, Artist
        Assert.Equal(5, slots.Count);
        Assert.Equal(MetaSpanKind.Artist, slots[0].Kind);
        Assert.Equal(MetaSpanKind.Separator, slots[1].Kind);
        Assert.Equal(MetaSpanKind.Artist, slots[2].Kind);
        Assert.Equal(MetaSpanKind.Separator, slots[3].Kind);
        Assert.Equal(MetaSpanKind.Artist, slots[4].Kind);
        Assert.Equal("A", slots[0].Name);
        Assert.Equal("B", slots[2].Name);
        Assert.Equal("C", slots[4].Name);
    }

    [Fact]
    public void MetadataWithArtistsAndAlbumAppendsAlbumAfterASeparator()
    {
        var t = Song([Artist("A"), Artist("B")]);
        var slots = RowSpanLayout.Metadata(Row(t, showArtist: true, showListMeta: true), showAlbumInMeta: true);
        // A ,(sep) B ·(sep) Album -> 5 slots
        Assert.Equal(5, slots.Count);
        Assert.Equal(MetaSpanKind.Artist, slots[0].Kind);
        Assert.Equal(MetaSpanKind.Separator, slots[1].Kind);
        Assert.Equal(MetaSpanKind.Artist, slots[2].Kind);
        Assert.Equal(MetaSpanKind.Separator, slots[3].Kind);
        Assert.Equal(MetaSpanKind.Album, slots[4].Kind);
    }

    [Fact]
    public void MetadataAlbumOnlyHasNoLeadingSeparator()
    {
        var t = Song([]);
        var slots = RowSpanLayout.Metadata(Row(t, showArtist: false, showListMeta: true), showAlbumInMeta: true);
        var album = Assert.Single(slots);
        Assert.Equal(MetaSpanKind.Album, album.Kind);
        Assert.Equal("Album", album.Name);
        Assert.Equal("spotify:album:album", album.Uri);
    }

    [Fact]
    public void MetadataOmitsAlbumWhenNotFoldedInAndUnnamed()
    {
        var t = Song([Artist("A")], album: new("", "", ""));
        var slots = RowSpanLayout.Metadata(Row(t, showArtist: true, showListMeta: true), showAlbumInMeta: true);
        // Album name is empty -> never appended, regardless of showAlbumInMeta/ShowListMetadata.
        var one = Assert.Single(slots);
        Assert.Equal(MetaSpanKind.Artist, one.Kind);
    }

    [Fact]
    public void MetadataOmitsAlbumWhenShowAlbumInMetaIsFalseAndNotAnEpisode()
    {
        var t = Song([Artist("A")]);
        var slots = RowSpanLayout.Metadata(Row(t, showArtist: true, showListMeta: true), showAlbumInMeta: false);
        var one = Assert.Single(slots);
        Assert.Equal(MetaSpanKind.Artist, one.Kind);
    }

    [Fact]
    public void MetadataAlwaysShowsAnEpisodesShowEvenWhenShowAlbumInMetaIsFalse()
    {
        // EntityUri.KindOf recognizes an episode uri regardless of the folded-album static flag — MetadataLine's own
        // episode-always-shows-show rule (TrackRow.cs), preserved verbatim on the bound path.
        var t = Song([], album: new("show1", "spotify:show:show1", "My Podcast"), uri: "spotify:episode:ep1");
        var slots = RowSpanLayout.Metadata(Row(t, showArtist: false, showListMeta: false), showAlbumInMeta: false);
        var album = Assert.Single(slots);
        Assert.Equal(MetaSpanKind.Album, album.Kind);
        Assert.Equal("My Podcast", album.Name);
    }

    [Fact]
    public void MetadataOfNothingToShowIsEmpty()
    {
        var t = Song([]);
        var slots = RowSpanLayout.Metadata(Row(t, showArtist: false, showListMeta: false), showAlbumInMeta: false);
        Assert.Empty(slots);
    }

    [Fact]
    public void SeparatorSlotsCarryNoUriOrName()
    {
        var slots = RowSpanLayout.Artists([Artist("A"), Artist("B")]);
        var sep = slots[1];
        Assert.Equal(MetaSpanKind.Separator, sep.Kind);
        Assert.Equal("", sep.Uri);
        Assert.Equal("", sep.Name);
    }

    // ── the artist chart row's subtitle (ArtistPopular) ─────────────────────────────────────────────────────────

    const string Page = "spotify:artist:Page";
    const string Glyph = "\uE714";

    static IReadOnlyList<MetaSpanSlot> Chart(Track t, bool video = false, string? plays = null)
        => RowSpanLayout.ChartSubtitle(t, Page, video, plays, "feat.", Glyph);

    [Fact]
    public void ChartSubtitleFeaturedCreditsExcludeThePageArtistAndLeadWithTheFeatLabel()
    {
        var t = Song([Artist("Page"), Artist("Ninajirachi")]);
        var slots = Chart(t);
        Assert.Equal(2, slots.Count);
        Assert.Equal(MetaSpanKind.Label, slots[0].Kind);
        Assert.Equal("feat. ", slots[0].Text);
        Assert.Equal(MetaSpanKind.Artist, slots[1].Kind);
        Assert.Equal("spotify:artist:Ninajirachi", slots[1].Uri);
    }

    [Fact]
    public void ChartSubtitleSeparatesSeveralFeaturedArtistsWithCommas()
    {
        var t = Song([Artist("A"), Artist("Page"), Artist("B")]);
        var slots = Chart(t);
        // feat. A , B
        Assert.Equal(4, slots.Count);
        Assert.Equal("A", slots[1].Name);
        Assert.Equal(MetaSpanKind.Separator, slots[2].Kind);
        Assert.Equal(", ", slots[2].Text);
        Assert.Equal("B", slots[3].Name);
    }

    [Fact]
    public void ChartSubtitleHasNoFeatWhenThePageArtistIsNotCredited()
    {
        var t = Song([Artist("A"), Artist("B")]);
        Assert.Empty(Chart(t));
    }

    [Fact]
    public void ChartSubtitleHasNoFeatWhenThePageArtistIsTheOnlyCredit()
    {
        var t = Song([Artist("Page")]);
        Assert.Empty(Chart(t));
    }

    [Fact]
    public void ChartSubtitleAppendsVideoGlyphAndPlaysAfterDots()
    {
        var t = Song([Artist("Page"), Artist("A")]);
        var slots = Chart(t, video: true, plays: "6.3M plays");
        // feat. A · glyph · 6.3M plays
        Assert.Equal(6, slots.Count);
        Assert.Equal(MetaSpanKind.Separator, slots[2].Kind);
        Assert.Equal(" · ", slots[2].Text);
        Assert.Equal(MetaSpanKind.Glyph, slots[3].Kind);
        Assert.Equal(Glyph, slots[3].Text);
        Assert.Equal(MetaSpanKind.Separator, slots[4].Kind);
        Assert.Equal(MetaSpanKind.Label, slots[5].Kind);
        Assert.Equal("6.3M plays", slots[5].Text);
    }

    [Fact]
    public void ChartSubtitleNeverStartsWithASeparator()
    {
        var t = Song([Artist("Page")]);
        var plays = Assert.Single(Chart(t, plays: "12 plays"));
        Assert.Equal(MetaSpanKind.Label, plays.Kind);
        var glyph = Assert.Single(Chart(t, video: true));
        Assert.Equal(MetaSpanKind.Glyph, glyph.Kind);
    }

    [Fact]
    public void ChartSubtitlePendingPlaysLeavesNoTrailingSeparator()
    {
        var t = Song([Artist("Page"), Artist("A")]);
        var slots = Chart(t, video: true, plays: null);
        Assert.Equal(4, slots.Count);
        Assert.Equal(MetaSpanKind.Glyph, slots[^1].Kind);
    }

    [Fact]
    public void ChartSubtitleWithoutAPageArtistUriHasNoFeat()
    {
        var t = Song([Artist("A"), Artist("B")]);
        var slots = RowSpanLayout.ChartSubtitle(t, "", hasVideo: false, "1 plays", "feat.", Glyph);
        var plays = Assert.Single(slots);
        Assert.Equal("1 plays", plays.Text);
    }
}
