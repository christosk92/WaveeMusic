// ── Playback/LocalLibrary.PlayAs.cs ─────────────────────────────────────────────────────────────────────────────────
// "Your files and Spotify" phase 2: a Spotify song that plays one of your files by default, as the audio pump and the reports see it, and the
// `spotify:local:` name of a file (its own names, never what Wavee shows)
//
// Role: SHELL (UI thread fills the map; ANY THREAD reads it: the pump's route, the path seam, the play report)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-link-phase2.md ("The swap happens at the source, not in the queue"); #240 E1, E7, E8
//
// THE SWAP. `LocalLink` decides which version a Spotify song plays (its default, or "Prefer my files"); the UI thread hands the decision here as
// an EntityId → LocalId map (`NotePlayAs`), as `RebuildItemMap` hands a playlist's `spotify:local:` items. Every load and preload goes through
// `Playback.Audio.Open` → `RouteOf`, which asks `PlayAsOf`: a song whose default file is in the index and plays routes to the local source, and
// `PathOf` answers that file. The queue, the now-playing row, the like, Connect state and context paging stay the Spotify song's. A file that
// cannot be opened (gone, an offline share) falls back to Spotify in `Open`: no dead end.
//
// THE NAME. A file's `spotify:local:` uri is minted from the file's OWN names, as the official client names it (E1): its tags' artist line and
// album, its matching title (the title tag, else the file name), its length. Never the rename, the split or an accepted match's Spotify info:
// those are what Wavee shows on this PC; the uri is what other devices, reports and playlist items match the file by.

using System.Text;

namespace Wavee;

public static partial class LocalLibrary
{
    static Dictionary<EntityId, long> s_playAsMap = new();

    /// <summary>UI thread: the songs that play a file by default (Spotify uri → LocalId, <see cref="LocalLink.PlayAs"/>), as ids for the pump.</summary>
    public static void NotePlayAs(IReadOnlyDictionary<string, long> playAs)
    {
        var map = new Dictionary<EntityId, long>(playAs.Count);
        foreach (var (uri, localId) in playAs)
            if (EntityUri.Parse(uri).Id is { IsValid: true } id) map[id] = localId;
        lock (s_pathGate) s_playAsMap = map;
    }

    /// <summary>The file a Spotify song plays in its place (its default version), when that file is indexed, present and playable; else 0.
    /// ANY THREAD (the pump's route and path, the play report): one probe under a lock, no id text read.</summary>
    public static long PlayAsOf(EntityId id)
    {
        long localId;
        lock (s_pathGate) { if (!s_playAsMap.TryGetValue(id, out localId)) return 0; }
        return s_index.TryGet(localId, out LocalEntry? e) && e.Playable && e.State == LocalState.Present ? localId : 0;
    }

    /// <summary>The <c>spotify:local:</c> uri of an indexed file, from its own names (see the file header), with its artist line and album; null
    /// when it is not indexed or the name would not fit. Any thread.</summary>
    public static (string Uri, string Artist, string Album)? LocalUriOfFile(long localId)
    {
        if (!s_index.TryGet(localId, out LocalEntry? e)) return null;
        LocalTagSet t = e.Tags;
        string artist = Playlist.LocalArtistLine(in t);
        Span<byte> utf8 = stackalloc byte[Spotify.Egress.MaxLocalUriBytes];
        int n = Spotify.Egress.LocalTrackUri(artist, t.Album, e.MatchTitle, t.DurationMs, utf8);
        return n > 0 ? (Encoding.UTF8.GetString(utf8[..n]), artist, t.Album) : null;
    }
}
