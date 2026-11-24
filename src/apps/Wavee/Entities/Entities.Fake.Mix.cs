// ── Entities/Entities.Fake.Mix.cs — FAKE (#48 Mix, plan §3.3 "--fake") ─────────────────────────────────────────────────
//
// One mixed playlist for --fake and the inspector, shaped like the capture's HAAAAMOOO (report §3.1, §3.3): owned by
// the account, the mix lens applied, ten mixable rows whose last pair runs into a LOCAL file (not-mixable, reason
// entity-kind), one stored Rise recipe on the first pair, synthetic kind-217 grids from each track's seeded tempo and
// kind-28 cue points. Route: pl:spotify:playlist:plmix.

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

            // The one edited pair: row 0 → row 1 saved as Rise (preset 3), the capture's saved transition in shape: a
            // 2890 ms window ending 1 ms before row 0's file does.
            var a = new global::Wavee.Track(targets[0]);
            var b = new global::Wavee.Track(targets[1]);
            long durA = a.DurationMs;
            var recipe = new MixRecipe
            {
                FromItemId = MixRecipe.Hex64(Entities.Strings.Resolve(ItemIdFor(200, 0))),
                ToItemId = MixRecipe.Hex64(Entities.Strings.Resolve(ItemIdFor(200, 1))),
                OutStartMs = (uint)Math.Max(0, durA - 2891),
                OverlapMs = 2890,
                RateA = 1f, RateB = 1f,
                TrackA = TrackUri(s_mixPool[0]), TrackB = TrackUri(s_mixPool[1]),
                BeatsIdA = FakeBeatsId(0), BeatsIdB = FakeBeatsId(1),
                BpmA = a.Tempo / 10f, BpmB = b.Tempo / 10f,
                Preset = 3, StyleVersion = 1,
            };
            Span<byte> raw = stackalloc byte[512];
            int len = MixRecipe.Encode(in recipe, default, raw);
            var bytes = raw[..len].ToArray();
            var stored = new Dictionary<ulong, MixTransition>
            {
                [recipe.FromItemId] = new MixTransition(recipe, bytes, Convert.ToBase64String(bytes),
                    "spotify:transition:fakeTransition00000000:1795386355891"),
            };
            MixData.Replace(p.Slot, stored);
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
