// ── Entities/Entities.Fake.Mix.cs — FAKE (#48 Mix, plan §3.3 "--fake") ─────────────────────────────────────────────────
//
// One mixed playlist for --fake and the inspector, shaped like a captured mixed list (report §3.1, §3.3): owned by
// the account, the mix lens applied, ten mixable rows whose last pair runs into a LOCAL file (not-mixable, reason
// entity-kind), seven stored recipes (Rise, a beatmatched Slam with an EQ override carrying fields this build does not know,
// preset 19 at 2 bars, Wave at 4 bars, a "no transition", a Fade and a Dissolve), synthetic kind-217 grids from each
// track's seeded tempo and kind-28 cue points. Every value is made up here. Route: pl:spotify:playlist:plmix.

using System.Globalization;

namespace Wavee;

public static partial class Entities
{
    internal static partial class LibrarySeed
    {
        public const string MixUri = "spotify:playlist:plmix";
        public const string MixLensEtag = "0000beef";

        /// <summary>The pool tracks of the mixed playlist, in order: matched tempos, two far-apart pairs (row 2 → 3 runs
        /// 120 → 77 BPM, row 4 → 5 78 → 122), then a local file as the last row.</summary>
        static readonly int[] s_mixPool = [47, 48, 50, 7, 8, 52, 54, 55, 58, 60];

        static void StageMix(Staging s)
        {
            ref var row = ref s.Playlists.RowFor(Text(s, MixUri), Authority.Seed,
                (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities | PlaylistFields.Visibility | PlaylistFields.Mix
                       | PlaylistFields.MixKinds));
            row.Title = Text(s, "Late Night Drive");
            row.Description = Text(s, "Ten songs that run into each other.");
            row.Image = Text(s, Cover(7));
            row.OwnerUri = Text(s, UserUri(0));
            row.Caps = OwnerCaps;
            row.TrackCount = s_mixPool.Length + 1;
            row.Flags = row.FlagsMask = (uint)PlaylistFlags.Public;
            row.MixFlags = (uint)(PlaylistMixFlags.ListMix | PlaylistMixFlags.AllowCustom | PlaylistMixFlags.HasCustom
                                  | PlaylistMixFlags.CanViewTransition | PlaylistMixFlags.IgnoreSetting
                                  | PlaylistMixFlags.AutomixQueue | PlaylistMixFlags.AutomixAutoplay | PlaylistMixFlags.LensApplied
                                  | PlaylistMixFlags.XmMixState | PlaylistMixFlags.XmAutoLens | PlaylistMixFlags.XmMixable);
            row.MixFlagsMask = uint.MaxValue;
            row.MixLensEtag = Text(s, MixLensEtag);
        }

        static void LandMix(long now0)
        {
            var p = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, MixUri));
            if (!p.IsValid) return;
            int me = Current.MeSlot;
            int n = s_mixPool.Length;
            Span<int> targets = stackalloc int[n + 1];
            Span<PlaylistTrackEdge> edges = stackalloc PlaylistTrackEdge[n + 1];
            for (int k = 0; k < n; k++)
            {
                targets[k] = SlotOf(EntityKind.Track, TrackUri(s_mixPool[k]));
                edges[k] = new PlaylistTrackEdge(ItemIdFor(200, k), (int)(now0 - k * 5 * Hour), me, 0, 0, 0, PlaylistItemFlags.Mixable);
            }
            targets[n] = SlotOf(EntityKind.Track, LocalTrackUri(0));
            edges[n] = new PlaylistTrackEdge(ItemIdFor(200, n), (int)(now0 - n * 5 * Hour), me, 0, 0, 0, PlaylistItemFlags.NotMixable);
            Land(p, targets, edges);

            for (int k = 0; k < n; k++) SeedAnalysis(targets[k], k);

