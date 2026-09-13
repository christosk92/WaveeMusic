using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// RecentsRecency (Wavee.Core, pure): what a grouped recents PAGE says about "last played" per uri, in the shape
// PlayLogStore.MergeRecency (Lane B) takes. This is the ONLY path a cross-device play reaches the library panes'
// "Recents" sort through — the local play-log ring never sees a play made on another device — so these tests pin the
// uris + timestamps a snapshot yields, not any ordering (LibraryNavOrder owns that, separately).
public class RecentsRecencyTests
{
    static RecentsRow SingleRow(string uri, long playedAtMs, RecentsEntityKind kind = RecentsEntityKind.Album)
        => new(RecentsRowKind.Single, ItemId: uri, Uri: uri, ContextUri: null, Title: null, Subtitle: null, Image: null,
            ChildCount: 0, PlayedAtMs: playedAtMs, EntityKind: kind);

    static RecentsRow GroupRow(string itemId, string contextUri, long playedAtMs, IReadOnlyList<RecentsMember>? members = null)
        => new(RecentsRowKind.Group, ItemId: itemId, Uri: contextUri, ContextUri: contextUri, Title: null, Subtitle: null,
            Image: null, ChildCount: members?.Count ?? 0, PlayedAtMs: playedAtMs, EntityKind: RecentsEntityKind.Playlist,
            Members: members);

    // ── Stamps ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stamps_SingleRow_ContextRow_Members()
    {
        var rows = new List<RecentsRow>
        {
            SingleRow("spotify:album:a", 100),
            GroupRow("h1", "spotify:playlist:p", 200, members:
            [
                new RecentsMember("m1", "spotify:track:x", 190),
                new RecentsMember("m2", "spotify:track:y", 180),
            ]),
        };

        var stamps = RecentsRecency.Stamps(rows).ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(100L, stamps["spotify:album:a"]);       // a Single row stamps its own uri
        Assert.Equal(200L, stamps["spotify:playlist:p"]);    // a Group row stamps its header/context uri
        Assert.Equal(190L, stamps["spotify:track:x"]);       // …plus every collapsed member, each with its OWN played time
        Assert.Equal(180L, stamps["spotify:track:y"]);
        Assert.Equal(4, stamps.Count);
    }

    [Fact]
    public void Stamps_SkipsZeroTimes()
    {
        var rows = new List<RecentsRow>
        {
            SingleRow("spotify:album:a", 0),                                  // no played time — never a play, never stamped
            GroupRow("h1", "spotify:playlist:p", 100, members:
            [
                new RecentsMember("m1", "spotify:track:x", 0),                // member with no played time is skipped too
                new RecentsMember("m2", "", 50),                              // member with no uri is skipped too
            ]),
        };

        var stamps = RecentsRecency.Stamps(rows);

        Assert.Single(stamps);
        Assert.Equal(new KeyValuePair<string, long>("spotify:playlist:p", 100L), stamps[0]);
    }

    // ── TrackStamps ───────────────────────────────────────────────────────────────────────────────────────────────

    static Track MakeTrack(string uri, string albumUri, params string[] artistUris)
        => new(Id: uri, Uri: uri, Title: "t",
            Artists: artistUris.Select(u => new ArtistRef(u, u, "a")).ToArray(),
            Album: new AlbumRef(albumUri, albumUri, "al"),
            DurationMs: 1000, IsExplicit: false, Image: null);

    [Fact]
    public void TrackStamps_ResolvesAlbumAndArtists_NewestMemberTimeWins()
    {
        var rows = new List<RecentsRow>
        {
            // The same track uri appears twice — as a member under two different headers, at two different instants —
            // exactly what a wire snapshot can legitimately contain (played more than once). The NEWER instant must win.
            GroupRow("h1", "spotify:playlist:p1", 500, members: [new RecentsMember("m1", "spotify:track:x", 100)]),
            GroupRow("h2", "spotify:playlist:p2", 600, members: [new RecentsMember("m2", "spotify:track:x", 300)]),
        };
        var uris = new[] { "spotify:track:x" };
        Track? Resolve(string u) => u == "spotify:track:x"
            ? MakeTrack(u, "spotify:album:al", "spotify:artist:a1", "spotify:artist:a2")
            : null;

        var stamps = RecentsRecency.TrackStamps(rows, uris, Resolve);

        Assert.Equal(3, stamps.Count);
        Assert.Contains(new KeyValuePair<string, long>("spotify:album:al", 300L), stamps);   // newest of {100,300} wins
        Assert.Contains(new KeyValuePair<string, long>("spotify:artist:a1", 300L), stamps);
        Assert.Contains(new KeyValuePair<string, long>("spotify:artist:a2", 300L), stamps);
    }

    [Fact]
    public void TrackStamps_UnresolvedTrackContributesNothing()
    {
        var rows = new List<RecentsRow>
        {
            SingleRow("spotify:track:x", 100, RecentsEntityKind.Track),
            SingleRow("spotify:track:y", 200, RecentsEntityKind.Track),
        };
        var uris = new[] { "spotify:track:x", "spotify:track:y" };
        // Only "y" is resident in the store — "x" is a track the identity hydration named but the store cannot yet name
        // (never happens in practice for the uris just hydrated, but the seam must not assume it — a network call of
        // its own is exactly what this method must never make).
        Track? Resolve(string u) => u == "spotify:track:y" ? MakeTrack(u, "spotify:album:aly", "spotify:artist:ay") : null;

        var stamps = RecentsRecency.TrackStamps(rows, uris, Resolve);

        Assert.Equal(2, stamps.Count);
        Assert.Contains(new KeyValuePair<string, long>("spotify:album:aly", 200L), stamps);
        Assert.Contains(new KeyValuePair<string, long>("spotify:artist:ay", 200L), stamps);
    }
}
