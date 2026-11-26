// ── Screens/Diagnostics.Inspect.Friends.cs ────────────────────────────────────────────────────────────────────────────
// the `friends` verb: read the friend-activity run, or land a push the way the dealer delivers it. `push=<user>` builds one
// frame in the captured `hm://listening-activity/` shape (Spotify.Decode.Friends.cs header: payloads[{username, accountId,
// activity{uri, isPlaying, timestamp as unix ms, contextUri}}]); `raw=<file>` lands a frame file as received. Either goes
// through Spotify.Friends.ApplyPush, the dealer's own path: the decoder, the commit and the panel are production's. With a
// mount the reply settles, so `trace=` and `render` see the row change and the run re-sort.
//
// Role: SHELL (UI thread; decides nothing: the row and the order are Entities.CommitFriends')
// Spec: docs/plans/wavee/friend-activity-implementation.md §5 (the push)

using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        string FriendsVerb(int id, string name, Inspect.Args a)
        {
            Spotify.Friends.Attach();   // idempotent: the panel's props builder does the same
            if (a.Has("push") && a.Has("raw")) return Inspect.Reply.Fail(id, name, "push= or raw=, not both");
            string landed = "";
            if (a.Has("raw"))
            {
                byte[] frame;
                try { frame = File.ReadAllBytes(a.Str("raw")); }
                catch (Exception ex) { return Inspect.Reply.Fail(id, name, "cannot read " + a.Str("raw") + ": " + ex.Message); }
                Spotify.Friends.ApplyPush(frame);
                landed = "raw";
            }
            else if (a.Has("push"))
            {
                string user = a.Str("push");
                if (user.StartsWith("spotify:user:", StringComparison.Ordinal)) user = user["spotify:user:".Length..];
                string track = a.Str("track");
                if (!track.StartsWith("spotify:track:", StringComparison.Ordinal)) return Inspect.Reply.Fail(id, name, "push needs track=spotify:track:…");
                string context = a.Str("context");
                if (context.Length > 0 && !context.StartsWith("spotify:", StringComparison.Ordinal)) return Inspect.Reply.Fail(id, name, "context is a spotify: uri");
                long started = Playback.UnixNowMs() - a.Int("agoMs", 0);
                Spotify.Friends.ApplyPush(PushFrame(user, track, context, started, a.Bool("playing", true)));
                landed = "push";
            }

            Action<Utf8JsonWriter> body = w =>
            {
                if (landed.Length > 0) w.WriteString("landed", landed);
                w.WriteString("state", Rail.Friends.State.Value.ToString());
                long now = Playback.UnixNowMs();
                w.WriteStartArray("rows");
                foreach (ref readonly var e in Entities.Current.Edges.Friends.Payload(Rail.Friends.Feed))
                {
                    w.WriteStartObject();
                    w.WriteString("user", new User(e.UserSlot).Uri.Text);
                    w.WriteString("track", e.TrackSlot > 0 ? new Track(e.TrackSlot).Uri.Text : "");
                    w.WriteBoolean("playing", e.Playing);
                    w.WriteNumber("ageMs", now - e.TimestampMs);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            };
            return _shell is null ? Inspect.Reply.Ok(id, name, body) : Settled(id, name, a, body);
        }

        /// <summary>One push frame in the captured wire shape.</summary>
        static byte[] PushFrame(string username, string track, string context, long startedMs, bool playing)
        {
            var buffer = new ArrayBufferWriter<byte>(512);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("type", "message");
                w.WriteString("uri", "hm://listening-activity/");
                w.WriteStartObject("headers");
                w.WriteString("Content-Type", "application/json");
                w.WriteEndObject();
                w.WriteStartArray("payloads");
                w.WriteStartObject();
                w.WriteString("username", username);
                w.WriteString("accountId", "0000000000");
                w.WriteStartObject("activity");
                w.WriteString("uri", track);
                w.WriteBoolean("isPlaying", playing);
                w.WriteNumber("timestamp", startedMs);
                if (context.Length > 0) w.WriteString("contextUri", context);
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }
    }
}
