// ── Playback/Playback.Dj.Connect.cs ──────────────────────────────────────────────────────────────────────────────────
// AI DJ over Connect (plan-dj D4, the parts that need no voice): what Wavee publishes while it plays a lexicon session, and the
// DJ list's own facts a DJ play names, here and to another device.
//
// Role: SHELL (UI thread: reads the session record, the DJ book and the playlist table)
// Spec: the AI DJ plan (plan-dj) §1.9, §2.7; the protocol (dj-protocol) §2.1, §4
//
// A NAMED PARTIAL OF `Playback.Dj` (`Playback.Dj.cs`):
//
//   CaptureWire (Playback.Host.Wire.cs) ─▶ WireNow(context, deck item) ─▶ DjWire: f3 url · f4 / f17 restrictions · f6 (page, track) ·
//                                                                          f22 page metadata · the narration seam
//   WindowNow's rows ─▶ the DJ book's keys per row (DjKeysOn) ─▶ next_tracks / prev_tracks / the current item's metadata
//   a local DJ start (PlayContext) ─▶ ListMetadataJson: the list's format attributes, the context metadata of the play (f21)
//
// THE INDEX'S NUMBERING. f6 is (page, track): `Dj.Row.Page` is Wavee's own count of the pages fetched for the DJ play that is going
// on (1 = the first real page; a renewal past the session's expiry keeps counting, so the index never goes back mid-play), and
// `Dj.Row.Track` is the row's slot in its page as the page listed it. A deck row no page of this session holds (a transferred or a
// restored track) has no index, as a narration has none.

using System.Text;
using System.Text.Json;

using FluentGpu.Foundation;
using FluentGpu.Signals;

using ClusterBuffer = Wavee.Spotify.Decode.ClusterBuffer;
using ClusterTrack = Wavee.Spotify.Decode.ClusterTrack;

namespace Wavee;

public static partial class Playback
{
    public static partial class Dj
    {
        // ── the PUT's DJ half ────────────────────────────────────────────────────────────────────────────────────────

        static DjWire? s_wire;
        static Session? s_wireSession;
        static ulong s_wireItem;
        static int s_wirePages = -1, s_wireRenewals = -1;

        /// <summary>The DJ half of the PUT for a deck on <paramref name="context"/> whose row is <paramref name="deckItemId"/>, or null when
        /// no lexicon session plays it. Rebuilt only when the session, the deck row or the pages moved, so a pause or a seek reuses the
        /// one built. UI thread (the capture).</summary>
        internal static DjWire? WireNow(EntityId context, ulong deckItemId)
        {
            if (Of(context) is not { } s) return null;
            if (s_wire is { } kept && ReferenceEquals(s, s_wireSession) && deckItemId == s_wireItem && s.Pages.Count == s_wirePages
                && s.Renewals == s_wireRenewals)
                return kept;
            Row? row = RowOf(deckItemId);
            int page = row?.Page ?? 0, track = row?.Track ?? 0;
            KeyValuePair<string, string>[] metadata = [];
            if (page > 0)
                foreach (Page p in s.Pages)
                    if (p.Ordinal == page) { metadata = SoundEntries(p.Metadata); break; }
            // THE NARRATION SEAM (plan-dj D2/D3): `Narrating` stays false until a narration item can be the deck row; see DjWire.
            s_wire = new DjWire(WireUrl(s.ContextUrl), s.Facts.NoRepeatContext, s.Facts.NoRepeatTrack, s.Facts.NoShuffle, page, track, metadata);
            s_wireSession = s;
            s_wireItem = deckItemId;
            s_wirePages = s.Pages.Count;
            s_wireRenewals = s.Renewals;
            return s_wire;
        }

        /// <summary>A map's entries a PUT may carry as they came (<see cref="Queue.ContextMetadataValueSound"/>: no binary decoded as
        /// text), in a stable order (by key).</summary>
        static KeyValuePair<string, string>[] SoundEntries(IReadOnlyDictionary<string, string> map)
        {
            var kept = new List<KeyValuePair<string, string>>(map.Count);
            foreach (KeyValuePair<string, string> entry in map)
                if (Queue.ContextMetadataValueSound(entry.Value)) kept.Add(entry);
            kept.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
            return kept.ToArray();
        }

