using Wavee;
using Xunit;

namespace Wavee.Tests;

// ── SidebarSubtitleRules: the subtitle grammar as data (the UI formats it through the loc table) ────────────────────

public sealed class SidebarSubtitleRulesTests
{
    static SidebarLibraryEntry Entry(SidebarEntryKind kind, string creator = "", int childCount = 0,
                                     bool countKnown = false)
        => new(Id: "x:" + kind, Kind: kind, Uri: "spotify:x", Name: "n", Creator: creator, Cover: default,
            MosaicTiles: null, ChildCount: childCount, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0,
            SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { CountKnown = countKnown };

    [Fact]
    public void Playlist_WithKnownCount_IsSongs()
    {
        var e = Entry(SidebarEntryKind.Playlist, childCount: 48, countKnown: true);
        Assert.Equal(new SidebarSubtitle(SidebarSubtitleKind.Songs, 48, ""), SidebarSubtitleRules.Of(e));
    }

    [Fact]
    public void YourEpisodes_CountsEpisodes_EvenThoughItHoldsEpisodes()
    {
        var e = Entry(SidebarEntryKind.Playlist, childCount: 12, countKnown: true) with { HasEpisodes = true, Episodes = true };
        var s = SidebarSubtitleRules.Of(in e);
        Assert.Equal(SidebarSubtitleKind.Episodes, s.Kind);
        Assert.Equal(e.TrackCount, s.Count);
    }

    [Fact]
    public void Playlist_WithEpisodes_IsItemsNotSongs()
    {
        var e = Entry(SidebarEntryKind.Playlist, childCount: 48, countKnown: true) with { HasEpisodes = true };
        Assert.Equal(new SidebarSubtitle(SidebarSubtitleKind.Items, 48, ""), SidebarSubtitleRules.Of(e));
    }

    [Fact]
    public void Playlist_WithUnknownCount_HasNoSubtitle()
    {
        // Bug A1: an unresolved count must never paint a confident "0 songs".
        var e = Entry(SidebarEntryKind.Playlist, childCount: 0, countKnown: false);
        Assert.Equal(SidebarSubtitle.None, SidebarSubtitleRules.Of(e));
    }

    [Fact]
    public void Album_IsItsFirstArtist()
    {
        var e = Entry(SidebarEntryKind.Album, creator: "Frank Ocean, Tyler") with { FirstArtistName = "Frank Ocean" };
        Assert.Equal(new SidebarSubtitle(SidebarSubtitleKind.Album, 0, "Frank Ocean"), SidebarSubtitleRules.Of(e));
    }

    [Fact]
    public void Album_WithOnlyACreator_IsTheCreator()
    {
        var e = Entry(SidebarEntryKind.Album, creator: "Tyler");
        Assert.Equal(new SidebarSubtitle(SidebarSubtitleKind.Album, 0, "Tyler"), SidebarSubtitleRules.Of(e));
    }

    [Fact]
    public void Show_IsItsPublisher()
    {
        var e = Entry(SidebarEntryKind.Show, creator: "NPR");
        Assert.Equal(new SidebarSubtitle(SidebarSubtitleKind.Podcast, 0, "NPR"), SidebarSubtitleRules.Of(e));
    }

    [Fact]
    public void Artist_IsTheKindAlone()
    {
        Assert.Equal(new SidebarSubtitle(SidebarSubtitleKind.Artist, 0, ""),
                     SidebarSubtitleRules.Of(Entry(SidebarEntryKind.Artist)));
    }

    [Fact]
    public void Folder_WithKnownCount_IsItems()
    {
        var e = Entry(SidebarEntryKind.Folder, childCount: 5, countKnown: true);
        Assert.Equal(new SidebarSubtitle(SidebarSubtitleKind.Items, 5, ""), SidebarSubtitleRules.Of(e));
    }

    [Fact]
    public void Folder_WithUnknownCount_HasNoSubtitle()
        => Assert.Equal(SidebarSubtitle.None, SidebarSubtitleRules.Of(Entry(SidebarEntryKind.Folder, childCount: 5)));

    [Theory]
    [InlineData(SidebarEntryKind.Track)]
    [InlineData(SidebarEntryKind.AppRoute)]
    public void TrackOrRoute_IsItsCreator(SidebarEntryKind kind)
    {
        Assert.Equal(new SidebarSubtitle(SidebarSubtitleKind.Text, 0, "Wavee"),
                     SidebarSubtitleRules.Of(Entry(kind, creator: "Wavee")));
    }

    [Theory]
    [InlineData(SidebarEntryKind.Track)]
    [InlineData(SidebarEntryKind.AppRoute)]
    public void TrackOrRoute_WithoutACreator_HasNoSubtitle(SidebarEntryKind kind)
        => Assert.Equal(SidebarSubtitle.None, SidebarSubtitleRules.Of(Entry(kind)));
}
