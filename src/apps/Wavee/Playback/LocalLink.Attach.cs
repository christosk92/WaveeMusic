using System.Globalization;

namespace Wavee;

// "Your files and Spotify" phase 2, one concept (owner, 2026-11-24): attaching a video to a Spotify track is not a special link. The file is a
// library entry (LocalLibrary.AddFile) LINKED to the track as an accepted version, chosen by the user (no matching step). A linked video version
// gives the track its video: VideoOf answers "does this track have a video of mine, and where?" for the row's mark, the switch to video, Now
// playing and the video host (LocalLink.Video.cs feeds the local-video lane's LocalVideos from here). The link persists in locallinks.tsv.

public static partial class LocalLink
{
    /// <summary>Link a file to a Spotify track as one of its versions, the user's choice: an accepted link of the file's own song group (never its
    /// album's: an album's match later never takes it over), Spotify's info winning, as any accepted link. A file linked elsewhere moves. UI thread.</summary>
    public static bool LinkVersion(long localId, Track song)
    {
        if (!song.IsValid || !LocalLibrary.Index.TryGet(localId, out _)) return false;
        string key = SongKey(localId);
        if (s_linkGroup.TryGetValue(localId, out string? old) && old != key) { s_links.Remove(localId); s_linkGroup.Remove(localId); }
        Decided d = DecidedOf(key);
        s_decided[key] = d with { Decision = LinkDecision.Accepted, Chosen = song.Uri.Text, SpotifyWins = song.Title.Length > 0 };
        s_links[localId] = TrackOf(song, default);
        s_linkGroup[localId] = key;
        if (s_groups.TryGetValue(key, out LinkGroup? g)) s_groups[key] = g with { Decision = LinkDecision.Accepted };
        Publish();
        Save();
        Log.Info("local.link", "local.link.version id=" + localId.ToString(CultureInfo.InvariantCulture) + " video=" + (IsVideo(localId) ? 1 : 0));
        return true;
    }

    /// <summary>The video file of mine a Spotify track plays as video, or 0: its default version when that is a video, else its first linked
    /// video version that is there to play. Tracked (reads <see cref="Version"/>). UI thread.</summary>
    public static long VideoOf(string spotifyUri)
    {
        _ = s_version.Value;
        return VideoOfNow(spotifyUri);
    }

    static long VideoOfNow(string spotifyUri)
    {
        if (!s_byTrack.TryGetValue(spotifyUri, out long[]? files)) return 0;
        if (s_defaults.TryGetValue(spotifyUri, out long chosen) && chosen != 0 && IsVideo(chosen) && Playable(chosen)) return chosen;
        foreach (long id in files) if (IsVideo(id) && Playable(id)) return id;
        return 0;

        static bool Playable(long id) => LocalLibrary.Index.TryGet(id, out LocalEntry? e) && e.State != LocalState.Missing;
    }

    /// <summary>The track's video version, a missing one included (what Settings lists and the menu offers to repair), or 0. UI thread.</summary>
    public static long VideoFileOf(string spotifyUri)
    {
        _ = s_version.Value;
        if (!s_byTrack.TryGetValue(spotifyUri, out long[]? files)) return 0;
        if (s_defaults.TryGetValue(spotifyUri, out long chosen) && chosen != 0 && IsVideo(chosen)) return chosen;
        foreach (long id in files) if (IsVideo(id)) return id;
        return 0;
    }

    /// <summary>What a video file of mine wears as its picture (#240 version cards, "Use as thumbnail"): absent = a frame from the video (its own
    /// still), -1 = the album cover, &gt; 0 = the frame the user chose at that time (ms).</summary>
    static readonly Dictionary<long, long> s_thumbs = new();

    /// <summary>The picture choice of a file: -1 the album cover, 0 the video's own still, &gt; 0 the chosen frame's time (ms). Tracked.</summary>
    public static long ThumbOf(long localId)
    {
        _ = s_version.Value;
        return s_thumbs.TryGetValue(localId, out long c) ? c : 0;
    }

    /// <summary>"Use as thumbnail": the album cover (-1), a frame from the video (0), or the frame at a time (ms). UI thread.</summary>
    public static void SetThumb(long localId, long choice)
    {
        if (choice == 0) s_thumbs.Remove(localId);
        else s_thumbs[localId] = choice;
        Publish();
        Save();
    }

    /// <summary>The picture of a video file of mine as its own version wears it: the chosen frame, else its still (asked for when not made yet: null
    /// until it lands); null for the album-cover choice and for any file that is not a video. Any thread (LocalArt is).</summary>
    public static string? FrameUrlOf(long localId)
    {
        if (!IsVideo(localId) || LocalLibrary.PathOf(localId) is not { } path) return null;
        long c = s_thumbs.TryGetValue(localId, out long v) ? v : 0;
        if (c < 0) return null;
        return (c > 0 ? LocalArt.FrameUri(path, c) : null) ?? LocalArt.FileCoverUri(path);
    }

    /// <summary>Does the file wear a frame (16:9) rather than the album cover? A video whose choice is not the cover.</summary>
    public static bool WearsFrame(long localId) => IsVideo(localId) && !(s_thumbs.TryGetValue(localId, out long c) && c < 0);

    /// <summary>Is the indexed file a video? Any thread.</summary>
    public static bool IsVideo(long localId) => LocalLibrary.Index.TryGet(localId, out LocalEntry? e) && e.Tags.Kind == LocalKind.Video;

    /// <summary>Every linked video version: (Spotify track uri, file id), for Settings' list. UI thread, tracked.</summary>
    public static List<(string Song, long File)> VideoVersions()
    {
        _ = s_version.Value;
        var list = new List<(string, long)>();
        foreach (var (uri, files) in s_byTrack)
            foreach (long id in files)
                if (IsVideo(id)) list.Add((uri, id));
        return list;
    }
}
