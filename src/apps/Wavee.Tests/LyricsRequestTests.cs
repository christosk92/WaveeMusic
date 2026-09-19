// ── Wavee.Tests/LyricsRequestTests.cs — the entity → Lyrics.Request mapping (G-008) ────────────────────────────────
//
// `Lyrics.RequestFrom` is the pure half of composing the lyrics stack's `resolveRequest`: track columns in, a
// `Lyrics.Request` out, or null when the row is not ready. The host half (`Lyrics.ResolveRequest`) hops to the UI thread
// and calls this once `TrackFields.Identity` has landed — nothing here touches a thread, a poster or the network, so it
// is testable exactly like `Detail.NoticeRules` is (`PlaylistPageNoticeRulesTests.cs`).
//
// G-252: a row that is not ready is PARKED on `Lyrics.RequestWaiter` and completed by the publication that commits its
// identity — never polled. The waiter facts drive `Begin` / `AfterPublish` the way the UI thread does, with the commit +
// publish a drain performs between them.

using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class LyricsRequestTests
{
    /// <summary>A track with a committed Identity group, named artists (in wire order) and a named album — the shape
    /// `resolveRequest` sees once the row is ready.</summary>
    static Track NamedRow(string id, string title, string album, long durationMs, string isrc, params string[] artists)
    {
        var s = Staging.Rent();
        ref var trow = ref s.Tracks.Add();
        trow.Id = s.Text("spotify:track:" + id);
        trow.Title = s.Text(title);
        trow.AlbumUri = s.Text("spotify:album:" + id);
        trow.DurationMs = (int)durationMs;
        trow.Known = (uint)TrackFields.Identity;
        trow.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var isrcRow = Staging.Rent();
        ref var extras = ref isrcRow.Tracks.Add();
        extras.Id = isrcRow.Text("spotify:track:" + id);
        extras.Isrc = isrcRow.Text(isrc);
        extras.Known = (uint)TrackFields.Isrc;
        extras.Authority = Authority.Thin;
        TestScope.CommitAndPublish(isrcRow);

        var albumRow = Staging.Rent();
        ref var arow = ref albumRow.Albums.Add();
        arow.Id = albumRow.Text("spotify:album:" + id);
        arow.Title = albumRow.Text(album);
        arow.Known = (uint)AlbumFields.Identity;
        arow.Authority = Authority.Full;
        TestScope.CommitAndPublish(albumRow);

        var track = Entities.Track(EntityUri.Parse("spotify:track:" + id));
        var artistSlots = new int[artists.Length];
        for (int i = 0; i < artists.Length; i++)
        {
            var artistUri = "spotify:artist:" + id + "-" + i;
            var arows = Staging.Rent();
            ref var artistRow = ref arows.Artists.Add();
            artistRow.Id = arows.Text(artistUri);
            artistRow.Name = arows.Text(artists[i]);
            artistRow.Known = (uint)ArtistFields.Identity;
            artistRow.Authority = Authority.Full;
            TestScope.CommitAndPublish(arows);
            artistSlots[i] = Entities.Artist(EntityUri.Parse(artistUri)).Slot;
        }
        Entities.Current.Edges.TrackArtists.Replace(track.Slot, artistSlots, [], EdgeState.Complete, artistSlots.Length);
        return track;
    }

    [Fact]
    public void An_invalid_track_answers_null()
    {
        Assert.Null(Lyrics.RequestFrom(default, "d16"));
    }

    [Fact]
    public void An_empty_track_id_answers_null_even_for_a_known_row()
    {
        TestScope.Fresh();
        var track = NamedRow("empty-id", "Title", "Album", 1000, "ISRC1", "Artist");
        Assert.Null(Lyrics.RequestFrom(track, ""));
    }

    [Fact]
    public void A_row_whose_identity_has_not_landed_answers_null_so_the_host_keeps_waiting()
    {
        TestScope.Fresh();
        // The slot exists (an edge or a cross-reference allocated it) but no Identity answer has committed.
        var track = Entities.Track(EntityUri.Parse("spotify:track:thin"));
        Assert.False(track.Knows(TrackFields.Identity));
        Assert.Null(Lyrics.RequestFrom(track, "thin"));
    }

    [Fact]
    public void A_ready_row_maps_every_field_the_sources_need()
    {
        TestScope.Fresh();
        var track = NamedRow("hot", "Weightless", "Ambient Works", 484_000, "GBUM71029601", "Marconi Union");

        var request = Lyrics.RequestFrom(track, "hot");

        Assert.NotNull(request);
        Assert.Equal("hot", request!.TrackId);
        Assert.Equal("spotify:track:hot", request.Uri);
        Assert.Equal("Weightless", request.Title);
        Assert.Equal(["Marconi Union"], request.Artists);
        Assert.Equal("Marconi Union", request.PrimaryArtist);
        Assert.Equal("Ambient Works", request.Album);
        Assert.Equal(484_000, request.DurationMs);
        Assert.Equal("GBUM71029601", request.Isrc);
        Assert.Null(request.HasSpotifyLyrics);
    }

    [Fact]
    public void Multiple_artists_stay_in_wire_order_for_the_credit_line()
    {
        TestScope.Fresh();
        var track = NamedRow("multi", "Song", "Album", 200_000, "ISRC2", "First", "Second", "Third");

        var request = Lyrics.RequestFrom(track, "multi");

        Assert.Equal(["First", "Second", "Third"], request!.Artists);
        Assert.Equal("First, Second, Third", request.ArtistsJoined);
        Assert.Equal("First", request.PrimaryArtist);
    }

    [Fact]
    public void No_artist_edge_yields_an_empty_list_not_a_null_or_a_throw()
    {
        TestScope.Fresh();
        var track = NamedRow("noartist", "Song", "Album", 200_000, "ISRC3");

        var request = Lyrics.RequestFrom(track, "noartist");

        Assert.NotNull(request);
        Assert.Empty(request!.Artists);
        Assert.Equal("", request.PrimaryArtist);
    }

    [Fact]
    public void An_unknown_isrc_maps_to_null_never_an_empty_string()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var trow = ref s.Tracks.Add();
        trow.Id = s.Text("spotify:track:noisrc");
        trow.Title = s.Text("Song");
        trow.DurationMs = 100_000;
        trow.Known = (uint)TrackFields.Identity;
        trow.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:noisrc"));
        var request = Lyrics.RequestFrom(track, "noisrc");

        Assert.NotNull(request);
        Assert.Null(request!.Isrc);
    }

    [Fact]
    public void An_unnamed_album_maps_to_an_empty_string_not_a_placeholder()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var trow = ref s.Tracks.Add();
        trow.Id = s.Text("spotify:track:coldalbum");
        trow.Title = s.Text("Song");
        trow.AlbumUri = s.Text("spotify:album:coldalbum");   // allocates the album row, but no answer names it
        trow.DurationMs = 100_000;
        trow.Known = (uint)TrackFields.Identity;
        trow.Authority = Authority.Full;
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:coldalbum"));
        Assert.True(track.Album.IsValid);
        Assert.False(track.Album.Knows(AlbumFields.Identity));

        var request = Lyrics.RequestFrom(track, "coldalbum");
        Assert.Equal("", request!.Album);
    }

    // ── the waiter (G-252) ─────────────────────────────────────────────────────────────────────────────────────────────

    static EntityId TrackId(string id) => EntityUri.Parse("spotify:track:" + id).Id;

    static TaskCompletionSource<Lyrics.Request?> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task A_ready_row_answers_at_once_and_parks_nothing()
    {
        TestScope.Fresh();
        NamedRow("ready", "Title", "Album", 1000, "ISRC4", "Artist");
        var waiter = new Lyrics.RequestWaiter();
        var completion = Completion();

        waiter.Begin(TrackId("ready"), "ready", completion);

        Assert.True(completion.Task.IsCompleted);
        Assert.Equal(0, waiter.PendingCount);
        Assert.Equal("Title", (await completion.Task)!.Title);
    }

    [Fact]
    public async Task A_parked_resolve_completes_on_the_publication_that_commits_its_identity()
    {
        TestScope.Fresh();
        var waiter = new Lyrics.RequestWaiter();
        var completion = Completion();

        waiter.Begin(TrackId("late"), "late", completion);
        Assert.False(completion.Task.IsCompleted);                        // parked: nothing re-probes it
        Assert.Equal(1, waiter.PendingCount);

        NamedRow("late", "Late Title", "Album", 2000, "ISRC5", "Artist");  // the drain commits and publishes
        waiter.AfterPublish();

        Assert.Equal(0, waiter.PendingCount);
        var request = await completion.Task;
        Assert.NotNull(request);
        Assert.Equal("late", request!.TrackId);
        Assert.Equal("Late Title", request.Title);
    }

    [Fact]
    public void A_publication_that_does_not_name_the_row_keeps_it_parked()
    {
        TestScope.Fresh();
        var waiter = new Lyrics.RequestWaiter();
        var completion = Completion();
        waiter.Begin(TrackId("waiting"), "waiting", completion);

        NamedRow("unrelated", "Other", "Album", 3000, "ISRC6", "Artist");
        waiter.AfterPublish();

        Assert.False(completion.Task.IsCompleted);
        Assert.Equal(1, waiter.PendingCount);
    }

    [Fact]
    public void A_resolve_its_caller_already_answered_is_released_on_the_next_look()
    {
        TestScope.Fresh();
        var waiter = new Lyrics.RequestWaiter();
        var completion = Completion();
        waiter.Begin(TrackId("abandoned"), "abandoned", completion);

        completion.TrySetResult(null);                                     // the resolver's timeout, or a cancel
        NamedRow("abandoned-other", "Other", "Album", 3000, "ISRC7", "Artist");
        waiter.AfterPublish();

        Assert.Equal(0, waiter.PendingCount);
    }

    [Fact]
    public async Task A_full_waiter_answers_its_oldest_resolve_null()
    {
        TestScope.Fresh();
        var waiter = new Lyrics.RequestWaiter();
        var first = Completion();
        waiter.Begin(TrackId("cold0"), "cold0", first);
        for (int i = 1; i <= Lyrics.RequestWaiter.Capacity; i++)
            waiter.Begin(TrackId("cold" + i), "cold" + i, Completion());

        Assert.Equal(Lyrics.RequestWaiter.Capacity, waiter.PendingCount);
        Assert.True(first.Task.IsCompleted);
        Assert.Null(await first.Task);
    }

    [Fact]
    public async Task The_resolver_refuses_an_id_that_is_not_base62()
    {
        Assert.Null(await Lyrics.ResolveRequest("", CancellationToken.None));
        Assert.Null(await Lyrics.ResolveRequest("not base62!", CancellationToken.None));
    }
}
