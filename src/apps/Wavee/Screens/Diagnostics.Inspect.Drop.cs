// ── Screens/Diagnostics.Inspect.Drop.cs ───────────────────────────────────────────────────────────────────────────────
// the `drop` verb: a local file read by the tag readers and imported into Local files the way a dropped file is (#240 P1a)
//
// Role: SHELL (the inspector's thread; reads the file once, writes the entity tables through Playlist.ImportLocalFile)
// Spec: handoff/request-plans/240-local-playlist-files-implementation.md §3.5 and §5 (P1: a dropped fixture file's row shows
//       its title, artist and length).
//
//   drop path=<file>                reads the file's format, tags, length and cover location (`LocalTags.TryRead`) and imports
//                                   it as a Local files row with them (`Playlist.ImportLocalFile`), as a drop does before it plays;
//                                   the reply is every field read, the row's uri, and the face the row now shows.
//   drop path=<file> import=false   the read alone: a diagnostic over real files (nothing is staged).
//
// It does not play the file (a drop does): the headless host has no audio chain to open it on, and `play` covers the deck.

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`drop {path, import?}`.</summary>
        string Drop(int id, string name, Inspect.Args a)
        {
            if (a.Str("os") is { Length: > 0 } step) return OsDrag(id, name, a, step);
            string path = a.Str("path");
            if (path.Length == 0) return Inspect.Reply.Fail(id, name, "drop needs path=<file>");
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) { return Inspect.Reply.Fail(id, name, "not a path: " + ex.Message); }
            if (!File.Exists(full)) return Inspect.Reply.Fail(id, name, "no file at " + full);

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool ok = LocalTags.TryRead(full, out Spotify.Audio.Format format, out LocalTagSet tags, out string why);
            double readMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            bool import = a.Bool("import", true);
            Track track = default;
            if (import) track = Playlist.ImportLocalFile(full, in tags);

            void Body(Utf8JsonWriter w)
            {
                w.WriteString("file", Path.GetFileName(full));
                w.WriteBoolean("read", ok);
                w.WriteString("format", format.ToString());
                w.WriteString("why", why);
                w.WriteNumber("readMs", Math.Round(readMs, 2));
                w.WriteString("kind", tags.Kind.ToString());
                w.WriteString("title", tags.Title);
                w.WriteString("titleOrFileName", LocalTags.TitleOrFileName(in tags, full));
                w.WriteStartArray("artists");
                foreach (string artist in tags.Artists) w.WriteStringValue(artist);
                w.WriteEndArray();
                w.WriteString("album", tags.Album);
                w.WriteString("albumArtist", tags.AlbumArtist);
                w.WriteNumber("track", tags.TrackNumber);
                w.WriteNumber("disc", tags.DiscNumber);
                w.WriteNumber("year", tags.Year);
                w.WriteNumber("durationMs", tags.DurationMs);
                w.WriteBoolean("estimated", tags.DurationEstimated);
                w.WriteNumber("kbps", tags.BitrateKbps);
                w.WriteNumber("rate", tags.SampleRate);
                w.WriteNumber("bits", tags.BitsPerSample);
                w.WriteNumber("channels", tags.Channels);
                if (tags.Kind == LocalKind.Video)
                {
                    w.WriteNumber("videoW", tags.VideoWidth);
                    w.WriteNumber("videoH", tags.VideoHeight);
                    w.WriteBoolean("hasAudio", tags.HasAudio);
                }
                w.WriteStartObject("cover");
                w.WriteBoolean("exists", tags.Cover.Exists);
                if (tags.Cover.Exists)
                {
                    w.WriteNumber("offset", tags.Cover.Offset);
                    w.WriteNumber("length", tags.Cover.Length);
                    w.WriteString("mime", tags.Cover.Mime.ToString());
                    w.WriteString("encoding", tags.Cover.Encoding.ToString());
                    w.WriteNumber("pictureType", tags.Cover.PictureType);
                }
                w.WriteEndObject();
                w.WriteStartObject("keys");
                w.WriteString("exactTitle", LocalMatch.Exact(LocalTags.TitleOrFileName(in tags, full)));
                w.WriteString("looseTitle", LocalMatch.Loose(LocalTags.TitleOrFileName(in tags, full)));
                w.WriteString("exactArtist", LocalMatch.Exact(tags.FirstArtist));
                w.WriteString("looseArtist", LocalMatch.Loose(tags.FirstArtist));
                w.WriteEndObject();
                Span<byte> uri = stackalloc byte[Spotify.Egress.MaxLocalUriBytes];
                int n = Spotify.Egress.LocalTrackUri(tags.FirstArtist, tags.Album, LocalTags.TitleOrFileName(in tags, full), tags.DurationMs, uri);
                w.WriteString("spotifyUri", n > 0 ? System.Text.Encoding.UTF8.GetString(uri[..n]) : "");
                if (!import) return;
                w.WriteStartObject("row");
                w.WriteBoolean("imported", track.IsValid);
                if (track.IsValid)
                {
                    w.WriteString("uri", track.Uri.Text);
                    w.WriteString("title", track.Title);
                    w.WriteString("artist", track.LocalArtist);
                    w.WriteString("album", Playlist.LocalNamesOf(track.Uri.Text).Album);
                    w.WriteNumber("durationMs", track.DurationMs);
                    w.WriteString("duration", Track.Format.DurationCell(track.DurationMs));
                }
                w.WriteEndObject();
            }

            if (_shell is null || !import) return Inspect.Reply.Ok(id, name, Body);
            RunFrames(2);
            return Settled(id, name, a, Body);
        }

        /// <summary>`drop os=enter|over|leave|drop {paths|uris|text} {x,y}`: one step of an OS drag through the engine's external-drop
        /// seam (DropAnywhere.Play), then the drag's plan and the drop panel's state; the step settles (and traces) like any acting verb.</summary>
        string OsDrag(int id, string name, Inspect.Args a, string step)
        {
            if (_shell is null) return Inspect.Reply.Fail(id, name, "drop os= needs a mounted shell (mount first)");
            static string[]? Split(string v) => v.Length == 0 ? null : v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // paths=* offers files with an EMPTY list on enter: an engine that reads the file list only at drop (the drop then hands
            // the last enter's list, or none).
            string[]? paths = a.Str("paths") == "*" ? [] : Split(a.Str("paths"));
            if (paths is { Length: > 0 })
                for (int i = 0; i < paths.Length; i++) paths[i] = Path.GetFullPath(paths[i]);
            string[]? uris = Split(a.Str("uris"));
            string text = a.Str("text");
            var scene = _shell.Host.Scene;
            var size = scene.AbsoluteRect(scene.Root);
            var at = new FluentGpu.Foundation.Point2(a.Float("x", size.W / 2f), a.Float("y", size.H / 2f));
            if (step == "enter" && paths is null && uris is null && text.Length == 0)
                return Inspect.Reply.Fail(id, name, "drop os=enter needs paths=, uris= or text=");
            // A drop of files hands the list again, as the OLE target does: the last enter's list when none is given.
            if (step == "drop" && paths is null && uris is null && text.Length == 0) paths = _osPaths;
            if (step == "enter") _osPaths = paths;
            if (!DropAnywhere.Play(step, at, paths, uris, text, out string effect))
                return Inspect.Reply.Fail(id, name, "the host wired no external-drop seam");
            var plan = DropAnywhere.Current;
            void Body(Utf8JsonWriter w)
            {
                w.WriteString("step", step);
                w.WriteNumber("x", at.X);
                w.WriteNumber("y", at.Y);
                w.WriteString("effect", effect);
                if (plan is not null)
                {
                    w.WriteString("kind", plan.Kind.ToString());
                    w.WriteBoolean("pathsKnown", plan.PathsKnown);
                    w.WriteStartArray("zones");
                    foreach (var verb in plan.Verbs) w.WriteStringValue(verb.ToString());
                    w.WriteEndArray();
                    w.WriteNumber("songs", plan.Songs.Peek());
                    w.WriteString("chip", plan.Chip);
                    if (plan.Refusal is { } why) w.WriteString("refusal", why);
                }
                w.WriteBoolean("panelShown", DropAnywhere.Shown.Peek());
                w.WriteNumber("hotZone", DropAnywhere.Hot.Peek());
                w.WriteNumber("centreDx", DropAnywhere.CentreOffset.Peek().X);
                w.WriteNumber("centreDy", DropAnywhere.CentreOffset.Peek().Y);
            }
            RunFrames(2);
            return Settled(id, name, a, Body);
        }

        string[]? _osPaths;
    }
}
