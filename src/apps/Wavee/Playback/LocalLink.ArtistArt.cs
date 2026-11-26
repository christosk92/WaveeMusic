using FluentGpu.Signals;

namespace Wavee;

// "Your files and Spotify": the artist picture of a linked song. A linked file files under its Spotify song's artist (LocalCollection.FactsOf),
// so its artist card wears that artist's Spotify picture, read through the entity layer as an artist page reads it: the song's first artist
// (Track.ArtistSlots), its image (Artist.ImageId), each asked for once when the catalogue has not answered yet. A watch re-reads when the track
// and artist tables publish, and the collection refolds when a picture arrives.

public static partial class LocalLink
{
    static volatile Dictionary<string, string> s_artistArt = new(StringComparer.Ordinal);
    static ReactiveRuntime? s_artRuntime;
    static Effect? s_artWatch;
    static bool s_artFlushPosted;

    /// <summary>The Spotify picture of a linked song's (first) artist, or null while it is unknown. Any thread (the collection's fold).</summary>
    public static string? ArtistArtOf(string songUri) => s_artistArt.TryGetValue(songUri, out string? url) ? url : null;

    /// <summary>Starts the watch (once) and reads the pictures of the linked songs now. UI thread.</summary>
    static void WatchArtistArt()
    {
        if (s_artRuntime is not null) { ReadArtistArt(); return; }
        s_artRuntime = new ReactiveRuntime { FrameRequested = static () =>
        {
            if (s_artFlushPosted) return;
            s_artFlushPosted = true;
            Playback.ToUi(static () => { s_artFlushPosted = false; s_artRuntime?.Flush(); });
        } };
        s_artWatch = new Effect(s_artRuntime, static () =>
        {
            _ = Entities.ScopeEpoch.Value;                 // a sign-in swaps the tables
            if (Entities.Current is not { } scope) return;
            _ = scope.Tracks.Changed.Value;                // a song's artists arrive
            _ = scope.Artists.Changed.Value;               // an artist's picture arrives
            ReadArtistArt();
        });
    }

    static void ReadArtistArt()
    {
        if (Entities.Current is null) return;
        var art = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (LinkTrack link in s_shown.Values)
        {
            if (art.ContainsKey(link.Uri) || !EntityId.TryParse(link.Uri, out EntityId id) || id.Kind != EntityKind.Track) continue;
            var song = Entities.Track(id);
            if (!song.IsValid) continue;
            var slots = song.ArtistSlots;
            if (slots.Length == 0) { Entities.Ensure(song, TrackFields.Artists, FetchPriority.Prefetch); continue; }
            var artist = new Artist(slots[0]);
            if (artist.ImageId.IsEmpty) { if (!artist.Knows(ArtistFields.Image)) Entities.Ensure(artist, ArtistFields.Image, FetchPriority.Prefetch); continue; }
            if (Controls.ArtUrl(artist.ImageId) is { } url) art[link.Uri] = url;
        }
        if (Same(art, s_artistArt)) return;
        s_artistArt = art;
        LocalCollection.Request();

        static bool Same(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var (k, v) in a) if (!b.TryGetValue(k, out string? w) || w != v) return false;
            return true;
        }
    }
}
