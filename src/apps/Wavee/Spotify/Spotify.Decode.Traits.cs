// ── Spotify/Spotify.Decode.Traits.cs ────────────────────────────────────────────────────────────────────────────────
// the five extension kinds the Api asked for and the decoder dropped (gap batch B1, G-044)
//
// Role: CORE
// Owner: E
// Wave: gap batch B1
// Budget: 330 lines
// Spec: gap register G-044 — a named partial of Spotify.Decode.cs, whose 1,600 budget it would pass. (G-045's JSON profile
// arm is gone: the REST profile is the protobuf view, Spotify.Decode.Profile.cs.)
//
// `Api.UserProfiles`, `Api.TrackCredits`, `Api.AlbumRecommendations` and `Api.TrackExpansion` POSTed kinds 15, 186,
// 151, 98 and 237 and `Decode.Extension` fell through its `default:` for every one of them, so each answered with
// nothing staged. These are the projectors 0.2.9 carried for them (`UserProfilePayloadDecoder`, the credits reader,
// `SpotifyTrackExpansionService.MapWaveform` / `CollectTargets`), rewritten in this file's one shape: pure over a span,
// no allocation after warm-up, rows and runs into a `Staging`, the commit's business after that.
//
// Where they land (Entities/Edges.cs §6/§8): a user row's Identity; `Edges.TrackCredits`, `Edges.TrackVersions`,
// `Edges.TrackWaveform` (kept as `WaveSample` triples, `Entities/Waveform.cs`) and `Edges.AlbumRecommendations`, each
// through a `StagedTraitRun` — whole, Complete, EMPTY INCLUDED, because "this track has no credits" is the answer that
// stops the drawer asking (finding 27's rule).

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>0.2.9's cap on "playlists featuring this album" (<c>Take(12)</c>).</summary>
        public const int MaxRecommendedPlaylists = 12;

        // ── kind 15: the user profile ────────────────────────────────────────────────────────────────────────────────

        /// <summary>Kind 15 (<c>USER_PROFILE</c>) → the user row's <see cref="UserFields.Identity"/>: display name, the
        /// largest avatar and the brand colour.
        ///
        /// <para><b>The body is protobuf, not JSON</b> — captured 2026-08-30 over five accounts: every scalar in a
        /// wrapper, <c>1:{1:username} 2:{1:name} 3:[{1:w, 2:h, 3:url}] 11:{1:0xRRGGBB}</c> (field 11 is the avatar COLOUR,
        /// not a follower count — research §4). The WinUI-era client fed it to a JSON parser, swallowed the exception and
        /// fell back to REST for EVERY owner, so the batch arm never answered once.</para>
        ///
        /// <para>The IDENTITY is the envelope's uri — the one the caller asked with — and never the payload's username
        /// (0.2.9 <c>ToOwner</c>): a profile answered for <c>spotify:user:Foo</c> must land on the row that asked.</para></summary>
        public static void UserProfile(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> entityUri, Staging s)
        {
            if (entityUri.IsEmpty || payload.IsEmpty) return;
            var r = new ProtoReader(payload);
            ReadOnlySpan<byte> name = default, url = default;
            uint color = 0;
            long bestArea = -1;
            bool any = false;
            while (r.Next())
            {
                if (r.Wire != 2) { r.Skip(); continue; }
                switch (r.Field)
                {
                    case 1: r.Skip(); any = true; break;                    // { 1: username } — never the identity
                    case 2: name = r.Message().Bytes(1); any = true; break;  // { 1: display name }
                    case 3:                                                  // { 1: w, 2: h, 3: url }, repeated
                        {
                            var image = r.Message();
                            long w = 0, h = 0;
                            ReadOnlySpan<byte> u = default;
                            while (image.Next())
                            {
                                if (image.Field == 1 && image.Wire == 0) w = (long)image.Varint();
                                else if (image.Field == 2 && image.Wire == 0) h = (long)image.Varint();
                                else if (image.Field == 3 && image.Wire == 2) u = image.Bytes();
                                else image.Skip();
                            }
                            any = true;
                            if (!u.IsEmpty && w * h > bestArea) { bestArea = w * h; url = u; }
                            break;
                        }
                    case 11: color = RgbColor((ulong)r.Message().Varint(1)); any = true; break;   // { 1: 0xRRGGBB } (research §4)
                    default: r.Skip(); break;
                }
            }
            // Bytes that merely START like a tag and name none of the three fields are not a profile (0.2.9's rule):
            // staging an empty Identity for them would seal the row with no name.
            if (!any) return;
            StageProfile(s, entityUri, name, url, color);
        }

        static void StageProfile(Staging s, ReadOnlySpan<byte> entityUri, ReadOnlySpan<byte> name, ReadOnlySpan<byte> url,
                                 uint color)
        {
            ref var row = ref s.Users.RowFor(Identity(s, entityUri), Authority.Full, (uint)UserFields.Identity);
            row.Name = s.AddText(name);
            row.Image = s.AddText(url);
            row.Color = color;
            s.Users.Settle();
        }

        // ── kind 186: credits ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Kind 186 (<c>CREDITS_V2_TRAIT</c>) → <c>Edges.TrackCredits</c>: every row in the server's own grouped,
        /// ordered sequence — name, role, group heading, and the credited artist when there is one (an unlinked
        /// contributor has none and keeps its row). <c>credits_v2_trait.proto</c>: <c>CreditsTrait{ rows=1: CreditRow{
        /// name=1, role=2, artist_uri=3, nav=4, songwriter_url=5, group=6{ name=1 } }, label=2 }</c>.</summary>
        public static void Credits(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var track = Identity(s, entityUri);
            if (track.IsEmpty) return;
            int start = s.Traits.Count;
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                if (r.Field != 1 || r.Wire != 2) { r.Skip(); continue; }
                var row = r.Message();
                ReadOnlySpan<byte> name = default, role = default, artist = default, nav = default, group = default;
                while (row.Next())
                {
                    switch (row.Field)
                    {
                        case 1: name = row.Bytes(); break;
                        case 2: role = row.Bytes(); break;
                        case 3: artist = row.Bytes(); break;
                        case 4: nav = row.Bytes(); break;
                        case 6: group = row.Message().Bytes(1); break;
                        default: row.Skip(); break;
                    }
                }
                if (name.IsEmpty) continue;                       // a row that names nobody is not a credit
                ref var member = ref s.Traits.Add();
                member.Name = s.AddText(name);
                member.Role = s.AddText(role);
                member.Group = s.AddText(group);
                // `artist_uri` is the link; `nav` is the client's own spelling of the same target and stands in for it.
                var link = artist.IsEmpty ? nav : artist;
                if (EntityUri.KindOf(link) == EntityKind.Artist) member.Target = Identity(s, link);
            }
            CloseTrait(s, in track, TraitRelation.TrackCredits, start);
        }

        // ── kind 151: recommended playlists ──────────────────────────────────────────────────────────────────────────

        /// <summary>Kind 151 → <c>Edges.AlbumRecommendations</c>: "playlists featuring this album", at most
        /// <see cref="MaxRecommendedPlaylists"/>, playlists only. <c>RecommendedPlaylists{ recommendation=1{ uri=1 } }</c>.</summary>
        public static void RecommendedPlaylists(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var album = Identity(s, entityUri);
            if (album.IsEmpty) return;
            int start = s.Traits.Count;
            var r = new ProtoReader(proto);
            while (r.Next() && s.Traits.Count - start < MaxRecommendedPlaylists)
            {
                if (r.Field != 1 || r.Wire != 2) { r.Skip(); continue; }
                var uri = r.Message().Bytes(1);
                if (EntityUri.KindOf(uri) != EntityKind.Playlist) continue;
                s.Traits.Add().Target = Identity(s, uri);
            }
            CloseTrait(s, in album, TraitRelation.AlbumRecommendations, start);
        }

        // ── kind 98: the audio counterpart ───────────────────────────────────────────────────────────────────────────

        /// <summary>Kind 98 (<c>AUDIO_ASSOCIATIONS</c>) → <c>Edges.TrackVersions</c> with <see cref="TrackVersionKind.Audio"/>:
        /// a video rendition's AUDIO recording. The message shape is kind 99's (<c>association=1{ associated_uri=1 }</c>);
        /// only a TRACK counterpart is a version — any other kind is a payload no drawer row can open (0.2.9
        /// <c>CollectTargets</c>). The video direction is kind 99's own column (<c>TrackTable.VideoCounterpart</c>).</summary>
        public static void AudioAssociations(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var track = Identity(s, entityUri);
            if (track.IsEmpty) return;
            int start = s.Traits.Count;
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                if (r.Field != 1 || r.Wire != 2) { r.Skip(); continue; }
                var uri = r.Message().Bytes(1);
                if (EntityUri.KindOf(uri) != EntityKind.Track) continue;
                ref var member = ref s.Traits.Add();
                member.Target = Identity(s, uri);
                member.B0 = (byte)TrackVersionKind.Audio;
            }
            CloseTrait(s, in track, TraitRelation.TrackVersions, start);
        }

        // ── kind 237: the three-band waveform ────────────────────────────────────────────────────────────────────────

        /// <summary>Kind 237, THREE-BAND WAVEFORM: f3/f4/f5 are the low/mid/high byte envelopes (f1 = 44100 and f2 = the 20 ms
        /// hop are skipped — constant on every observed answer). Kept as N <see cref="WaveSample"/> triples, N = the longest
        /// band capped at <see cref="WaveformBands.MaxSamples"/> (a longer answer is MAX-POOLED onto it, never rejected — V-D9;
        /// the shorter bands are folded onto the same axis, each across its OWN length — the wire ships <c>band_low</c> longer
        /// than the other two, 12,886 vs 12,466 on the reference track, so one cursor over all three would drift ~8 s by the
        /// end); the edge's Count and Total are N. Silence (no bands, or every byte 0 — V-D6) stages an EMPTY run: a Complete
        /// "no waveform" answer.</summary>
        public static void Waveform(ReadOnlySpan<byte> proto, ReadOnlySpan<byte> entityUri, Staging s)
        {
            var track = Identity(s, entityUri);
            if (track.IsEmpty) return;
            ReadOnlySpan<byte> low = default, mid = default, high = default;
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                if (r.Wire != 2) { r.Skip(); continue; }
                switch (r.Field)
                {
                    case 3: low = r.Bytes(); break;
                    case 4: mid = r.Bytes(); break;
                    case 5: high = r.Bytes(); break;
                    default: r.Skip(); break;
                }
            }
            ref var run = ref s.TraitRuns.Add();
            run.Parent = track;
            run.Relation = TraitRelation.TrackWaveform;
            run.Start = s.Traits.Count;
            run.Length = 0;
            int n = Math.Min(WaveformBands.MaxSamples, Math.Max(low.Length, Math.Max(mid.Length, high.Length)));
            if (n == 0) return;                                              // no bands: an empty waveform is the answer
            byte[] scratch = System.Buffers.ArrayPool<byte>.Shared.Rent(3 * n);
            try
            {
                Span<byte> bytes = scratch.AsSpan(0, 3 * n);
                Span<WaveSample> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, WaveSample>(bytes);
                for (int i = 0; i < n; i++)
                    samples[i] = new WaveSample(WaveformBands.Fold(low, i, n), WaveformBands.Fold(mid, i, n), WaveformBands.Fold(high, i, n));
                if (bytes.IndexOfAnyExcept((byte)0) < 0) return;           // silence: every sample 0 — stage NOTHING (DecodeTests)
                run.Bytes = s.AddText(bytes);
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(scratch); }
        }

        /// <summary>Close a trait run over the members appended since <paramref name="start"/> — even when there are
        /// none, because an empty trait is an answer (the file header).</summary>
        static void CloseTrait(Staging s, in StagedId parent, TraitRelation relation, int start)
        {
            ref var run = ref s.TraitRuns.Add();
            run.Parent = parent;
            run.Relation = relation;
            run.Start = start;
            run.Length = s.Traits.Count - start;
        }
    }
}
