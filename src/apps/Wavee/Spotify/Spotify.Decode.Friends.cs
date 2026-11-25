// ── Spotify/Spotify.Decode.Friends.cs — the listening-activity answers → staged rows and one friends feed (#277) ──────
//
// THE FEED, as the wire was seen to send it (strict: an unexpected shape is a logged skip, never half-read):
//   POST spclient /listening-activity/v1/feed   body {"unused":true,"resultLimit":100}
//   → { "entities": [ { "followEntity" | "userEntity": { "uri": "spotify:user:…",
//                         "activity": { "entityUri": "spotify:track:…", "contextUri": "spotify:playlist:…"?,
//                                       "timestamp": "2026-11-22T18:04:11.123Z", "isPlaying": true? } } } ] }
//   The user object carries only `uri` and `activity`: names, avatars and titles come from extended-metadata
//   (`Spotify.Friends.EnsureWords`), the album and artist off the track row (`Entities.RefillFriends`). `timestamp` is when
//   the activity STARTED and is kept as it is; `isPlaying` travels as the edge's own flag. Non-track activity (an episode)
//   has no row in the panel's track lane and is skipped.
// THE SUBSCRIBE ANSWER: POST /listening-activity/v1/subscribe → { activities: [...], subscriptionExpirationTimestamp }.
//   Only the expiry is read (the activities repeat the feed's).
//
// Every row is staged THIN with no words, so a fuller row keeps its own (Authority.Thin). Socket thread: no interner, no
// live table (C1); `Entities.CommitFriends` resolves and lands the run.

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>The listening-activity feed → a friends feed batch. False when the body is not an object holding an
        /// <c>entities</c> array. <paramref name="entries"/> counts <c>entities[]</c>; <paramref name="skipped"/> says why the
        /// first few staged nothing (reasons and key names only, never a value), for the always-on line.</summary>
        public static bool ListeningFeed(ReadOnlySpan<byte> json, Staging s, out int entries, out string skipped)
        {
            entries = 0;
            skipped = "";
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            bool sawEntities = false;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("entities"u8)) { SkipValue(ref r); continue; }
                if (!EnterArray(ref r)) return false;
                sawEntities = true;
                s.FriendsFeedAnswered();
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    entries++;
                    string why = ListeningEntity(ref r, s);
                    if (why.Length > 0 && skipped.Length < 300) skipped += (skipped.Length > 0 ? "; " : "") + why;
                }
            }
            return sawEntities;
        }

        /// <summary>One <c>entities[]</c> object; the reader sits ON its StartObject. Answers why it staged nothing ("" when it did).</summary>
        static string ListeningEntity(ref Utf8JsonReader r, Staging s)
        {
            TextRef userUri = default, trackUri = default, contextUri = default;
            long ts = 0;
            bool playing = false;
            string unknown = "";
            for (int o = r.CurrentDepth; Next(ref r, o);)
            {
                // Two names for the same slot, both observed: `followEntity` in the official desktop client's capture
                // (1.3.10.2586, PR #328's review), `userEntity` from a Wavee account (2026-11-22, the plan doc's sample).
                // Almost certainly one gRPC oneof, so one shape.
                if (r.ValueTextEquals("followEntity"u8) || r.ValueTextEquals("userEntity"u8))
                {
                    for (int ue = Fields(ref r); Next(ref r, ue);)
                    {
                        if (r.ValueTextEquals("uri"u8)) { r.Read(); userUri = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("activity"u8)) Activity(ref r, s, ref trackUri, ref contextUri, ref ts, ref playing);
                        else SkipValue(ref r);
                    }
                }
                else
                {
                    if (unknown.Length == 0) unknown = r.GetString() ?? "";
                    SkipValue(ref r);
                }
            }
            if (userUri.IsEmpty) return unknown.Length > 0 ? "unknownEntity(" + unknown + ")" : "noUser";
            if (trackUri.IsEmpty) return "noActivity";
            if (ts <= 0) return "badTimestamp";
            var track = new StagedId(trackUri);
            var kind = track.Kind(s);
            if (kind != EntityKind.Track) return "notTrack(" + kind + ")";

            var user = new StagedId(userUri);
            s.Users.RowFor(user, Authority.Thin, 0u);
            s.Tracks.RowFor(track, Authority.Thin, 0u);
            var context = contextUri.IsEmpty ? default : new StagedId(contextUri);
            if (!context.IsEmpty && context.Kind(s) == EntityKind.Playlist) s.Playlists.RowFor(context, Authority.Thin, 0u);

            ref var f = ref s.Friends.Add();
            f.User = user;
            f.Track = track;
            f.Context = context;
            f.TimestampMs = ts;
            f.Playing = playing;
            return "";
        }

        /// <summary><c>activity: { entityUri, contextUri, timestamp, isPlaying }</c>; the reader sits on the property name.</summary>
        static void Activity(ref Utf8JsonReader r, Staging s, ref TextRef trackUri, ref TextRef contextUri, ref long ts, ref bool playing)
        {
            for (int a = Fields(ref r); Next(ref r, a);)
            {
                if (r.ValueTextEquals("entityUri"u8)) { r.Read(); trackUri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("contextUri"u8)) { r.Read(); contextUri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("timestamp"u8)) { r.Read(); ts = IsoMs(ref r); }
                else if (r.ValueTextEquals("isPlaying"u8)) { r.Read(); playing = Flag(ref r); }
                else SkipValue(ref r);
            }
        }

        /// <summary>The subscribe answer's <c>subscriptionExpirationTimestamp</c> as unix ms, or 0 when the answer is not an
        /// object carrying one.</summary>
        public static long ListeningSubscription(ReadOnlySpan<byte> json)
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return 0;
            long expires = 0;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (r.ValueTextEquals("subscriptionExpirationTimestamp"u8)) { r.Read(); expires = IsoMs(ref r); }
                else SkipValue(ref r);
            }
            return expires;
        }

        /// <summary>An ISO 8601 UTC string (<c>2026-11-22T18:04:11.123Z</c>, the only spelling seen) as unix ms; 0 otherwise.</summary>
        static long IsoMs(ref Utf8JsonReader r)
            => r.TokenType == JsonTokenType.String
               && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.ToUnixTimeMilliseconds() : 0;
    }
}
