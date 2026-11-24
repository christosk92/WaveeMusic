// ── Playback/Playback.Video.Local.cs ───────────────────────────────────────────────────────────────────────────────
// local VIDEO files (#240): the one path every local video plays through, and the entry point a caller uses to watch one
//
// Role: SHELL (the resolver half runs on api threads; `WatchLocalVideo` on the UI thread)
//
// WHAT PLAYS. A local video file plays on the VIDEO host with its own sound, exactly as a music video does: the same player, the same
// surfaces (the docked card in Now playing's slot, the mini player, full screen), the same transport, seek and A/V clock (Media Foundation
// plays the file's picture and sound as one session, so they cannot drift). Nothing decodes it as audio unless the user asks for audio
// only, or the video fails (the reducer's video fault, `DoVideoFault`, then plays the row on the audio host at the carried position).
//
// WHICH FILE. `LocalVideos.PathOf` answers for a playable, in order:
//   1. a local version LINKED to the track (`Link`): a Spotify track the user paired with a video file on this PC;
//   2. the row's own file, when the row is a local video file (`TrackFlags.LocalVideoFile`, stated from its tags at staging:
//      `Playlist.StateLocalVideo`).
// The resolver's tier `Local` (`Playback.Video.Source.cs`) opens it; a user-attached override (tier 1) still wins over both.
//
// THE DECK. `Playback.IsLocalVideoRow` / `DeckKind` / `LocalVideoWish` (Playback.Transitions.cs): a row that is a local video turns the video
// mode on for itself as it lands (the surface opens at the preferred placement) and the deck turns it off again when it moves on to a row
// that is not one, unless the user had it on. Crossfade and gapless never span a video boundary (`MediaSwitch.AllowCrossfade`).
//
// THE ENTRY POINT. `Playback.WatchLocalVideo(row, path?)`: play the row as its video, or switch the playing row to its video at its position
// (the song keeps playing while the video attaches, then the cut: `ParkAndCut`). With a path, the file is linked to the row first.

using System.Collections.Concurrent;

namespace Wavee;

public static partial class Playback
{
    public static partial class Video
    {
        /// <summary>Which local video file plays for a playable (see the file header). ANY THREAD.</summary>
        public static class LocalVideos
        {
            static readonly ConcurrentDictionary<EntityId, string> s_linked = new();

            /// <summary>Bumps on every <see cref="Link"/> / <see cref="Unlink"/>: a reader that caches an answer re-asks when it moved.</summary>
            public static int Version => Volatile.Read(ref s_version);
            static int s_version;

            /// <summary>Pair <paramref name="track"/> with a video file on this PC: it plays that file as its video (a session fact; the caller
            /// that owns the pairing persists it).</summary>
            public static void Link(EntityId track, string path)
            {
                if (track.IsEmpty || string.IsNullOrEmpty(path)) return;
                s_linked[track] = path;
                Interlocked.Increment(ref s_version);
            }

            /// <summary>Undo <see cref="Link"/>.</summary>
            public static void Unlink(EntityId track)
            {
                if (s_linked.TryRemove(track, out _)) Interlocked.Increment(ref s_version);
            }

            /// <summary>Is a local video file linked to <paramref name="track"/>? No allocation (the deck's facts ask it per pass).</summary>
            public static bool IsLinked(EntityId track) => !s_linked.IsEmpty && s_linked.ContainsKey(track);

            /// <summary>The local video file that plays for <paramref name="id"/>, or null: its linked version, else its own file when it is a
            /// local video container. The row's own kind (<c>TrackFlags.LocalVideoFile</c>) decides whether it loads on the video host at all;
            /// this only names the file.</summary>
            public static string? PathOf(EntityId id)
            {
                if (id.IsEmpty) return null;
                if (s_linked.TryGetValue(id, out string? linked)) return linked;
                if (id.Provider != EntityProvider.Local) return null;
                string? own = Audio.LocalPath?.Invoke(id) ?? Playlist.LocalPathOf(id);
                return Playlist.IsLocalVideoFile(own) ? own : null;
            }

            /// <summary>The source for a local video file: its picture's size from its header (one read), so the surface opens at the video's
            /// own aspect before the first frame. Api threads (the read can block on a slow drive).</summary>
            public static VideoSource Source(string path)
            {
                int w = 0, h = 0;
                try
                {
                    if (LocalTags.TryRead(path, out _, out LocalTagSet tags, out _) || tags.Kind == LocalKind.Video) { w = tags.VideoWidth; h = tags.VideoHeight; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return VideoSource.LocalFile(path) with { NaturalWidth = Math.Max(0, w), NaturalHeight = Math.Max(0, h) };
            }
        }
    }

    /// <summary>THE entry point for "watch this as a video" over a local video file. <paramref name="row"/> is a local video file's row, or any
    /// track with <paramref name="linkedPath"/> (a video file on this PC paired with it as its version, <see cref="Video.LocalVideos.Link"/>).
    /// The playing row switches to its video at its position (the surface opens at the preferred placement); any other row starts playing
    /// as its video. UI thread.</summary>
    public static void WatchLocalVideo(EntityRef row, string? linkedPath = null)
    {
        if (row.IsNone) return;
        if (linkedPath is { Length: > 0 }) Video.LocalVideos.Link(row.Id, linkedPath);
        if (row.Equals(s_state.Current) && s_state.HasCurrent && s_state.RoutesLocal)
        {
            OnVideoIntent?.Invoke(true, Fault.None);   // the surface opens (its placement post turns the wish on)
            SwitchVideo(true);                         // and the deck reloads the row on the video host at the carried position
            return;
        }
        PlayNow(row, s_state.Context, QueueCursor.None, PlayableKind.Video, 0);
    }
}
