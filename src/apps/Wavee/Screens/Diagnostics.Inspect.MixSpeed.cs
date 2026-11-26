// ── Screens/Diagnostics.Inspect.MixSpeed.cs ───────────────────────────────────────────────────────────────────────────
// `mix show=speed` (#48 M4, protocol §6.2): the playlist's beatmatched pair as the pump plays it, and what the deck publishes of
// B's speed. The pair's plan (Playback.Mix.PlanListPair) is armed on an offline headless PcmAudioSession by MixRender.Join +
// MixRender.Arm (the pump's own calls; `--fake` loads never prepare a join, so the live deck cannot reach one), handed off when A
// is over and pulled through B's glide back to 1, its rate read on a 200 ms tick of mixer time as TickMixSpeed reads it. Each step
// is then posted to the LIVE reducer as the pump posts it (AudioSignal.MixSpeed, the deck's load epoch) and the connect-state PUT
// the deck would send is encoded and read back: player_state.playback_speed (f9) and the playing row's `playback_speed` metadata.
// Play a row of the mixed list first (`transport do=clear`, then `play uri=<row> context=spotify:playlist:plmix`): the PUT carries
// the speed only while the deck plays, and the metadata key only in a mixed context. The last step (1.0) leaves the deck at 1.
//
// Role: SHELL (UI thread; the offline session is its own)

using System.Globalization;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`mix show=speed {uri?, from?}`.</summary>
        string MixSpeedVerb(int id, string name, Inspect.Args a)
        {
            string uri = a.Str("uri", Entities.LibrarySeed.MixUri);
            var eid = EntityId.Parse(uri.AsSpan());
            if (eid.Kind != EntityKind.Playlist || !Entities.Current.Playlists.TryGetSlot(eid, out int slot))
                return Inspect.Reply.Fail(id, name, "no playlist row for '" + uri + "'");
            int count = Entities.Current.Edges.PlaylistTracks.Targets(slot).Length, from = a.Int("from", -1), pair = -1;
            MixTiming t = default;
            for (int i = 0; i + 1 < count && pair < 0; i++)
            {
                var candidate = Playback.Mix.PlanListPair(slot, i, i + 1);
                if (candidate.Mix && (from >= 0 ? i == from : candidate.SpeedB != 1f)) { t = candidate; pair = i; }
            }
            if (pair < 0) return Inspect.Reply.Fail(id, name, "no beatmatched pair is planned in " + uri + (from >= 0 ? " at from=" + from : ""));

            var run = MixOffline.Run(in t, tailSeconds: 1);
            // The published sequence through the live reducer and the PUT encoder.
            var deck = Playback.Snap();
            bool playing = deck.CurrentId.Kind == EntityKind.Track && deck.IsPlaying;
            var published = new List<(double Factor, float PlayRate, double F9, string Meta)>();
            if (playing)
                foreach (var step in run.Steps)
                {
                    Playback.Post(Playback.Input.Audio(Playback.AudioSignal.MixSpeed, Playback.Snap().LoadEpoch, Playback.FrameNowMs(),
                        BitConverter.DoubleToInt64Bits(step.Factor)));
                    RunFrames(2);
                    Playback.Snapshot snap = Playback.SnapshotForConnect(Playback.PublishReason.PlayerStateChanged);
                    byte[] body = new byte[Spotify.Decode.PutStateCapacity(in snap)];
                    int n = Spotify.Decode.PutState(in snap, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64);
                    var (f9, meta) = PutSpeed(body.AsSpan(0, Math.Max(0, n)));
                    published.Add((step.Factor, Playback.Snap().PlayRate, f9, meta));
                }
            var plan = run.Join.InRate;
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("uri", uri);
                w.WriteString("pair", pair + "→" + (pair + 1) + " " + t.Source + " preset=" + t.Preset + " bars=" + t.Bars);
                w.WriteNumber("speedB", t.SpeedB);
                w.WriteNumber("outDurMs", t.OutDurationMs);
                w.WriteNumber("inDurMs", t.InDurationMs);                // B's media window (audio.fade_in_duration = outDur × speedB)
                w.WriteNumber("inFrames", run.Join.InFrames);            // B's window in mixer frames (= outDur at speedB)
                w.WriteString("lanes", run.Join.Lanes);
                w.WriteString("skipped", Playback.MixRender.Lanes(in t, rendered: false));
                if (plan is not null)
                {
                    w.WriteNumber("windowStartFrame", MixOffline.Start);
                    w.WriteNumber("glideStartFrame", plan.GlideStartFrame);
                    w.WriteNumber("glideDelayMsAfterFadeIn", (plan.GlideStartFrame - MixOffline.Start - run.Join.InFrames) * 1000 / MixOffline.Rate);
                    w.WriteNumber("glideSteps", plan.StepCount);
                }
                w.WriteNumber("handOffFrame", run.HandOffFrame);
                w.WriteNumber("overlapAllocBytes", run.OverlapAllocBytes);
                w.WriteNumber("overlapBlocks", run.OverlapBlocks);
                w.WriteBoolean("nan", run.NaN);
                w.WriteStartArray("steps");
                foreach (var s in run.Steps)
                    w.WriteStringValue(string.Create(CultureInfo.InvariantCulture,
                        $"tick {s.TickFrame} landed {s.LandedFrame} plan {s.PlanFrame} factor {s.Factor:0.000000}"));
                w.WriteEndArray();
                w.WriteString("deck", playing ? deck.CurrentId.ToString() : "not playing a track: nothing published (play a row of the mixed list first)");
                w.WriteStartArray("published");
                foreach (var (factor, playRate, f9, meta) in published)
                    w.WriteStringValue(string.Create(CultureInfo.InvariantCulture,
                        $"step {factor:0.000000} → state.PlayRate {playRate:0.000000}, PUT f9 playback_speed {f9:0.0#######}, metadata playback_speed \"{meta}\""));
                w.WriteEndArray();
            });
        }

        /// <summary>The PUT's <c>player_state.playback_speed</c> (f9, 0 when unwritten) and the playing row's <c>playback_speed</c>
        /// metadata ("" when absent), read back off the encoded bytes.</summary>
        static (double F9, string Meta) PutSpeed(ReadOnlySpan<byte> put)
        {
            ReadOnlySpan<byte> device = new Spotify.Decode.ProtoReader(put).Bytes(2);
            ReadOnlySpan<byte> player = new Spotify.Decode.ProtoReader(device).Bytes(2);
            double f9 = 0;
            string meta = "";
            var r = new Spotify.Decode.ProtoReader(player);
            while (r.Next())
            {
                if (r.Field == 9 && r.Wire == 1) { f9 = r.Double(); continue; }
                if (r.Field == 7 && r.Wire == 2)
                {
                    var track = r.Message();
                    while (track.Next())
                    {
                        if (track.Field != 3 || track.Wire != 2) { track.Skip(); continue; }
                        var entry = track.Message();
                        string key = "", value = "";
                        while (entry.Next())
                        {
                            if (entry.Field == 1 && entry.Wire == 2) key = System.Text.Encoding.UTF8.GetString(entry.Bytes());
                            else if (entry.Field == 2 && entry.Wire == 2) value = System.Text.Encoding.UTF8.GetString(entry.Bytes());
                            else entry.Skip();
                        }
                        if (key == "playback_speed") meta = value;
                    }
                    continue;
                }
                r.Skip();
            }
            return (f9, meta);
        }
    }
}
