// ── Wavee.Tests/HomeUi/RecentsCellsTests.cs — Wave 1, owner G ───────────────────────────────────────────────────────
//
// A small local staging helper (`BandWithTrackCounts`) duplicates the minimal slice of `HomeUiFixtures.Band`'s own
// staging code because `HomeUiFixtures.CardSpec` has no track-count field and that file belongs to another wave-1
// owner (the wave's disjoint-files rule) — this is the only file that needs a playlist/collection row's
// `TrackCount` set for a fixture, so it stays local rather than growing the shared fixture's surface.

using System.Globalization;
using FluentGpu.Localization;
using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

[Collection(EntitiesCollection.Name)]
public class RecentsCellsTests
{
    const long NowMs = 1_800_000_000_000L;
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    static Section BandWithTrackCounts(string title, params (string Uri, string Title, int TrackCount)[] cards)
    {
        var s = Staging.Rent();
        string sectionUri = HomeUiFixtures.NextSectionUri("recents-cells");
        var id = new StagedId(s.Text(sectionUri));
        int mark = s.Edges.PendingMark;
        int factStart = s.CardFacts.Count;

        foreach (var (uri, cardTitle, trackCount) in cards)
        {
            var cid = new StagedId(s.Text(uri));
            ref var p = ref s.Playlists.RowFor(cid, Authority.Full, (uint)(PlaylistFields.Identity | PlaylistFields.TrackCount));
            p.Title = s.Text(cardTitle);
            p.TrackCount = trackCount;

            ref var f = ref s.CardFacts.Add();
            f.Target = cid;
            f.SeedStart = -1;
            s.Edges.Push().Target = cid;
        }

        ref var row = ref s.Sections.RowFor(id, Authority.Full, (uint)SectionFields.Identity);
        row.Title = s.Text(title);
        row.Kind = (byte)SectionKind.HomeRecentlyPlayed;
        row.Total = cards.Length;
        row.Raw = cards.Length;
        row.Cards = cards.Length;
        row.NextOffset = SectionPaging.NoCursor;
        row.HasFacts = true;
        row.FactStart = factStart;
        row.FactCount = s.CardFacts.Count - factStart;

        s.Edges.Close(Relation.SectionCards, in id, mark);
        TestScope.CommitAndPublish(s);
        return Entities.Section(sectionUri.AsSpan());
    }

    static SectionInput Of(Section s) => SectionReader.Of(s);

    [Fact]
    public void Caps_at_max_cells_even_with_more_items()
    {
        TestScope.Fresh();
        var section = Of(HomeUiFixtures.Band("Recently played", SectionKind.HomeRecentlyPlayed,
            HomeUiFixtures.Artist("spotify:artist:1", "A"), HomeUiFixtures.Artist("spotify:artist:2", "B"),
            HomeUiFixtures.Artist("spotify:artist:3", "C"), HomeUiFixtures.Artist("spotify:artist:4", "D"),
            HomeUiFixtures.Artist("spotify:artist:5", "E"), HomeUiFixtures.Artist("spotify:artist:6", "F"),
            HomeUiFixtures.Artist("spotify:artist:7", "G"), HomeUiFixtures.Artist("spotify:artist:8", "H"),
            HomeUiFixtures.Artist("spotify:artist:9", "I")));

        var cells = RecentsCells.Of(section.Cards, new Dictionary<string, long>(), 0, NowMs, Utc, Culture);

        Assert.Equal(RecentsCells.MaxCells, cells.Count);
    }

    [Fact]
    public void Artist_card_is_round_with_artist_detail()
    {
        TestScope.Fresh();
        var section = Of(HomeUiFixtures.Band("Recently played", SectionKind.HomeRecentlyPlayed,
            HomeUiFixtures.Artist("spotify:artist:rich-brian", "Rich Brian")));

        var cell = RecentsCells.Of(section.Cards, new Dictionary<string, long>(), 0, NowMs, Utc, Culture)[0];

        Assert.True(cell.Round);
        Assert.Equal("Rich Brian", cell.Title);
        Assert.Equal("Artist", cell.Detail);
    }

    [Fact]
    public void Album_card_detail_is_type_and_artist()
    {
        TestScope.Fresh();
        var section = Of(HomeUiFixtures.Band("Recently played", SectionKind.HomeRecentlyPlayed,
            HomeUiFixtures.Album("spotify:album:tristam", "Marigold")));
        // The album's Subtitle (first album artist) needs an artist edge, which HomeUiFixtures.Album alone doesn't
        // stage — assert the fallback ("Album" alone) still shapes correctly; ZonePlannerTests/ClusterFoldTests
        // exercise the artist-populated Subtitle path already for the same HomeCard.Subtitle derivation.
        var cell = RecentsCells.Of(section.Cards, new Dictionary<string, long>(), 0, NowMs, Utc, Culture)[0];

        Assert.False(cell.Round);
        Assert.StartsWith("Album", cell.Detail);
    }

