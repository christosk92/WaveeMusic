using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// <see cref="SidebarNavPreview"/> — the sidebar's half of S2 #7 (opening a playlist/album from the sidebar used to
/// paint a header-less skeleton that reshaped once the full model landed, unlike the identical Home card). This
/// class pins the pure mapping <c>SidebarPane.Navigate(routeKey, arg, in entry)</c> stashes into
/// <c>NavPreviewStore</c> through <c>DetailPreview.FromPlaylist</c>/<c>FromAlbum</c>: prefer the library store's own
/// record when the uri resolves there, fall back to the row's own display cache otherwise.
/// </summary>
public class SidebarNavPreviewTests
{
    static SidebarLibraryEntry Entry(string id, SidebarEntryKind kind, string uri, string name, string creator,
        Image? cover = null, IReadOnlyList<string>? mosaic = null, int childCount = 0,
        bool canEdit = false, bool isOwner = false) =>
        new(id, kind, uri, name, creator, cover, mosaic, childCount, AddedAtMs: 0, SortStamp: 0,
            LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { CanEdit = canEdit, IsOwner = isOwner };

    // ── FindPlaylist / FindAlbum — the store lookup ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FindPlaylist_MatchesByUri()
    {
        var playlists = new List<PlaylistSummary>
        {
            new("spotify:playlist:1", "Mix", "Alice", 10, null),
            new("spotify:playlist:2", "Daylist: mandopop mix", "spotify", 25, null,
                DaylistExpiresAtMs: 1_700_003_600_000L, DaylistCreatedAtMs: 1_700_000_000_000L, Accent: 0xFF112233u),
        };

        var hit = SidebarNavPreview.FindPlaylist(playlists, "spotify:playlist:2");

        Assert.NotNull(hit);
        Assert.Equal("Daylist: mandopop mix", hit!.Name);
        Assert.Equal(1_700_003_600_000L, hit.DaylistExpiresAtMs);   // the daylist window rides the STORE record
        Assert.Equal(0xFF112233u, hit.Accent);
    }

    [Fact]
    public void FindPlaylist_UnresolvedUri_ReturnsNull()
    {
        // An unlisted/editorial pin (a daylist never saved to the library) is never in this list — see
        // SidebarProjectionBinder.ResolvePins. The lookup must say so honestly, not fabricate a match.
        var playlists = new List<PlaylistSummary> { new("spotify:playlist:1", "Mix", "Alice", 10, null) };
        Assert.Null(SidebarNavPreview.FindPlaylist(playlists, "spotify:playlist:unlisted"));
    }

    [Fact]
    public void FindPlaylist_NullOrEmptyInputs_ReturnNull()
    {
        Assert.Null(SidebarNavPreview.FindPlaylist(null, "spotify:playlist:1"));
        Assert.Null(SidebarNavPreview.FindPlaylist(new List<PlaylistSummary>(), ""));
    }

    [Fact]
    public void FindAlbum_MatchesByUri()
    {
        var albums = new List<Album>
        {
            new("1", "spotify:album:1", "First", null, System.Array.Empty<ArtistRef>(), 2020, 12),
            new("2", "spotify:album:2", "Second", null, System.Array.Empty<ArtistRef>(), 2023, 9),
        };

        var hit = SidebarNavPreview.FindAlbum(albums, "spotify:album:2");

        Assert.NotNull(hit);
        Assert.Equal("Second", hit!.Name);
        Assert.Equal(2023, hit.Year);
    }

    [Fact]
    public void FindAlbum_UnresolvedUri_ReturnsNull()
    {
        var albums = new List<Album> { new("1", "spotify:album:1", "First", null, System.Array.Empty<ArtistRef>(), 2020, 12) };
        Assert.Null(SidebarNavPreview.FindAlbum(albums, "spotify:album:missing"));
    }

    // ── PlaylistSummaryOf / AlbumOf — the row's own fallback ────────────────────────────────────────────────────────

    [Fact]
    public void PlaylistSummaryOf_CarriesTheRowsOwnDisplayCache()
    {
        var cover = new Image("https://example/cover.jpg");
        var entry = Entry(SidebarPinId.PlaylistPrefix + "spotify:playlist:9", SidebarEntryKind.Playlist,
            "spotify:playlist:9", "Pinned Daylist", "Spotify", cover, childCount: 40, canEdit: false, isOwner: false);

        var summary = SidebarNavPreview.PlaylistSummaryOf(in entry);

        Assert.Equal("spotify:playlist:9", summary.Uri);
        Assert.Equal("Pinned Daylist", summary.Name);
        Assert.Equal("Spotify", summary.OwnerName);
        Assert.Equal(40, summary.TrackCount);
        Assert.Same(cover, summary.Cover);
        // The row has nowhere to carry these (SidebarLibraryEntry has no Daylist/Accent fields) — honestly absent,
        // never guessed.
        Assert.Equal(0, summary.DaylistExpiresAtMs);
        Assert.Equal(0, summary.DaylistCreatedAtMs);
        Assert.Equal(0u, summary.Accent);
    }

    [Fact]
    public void AlbumOf_UsesTheBareEntityId_NotThePinId()
    {
        var entry = Entry(SidebarPinId.AlbumPrefix + "spotify:album:77", SidebarEntryKind.Album,
            "spotify:album:77", "Some Album", "Some Artist", childCount: 11);

        var album = SidebarNavPreview.AlbumOf(in entry);

        Assert.Equal("77", album.Id);                 // EntityUri.IdOf, never the "album:" pin id
        Assert.Equal("spotify:album:77", album.Uri);
        Assert.Equal("Some Album", album.Name);
        Assert.Equal(11, album.TrackCount);
        Assert.Equal(0, album.Year);                   // unknown at row granularity — never guessed
        Assert.Empty(album.Artists);
    }
}
