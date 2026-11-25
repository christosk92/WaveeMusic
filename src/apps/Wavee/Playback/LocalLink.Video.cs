namespace Wavee;

// "Your files and Spotify", one concept: the link store owns which video file a Spotify track plays as video. Every publish of the links (start-up
// included) re-states the local-video lane's session map (Playback.Video.LocalVideos: the video host's resolver and the deck's has-video facts
// read it) from VideoOf, so a linked video version is the track's video everywhere, and an unlinked or removed one stops being it.

public static partial class LocalLink
{
    /// <summary>What this store last fed: Spotify track uri → its video version's file and the picture it wears (null: the album cover).</summary>
    static Dictionary<string, LinkedVideoFeed> s_videosFed = new(StringComparer.Ordinal);

    /// <summary>The tracks whose chosen default version ("Play by default") is a video file: immutable, swapped whole. ANY thread (the deck).</summary>
    static volatile HashSet<EntityId> s_defaultVideos = [];

    /// <summary>Does this Spotify track play its video version by default (the user picked the video under "Play by default")? Then it plays as
    /// a local video file does: on the video host, its picture asked for as it lands (Playback.Transitions IsLocalVideoRow). ANY thread.</summary>
    public static bool PlaysAsVideo(EntityId id) => s_defaultVideos.Count > 0 && s_defaultVideos.Contains(id);

    /// <summary>A still of a video landed (LocalArt): the pictures the feed carries may have changed. UI thread.</summary>
    public static void RefreshVideoPictures() => FeedVideos();

    /// <summary>Feed <c>Playback.Video.LocalVideos</c> the linked video versions, and the rows their pictures (UI thread, after the links moved).</summary>
    static void FeedVideos()
    {
        var now = new Dictionary<string, LinkedVideoFeed>(StringComparer.Ordinal);
        foreach (string uri in s_byTrack.Keys)
            if (VideoOfNow(uri) is var file and not 0 && LocalLibrary.PathOf(file) is { } path) now[uri] = new LinkedVideoFeed(path, FrameUrlOf(file));
        foreach (var (uri, _) in s_videosFed)
            if (!now.ContainsKey(uri) && EntityId.TryParse(uri, out EntityId gone)) Playback.Video.LocalVideos.Unlink(gone);
        foreach (var (uri, feed) in now)
            if ((!s_videosFed.TryGetValue(uri, out LinkedVideoFeed? was) || was.Path != feed.Path) && EntityId.TryParse(uri, out EntityId id))
                Playback.Video.LocalVideos.Link(id, feed.Path);
        var defaults = new HashSet<EntityId>();
        foreach (var (uri, file) in s_defaults)
            if (file != 0 && now.ContainsKey(uri) && IsVideo(file) && EntityId.TryParse(uri, out EntityId d)) defaults.Add(d);
        s_defaultVideos = defaults;
        var before = s_videosFed;
        s_videosFed = now;
        Video.LinkedVideoMirror.Apply(before, now);   // the rows' has-video bit and video picture (Shell/Video.LinkedVideos.cs)
    }

    /// <summary>The path of the video a Spotify track plays as video (its linked video version), or null. Any thread.</summary>
    public static string? VideoPathOf(string spotifyUri) => s_videosFed.TryGetValue(spotifyUri, out LinkedVideoFeed? f) ? f.Path : null;
}

/// <summary>One track's linked video version as the feed carries it: the file, and the picture it wears (a frame; null: the album cover).</summary>
public sealed record LinkedVideoFeed(string Path, string? Frame);