        /// <summary>The context url as a DJ player publishes it (f3): the lexicon url WITHOUT its <c>reason</c> (dj-protocol §2.1: "PutState
        /// keeps the URL without reason"), whether the play named it bare (a local start, the official controller) or with one. PURE.</summary>
        public static string WireUrl(string url)
        {
            int query = url.IndexOf('?');
            if (query < 0) return url;
            int at = url.IndexOf("reason=", query + 1, StringComparison.Ordinal);
            while (at > 0 && url[at - 1] is not ('?' or '&')) at = url.IndexOf("reason=", at + 1, StringComparison.Ordinal);
            if (at < 0) return url;
            int end = url.IndexOf('&', at);
            // "?reason=x&a" → "?a", "&reason=x&a" → "&a", "…&reason=x" → "…", "?reason=x" → ""
            string head = url[..(url[at - 1] == '&' ? at - 1 : at)];
            return end < 0 ? (head.EndsWith('?') ? head[..^1] : head) : head + (url[at - 1] == '&' ? url[end..] : url[(end + 1)..]);
        }

        // ── another device's DJ, mirrored (Wavee as the controller) ──────────────────────────────────────────────────

        /// <summary>The toggles a foreign owner's cluster forbids (<c>ClusterDelta</c>'s 8 / 9 / 10 reasons) and why, in the words a
        /// local DJ's locks use (<c>dj-disallow</c> → <see cref="LockReason.Dj"/>, "Not available with DJ"). Allocation-free; UI thread.</summary>
        internal static ToggleLock LocksOf(ClusterBuffer buffer, in Spotify.Decode.ClusterDelta d, out LockReason reason)
        {
            ReadOnlySpan<byte> repeatContext = buffer.Utf8(d.NoRepeatContextReason), repeatTrack = buffer.Utf8(d.NoRepeatTrackReason),
                shuffle = buffer.Utf8(d.NoShuffleReason);
            ToggleLock locks = (shuffle.IsEmpty ? ToggleLock.None : ToggleLock.Shuffle)
                               | (repeatContext.IsEmpty ? ToggleLock.None : ToggleLock.RepeatContext)
                               | (repeatTrack.IsEmpty ? ToggleLock.None : ToggleLock.RepeatTrack);
            ReadOnlySpan<byte> first = !shuffle.IsEmpty ? shuffle : !repeatContext.IsEmpty ? repeatContext : repeatTrack;
            reason = first.IsEmpty ? LockReason.None : first.SequenceEqual("dj-disallow"u8) ? LockReason.Dj : LockReason.Other;
            return locks;
        }

        /// <summary>A foreign owner's DJ narration: <see cref="Active"/> while its current item is one (<c>is_narration</c>), its
        /// <see cref="Slot"/> (<c>welcome</c>, <c>intro</c>, <c>jump</c>, <c>outro</c>, from the provider <c>narration/&lt;slot&gt;</c>)
        /// and its <see cref="Ssml"/> when the cluster carries it ("" otherwise).</summary>
        public readonly record struct NarrationFacts(bool Active, string Slot, string Ssml)
        {
            /// <summary>The words, for a caption (<see cref="CaptionText"/>).</summary>
            public string Text => CaptionText(Ssml ?? "");
        }

        /// <summary>The foreign owner's narration, for the caption surfaces (the narration lanes draw it; the reducer's
        /// <see cref="State.RemoteNarrating"/> is the same fact as a flag). Default while none plays.</summary>
        public static readonly Signal<NarrationFacts> RemoteNarration = new(default) { DebugName = "playback.dj.remoteNarration" };

        static ulong s_narrationHash;

