using System;
using Wavee;
using Wavee.Backend.Modules;
using Wavee.Core;
using Wavee.Sdk;
using Xunit;

namespace Wavee.Tests.Actions;

/// <summary>
/// WHERE a now-playing span goes (Part 9.2). The player bar and the immersive stage both used to answer this inline,
/// with the same gate — <c>ArtistRef.Uri.Length &gt; 0</c> — which is a SPOTIFY question wearing a general name: a
/// module playable carries artist NAMES with no uri and an empty album, so every one of its spans was
/// styled-but-inert. These pin the one table both surfaces now ask.
/// </summary>
public class PlayableLinksTests
{
    const string ModuleId = "wavee.youtube";

    static Track Spotify(string id = "t1", string albumUri = "spotify:album:al1", string artistUri = "spotify:artist:ar1")
        => new(id, "spotify:track:" + id, "Song",
               [new ArtistRef("ar1", artistUri, "An Artist")],
               new AlbumRef("al1", albumUri, "An Album"), 180_000, false, null);

    // ── the catalogue arms are unchanged ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASpotifyTrack_LinksTitleToItsAlbumAndSubtitleToItsArtist()
    {
        Track t = Spotify();
        Assert.Equal("album:spotify:album:al1", PlayableLinks.RouteFor(t, LinkSlot.Title));
        Assert.Equal("artist:spotify:artist:ar1", PlayableLinks.RouteFor(t, LinkSlot.Artist));
        Assert.Equal("An Album", PlayableLinks.LabelFor(t, LinkSlot.Title));
        Assert.Equal("An Artist", PlayableLinks.LabelFor(t, LinkSlot.Artist));
    }

    [Fact]
    public void ASpotifyTracksArtTile_IsNotAnsweredHere()
    {
        // The bar's art tile opens the playback CONTEXT (a playlist, Liked Songs), which the track cannot know. The
        // caller keeps that answer; this table must not invent one.
        Assert.Null(PlayableLinks.RouteFor(Spotify(), LinkSlot.Art));
    }

    [Fact]
    public void AUriLessRefIsInert_NotADeadRoute()
    {
        // The reported shape: a projected row carrying an artist NAME with no uri. "artist:" is a page nothing renders.
        Track t = Spotify(artistUri: "", albumUri: "");
        Assert.Null(PlayableLinks.RouteFor(t, LinkSlot.Title));
        Assert.Null(PlayableLinks.RouteFor(t, LinkSlot.Artist));
        Assert.Null(PlayableLinks.LabelFor(t, LinkSlot.Artist));
    }

    [Fact]
    public void NoTrack_IsAlwaysInert()
    {
        Assert.Null(PlayableLinks.RouteFor(null, LinkSlot.Title));
        Assert.Null(PlayableLinks.RouteFor(null, LinkSlot.Artist));
        Assert.Null(PlayableLinks.RouteFor(null, LinkSlot.Art));
        Assert.False(PlayableLinks.IsModule(null));
    }

    // ── the module arms ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AModulePlayable_LinksTitleAndArtToItsOwnPage_AndTheSubtitleToItsPublisher()
    {
        var cache = new ModulePlayableCache(() => 0);
        ModulePlayables.Attach(cache);
        try
        {
            Track t = ModuleTrack("video:abc");
            cache.Put(t.Uri, ModulePagesResolved("video:abc", "channel:UC1"));

            string video = ModulePages.RouteForEntity(ModuleId, "video:abc")!;
            string channel = ModulePages.RouteForEntity(ModuleId, "channel:UC1")!;
            Assert.Equal(video, PlayableLinks.RouteFor(t, LinkSlot.Title));
            Assert.Equal(video, PlayableLinks.RouteFor(t, LinkSlot.Art));
            Assert.Equal(channel, PlayableLinks.RouteFor(t, LinkSlot.Artist));
            Assert.True(PlayableLinks.IsModule(t));
        }
        finally { ModulePlayables.Attach(null); }
    }

    [Fact]
    public void AModulePlayableNeverFallsThroughToTheSpotifyArms()
    {
        var cache = new ModulePlayableCache(() => 0);
        ModulePlayables.Attach(cache);
        try
        {
            Track t = ModuleTrack("video:abc");
            cache.Put(t.Uri, ModulePagesResolved(null, null));
            // Its AlbumRef is empty and its artists are name-only by construction, but the point is stronger than
            // that: even a module track that somehow carried refs must not be routed as a Spotify entity.
            Assert.Null(PlayableLinks.RouteFor(t, LinkSlot.Title));
            Assert.Null(PlayableLinks.RouteFor(t, LinkSlot.Artist));
            Assert.Null(PlayableLinks.LabelFor(t, LinkSlot.Title));
        }
        finally { ModulePlayables.Attach(null); }
    }

    [Fact]
    public void WithNoModuleHostAttached_EveryModuleSpanIsInert()
    {
        ModulePlayables.Attach(null);
        Track t = ModuleTrack("video:abc");
        Assert.Null(PlayableLinks.RouteFor(t, LinkSlot.Title));
        Assert.Null(PlayableLinks.RouteFor(t, LinkSlot.Artist));
        Assert.True(PlayableLinks.IsModule(t));   // …but it is still recognisably a module playable
    }

    static Track ModuleTrack(string playableId)
        => LocalPlayables.ForModule(ModuleId, playableId, "Claude FM", Wavee.Sdk.MediaForm.Video, ["Anthropic"], null);

    static ResolvedPlayable ModulePagesResolved(string? page, string? subtitle)
        => Wavee.Tests.Modules.ModulePagesTests.Resolved(page, subtitle);
}