            // Seven edited pairs, every byte made up here (no captured recipe); 2 → 3 (the far-apart pair) and 8 → 9 stay Auto,
            // 9 → 10 runs into the local file:
            //   0 → 1  Rise (preset 3), as Wavee writes a recipe of its own: a 3000 ms window ending 1 ms before row 0's file does.
            //   1 → 2  Slam (11), beatmatched over 2 bars with an EQ override to style 15, written field by field as another client
            //          would: a curve override and a field this build does not know at every level, which a decode → encode must keep.
            //   3 → 4  preset 19 (internal), beatmatched over 2 bars.
            //   4 → 5  Fade (1), not beatmatched, a 4.2 s overlap.
            //   5 → 6  "no transition": no preset id, a 1 ms overlap, no out-point (A plays to its end, B from its start).
            //   6 → 7  Wave (17), beatmatched over 4 bars.
            //   7 → 8  Dissolve (20), not beatmatched: an echo the engine cannot render yet, into a row with no cue candidates.
            var stored = new Dictionary<ulong, MixTransition>();
            var rise = Unmatched(0, targets, MixPresetId.Rise, 1, 3_000);
            Store(stored, in rise, MixRecipe.EncodeToArray(in rise, default), 0);
            Store(stored, BeatmatchedRaw(targets), 1);
            var preset19 = Matched(3, targets, MixPresetId.LowPassFilterFadeInOut, 2);
            Store(stored, in preset19, MixRecipe.EncodeToArray(in preset19, default), 3);
            var fade = Unmatched(4, targets, MixPresetId.Fade, 1_300, 4_200);
            Store(stored, in fade, MixRecipe.EncodeToArray(in fade, default), 4);
            var none = PairRecipe(5, targets);
            none.OverlapMs = 1;
            none.PresetType = MixPresetType.Fade;
            none.IsPresetIdOverride = true;
            Store(stored, in none, MixRecipe.EncodeToArray(in none, default), 5);
            var wave = Matched(6, targets, MixPresetId.Wave, 4);
            Store(stored, in wave, MixRecipe.EncodeToArray(in wave, default), 6);
            var dissolve = Unmatched(7, targets, MixPresetId.Dissolve, 2_400, 6_000);
            Store(stored, in dissolve, MixRecipe.EncodeToArray(in dissolve, default), 7);
            MixData.Replace(p.Slot, stored);

            // The signal templates a lens read carries: attribute 14 on every mixable row, the list's mix-state entry, and the
            // local file's not-mixable reason.
            var signals = new MixSignals();
            Span<byte> template = stackalloc byte[256];
            for (int k = 0; k < n; k++)
                signals.SetTransitionTemplate(FakeItemId(k), template[..SetTransitionTemplate(TrackUri(s_mixPool[k]), FakeItemId(k), template)]);
            signals.SetNotMixableReason(FakeItemId(n), "entity-kind");
            var w = new Spotify.Decode.ProtoWriter(template);
            w.Utf8Always(1, "mix-state"u8);
            int args = w.Open(2);
            w.Utf8Always(1, "mix"u8);
            w.Close(args);
            signals.MixState = template[..w.Length].ToArray();
            MixData.ReplaceSignals(p.Slot, signals);