        /// <summary>The owner's cluster (null: no foreign owner any more): its narration's slot and caption, rebuilt only when the item
        /// moved. The SSML comes from the item's <c>media.manifest</c> (the TTS request, field 1.1.4.2), else from what the session said
        /// beside it: the context's <c>narration.welcome.ssml</c> for the welcome, the target row's <c>narration.&lt;slot&gt;.ssml</c> for the
        /// others (the row in next_tracks whose id is the item's). UI thread.</summary>
        internal static void NoteRemoteNarration(ClusterBuffer? buffer, in Spotify.Decode.ClusterDelta d)
        {
            if (buffer is null || !d.HasTrack || !d.Track.Narration)
            {
                if (s_narrationHash != 0) { s_narrationHash = 0; RemoteNarration.Value = default; Log.Info("dj", "the owner's DJ narration ended"); }
                return;
            }
            ReadOnlySpan<byte> uri = buffer.Utf8(d.Track.Uri), manifest = buffer.Utf8(d.Track.Manifest), provider = buffer.Utf8(d.Track.Provider);
            ulong hash = ((DeviceHash(uri) * 31UL) + DeviceHash(manifest)) * 31UL + DeviceHash(provider) + 1UL;
            if (hash == s_narrationHash) return;
            s_narrationHash = hash;
            string slot = provider.StartsWith("narration/"u8) ? Encoding.UTF8.GetString(provider["narration/"u8.Length..]) : "";
            string ssml = SsmlOfManifest(manifest);
            string from = ssml.Length > 0 ? "manifest" : "";
            if (ssml.Length == 0 && slot == "welcome")
                foreach (ref readonly Spotify.Decode.ClusterMeta m in buffer.Meta(d.MetaStart, d.MetaCount))
                    if (buffer.Utf8(m.Key).SequenceEqual("narration.welcome.ssml"u8)) { ssml = Encoding.UTF8.GetString(buffer.Utf8(m.Value)); from = "context"; break; }
            if (ssml.Length == 0 && slot.Length > 0)
            {
                ReadOnlySpan<byte> id = uri[(uri.LastIndexOf((byte)':') + 1)..];
                byte[] key = Encoding.UTF8.GetBytes("narration." + slot + ".ssml");
                foreach (ref readonly ClusterTrack row in buffer.Tracks(d.NextStart, d.NextCount))
                {
                    ReadOnlySpan<byte> rowUri = buffer.Utf8(row.Uri);
                    if (!rowUri[(rowUri.LastIndexOf((byte)':') + 1)..].SequenceEqual(id)) continue;
                    for (var r = new Spotify.Decode.ProtoReader(buffer.Utf8(row.Raw)); r.Next();)
                    {
                        if (r.Field != 3 || r.Wire != 2) { r.Skip(); continue; }
                        r.Message().Fields(1, 2, out var k, out var v);
                        if (k.SequenceEqual(key)) { ssml = Encoding.UTF8.GetString(v); from = "target row"; break; }
                    }
                    if (ssml.Length > 0) break;
                }
            }
            RemoteNarration.Value = new NarrationFacts(true, slot, ssml);
            Log.Info("dj", "the owner's DJ narrates: slot=" + (slot.Length > 0 ? slot : "?") + " caption=" + (ssml.Length > 0 ? "from the " + from : "none"));
        }

        /// <summary>A narration item's <c>media.manifest</c> (base64 of <c>{1 {1 {1 "POST", 3 path, 4 {2 ssml, …}, 5 1}, 5 {…}}}</c>,
        /// dj-protocol §2.4) → its SSML, or "". PURE.</summary>
        public static string SsmlOfManifest(ReadOnlySpan<byte> base64)
        {
            if (base64.IsEmpty) return "";
            byte[] bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(base64.Length);
            try
            {
                if (System.Buffers.Text.Base64.DecodeFromUtf8(base64, bytes, out _, out int written) != System.Buffers.OperationStatus.Done) return "";
                ReadOnlySpan<byte> request = new Spotify.Decode.ProtoReader(new Spotify.Decode.ProtoReader(bytes.AsSpan(0, written)).Bytes(1)).Bytes(1);
                ReadOnlySpan<byte> body = new Spotify.Decode.ProtoReader(request).Bytes(4);
                ReadOnlySpan<byte> ssml = new Spotify.Decode.ProtoReader(body).Bytes(2);
                return ssml.IsEmpty ? "" : Encoding.UTF8.GetString(ssml);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException) { return ""; }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(bytes); }
        }

        /// <summary>SSML → the words a caption shows: the tags dropped (an <c>&lt;entity&gt;</c>'s name kept), the entities decoded, the
        /// white space folded. The caption lanes may link the entity names themselves from the SSML. PURE.</summary>
        public static string CaptionText(string ssml)
        {
            if (ssml.Length == 0) return "";
            var text = new StringBuilder(ssml.Length);
            bool tag = false, space = false;
            foreach (char ch in ssml)
            {
                if (ch == '<') { tag = true; continue; }
                if (tag) { if (ch == '>') tag = false; continue; }
                if (char.IsWhiteSpace(ch)) { space = text.Length > 0; continue; }
                if (space) { text.Append(' '); space = false; }
                text.Append(ch);
            }
            return System.Net.WebUtility.HtmlDecode(text.ToString());
        }