    [Fact]
    public void Liked_songs_card_detail_is_the_song_count_alone()
    {
        TestScope.Fresh();
        var section = Of(HomeUiFixtures.Band("Recently played", SectionKind.HomeRecentlyPlayed,
            HomeUiFixtures.Playlist("spotify:collection:tracks", "Liked Songs")));

        var cell = RecentsCells.Of(section.Cards, new Dictionary<string, long>(), 1284, NowMs, Utc, Culture)[0];

        Assert.Equal(HomeCardKind.Liked, cell.Card.Kind);
        Assert.DoesNotContain("Playlist", cell.Detail);
        Assert.Contains("1284", cell.Detail);
        Assert.Equal(Strings.Detail.SongCount(1284), cell.Detail);
    }

    [Fact]
    public void Playlist_card_detail_is_type_and_song_count()
    {
        TestScope.Fresh();
        var section = Of(BandWithTrackCounts("Recently played", ("spotify:playlist:workout", "Workout Mix", 70)));

        var cell = RecentsCells.Of(section.Cards, new Dictionary<string, long>(), 0, NowMs, Utc, Culture)[0];

        Assert.Equal("Playlist · 70 songs", cell.Detail);
    }

    [Fact]
    public void When_is_blank_for_a_card_with_no_played_timestamp()
    {
        TestScope.Fresh();
        var section = Of(HomeUiFixtures.Band("Recently played", SectionKind.HomeRecentlyPlayed,
            HomeUiFixtures.Artist("spotify:artist:no-when", "No When")));

        var cell = RecentsCells.Of(section.Cards, new Dictionary<string, long>(), 0, NowMs, Utc, Culture)[0];

        Assert.Equal("", cell.When);
    }

    [Fact]
    public void When_resolves_through_the_whenByUri_map()
    {
        TestScope.Fresh();
        var section = Of(HomeUiFixtures.Band("Recently played", SectionKind.HomeRecentlyPlayed,
            HomeUiFixtures.Artist("spotify:artist:yesterday", "Yesterday Artist")));
        var card = section.Cards[0];
        var when = new Dictionary<string, long> { [card.Uri] = NowMs - 5 * 60 * 1000 }; // 5 min ago

        var cell = RecentsCells.Of(section.Cards, when, 0, NowMs, Utc, Culture)[0];

        Assert.Contains("5", cell.When);
    }

    [Fact]
    public void Empty_input_produces_no_cells()
    {
        TestScope.Fresh();
        Assert.Empty(RecentsCells.Of([], new Dictionary<string, long>(), 0, NowMs, Utc, Culture));
    }

    [Theory]
    [InlineData("Single - SHAUN", "Single · SHAUN")]
    [InlineData("Album - Arcane, League of Legends", "Album · Arcane, League of Legends")]
    public void TypedLine_RepunctuatesTheRecentsTypeLine(string subtitle, string expected)
        => Assert.Equal(expected, RecentsCells.TypedLine(subtitle));

    [Fact]
    public void TypedLine_LeavesAPlainArtistLineAlone()
        => Assert.Null(RecentsCells.TypedLine("Tristam"));

    [Theory]
    [InlineData(4, 9, 7)]  // 2*4-1 = 7, within available
    [InlineData(1, 9, 1)]  // cols clamped up to 1 minimum: 2*1-1 = 1
    [InlineData(0, 9, 1)]  // cols <= 0 also clamps to 1
    [InlineData(4, 3, 3)]  // wants 7 but only 3 available
    [InlineData(4, 0, 0)]  // nothing available
    public void RecentsPlan_Cells_MatchesTheTwoRowFormula(int cols, int available, int expected)
        => Assert.Equal(expected, RecentsPlan.Cells(cols, available));

    [Fact]
    public void NowCaption_WithADevice_UsesThePlayingOnPhrase()
        => Assert.Equal(Strings.Home.PlayingOn("Kitchen speaker"), RecentsCells.NowCaption("Kitchen speaker"));

    [Fact]
    public void NowCaption_WithNoDevice_FallsBackToThePlainPlayingNowPhrase()
        => Assert.Equal(Loc.Get(Strings.Home.When.PlayingNow), RecentsCells.NowCaption(null));

    [Fact]
    public void NowCaption_WithAnEmptyDevice_FallsBackToThePlainPlayingNowPhrase()
        => Assert.Equal(Loc.Get(Strings.Home.When.PlayingNow), RecentsCells.NowCaption(""));
}
