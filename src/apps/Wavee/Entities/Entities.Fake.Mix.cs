// ── Entities/Entities.Fake.Mix.cs — FAKE (#48 Mix, plan §3.3 "--fake") ─────────────────────────────────────────────────
//
// One mixed playlist for --fake and the inspector, shaped like a captured mixed list (report §3.1, §3.3): owned by
// the account, the mix lens applied, ten mixable rows whose last pair runs into a LOCAL file (not-mixable, reason
// entity-kind), three stored recipes (a Rise, a beatmatched Fade carrying fields this build does not know, a "no
// transition"), synthetic kind-217 grids from each track's seeded tempo and kind-28 cue points. Every value is made up
// here. Route: pl:spotify:playlist:plmix.

using System.Globalization;

namespace Wavee;

public static partial class Entities
{
    /// <summary>--fake's answer to Spotify.Mixes.TurnOn: the row reads as a list the server just mixed (the lens applied, the list
    /// and XM bits set). There is no account to write with.</summary>
    public static void FakeMixOn(Playlist p)
    {
        if (!p.IsValid) return;
        var t = Current.Playlists;
        t.MixFlags[p.Slot] |= (uint)(PlaylistMixFlags.ListMix | PlaylistMixFlags.AllowCustom | PlaylistMixFlags.CanViewTransition
                                     | PlaylistMixFlags.LensApplied | PlaylistMixFlags.XmMixState | PlaylistMixFlags.XmAutoLens);
        t.Bump(p.Slot);
        Log.Info("mix", "fake: mix on");
    }

    internal static partial class LibrarySeed
    {
        public const string MixUri = "spotify:playlist:plmix";
        public const string MixLensEtag = "1f3d4c55";

        /// <summary>The pool tracks of the mixed playlist, in order: matched tempos, one far-apart pair (row 3 → 4 runs
        /// 117 → 77 BPM, the capture's warning pair), then a local file as the last row.</summary>
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

            // Three edited pairs, every byte made up here (no captured recipe):
            //   0 → 1  Rise (preset 3), as Wavee writes a recipe of its own: a 2890 ms window ending 1 ms before row 0's file does.
            //   1 → 2  a beatmatched Fade over 2 bars with an EQ override, written field by field as another client would: a
            //          curve override and a field this build does not know at every level, which a decode → encode must keep.
            //   5 → 6  "no transition": no preset id, a 1 ms overlap, no out-point (A plays to its end, B from its start).
            var stored = new Dictionary<ulong, MixTransition>();
            var a = new global::Wavee.Track(targets[0]);
            var rise = PairRecipe(0, targets);
            rise.OutStartMs = (uint)Math.Max(0, a.DurationMs - 2891);
            rise.OverlapMs = 2890;
            rise.Preset = (byte)MixPreset.Rise;
            rise.PresetType = MixPresetType.Fade;
            rise.IsOverlapOverride = rise.IsPresetIdOverride = true;
            Store(stored, in rise, MixRecipe.EncodeToArray(in rise, default), 0);
            Store(stored, BeatmatchedRaw(targets), 1);
            var none = PairRecipe(5, targets);
            none.OverlapMs = 1;
            none.PresetType = MixPresetType.Fade;
            none.IsPresetIdOverride = true;
            Store(stored, in none, MixRecipe.EncodeToArray(in none, default), 5);
            MixData.Replace(p.Slot, stored);
        }

        static void Store(Dictionary<ulong, MixTransition> into, in MixRecipe recipe, byte[] raw, int k)
            => into[recipe.FromItemId] = new MixTransition(recipe, raw, Convert.ToBase64String(raw),
                "spotify:transition:fakeTransition0000000" + k.ToString(CultureInfo.InvariantCulture) + ":1795386355891");

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

        /// <summary>Row 1 → row 2 as a foreign client would store a beatmatched Fade (type 3) over 2 bars: B stretched to A's tempo,
        /// an EQ override to style 15, a volume-out curve override, and an unknown field in the top message, the Overlap, the Preset
        /// and the EQ override. Written by hand, so nothing of Wavee's encoder shapes it.</summary>
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
            w.UAlways(1, (ulong)MixPreset.Fade);
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

        static string FakeBeatsId(int k) => (0xE2FD_6349_0000_0000UL + (ulong)k).ToString("x16", CultureInfo.InvariantCulture) + "00000000000000aa";

        /// <summary>A constant grid at the track's seeded tempo (4/4, the first downbeat 180 ms in), its analysis id, and
        /// cue points: the start cue on the second bar for every third row (so some pairs enter mid-file), the end cue
        /// 16 s before the end.</summary>
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
            uint start = k % 3 == 1 ? (uint)(180 + 4 * period + 0.5) : 0u;
            uint end = (uint)Math.Max(0, dur - 16_000);
            var cue = new[] { new TrackCues(start, end, t.Tempo) };
            edges.TrackCues.Replace(trackSlot, new int[1], cue, EdgeState.Complete, 1);
        }
    }
}
