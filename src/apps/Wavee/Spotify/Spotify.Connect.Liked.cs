// ── Spotify/Spotify.Connect.Liked.cs ────────────────────────────────────────────────────────────────────────────────
// the account's Liked Songs PLAYLIST: the context a Liked Songs play forwarded to another device names (remote-play plan
// 1.2, 2026-11-20)
//
// Role: SHELL (the per-account holder, the kind-246 ask) + PURE reads (the trait payload, the cluster's context metadata)
// Owner: F
// Wave: remote-play fixes (2026-11-20)
// Budget: 200 lines
// Spec: the remote-play gap analysis of 2026-11-20, §2.4, §2.9 and §3 (captures bro.saz, sppp.saz, fresh.saz; Wavee's
//       realtime capture records #92 and #1702) — a named partial of Spotify.Connect.cs
//
// WHY. Wavee forwarded a Liked Songs play with the context `spotify:collection:tracks`. The gateway answered 403, six times of
// six, and the phone it went to stopped playing (Wavee log lines 1241-1243, 1666-1668, 1773-1776, 6623-6645; capture record
// #92: the phone's next cluster still names that context, with "not playing context" restrictions and no next tracks). No
// official client sends that spelling: desktop 1.3.10 names the account's Liked Songs PLAYLIST (`spotify:playlist:37i9dQZF1F…`),
// 13 of 13, all 200 (bro.saz 352-507, five of them to the same iPhone; sppp.saz 156-539). That id is the account's own, so it
// is learned and kept per account, the way Your Episodes' listen-later playlist is (`Spotify.Podcasts.KnownSavedUri`):
//
//   (a) extension kind 246 (CURATION_EXPERIENCE_TRAIT) on a TRACK: payload field 2 → field 2 → field 1 is the Liked Songs
//       playlist (bro.saz 313, 351, 388; fresh.saz 220). On an episode the first list is another one, so only tracks ask.
//   (b) a cluster whose context_metadata says `format_list_type: liked-songs` and whose `liked_songs_collection_uri` names
//       THIS account: its context_uri is the playlist (record #1702, the iPhone playing the official client's Liked play).
//
// Nothing here guesses: a value that is not `spotify:playlist:` + 22 base62 characters is never taken.