        /// <summary>The lexicon answers come without the network (<c>--fake</c>): a DJ restore need not wait for the session to be online.</summary>
        internal static bool AnswersOffline => s_fake is not null;

        /// <summary>How a DJ play began (<see cref="Session.Start"/>).</summary>
        public enum DjStart : byte { Play, Transfer, Restore }

        /// <summary>The lexicon url a deck on <paramref name="context"/> plays from, for the session document (the launch restore resolves it
        /// again): the session's while one plays it, else the list's attribute; "" for any other context. UI thread.</summary>
        public static string UrlFor(EntityId context) => Of(context) is { } s ? WireUrl(s.ContextUrl) : ListUrl(context);

        // ── the DJ list's own facts ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>The lexicon url <paramref name="list"/> plays from (its <c>lexicon_context_url</c> attribute), or "" for a list that is
        /// not a DJ (or not known). UI thread.</summary>
        public static string ListUrl(EntityId list)
        {
            if (list.Kind != EntityKind.Playlist || Entities.Current is not { } scope || !scope.Playlists.TryGetSlot(list, out int slot)) return "";
            StringId url = new Playlist(slot).LexiconUrlId;
            return url.IsEmpty ? "" : Entities.Strings.Resolve(url);
        }

        /// <summary>A DJ list's format attributes as the JSON object the decoder kept (<see cref="PlaylistTable.DjAttributes"/>), or "".
        /// UI thread.</summary>
        public static string ListMetadataJson(EntityId list)
        {
            if (list.Kind != EntityKind.Playlist || Entities.Current is not { } scope || !scope.Playlists.TryGetSlot(list, out int slot)) return "";
            StringId json = new Playlist(slot).DjAttributesId;
            return json.IsEmpty ? "" : Entities.Strings.Resolve(json);
        }

        /// <summary>A DJ start to another device (plan-dj §2.7, dj-protocol §1.2's S − 9.32): the list's lexicon url, its format attributes
        /// with <c>context_description</c> (the list's name) in key order, a fresh session id. Null for a list that plays no lexicon session.
        /// UI thread.</summary>
        internal static Spotify.Connect.DjPlay? PlayOf(EntityId list)
        {
            string url = ListUrl(list);
            if (!IsLexicon(url) || Entities.Current is not { } scope || !scope.Playlists.TryGetSlot(list, out int slot)) return null;
            var p = new Playlist(slot);
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in Entries(ListMetadataJson(list))) metadata[entry.Key] = entry.Value;
            // A list whose attributes were not kept whole (an older read) still says what it is and where it plays from.
            if (p.IsDj) metadata.TryAdd("lexicon_set_type", "your_dj");
            metadata.TryAdd("lexicon_context_url", url);
            string name = p.TitleId.IsEmpty ? "" : Entities.Strings.Resolve(p.TitleId);
            metadata["context_description"] = name.Length > 0 ? name : "DJ";
            var entries = new KeyValuePair<string, string>[metadata.Count];
            int n = 0;
            foreach (KeyValuePair<string, string> entry in metadata) entries[n++] = entry;
            Array.Sort(entries, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
            return new Spotify.Connect.DjPlay(url, entries, Spotify.Connect.NewSessionId());
        }

        /// <summary>A JSON object of strings (<see cref="ListMetadataJson"/>) → its entries, in order; a value that is not a string is
        /// skipped, a body that is not an object answers none. PURE.</summary>
        public static KeyValuePair<string, string>[] Entries(string json)
        {
            if (json.Length == 0) return [];
            var entries = new List<KeyValuePair<string, string>>();
            try
            {
                var r = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return [];
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    string key = r.GetString() ?? "";
                    r.Read();
                    if (r.TokenType == JsonTokenType.String) entries.Add(new(key, r.GetString() ?? ""));
                    else r.Skip();
                }
            }
            catch (JsonException) { /* what was read stands */ }
            return entries.ToArray();
        }
    }
}
