// ── Spotify/Spotify.Decode.Show.cs — D2: the show's OWN facts beside `ShowV4` ─────────────────────────────────────
//
// Role: CORE
// Owner: D2 (plan §4.3, ledger row 10)
//
// Two decoders `ShowV4` (Spotify.Decode.cs) does not carry, because they never ride `metadata.Show` at all:
//
//   ShowHeaderAttributes — `GET playlist/v2/show/{id}` answers a `SelectedListContent` whose list-header ATTRIBUTES
//   carry `is_audiobook` / `autoplay_candidate` as plain string key/value pairs (the official capture:
//   `is_audiobook=true`, `autoplay_candidate=false`, `end_of_list_action=STOP`) — the exact shape
//   `Spotify.Decode.Playlist.cs`'s `PlaylistFormatAttributes` already reads for a playlist's daylist/chart/tuning
//   facts (field 3 = attributes, field 12 = repeated {1:key, 2:value}), ported here rather than shared because a
//   show's classification is its own decoder, not a playlist one, and that file is out of this ticket's ownership.
//   A show's `ShowV4` (kind 11, field 89) can answer a cached 304 with no payload behind it — the capture that proved
//   this route showed exactly that — so the membership read is the one place that can classify a book cold.
//
//   AudiobookGenres — extended-metadata kind 83 (`spotify.audiobookgenres.AudiobookGenres`, an `Any` payload):
//   repeated `{1:id, 2:name, 3:level, 4:parentId, 6:name}` entries → the show's `Topics` column, the same slot
//   `PodcastTopics` (kind 3) fills for an ordinary podcast — an audiobook has no kind-3 answer, so reusing the
//   column rather than adding a second one keeps every reader that already shows `Show.TopicsId` working unchanged.
//   NOT wired into `Spotify.Decode.cs`'s `Extension` dispatch: that switch is shared across every decode owner, and
//   this ticket's file list does not include it outside the `ShowV4`/`ThinShow` functions. A one-line follow-up —
//   `case (Ext)83: AudiobookGenres(payload, entityUri, s); break;` — wires it in.
//
// Both stage OUTSIDE the ordinary Facts/Topics authority ladder where the wire cannot state the WHOLE group: see
// `StagedShow.HeaderFacts` (Entities/Show.cs) for why the header route is a pure OR rather than a group claim.

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>A show's OWN list-header attributes (`playlist/v2/show/{id}`'s `SelectedListContent.attributes.
        /// format_attributes`) → <see cref="StagedShow.HeaderFacts"/>: <see cref="ShowFlags.Audiobook"/> when
        /// `is_audiobook=true`, <see cref="ShowFlags.NoAutoplay"/> when `autoplay_candidate=false`. A diff answer (no
        /// `ListAttributes` this time — the header did not change) states nothing, so nothing is staged; an answer
        /// that names neither key states nothing either. Bits only ever turn ON here — see
        /// <see cref="StagedShow.HeaderFacts"/>'s remark for why a `false`/absent key never clears the sibling bit.</summary>
        public static void ShowHeaderAttributes(ReadOnlySpan<byte> selectedListContent, ReadOnlySpan<byte> showUri, Staging s)
        {
            var id = Identity(s, showUri);
            if (id.IsEmpty) return;

            ReadOnlySpan<byte> attributes = default;
            for (var r = new ProtoReader(selectedListContent); r.Next();)
            {
                if (r.Field == 3 && r.Wire == 2) attributes = r.Bytes();
                else r.Skip();
            }
            if (attributes.IsEmpty) return;

            uint flags = 0;
            for (var a = new ProtoReader(attributes); a.Next();)
            {
                if (a.Field != 12 || a.Wire != 2) { a.Skip(); continue; }
                a.Message().Fields(1, 2, out var key, out var value);
                if (key.IsEmpty || value.IsEmpty) continue;
                if (Is(key, "is_audiobook") && Is(value, "true")) flags |= (uint)ShowFlags.Audiobook;
                else if (Is(key, "autoplay_candidate") && Is(value, "false")) flags |= (uint)ShowFlags.NoAutoplay;
            }
            if (flags == 0) return;

            ref var row = ref s.Shows.RowFor(id, Authority.Thin, 0);
            row.HeaderFacts = flags;
            s.Shows.Settle();
        }

        /// <summary>Extended-metadata kind 83 (`spotify.audiobookgenres.AudiobookGenres`, an `Any` payload) → the
        /// show's <see cref="ShowFields.Topics"/>, the same column <c>PodcastTopics</c> (kind 3) fills for an
        /// ordinary podcast. Each repeated entry is `{1:id, 2:name, 3:level, 4:parentId, 6:name}`; field 6, when
        /// present, is the display name the client shows (field 2 otherwise) — both are read defensively since the
        /// capture that proved this kind exists did not exercise every entry shape. Names join the same way
        /// `PodcastTopics` joins its titles (one per line) so every reader of <see cref="Show.TopicsId"/> needs no
        /// audiobook/podcast branch. NOT wired into `Extension`'s dispatch — see the file header.</summary>
        public static void AudiobookGenres(ReadOnlySpan<byte> anyPayload, ReadOnlySpan<byte> showUri, Staging s)
        {
            if (!showUri.StartsWith("spotify:show:"u8)) return;
            var names = new List<string>();
            for (var r = new ProtoReader(anyPayload); r.Next();)
            {
                if (r.Field != 1 || r.Wire != 2) { r.Skip(); continue; }
                ReadOnlySpan<byte> name = default, fallback = default;
                for (var e = r.Message(); e.Next();)
                {
                    if (e.Field == 6 && e.Wire == 2) name = e.Bytes();
                    else if (e.Field == 2 && e.Wire == 2) fallback = e.Bytes();
                    else e.Skip();
                }
                ReadOnlySpan<byte> chosen = name.IsEmpty ? fallback : name;
                if (!chosen.IsEmpty) names.Add(System.Text.Encoding.UTF8.GetString(chosen));
            }
            if (names.Count == 0) return;

            ref var row = ref s.Shows.RowFor(Identity(s, showUri), Authority.Full, (uint)ShowFields.Topics);
            row.Topics = s.AddText(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', names)));
            s.Shows.Settle();
        }
    }
}