using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Connect
    {
        /// <summary>The play_origin feature a forwarded Liked Songs play names: the official client's (bro.saz 352,
        /// <c>feature_identifier: your_library</c>).</summary>
        public const string LikedFeature = "your_library";

        const string PlaylistPrefix = "spotify:playlist:";
        const int PlaylistIdChars = 22;

        static Scope? s_likedScope;
        static string s_likedPlaylist = "";

        /// <summary>The account's Liked Songs playlist uri as far as anything knows it: learned this session, else the one
        /// persisted the last time it was learned (<see cref="Platform.Keys.LikedSongsPlaylist"/>), so a cold start forwards a
        /// Liked play at once. "" when never learned. UI thread (C1: the scope and the settings store); the settings are read
        /// once per scope, so a cluster heartbeat that asks costs a reference compare.</summary>
        public static string KnownLikedPlaylist
        {
            get
            {
                Scope? scope = Entities.Current;
                if (scope is null) return "";
                if (!ReferenceEquals(s_likedScope, scope))
                {
                    s_likedScope = scope;
                    string stored = scope.Key.Account.Length > 0 ? Platform.Settings.Get(Platform.Keys.LikedSongsPlaylist(scope.Key.Account)) : "";
                    s_likedPlaylist = IsPlaylistUri(stored.AsSpan()) ? stored : "";
                }
                return s_likedPlaylist;
            }
        }

        /// <summary>Keep <paramref name="uri"/> as the account's Liked Songs playlist, in memory and in the settings store; a value
        /// that is not a playlist uri, or the one already known, changes nothing. <paramref name="source"/> names where it came
        /// from, for the one log line. UI thread.</summary>
        public static void RememberLikedPlaylist(string uri, string source)
        {
            if (!IsPlaylistUri(uri.AsSpan())) return;
            Scope? scope = Entities.Current;
            if (scope is null || string.Equals(KnownLikedPlaylist, uri, StringComparison.Ordinal)) return;
            s_likedPlaylist = uri;
            if (scope.Key.Account.Length > 0) Platform.Settings.Set(Platform.Keys.LikedSongsPlaylist(scope.Key.Account), uri);
            Log.Info("connect", "liked.playlist learned source=" + source + " uri=" + uri);
        }

        /// <summary>(a): ask kind 246 for <paramref name="trackUri"/> — ONE extended-metadata POST — and read the Liked Songs
        /// playlist out of the answer; null when the answer names none. BLOCKS: an api thread only (C9).</summary>
        public static string? AskLikedPlaylist(string trackUri, CancellationToken ct)
        {
            (int status, byte[]? payload) = Api.ExtensionPayload(trackUri, FetchRoutes.CurationExperience, ct);
            string? uri = payload is null ? null : LikedPlaylistOfTrait(payload);
            Log.Info("connect", "liked.playlist ask kind=246 status=" + status + " found=" + (uri is null ? "no" : "yes"));
            return uri;
        }

        /// <summary>Kind 246's payload (the <c>Any</c>'s value) → the Liked Songs playlist uri, or null: field 2 → field 2 →
        /// field 1, the first list the trait names for a track (bro.saz 313; fresh.saz 220). PURE.</summary>
        public static string? LikedPlaylistOfTrait(ReadOnlySpan<byte> payload)
        {
            ReadOnlySpan<byte> uri = new Decode.ProtoReader(payload).Sub(2).Sub(2).Bytes(1);
            return IsPlaylistUri(uri) ? Encoding.ASCII.GetString(uri) : null;
        }

        /// <summary>(b): a cluster that plays the account's Liked Songs playlist teaches it. UI thread (the username is the
        /// interner's). Allocation-free unless the cluster names a playlist that is not the one already known — a heartbeat of
        /// the same Liked play costs a scan of its context metadata.</summary>
        public static void NoteLikedCluster(Decode.ClusterBuffer buffer, in Decode.ClusterDelta delta)
        {
            if (delta.MetaCount == 0 || Current.Username.IsEmpty) return;
            ReadOnlySpan<byte> uri = LikedPlaylistOfCluster(buffer, in delta, Entities.Strings.Resolve(Current.Username));
            if (uri.IsEmpty || SameAscii(uri, KnownLikedPlaylist)) return;
            RememberLikedPlaylist(Encoding.ASCII.GetString(uri), "cluster");
        }

        /// <summary>The cluster's context uri when it is the Liked Songs playlist of <paramref name="user"/>: the context
        /// metadata says <c>format_list_type: liked-songs</c>, its <c>liked_songs_collection_uri</c> is
        /// <c>spotify:user:&lt;user&gt;:collection</c> (THIS account — a Jam host's Liked list is not ours), and the context is
        /// a playlist uri (record #1702). Empty otherwise. PURE, allocation-free.</summary>
        public static ReadOnlySpan<byte> LikedPlaylistOfCluster(Decode.ClusterBuffer buffer, in Decode.ClusterDelta delta,
            ReadOnlySpan<char> user)
        {
            if (user.IsEmpty || delta.MetaCount == 0) return default;
            ReadOnlySpan<byte> context = buffer.Utf8(delta.ContextUri);
            if (!IsPlaylistUri(context)) return default;
            bool liked = false, ours = false;
            foreach (ref readonly Decode.ClusterMeta m in buffer.Meta(delta.MetaStart, delta.MetaCount))
            {
                ReadOnlySpan<byte> key = buffer.Utf8(m.Key);
                if (key.SequenceEqual("format_list_type"u8)) liked = buffer.Utf8(m.Value).SequenceEqual("liked-songs"u8);
                else if (key.SequenceEqual("liked_songs_collection_uri"u8)) ours = NamesCollectionOf(buffer.Utf8(m.Value), user);
            }
            return liked && ours ? context : default;
        }

        /// <summary>Is <paramref name="uri"/> exactly <c>spotify:user:&lt;user&gt;:collection</c>? PURE.</summary>
        static bool NamesCollectionOf(ReadOnlySpan<byte> uri, ReadOnlySpan<char> user)
        {
            ReadOnlySpan<byte> head = "spotify:user:"u8, tail = ":collection"u8;
            if (uri.Length != head.Length + user.Length + tail.Length || !uri.StartsWith(head) || !uri.EndsWith(tail)) return false;
            return SameAscii(uri.Slice(head.Length, user.Length), user);
        }

        /// <summary>Do ASCII bytes and a string hold the same text? A non-ASCII char never matches. PURE.</summary>
        static bool SameAscii(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> text)
        {
            if (bytes.Length != text.Length) return false;
            for (int i = 0; i < text.Length; i++) if (text[i] > 0x7F || bytes[i] != text[i]) return false;
            return true;
        }

        /// <summary><c>spotify:playlist:</c> + 22 base62 characters, nothing else. PURE.</summary>
        static bool IsPlaylistUri(ReadOnlySpan<char> uri)
        {
            if (uri.Length != PlaylistPrefix.Length + PlaylistIdChars || !uri.StartsWith(PlaylistPrefix, StringComparison.Ordinal)) return false;
            foreach (char c in uri[PlaylistPrefix.Length..]) if (!char.IsAsciiLetterOrDigit(c)) return false;
            return true;
        }

        /// <inheritdoc cref="IsPlaylistUri(ReadOnlySpan{char})"/>
        static bool IsPlaylistUri(ReadOnlySpan<byte> uri)
        {
            ReadOnlySpan<byte> prefix = "spotify:playlist:"u8;
            if (uri.Length != prefix.Length + PlaylistIdChars || !uri.StartsWith(prefix)) return false;
            foreach (byte b in uri[prefix.Length..]) if (!char.IsAsciiLetterOrDigit((char)b)) return false;
            return true;
        }
    }
}