            // The recording server Mix's writes go to under --fake (Entities.Fake.MixServer.cs): it holds the list as Spotify would.
            FakeMixServer.Install();
            FakeMixServer.Adopt(p, MixLensEtag);
        }

        public const string MixWireUri = "spotify:playlist:plmixwire";

        /// <summary>`mix decode` (--fake): a made-up playlist read through the mix lens, run through the REAL decoder and commit
        /// (Api.PlaylistAnswer → Decode.MixList → CommitMix), so the dump of <see cref="MixWireUri"/> shows what the wire path keeps:
        /// three rows (an edited pair 0 → 1 with its recipe, a mixable row, a local file with not-mixable + not-mixable.reason),
        /// attribute 14 on the two mixable rows, and contents.5 holding a made-up signal and the mix-state template.</summary>
        internal static void LandMixWire()
        {
            if (Current is null) return;
            var body = MixWireBody();
            var s = Staging.Rent();
            try
            {
                Spotify.Api.PlaylistAnswer(body, MixWireUri, s);
                // Kind 28 answers for the two mixable rows (the same tracks as plmix's rows 0 and 1) through the real decoder:
                // candidate lists in confidence order, as the wire sorts them, whose smallest start and largest end candidate are the
                // ones s_mixCandidates seeds, so the decode lands what the seed holds.
                for (int k = 0; k < 2; k++)
                {
                    var t = new global::Wavee.Track(SlotOf(EntityKind.Track, TrackUri(s_mixPool[k])));
                    var (firstStart, endGap) = s_mixCandidates[k];
                    uint lastEnd = (uint)Math.Max(0, t.DurationMs - endGap);
                    Spotify.Decode.Cuepoints(CuepointsBody((uint)firstStart, lastEnd, t.Tempo / 10f, (uint)t.DurationMs),
                        System.Text.Encoding.UTF8.GetBytes(TrackUri(s_mixPool[k])), s);
                }
                Commit(s);
            }
            finally { Staging.Return(s); }
        }

        /// <summary>A made-up kind 28 <c>Cuepoints</c> answer: start and end cue, three start and three end candidates.</summary>
        static byte[] CuepointsBody(uint firstStart, uint lastEnd, float bpm, uint durationMs)
        {
            var buffer = new byte[512];
            var w = new Spotify.Decode.ProtoWriter(buffer);
            Cue(ref w, 1, firstStart + 8_000, bpm, 0.61);
            Cue(ref w, 2, lastEnd - 12_000, bpm, 0.55);
            Cue(ref w, 3, firstStart + 8_000, bpm, 0.61);
            Cue(ref w, 3, Math.Min(durationMs / 2, firstStart + 40_000), bpm, 0.18);
            Cue(ref w, 3, firstStart, bpm, 0.12);
            Cue(ref w, 4, lastEnd - 12_000, bpm, 0.55);
            Cue(ref w, 4, lastEnd, bpm, 0.21);
            Cue(ref w, 4, lastEnd - 30_000, bpm, 0.07);
            return buffer.AsSpan(0, w.Length).ToArray();

            static void Cue(ref Spotify.Decode.ProtoWriter w, int field, uint ms, float bpm, double confidence)
            {
                int m = w.Open(field);
                if (ms != 0) w.UAlways(1, ms);
                w.F32(2, bpm);
                w.UAlways(3, 1);
                w.F64(4, confidence);
                w.Close(m);
            }
        }

        static byte[] MixWireBody()
        {
            ReadOnlySpan<ulong> ids = [0x0BAD_C0DE_0000_0001UL, 0x0BAD_C0DE_0000_0002UL, 0x0BAD_C0DE_0000_0003UL];
            string[] uris = [TrackUri(s_mixPool[0]), TrackUri(s_mixPool[1]), LocalTrackUri(0)];
            var recipe = new MixRecipe
            {
                FromItemId = ids[0], ToItemId = ids[1], OutStartMs = 200_000, OverlapMs = 3_000, Preset = (byte)MixPresetId.Rise,
                PresetType = MixPresetType.Fade, TrackA = uris[0], TrackB = uris[1], PlayableA = uris[0], PlayableB = uris[1],
                SpeedA = 1f, SpeedB = 1f, ItemSpeedA = 1.0, ItemSpeedB = 1.0, IsOverlapOverride = true, IsPresetIdOverride = true,
            };
            string b64 = Convert.ToBase64String(MixRecipe.EncodeToArray(in recipe, default));
            var buffer = new byte[4096];
            var w = new Spotify.Decode.ProtoWriter(buffer);
            Span<byte> revision = stackalloc byte[24];
            revision[3] = 7;                                    // revision 7, an all-zero hash
            w.Utf8Always(1, revision);
            w.UAlways(2, 3);                                    // length
            int attributes = w.Open(3);
            w.Str(1, "Wire Mix");
            w.Entry(12, "mix", "true");
            w.Entry(12, "mix-type", "allow-custom");
            w.Entry(12, "can-view-transition", "true");
            w.Close(attributes);
            int contents = w.Open(5);
            Span<byte> id = stackalloc byte[8];
            Span<byte> template = stackalloc byte[256];
            for (int i = 0; i < 3; i++)
            {
                int item = w.Open(3);
                w.StrAlways(1, uris[i]);
                int attrs = w.Open(2);
                if (i < 2) w.Raw(template[..AttributeTemplate(uris[i], ids[i], template)]);
                if (i == 0)
                {
                    w.Entry(11, "automix.auto_transition_recipe", b64);
                    w.Entry(11, "automix.transition_uri", "spotify:transition:fakeWireTransition0000:1700000000000");
                }
                if (i == 2)
                {
                    w.Entry(11, "not-mixable", "true");
                    w.Entry(11, "not-mixable.reason", "entity-kind");
                }
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(id, ids[i]);
                w.Utf8Always(12, id);
                w.Close(attrs);
                w.Close(item);
            }
            int other = w.Open(5);                              // a list signal that is not Mix's
            w.Utf8Always(1, "made-up-signal"u8);
            int otherArgs = w.Open(2); w.Utf8Always(1, "made-up-lens"u8); w.Close(otherArgs);
            w.Close(other);
            int mixState = w.Open(5);
            w.Utf8Always(1, "mix-state"u8);
            int mixArgs = w.Open(2); w.Utf8Always(1, "mix"u8); w.Close(mixArgs);
            w.Close(mixState);
            w.Close(contents);
            int lens = w.Open(22);                              // the applied mix lens and its etag
            w.Utf8Always(1, "mix"u8);
            w.Utf8Always(2, "0badc0de"u8);
            w.Close(lens);
            return buffer.AsSpan(0, w.Length).ToArray();

            // Item attribute 14 as a whole field (tag included), the way the item's attribute message carries it.
            static int AttributeTemplate(string uri, ulong itemId, Span<byte> into)
            {
                Span<byte> value = stackalloc byte[200];
                int n = SetTransitionTemplate(uri, itemId, value);
                var w = new Spotify.Decode.ProtoWriter(into);
                w.Utf8Always(14, value[..n]);
                return w.Length;
            }
        }

        /// <summary>Item attribute 14 as a lens read carries it: <c>{1 "set-transition", 2 {1 "mix", 2 uri, 3 item id (8 bytes)}}</c>.</summary>
        static int SetTransitionTemplate(string uri, ulong itemId, Span<byte> into)
        {
            var w = new Spotify.Decode.ProtoWriter(into);
            w.Utf8Always(1, "set-transition"u8);
            int args = w.Open(2);
            w.Utf8Always(1, "mix"u8);
            w.StrAlways(2, uri);
            Span<byte> id = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(id, itemId);
            w.Utf8Always(3, id);
            w.Close(args);
            return w.Length;
        }

        static void Store(Dictionary<ulong, MixTransition> into, in MixRecipe recipe, byte[] raw, int k)
            => into[recipe.FromItemId] = new MixTransition(recipe, raw, Convert.ToBase64String(raw),
                "spotify:transition:fakeTransition0000000" + k.ToString(CultureInfo.InvariantCulture) + ":1700000000000");

        static void Store(Dictionary<ulong, MixTransition> into, byte[] raw, int k)
        {
            if (MixRecipe.TryDecode(raw, out var recipe)) Store(into, in recipe, raw, k);
        }

        /// <summary>The pair (row k → row k + 1)'s identity half of a recipe, in the official shape: item ids, uris (playable =
        /// catalogue here), analysis ids, the seeded tempos, speeds 1.0.</summary>
        static MixRecipe PairRecipe(int k, ReadOnlySpan<int> targets)
        {
            var a = new global::Wavee.Track(targets[k]);
            var b = new global::Wavee.Track(targets[k + 1]);
            return new MixRecipe
            {
                FromItemId = FakeItemId(k), ToItemId = FakeItemId(k + 1),
                TrackA = TrackUri(s_mixPool[k]), TrackB = TrackUri(s_mixPool[k + 1]),
                PlayableA = TrackUri(s_mixPool[k]), PlayableB = TrackUri(s_mixPool[k + 1]),
                BeatsIdA = FakeBeatsId(k), BeatsIdB = FakeBeatsId(k + 1),
                BpmA = a.Tempo / 10f, BpmB = b.Tempo / 10f,
                SpeedA = 1f, SpeedB = 1f, ItemSpeedA = 1.0, ItemSpeedB = 1.0,
            };
        }

        static ulong FakeItemId(int k) => MixRecipe.Hex64(Entities.Strings.Resolve(ItemIdFor(200, k)));

        /// <summary>Row k → k + 1 saved as a non-beatmatched <paramref name="preset"/> (type FADE, bars 0): an overlap of
        /// <paramref name="overlapMs"/> that ends <paramref name="endGapMs"/> before row k's end, B from its start.</summary>
        static MixRecipe Unmatched(int k, ReadOnlySpan<int> targets, MixPresetId preset, int endGapMs, uint overlapMs)
        {
            var r = PairRecipe(k, targets);
            var a = new global::Wavee.Track(targets[k]);
            r.OutStartMs = (uint)Math.Max(0, a.DurationMs - endGapMs - overlapMs);
            r.OverlapMs = overlapMs;
            r.Preset = (byte)preset;
            r.PresetType = MixPresetType.Fade;
            r.IsOverlapOverride = r.IsPresetIdOverride = true;
            return r;
        }

        /// <summary>Row k → k + 1 saved as a beatmatched <paramref name="preset"/> over <paramref name="bars"/> bars (type
        /// BEATMATCHED_FADE): the overlap is that many bars of A's seeded tempo, from a bar 20 s before A's end into B's third bar,
        /// B stretched to A's tempo (both recipe tempos then name A's, as a matched pair records them).</summary>
        static MixRecipe Matched(int k, ReadOnlySpan<int> targets, MixPresetId preset, int bars)
        {
            var r = PairRecipe(k, targets);
            var a = new global::Wavee.Track(targets[k]);
            float bpm = r.BpmA > 0 ? r.BpmA : 120f;
            double periodA = 60_000.0 / bpm, periodB = 60_000.0 / (r.BpmB > 0 ? r.BpmB : bpm);
            r.OverlapMs = (uint)Math.Round(bars * 4 * periodA);
            r.OutStartMs = (uint)(180 + Math.Floor((a.DurationMs - 20_000 - 180) / (4 * periodA)) * 4 * periodA);
            r.InStartMs = (uint)(180 + 8 * periodB);
            r.SpeedB = r.BpmB > 0 ? bpm / r.BpmB : 1f;
            r.BpmB = bpm;
            r.DurationBars = bars;
            r.IsBeatmatched = true;
            r.Preset = (byte)preset;
            r.PresetType = MixPresetType.BeatmatchedFade;
            r.IsOverlapOverride = r.IsPresetIdOverride = true;
            return r;
        }

        /// <summary>Row 1 → row 2 as a foreign client would store a beatmatched Slam (preset 11, type 3) over 2 bars: B stretched to
        /// A's tempo, an EQ override to style 15, a volume-out curve override, and an unknown field in the top message, the Overlap,
        /// the Preset and the EQ override. Written by hand, so nothing of Wavee's encoder shapes it.</summary>
        static byte[] BeatmatchedRaw(ReadOnlySpan<int> targets)
        {
            var id = PairRecipe(1, targets);
            float bpm = id.BpmA > 0 ? id.BpmA : 120f;
            uint overlap = (uint)Math.Round(2 * 4 * 60_000.0 / bpm);
            var a = new global::Wavee.Track(targets[1]);
            var buffer = new byte[1024];
            var w = new Spotify.Decode.ProtoWriter(buffer);
            Span<byte> hex = stackalloc byte[16];
            int overlapMsg = w.Open(1);
            Hex(FakeItemId(1), hex); w.Utf8Always(1, hex);
            Hex(FakeItemId(2), hex); w.Utf8Always(2, hex);
            w.UAlways(3, (ulong)Math.Max(0, a.DurationMs - 24_000));
            w.UAlways(4, (ulong)(180 + 16 * 60_000.0 / (id.BpmB > 0 ? id.BpmB : bpm)));   // B's fifth bar on its seeded grid
            w.UAlways(5, overlap);
            w.F32(6, 1f);
            w.F32(7, id.BpmB > 0 ? bpm / id.BpmB : 1f);
            w.UAlways(8, 2);
            w.UAlways(9, 1);
            w.StrAlways(10, id.TrackA); w.StrAlways(11, id.TrackB);
            w.StrAlways(12, id.PlayableA); w.StrAlways(13, id.PlayableB);
            w.StrAlways(14, id.BeatsIdA); w.StrAlways(15, id.BeatsIdB);
            w.F32(16, bpm); w.F32(17, bpm);
            w.F64(18, 1.0); w.F64(19, 1.0);
            w.UAlways(25, 7);                                   // unknown to this build
            w.Close(overlapMsg);
            int preset = w.Open(2);
            w.UAlways(1, (ulong)MixPresetId.Slam);
            w.UAlways(2, (ulong)MixPresetType.BeatmatchedFade);
            int eq = w.Open(4);
            w.UAlways(1, 15);                                   // BassFadeOut
            w.UAlways(2, 1);                                    // unknown to this build
            w.Close(eq);
            int curves = w.Open(6);                             // volumeOutCurveOverride: Curves { 1[] Curve, 3 min, 4 max }
            int curve = w.Open(1);                              // Curve { 1[] Point, 2 start, 3 end }
            int point = w.Open(1); w.F64(2, 1.0); w.Close(point);                 // (0, 1)
            point = w.Open(1); w.F64(1, 0.6); w.F64(2, 1.0); w.Close(point);     // (0.6, 1)
            point = w.Open(1); w.F64(1, 1.0); w.Close(point);                     // (1, 0)
            w.F64(3, 1.0);
            w.Close(curve);
            w.F64(4, 1.0);
            w.Close(curves);
            w.F32(30, 0.25f);                                   // unknown to this build
            w.Close(preset);
            w.UAlways(3, 1);
            w.UAlways(4, 1);
            w.UAlways(5, (ulong)MixBeatmatchPreference.On);
            w.Utf8Always(9, "made-up"u8);                       // unknown to this build
            return buffer.AsSpan(0, w.Length).ToArray();

            static void Hex(ulong v, Span<byte> into)
            {
                for (int i = 15; i >= 0; i--) { int d = (int)(v & 0xF); into[i] = (byte)(d < 10 ? '0' + d : 'a' + d - 10); v >>= 4; }
            }
        }

        static string FakeBeatsId(int k) => (0xBEEF_0000_0000_0000UL + (ulong)k).ToString("x16", CultureInfo.InvariantCulture) + "0000000000000000";

        /// <summary>Kind 28's candidates per pool row, made up: the smallest start candidate (ms) and how far before the row's end
        /// its largest end candidate sits (ms); -1 = the answer carried no such list (row 8: Auto's fallback for 7 → 8 and 8 → 9).</summary>
        static readonly (int FirstStart, int EndGap)[] s_mixCandidates =
        [
            (20, 1_100), (260, 4_700), (480, 30), (40, 2_200), (10, 3_300), (590, 20), (1_170, 6_100), (90, 2_600), (-1, -1), (370, 900),
        ];

        /// <summary>A constant grid at the track's seeded tempo (4/4, the first downbeat 180 ms in), its analysis id, and
        /// cue points: the start cue on the second bar for every third row (so some pairs enter mid-file), the end cue
        /// 16 s before the end, and the candidates of <see cref="s_mixCandidates"/>.</summary>
        static void SeedAnalysis(int trackSlot, int k)
        {
            var t = new global::Wavee.Track(trackSlot);
            long dur = t.DurationMs;
            double bpm = t.Tempo > 0 ? t.Tempo / 10.0 : 120.0;
            double period = 60_000.0 / bpm;
            int count = (int)Math.Min(BeatGrid.MaxBeats, Math.Max(0, (dur - 180) / period));
            var beats = new uint[count];
            for (int i = 0; i < count; i++)
                beats[i] = (uint)(180 + i * period + 0.5) | (i % 4 == 0 ? BeatGrid.DownbeatBit : 0u);
            var edges = Current.Edges;
            edges.TrackBeats.Replace(trackSlot, new int[count], beats, EdgeState.Complete, count);
            MixData.SetBeatsId(trackSlot, FakeBeatsId(k));
            // A kind-237 envelope when the row has none (the editor's waveform lanes): made up, a ramp in, sections that swell on the bar and
            // a fade out, so the lanes and their beat ticks read as a song.
            if (edges.TrackWaveform.Payload(trackSlot).IsEmpty && dur > 0)
            {
                int samples = (int)Math.Clamp(dur / WaveformBands.NominalHopMs, 64, WaveformBands.MaxSamples);
                var wave = new WaveSample[samples];
                for (int i = 0; i < samples; i++)
                {
                    double ms = (double)i * dur / samples;
                    double edge = Math.Min(1.0, Math.Min(ms / 12_000.0, (dur - ms) / 9_000.0));
                    double section = 0.55 + 0.35 * Math.Abs(Math.Sin((ms / 30_000.0 + k * 0.37) * Math.PI));
                    double pulse = 0.82 + 0.18 * Math.Cos((ms - 180) / period * 2 * Math.PI);
                    byte low = (byte)Math.Clamp(255 * edge * section * pulse, 8, 255);
                    wave[i] = new WaveSample(low, (byte)(low * 0.7), (byte)(low * 0.45));
                }
                edges.TrackWaveform.Replace(trackSlot, new int[samples], wave, EdgeState.Complete, samples);
            }
            uint start = k % 3 == 1 ? (uint)(180 + 4 * period + 0.5) : 0u;
            uint end = (uint)Math.Max(0, dur - 16_000);
            var (firstStart, endGap) = s_mixCandidates[k % s_mixCandidates.Length];
            var has = (firstStart >= 0 ? CueCandidates.Start : 0) | (endGap >= 0 ? CueCandidates.End : 0);
            var cue = new[] { new TrackCues(start, end, t.Tempo, has, (uint)Math.Max(0, firstStart), (uint)Math.Max(0, dur - Math.Max(0, endGap))) };
            edges.TrackCues.Replace(trackSlot, new int[1], cue, EdgeState.Complete, 1);
        }
    }
}
