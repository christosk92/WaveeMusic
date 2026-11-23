// ── Spotify/Spotify.Decode.Friends.cs — the friend-activity answers → staged rows and one friends feed (#277) ─────────
//
// THE FEED (what the desktop client's panel reads; spotifast/librespot's get_listening_activity):
//   POST spclient /listening-activity/v1/feed   body {"unused":true,"resultLimit":100}
//   → { "entities": [ { "userEntity" | "followEntity": { "uri": "spotify:user:…", (name, imageUrl)? },
//                       "activity": { "entityUri": "spotify:track:…", "timestamp": "<ISO 8601 | ms>",
//                                     "contextUri": "spotify:playlist:…"?, "isPlaying": true? } } ] }
//   It names no titles: the track, user and context rows are staged by identity and the host asks for their words
//   (`Entities.Ensure`); the album and artist are read off the track row (`Entities.RefillFriends`). A playing friend's
//   time is "now", so it sorts first and shows live, as the desktop client draws it. Non-track activity (an episode)
//   has no row in the panel's track lane and is skipped.
// THE FALLBACK (the older buddy list, for an account the feed refuses):
//   GET spclient /presence-view/v1/buddylist → { "friends": [ { timestamp, user{uri,name,imageUrl}, track{uri,name,
//   imageUrl, album{uri,name}, artist{uri,name}, context{uri,name}} } ] } — it carries the words itself.
//
// Every row is staged THIN with only the words the answer carries, so a fuller row keeps its own (Authority.Thin). Socket
// thread: no interner, no live table (C1); `Entities.CommitFriends` resolves and lands the run.

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>The listening-activity feed → a friends feed batch. False when the body is not a JSON object.
        /// <paramref name="entries"/> counts <c>entities[]</c>; <paramref name="skipped"/> says why the first few staged
        /// nothing (reasons and key names only, never a value), for the always-on poll line.</summary>
        public static bool ListeningFeed(ReadOnlySpan<byte> json, Staging s, long nowMs, out int entries, out string skipped)
        {
            entries = 0;
            skipped = "";
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            s.FriendsFeedAnswered();
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("entities"u8)) { SkipValue(ref r); continue; }
                if (!EnterArray(ref r)) continue;
                bool shown = false;
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    entries++;
                    long start = r.TokenStartIndex;
                    string why = ListeningEntity(ref r, s, nowMs);
                    if (why.Length == 0) continue;
                    // The FIRST skipped entity's key tree (names only, never a value) says what the feed actually sent.
                    if (!shown) { shown = true; why += " keys=" + KeyTree(json[(int)start..(int)r.BytesConsumed], 4); }
                    if (skipped.Length < 600) skipped += (skipped.Length > 0 ? "; " : "") + why;
                }
            }
            return true;
        }

        /// <summary>One <c>entities[]</c> object; the reader sits ON its StartObject. Answers why it staged nothing ("" when it did).</summary>
        static string ListeningEntity(ref Utf8JsonReader r, Staging s, long nowMs)
        {
            TextRef userUri = default, userName = default, userImage = default, trackUri = default, contextUri = default;
            long ts = 0;
            bool playing = false;
            // The live feed nests the activity INSIDE the user (`{userEntity:{uri, activity:{entityUri, contextUri, isPlaying,
            // timestamp}}}`, 2026-11-22); a sibling `activity` is read too.
            for (int o = r.CurrentDepth; Next(ref r, o);)
            {
                if (r.ValueTextEquals("userEntity"u8) || r.ValueTextEquals("followEntity"u8) || r.ValueTextEquals("user"u8))
                {
                    for (int ue = Fields(ref r); Next(ref r, ue);)
                    {
                        if (r.ValueTextEquals("uri"u8)) { r.Read(); userUri = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("name"u8)) { r.Read(); userName = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("imageUrl"u8) || r.ValueTextEquals("image_url"u8)) { r.Read(); userImage = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("activity"u8)) Activity(ref r, s, ref trackUri, ref contextUri, ref ts, ref playing);
                        else SkipValue(ref r);
                    }
                }
                else if (r.ValueTextEquals("activity"u8)) Activity(ref r, s, ref trackUri, ref contextUri, ref ts, ref playing);
                else SkipValue(ref r);
            }
            if (userUri.IsEmpty) return "noUser";
            if (trackUri.IsEmpty) return "noActivity";
            var track = new StagedId(trackUri);
            var kind = track.Kind(s);
            if (kind != EntityKind.Track) return "notTrack(" + kind + ")";

            var user = new StagedId(userUri);
            ref var u = ref s.Users.RowFor(user, Authority.Thin, userName.IsEmpty && userImage.IsEmpty ? 0u : (uint)UserFields.Identity);
            u.Name = userName;
            u.Image = userImage;
            s.Tracks.RowFor(track, Authority.Thin, 0u);
            var context = contextUri.IsEmpty ? default : new StagedId(contextUri);
            if (!context.IsEmpty && context.Kind(s) == EntityKind.Playlist) s.Playlists.RowFor(context, Authority.Thin, 0u);

            ref var f = ref s.Friends.Add();
            f.User = user;
            f.Track = track;
            f.Context = context;
            f.TimestampMs = playing ? Math.Max(ts, nowMs) : ts;
            return "";
        }

        /// <summary><c>activity: { entityUri, contextUri, isPlaying, timestamp }</c>; the reader sits on the property name.</summary>
        static void Activity(ref Utf8JsonReader r, Staging s, ref TextRef trackUri, ref TextRef contextUri, ref long ts, ref bool playing)
        {
            for (int a = Fields(ref r); Next(ref r, a);)
            {
                if (r.ValueTextEquals("entityUri"u8) || r.ValueTextEquals("uri"u8)) { r.Read(); trackUri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("contextUri"u8)) { r.Read(); contextUri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("timestamp"u8)) { r.Read(); ts = Timestamp(ref r); }
                else if (r.ValueTextEquals("isPlaying"u8)) { r.Read(); playing = Flag(ref r); }
                else SkipValue(ref r);
            }
        }

        /// <summary>A timestamp however the feed spells it: unix ms as a number or a digit string, or ISO 8601 text.</summary>
        static long Timestamp(ref Utf8JsonReader r)
        {
            if (r.TokenType == JsonTokenType.Number) return r.TryGetInt64(out long n) ? n : 0;
            if (r.TokenType != JsonTokenType.String) return 0;
            string? text = r.GetString();
            if (string.IsNullOrEmpty(text)) return 0;
            if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long ms)) return ms;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.ToUnixTimeMilliseconds() : 0;
        }

        /// <summary>The buddy list's <c>{ "friends": [...] }</c> → a friends feed batch (the fallback). False when the body is
        /// not a JSON object.</summary>
        public static bool FriendFeed(ReadOnlySpan<byte> json, Staging s, out int entries, out string skipped)
        {
            entries = 0;
            skipped = "";
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            s.FriendsFeedAnswered();
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("friends"u8)) { SkipValue(ref r); continue; }
                if (!EnterArray(ref r)) continue;
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    entries++;
                    string why = Friend(ref r, s);
                    if (why.Length > 0 && skipped.Length < 300) skipped += (skipped.Length > 0 ? "; " : "") + why;
                }
            }
            return true;
        }

        /// <summary>One buddy-list friend object; the reader sits ON its StartObject. Answers why it staged nothing.</summary>
        static string Friend(ref Utf8JsonReader r, Staging s)
        {
            TextRef userUri = default, userName = default, userImage = default;
            TextRef trackUri = default, trackName = default, trackImage = default;
            TextRef albumUri = default, albumName = default, artistUri = default, artistName = default;
            TextRef contextUri = default, contextName = default;
            long ts = 0;
            for (int o = r.CurrentDepth; Next(ref r, o);)
            {
                if (r.ValueTextEquals("timestamp"u8)) { r.Read(); ts = Timestamp(ref r); }
                else if (r.ValueTextEquals("user"u8)) FriendNamed(ref r, s, out userUri, out userName, out userImage);
                else if (r.ValueTextEquals("track"u8))
                {
                    for (int t = Fields(ref r); Next(ref r, t);)
                    {
                        if (r.ValueTextEquals("uri"u8)) { r.Read(); trackUri = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("name"u8)) { r.Read(); trackName = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("imageUrl"u8) || r.ValueTextEquals("image_url"u8)) { r.Read(); trackImage = s.AddJson(ref r); }
                        else if (r.ValueTextEquals("album"u8)) FriendNamed(ref r, s, out albumUri, out albumName, out _);
                        else if (r.ValueTextEquals("artist"u8)) FriendNamed(ref r, s, out artistUri, out artistName, out _);
                        else if (r.ValueTextEquals("context"u8)) FriendNamed(ref r, s, out contextUri, out contextName, out _);
                        else SkipValue(ref r);
                    }
                }
                else SkipValue(ref r);
            }
            if (userUri.IsEmpty) return "noUser";
            if (trackUri.IsEmpty) return "noTrack";

            var user = new StagedId(userUri);
            ref var u = ref s.Users.RowFor(user, Authority.Thin, userName.IsEmpty && userImage.IsEmpty ? 0u : (uint)UserFields.Identity);
            u.Name = userName;
            u.Image = userImage;

            var album = albumUri.IsEmpty ? default : new StagedId(albumUri);
            if (!album.IsEmpty && !albumName.IsEmpty)
                s.Albums.RowFor(album, Authority.Thin, (uint)AlbumFields.Title).Title = albumName;
            var artist = artistUri.IsEmpty ? default : new StagedId(artistUri);
            if (!artist.IsEmpty && !artistName.IsEmpty)
                s.Artists.RowFor(artist, Authority.Thin, (uint)ArtistFields.Name).Name = artistName;
            var context = contextUri.IsEmpty ? default : new StagedId(contextUri);
            if (!context.IsEmpty && !contextName.IsEmpty && context.Kind(s) == EntityKind.Playlist)
                s.Playlists.RowFor(context, Authority.Thin, (uint)PlaylistFields.Identity).Title = contextName;

            var track = new StagedId(trackUri);
            uint trackKnown = (trackName.IsEmpty ? 0u : (uint)TrackFields.Title) | (trackImage.IsEmpty ? 0u : (uint)TrackFields.Image);
            ref var t2 = ref s.Tracks.RowFor(track, Authority.Thin, trackKnown);
            t2.Title = trackName;
            t2.Image = trackImage;
            t2.AlbumUri = album;

            ref var f = ref s.Friends.Add();
            f.User = user;
            f.Track = track;
            f.Album = album;
            f.Artist = artist;
            f.Context = context;
            f.TimestampMs = ts;
            return "";
        }

        /// <summary><c>{ uri, name, imageUrl | image_url }</c>; the reader sits on the property name.</summary>
        static void FriendNamed(ref Utf8JsonReader r, Staging s, out TextRef uri, out TextRef name, out TextRef image)
        {
            uri = name = image = default;
            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("uri"u8)) { r.Read(); uri = s.AddJson(ref r); }
                else if (r.ValueTextEquals("name"u8)) { r.Read(); name = s.AddJson(ref r); }
                else if (r.ValueTextEquals("imageUrl"u8) || r.ValueTextEquals("image_url"u8)) { r.Read(); image = s.AddJson(ref r); }
                else SkipValue(ref r);
            }
        }

        /// <summary>An object's KEY TREE for the always-on log: <c>{a,b{c,d[2]{e}}}</c> — key names, nested objects to
        /// <paramref name="depth"/>, an array as its length and its first element's keys. Never a value.</summary>
        static string KeyTree(ReadOnlySpan<byte> json, int depth)
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read()) return "";
            var b = new System.Text.StringBuilder(200);
            KeyTreeValue(ref r, b, depth);
            return b.ToString();
        }

        static void KeyTreeValue(ref Utf8JsonReader r, System.Text.StringBuilder b, int depth)
        {
            if (r.TokenType == JsonTokenType.StartObject)
            {
                if (depth <= 0) { b.Append("{…}"); r.Skip(); return; }
                b.Append('{');
                bool first = true;
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    if (!first) b.Append(',');
                    first = false;
                    b.Append(r.GetString());
                    if (!r.Read()) return;
                    if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) KeyTreeValue(ref r, b, depth - 1);
                }
                b.Append('}');
            }
            else if (r.TokenType == JsonTokenType.StartArray)
            {
                int n = 0;
                var inner = new System.Text.StringBuilder();
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    n++;
                    if (n == 1 && r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) KeyTreeValue(ref r, inner, depth - 1);
                    else if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip();
                }
                b.Append('[').Append(n.ToString(CultureInfo.InvariantCulture)).Append(']').Append(inner);
            }
        }

        /// <summary>An answer's SHAPE for the always-on log: each top-level key with its array length or value kind, and the
        /// keys of the first array element — names and counts only, never a value.</summary>
        public static string FriendFeedShape(ReadOnlySpan<byte> json)
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return "notObject(" + json.Length.ToString(CultureInfo.InvariantCulture) + "b)";
            var b = new System.Text.StringBuilder(160);
            int root = r.CurrentDepth;
            while (r.Read() && !(r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == root))
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (b.Length > 0) b.Append(',');
                b.Append(r.GetString());
                if (!r.Read()) break;
                if (r.TokenType == JsonTokenType.StartArray)
                {
                    int depth = r.CurrentDepth, n = 0;
                    string first = "";
                    while (r.Read() && !(r.TokenType == JsonTokenType.EndArray && r.CurrentDepth == depth))
                    {
                        if (r.CurrentDepth != depth + 1 || r.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) continue;
                        n++;
                        if (n == 1 && r.TokenType == JsonTokenType.StartObject)
                        {
                            int od = r.CurrentDepth;
                            while (r.Read() && !(r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == od))
                                if (r.TokenType == JsonTokenType.PropertyName && r.CurrentDepth == od + 1) first += (first.Length > 0 ? "|" : "") + r.GetString();
                        }
                        else r.Skip();
                    }
                    b.Append('[').Append(n.ToString(CultureInfo.InvariantCulture));
                    if (first.Length > 0) b.Append(":{").Append(first).Append('}');
                    b.Append(']');
                }
                else if (r.TokenType == JsonTokenType.StartObject) { b.Append("{}"); r.Skip(); }
                else b.Append('=').Append(r.TokenType.ToString());
            }
            return b.ToString();
        }
    }
}
