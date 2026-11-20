// ── Spotify/Spotify.Connect.Play.cs ─────────────────────────────────────────────────────────────────────────────────
// the `play` envelope a local play sends the device that owns playback, field for field as the official desktop client
// sends it: the row by uid and index, a playlist's page inline in the order Wavee shows it with its sort and filter, the
// artist's Popular list as its own context (remote-play plan 4.1, 4.2, 4.4, 4.5, 2026-11-20)
//
// Role: SHELL (the send) + PURE writers and spellings
// Owner: F
// Wave: remote-play fixes (2026-11-20)
// Budget: 350 lines
// Spec: the remote-play gap analysis of 2026-11-20, §2.1, §2.3, §2.4, §2.5, §5 phase 4 (captures bigone.saz, all.saz,
//       bro.saz, playback_remote.saz; evidence/play-shapes.tsv) — a named partial of Spotify.Connect.cs, split out of
//       Spotify.Connect.Outbound.cs (already twice its budget) when the envelope grew its page
//
// THE ENVELOPE (bigone.saz 891, desktop 1.3.10; the same field set on 1.2.94 and 1.2.96, play-shapes.tsv):
//
//   command { endpoint: play,
//             context { entity_uri, uri, url, metadata { filtering.predicate?, is_single_track_video_content?,
//                       sorting.criteria? }, restrictions { 22 empty lists }, pages? [ { page_url, next_page_url,
//                       tracks [ { uri, uid, metadata { original_index? } } ], metadata } ] },
//             play_origin { device_identifier, feature_identifier, feature_version, view_uri, external_referrer,
//                           referrer_identifier, feature_classes, restriction_identifier },
//             prepare_play_options { always_play_something, skip_to { track_uri, track_uid?, track_index? } | {}, seek_to?,
//                           initially_paused, system_initiated, player_options_override { shuffling_context, modes },
//                           session_id, license, suppressions { providers }, prefetch_level, audio_stream,
//                           configuration_override },
//             play_options { reason, operation, trigger, override_restrictions, only_for_local_device, system_initiated },
//             logging_params { … } },
//   connection_type, intent_id
//
// Where Wavee has no data for a field the official client fills (its xpui feature_version, the page it was navigated from as
// referrer, page-instance and interaction ids, a playlist's volatile context id and player_filter_info) the field keeps
// Wavee's value or stays empty, as the web player's far thinner play is accepted too (bbbb.saz 319).
//
// A PLAYLIST'S PAGE (4.2). The official client never lets the owner resolve a playlist it plays: the whole list goes inline
// as pages[0] in the order its view shows, 1494 rows in all.saz 1169 (131 KB before gzip), one row when a filter leaves one
// (bigone.saz 1628), with the view's `sorting.criteria` and `filtering.predicate` in the context metadata, each row's
// `original_index` when the view is sorted (bigone.saz 784, 1628), a fresh 22-character `session_id`, modes
// `context_enhancement: NONE`, and no `command_received_time`. skip_to then names the row by uri, uid and its index in THAT
// page (bigone.saz 1391; all.saz 1270: index 6 = row 6 of the page), so a track that sits in the list twice starts at the
// copy that was clicked. A page longer than MaxPageRows goes as before: uri only, the row by uri and uid.

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Connect
    {
        /// <summary>The most rows a play sends inline. The largest captured official page is 1494 rows, 131 KB before gzip
        /// (all.saz 1169), answered 200; a longer list goes uri-only, as every play did before.</summary>
        public const int MaxPageRows = 1500;

        /// <summary>A playlist's rows as a play names them inline (remote-play plan 4.2): in the order Wavee shows them, each with
        /// the uid the list gave the row ("" for none) and — only when the view is sorted — its index in the list's own order
        /// (<c>original_index</c>). <paramref name="sorting"/> and <paramref name="filter"/> are the context metadata's
        /// <c>sorting.criteria</c> / <c>filtering.predicate</c> ("" = not written); <paramref name="sessionId"/> is the
        /// <c>prepare_play_options.session_id</c>, 22 base62 characters, minted per play (<see cref="NewSessionId"/>). Immutable
        /// once built (UI thread); the api thread only reads it.</summary>
        public sealed class PlayPage(string[] uris, string[] uids, int[]? originalIndex, string sorting, string filter, string sessionId)
        {
            public string[] Uris { get; } = uris;
            public string[] Uids { get; } = uids;
            public int[]? OriginalIndex { get; } = originalIndex;
            public string Sorting { get; } = sorting;
            public string Filter { get; } = filter;
            public string SessionId { get; } = sessionId;
        }

        /// <summary>"Play this context from this row" on the device that owns playback (the envelope in this file's header).
        /// <paramref name="feature"/> overrides the play_origin feature <see cref="PlayFeatureOf"/> derives from the uri (a Liked
        /// Songs play names its playlist but the feature <see cref="LikedFeature"/>; the artist's Popular list names
        /// <c>artist</c>). <paramref name="page"/> names a playlist's rows inline (4.2). Blocks; api threads only.</summary>
        public static bool PlayContext(string targetDeviceId, string contextUri, string? trackUri, bool shuffle, CancellationToken ct,
            int fromMs = -1, string? skipUid = null, int skipIndex = -1, bool video = false, string? feature = null, PlayPage? page = null)
        {
            if (!Egress.Admits(contextUri.AsSpan()) || (!string.IsNullOrEmpty(trackUri) && !Egress.Admits(trackUri.AsSpan())))
            {
                Log.Info("spotify", "play not forwarded: a non-Spotify item cannot play on another device");
                return false;
            }
            var buffer = new ArrayBufferWriter<byte>(page is null ? 2560 : 1024 + 128 * page.Uris.Length);
            PlayBody(buffer, contextUri, trackUri, shuffle, OurDeviceId, NewId(), NewId(), Playback.UnixNowMs(), fromMs, skipUid, skipIndex, video,
                feature, page);
            return PostCommand("play", targetDeviceId, buffer.WrittenSpan, ct);
        }

        /// <summary>The uid a <c>skip_to</c> or a page row may carry: a row uid as the official client sends it, lowercase hex of
        /// 16 (a user's playlist, bigone.saz 1391), 20 (the Liked list, an album, an artist list: bro.saz 352, bigone.saz 891,
        /// 425) or 32 characters (a generated list, bigone.saz 578); null for anything else — never our own <c>q&lt;n&gt;</c>.
        /// A shape check only: the CALLER vouches that the uid came from the context (a list's own item id, or one the owner
        /// booked into the mirror), since an owner that does not know a uid may ignore the row it names. PURE.</summary>
        public static string? SkipUidOf(string? uid)
        {
            if (uid is not { Length: 16 or 20 or 32 }) return null;
            foreach (char c in uid) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
            return uid;
        }

        /// <summary>The `play` envelope body (this file's header). <paramref name="trackUri"/> null/empty plays the context from
        /// its head (<c>skip_to: {}</c>, bigone.saz 1751). Owners match <c>skip_to</c> by uid, then uri, then index — a uri alone
        /// lands on the FIRST copy of a track that sits in the context twice — so the row's <paramref name="skipUid"/>
        /// (<see cref="SkipUidOf"/>) and a known <paramref name="skipIndex"/> (&gt;= 0) ride beside the uri.
        /// <paramref name="video"/> asks the owner for the row's music video — the desktop's "song videos" play,
        /// <c>is_single_track_video_content</c> in the context metadata (all.saz 1831). <paramref name="feature"/> null derives
        /// the play_origin feature from the uri. <paramref name="page"/> (4.2) adds the rows inline with their sort and filter, a
        /// session id, modes <c>context_enhancement: NONE</c> and logging without a received time, as every official page play
        /// carries. PURE.</summary>
        public static void PlayBody(IBufferWriter<byte> into, string contextUri, string? trackUri, bool shuffle, string deviceId,
            string commandId, string intentId, long nowMs, int fromMs = -1, string? skipUid = null, int skipIndex = -1, bool video = false,
            string? feature = null, PlayPage? page = null)
        {
            using var w = new Utf8JsonWriter(into);
            w.WriteStartObject();
            w.WriteStartObject("command");
            w.WriteString("endpoint", "play");

            w.WriteStartObject("context");
            w.WriteString("entity_uri", contextUri);
            w.WriteString("uri", contextUri);
            w.WriteString("url", "context://" + contextUri);
            w.WriteStartObject("metadata");                    // the keys in the captures' (alphabetical) order
            if (page is { Filter.Length: > 0 }) w.WriteString("filtering.predicate", page.Filter);
            if (video) w.WriteString("is_single_track_video_content", "true");
            if (page is { Sorting.Length: > 0 }) w.WriteString("sorting.criteria", page.Sorting);
            w.WriteEndObject();
            EmptyRestrictions(w);
            if (page is not null) Pages(w, page);
            w.WriteEndObject();

            w.WriteStartObject("play_origin");                 // bigone.saz 891, key for key
            w.WriteString("device_identifier", "");
            w.WriteString("feature_identifier", feature ?? PlayFeatureOf(contextUri));
            w.WriteString("feature_version", "harmony");
            w.WriteString("view_uri", "");
            w.WriteString("external_referrer", "");
            w.WriteString("referrer_identifier", "harmony");
            w.WriteStartArray("feature_classes"); w.WriteEndArray();
            w.WriteString("restriction_identifier", "");
            w.WriteEndObject();

            w.WriteStartObject("prepare_play_options");        // the 1.3.10 key order (play-shapes.tsv: 63 of 66)
            w.WriteBoolean("always_play_something", false);
            w.WriteStartObject("skip_to");
            if (!string.IsNullOrEmpty(trackUri))
            {
                w.WriteString("track_uri", trackUri);
                if (SkipUidOf(skipUid) is { } uid) w.WriteString("track_uid", uid);
                if (skipIndex >= 0) w.WriteNumber("track_index", skipIndex);
            }
            w.WriteEndObject();
            if (fromMs >= 0) w.WriteNumber("seek_to", fromMs);  // after skip_to, as the two episode plays carry it
            w.WriteBoolean("initially_paused", false);
            w.WriteBoolean("system_initiated", false);
            w.WriteStartObject("player_options_override");
            w.WriteBoolean("shuffling_context", shuffle);
            w.WriteStartObject("modes");
            if (page is not null) w.WriteString("context_enhancement", "NONE");   // a page play (bigone.saz 578); {} otherwise (891)
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteString("session_id", page?.SessionId ?? "");
            w.WriteString("license", "premium");
            w.WriteStartObject("suppressions");
            w.WriteStartArray("providers"); w.WriteEndArray();
            w.WriteEndObject();
            w.WriteString("prefetch_level", "none");
            w.WriteString("audio_stream", "default");
            w.WriteStartObject("configuration_override"); w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject("play_options");
            w.WriteString("reason", "interactive");
            w.WriteString("operation", "replace");
            w.WriteString("trigger", "immediately");
            w.WriteBoolean("override_restrictions", false);
            w.WriteBoolean("only_for_local_device", false);
            w.WriteBoolean("system_initiated", false);
            w.WriteEndObject();
            LoggingParams(w, deviceId, commandId, "", nowMs, receivedTime: page is null);
            w.WriteEndObject();
            w.WriteString("connection_type", "wlan");
            w.WriteString("intent_id", intentId);
            w.WriteEndObject();

            static void Pages(Utf8JsonWriter w, PlayPage page)
            {
                w.WriteStartArray("pages");
                w.WriteStartObject();
                w.WriteString("page_url", "");
                w.WriteString("next_page_url", "");
                w.WriteStartArray("tracks");
                Span<char> digits = stackalloc char[12];
                for (int i = 0; i < page.Uris.Length; i++)
                {
                    w.WriteStartObject();
                    w.WriteString("uri", page.Uris[i]);
                    w.WriteString("uid", i < page.Uids.Length ? page.Uids[i] : "");
                    w.WriteStartObject("metadata");
                    if (page.OriginalIndex is { } original && i < original.Length && original[i] >= 0
                        && original[i].TryFormat(digits, out int n, provider: System.Globalization.CultureInfo.InvariantCulture))
                        w.WriteString("original_index", digits[..n]);   // a STRING, as bigone.saz 784 carries it
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartObject("metadata"); w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndArray();
            }
        }

        /// <summary>The play_origin's feature for a context uri — the surface the desktop names for the same kind. PURE.</summary>
        public static string PlayFeatureOf(string contextUri)
        {
            if (contextUri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) return "playlist";
            if (contextUri.StartsWith("spotify:album:", StringComparison.Ordinal)) return "album";
            if (contextUri.StartsWith("spotify:artist:", StringComparison.Ordinal)) return "artist";
            if (contextUri.StartsWith(PopularListPrefix, StringComparison.Ordinal)) return "artist";
            if (contextUri.StartsWith("spotify:show:", StringComparison.Ordinal)) return "show";
            if (contextUri.Contains(":collection", StringComparison.Ordinal)) return "your_library";
            if (contextUri.StartsWith("spotify:track:", StringComparison.Ordinal) || contextUri.StartsWith("spotify:episode:", StringComparison.Ordinal)) return "track";
            return "harmony";
        }

        // ── the view, as the owner is told it ───────────────────────────────────────────────────────────────────────────

        /// <summary>A track list's sort as <c>sorting.criteria</c>, only in the spellings the captures show and their mirror in
        /// the other direction: title (<c>title,original_index</c>, bigone.saz 784), album (<c>album_title,artist_name,
        /// album_disc_number,album_track_number,original_index</c>, all.saz 939, fresh.saz 512) and artist (<c>artist_name
        /// DESC,album_title,album_disc_number,album_track_number,original_index</c>, Wavee capture record #1702), " DESC" on the
        /// first key for a descending sort as the artist one carries it. "" for the list's own order AND for a sort no capture
        /// spells (date added, duration, plays): the page then goes in Wavee's order with no criteria, so the owner plays it as
        /// sent. PURE.</summary>
        public static string SortingCriteria(global::Wavee.Track.SortColumn column, bool descending) => column switch
        {
            global::Wavee.Track.SortColumn.Title => descending ? "title DESC,original_index" : "title,original_index",
            global::Wavee.Track.SortColumn.Album => descending
                ? "album_title DESC,artist_name,album_disc_number,album_track_number,original_index"
                : "album_title,artist_name,album_disc_number,album_track_number,original_index",
            global::Wavee.Track.SortColumn.Artist => descending
                ? "artist_name DESC,album_title,album_disc_number,album_track_number,original_index"
                : "artist_name,album_title,album_disc_number,album_track_number,original_index",
            _ => "",
        };

        /// <summary>Does <see cref="SortingCriteria"/> say this sort — the list's own order counts (no criteria is what it is)?
        /// PURE.</summary>
        public static bool SortSayable(global::Wavee.Track.SortSpec sort)
            => sort == global::Wavee.Track.SortSpec.Default || SortingCriteria(sort.Column, sort.Descending).Length > 0;

        /// <summary>A view's text filter as <c>filtering.predicate</c>: <c>text=^#"&lt;query&gt;"</c>, the one spelling captured
        /// (bigone.saz 1628, the filter "wa"). "" for no query. NULL when the query cannot be said: its escaping is not known, so
        /// only letters, digits and spaces are written; the page sent is filtered either way. PURE.</summary>
        public static string? FilterPredicate(string query)
        {
            if (query.Length == 0) return "";
            foreach (char c in query) if (!char.IsLetterOrDigit(c) && c != ' ') return null;
            return "text=^#\"" + query + "\"";
        }

        /// <summary>The context the official client plays an artist's Popular tracks from (4.4):
        /// <c>spotify:list:popular-release-segments-main-roles:artist_&lt;id&gt;</c> (bigone.saz 425 on 1.3.10; all.saz 2079,
        /// spotify.saz 975 and 1085, playback_remote.saz 439 on 1.2.94), the row click and the page's Play alike, both with the
        /// play_origin feature <c>artist</c>. Wavee forwarded <c>spotify:artist:&lt;id&gt;</c>, which the owner resolves to the
        /// artist context, not that list (Wavee log lines 5830-8268). Wavee already reads the list form on the way in
        /// (<c>Queue.ListArtist</c>).</summary>
        public const string PopularListPrefix = "spotify:list:popular-release-segments-main-roles:artist_";

        /// <summary><paramref name="artistUri"/> (<c>spotify:artist:</c> + 22 base62) → its Popular list uri; null for any other
        /// shape. PURE.</summary>
        public static string? PopularListOf(string artistUri)
        {
            const string Artist = "spotify:artist:";
            if (artistUri.Length != Artist.Length + Base62.GidChars || !artistUri.StartsWith(Artist, StringComparison.Ordinal)) return null;
            return string.Concat(PopularListPrefix.AsSpan(), artistUri.AsSpan(Artist.Length));
        }

        /// <summary>A fresh <c>prepare_play_options.session_id</c>: 22 base62 characters, as every official page play mints one
        /// (bigone.saz 578 "4ANxy…", 1391 "2ZQuq…"). Thread-safe.</summary>
        public static string NewSessionId()
        {
            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes);
            Span<char> chars = stackalloc char[Base62.GidChars];
            int n = Base62.Encode(BinaryPrimitives.ReadUInt128BigEndian(bytes), chars);
            return new string(chars[..n]);
        }
    }
}
