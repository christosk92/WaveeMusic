using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

static class DetailQueueActions
{
    public const int MaxBatch = 50;

    // WHERE a queue verb lands is the player's business, not this table's: with no active device the controller routes
    // LOCAL (starting playback when the session is idle — PlaybackController.EnqueueLocalAsync), with one it forwards.
    // The old "remote device required" gate here predates local playback and swallowed every idle-local enqueue; the
    // one case that genuinely cannot proceed (no local audio stack) is refused BY the controller, which raises its own
    // "choose a remote device" toast. So these report what they ISSUED and the caller confirms it.
    public static int PlayNext(IPlaybackPlayer? player, IReadOnlyList<Track> tracks, int max = MaxBatch)
    {
        if (player is null) return 0;
        int n = Count(tracks, max);
        if (n <= 0) return 0;
        _ = player.PlayNextAsync(ToPlaybackContextTracks(tracks, n));
        return n;
    }

    public static int AddToEnd(IPlaybackPlayer? player, IReadOnlyList<Track> tracks, int max = MaxBatch)
    {
        if (player is null) return 0;
        int n = Count(tracks, max);
        if (n <= 0) return 0;
        for (int i = 0; i < n; i++) _ = player.EnqueueAsync(tracks[i]);
        return n;
    }

    /// <summary>Insert at a queue-relative SLOT (the drag-drop deposit; index 0 == <see cref="PlayNext"/>). Returns the
    /// number of tracks issued — capped at <paramref name="max"/>, so a caller that dropped more can say so.</summary>
    public static int InsertAt(IPlaybackPlayer? player, IReadOnlyList<Track> tracks, int index, int max = MaxBatch)
    {
        if (player is null) return 0;
        int n = Count(tracks, max);
        if (n <= 0) return 0;
        _ = player.InsertIntoQueueAsync(ToPlaybackContextTracks(tracks, n), System.Math.Max(0, index));
        return n;
    }

    public static PlaybackContextTrack[] ToPlaybackContextTracks(IReadOnlyList<Track> tracks, int count)
    {
        var ordered = new PlaybackContextTrack[count];
        for (int i = 0; i < count; i++)
        {
            var t = tracks[i];
            ordered[i] = new PlaybackContextTrack(t.Uri, t.ContextUid ?? string.Empty, BuildMetadata(t));
        }
        return ordered;
    }

    // The per-track display metadata the target device shows for an inserted "play next" row (set_queue). Mirrors the
    // desktop capture's metadata map (title/artist/album/duration/image/explicit/player). is_queued is NOT set here — the
    // wire serializer (OutboundEnvelope.WriteQueueEntry) stamps is_queued:"true" onto queued rows.
    static IReadOnlyDictionary<string, string> BuildMetadata(Track t)
    {
        var m = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["title"] = t.Title ?? "",
            ["album_title"] = t.Album?.Name ?? "",
            ["album_uri"] = t.Album?.Uri ?? "",
            ["duration"] = t.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["image_url"] = t.Image?.Url ?? "",
            ["is_explicit"] = t.IsExplicit ? "true" : "false",
        };
        if (t.Artists.Count > 0)
        {
            var a = t.Artists[0];
            m["artist_name"] = a.Name ?? "";
            m["artist_uri"] = a.Uri ?? "";
            m["album_artist_name"] = a.Name ?? "";
        }
        // The target has no reason to doubt an explicit claim: an unconditional "audio" here downgraded every
        // video-capable track we insert into a remote device's queue. Derive it from the association plane the app
        // already reads for the row indicator / player-bar offer (VideoPresence), not a hardcoded constant.
        var assoc = VideoPresence.Association(t.Uri);
        MediaSwitchLogic.StampVideoAssociation(m, assoc?.HasVideo == true, assoc?.CounterpartUri);
        return m;
    }

    static int Count(IReadOnlyList<Track> tracks, int max) => System.Math.Min(tracks.Count, System.Math.Max(0, max));
}
