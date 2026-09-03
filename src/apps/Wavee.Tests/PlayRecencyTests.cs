using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

// PlayRecency (§F.0): uri → last-played unix ms, the "recently played" fact PlayLogStore.Recency exposes and the
// library panes sort on. Pure — no file I/O here (PlayLogStoreTests covers the play-recency.json sidecar round trip).
public class PlayRecencyTests
{
    [Fact]
    public void Stamp_FirstSighting_ReturnsTrue_OlderStamp_ReturnsFalse()
    {
        var r = new PlayRecency();
        Assert.True(r.Stamp("spotify:track:t1", 1000));
        Assert.False(r.Stamp("spotify:track:t1", 500));    // older — a server history must not demote a local play
        Assert.False(r.Stamp("spotify:track:t1", 1000));   // equal — not strictly newer
        Assert.True(r.Stamp("spotify:track:t1", 1500));    // newer — accepted
        Assert.Equal(1500, r.Of("spotify:track:t1"));

        Assert.False(r.Stamp(null, 999));
        Assert.False(r.Stamp("", 999));
        Assert.False(r.Stamp("spotify:track:t2", 0));
        Assert.False(r.Stamp("spotify:track:t2", -5));
        Assert.Equal(0, r.Of("spotify:track:t2"));          // never stamped — the zero-value default
    }

    [Fact]
    public void Stamp_Entry_TouchesTrackContextAlbumArtists()
    {
        var r = new PlayRecency();
        var e = new PlayLogEntry("spotify:track:t1", "spotify:playlist:p1", PlayContextKind.Playlist, 1000,
            AlbumUri: "spotify:album:al1", ArtistUris: new[] { "spotify:artist:ar1", "spotify:artist:ar2" });

        Assert.True(r.Stamp(e));
        Assert.Equal(1000, r.Of("spotify:track:t1"));
        Assert.Equal(1000, r.Of("spotify:playlist:p1"));
        Assert.Equal(1000, r.Of("spotify:album:al1"));
        Assert.Equal(1000, r.Of("spotify:artist:ar1"));
        Assert.Equal(1000, r.Of("spotify:artist:ar2"));
        Assert.Equal(5, r.Count);   // track + context + album + 2 artists, all distinct uris
    }

    [Fact]
    public void Stamp_Entry_BareTrack_SkipsContext()
    {
        var r = new PlayRecency();
        // No context, no album/artists — the shape a bare single (or a pre-change ring row) produces.
        var e = new PlayLogEntry("spotify:track:t1", "", PlayContextKind.None, 1000);

        Assert.True(r.Stamp(e));
        Assert.Equal(1, r.Count);
        Assert.Equal(1000, r.Of("spotify:track:t1"));
    }

    [Fact]
    public void Trim_DropsOldestDownToTrimTo_OncePerBatch()
    {
        var r = new PlayRecency();
        for (int i = 0; i <= PlayRecency.Cap; i++)   // Cap+1 distinct uris — crosses the cap exactly once
            r.Stamp("spotify:track:t" + i, 1000 + i);

        Assert.Equal(PlayRecency.TrimTo, r.Count);
        Assert.Equal(0, r.Of("spotify:track:t0"));                     // oldest — dropped
        Assert.True(r.Of("spotify:track:t" + PlayRecency.Cap) > 0);    // newest — survives

        // A batch that stays under the (now lower) count must not trigger a second trim — the whole point of
        // trimming to TrimTo rather than Cap-1 is that a trim costs one sort per 256 NEW uris, not per append.
        int countAfterTrim = r.Count;
        for (int i = 0; i < 100; i++) r.Stamp("spotify:track:new" + i, 100_000 + i);
        Assert.Equal(countAfterTrim + 100, r.Count);
    }

    [Fact]
    public void Merge_ReportsChangeOnlyWhenNewer()
    {
        var r = new PlayRecency();
        Assert.True(r.Merge(new[] { new KeyValuePair<string, long>("spotify:artist:a1", 1000) }));
        Assert.False(r.Merge(new[] { new KeyValuePair<string, long>("spotify:artist:a1", 500) }));   // older only
        Assert.True(r.Merge(new[]
        {
            new KeyValuePair<string, long>("spotify:artist:a1", 1500),   // newer — changes
            new KeyValuePair<string, long>("spotify:artist:a2", 10),     // new uri — changes
        }));
        Assert.Equal(1500, r.Of("spotify:artist:a1"));
        Assert.Equal(10, r.Of("spotify:artist:a2"));
    }
}
