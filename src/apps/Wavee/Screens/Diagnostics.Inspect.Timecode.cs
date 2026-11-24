// ── Screens/Diagnostics.Inspect.Timecode.cs ──────────────────────────────────────────────────────────────────────────────────
// the `timecode` verb: read the playback timeline tap (#154 G2a) the way the LTC generator and the module fan-out read it
//
// Role: SHELL (reads the playback statics; plays nothing, changes nothing)
// Spec: docs/plans 154-timecode-implementation §3.7. Diagnostics only: no assertion lives here, the agent compares the readout
// against what it drove (`transport do=seek`, `play`, a pause) itself.
//
// THE READOUT. `tap`: the composed snapshot (uri, positionMs extrapolated to the reply's instant, durationMs, rate, playing,
// flags, epoch as `generation.engine`); `engine`: the raw anchor it came from (content / source / mix frames, mix rate, voice,
// advancing, anchor age) and the publisher's counters (anchors published, epochs, drift epochs), plus the session's output delay.
// `samples=N every=ms` takes N readings every= ms apart and returns them as `series`: on a mounted inspector the gap is run as
// frames (the headless endpoint is paced by the frame clock), otherwise slept on the wall clock.

using System.Globalization;
using System.Text.Json;
using System.Threading;

using FluentGpu.Media;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`timecode {do=status, samples, every}`.</summary>
        string Timecode(int id, string name, Inspect.Args a)
        {
            string verb = a.Str("do").ToLowerInvariant();
            if (verb == "set")
            {
                // The Settings rows' own verbs (SettingsVerbs), by the dial the row shows: what a user's click writes.
                string key = a.Str("key").ToLowerInvariant(), value = a.Str("value");
                bool ok = key switch
                {
                    "enabled" => SettingsVerbs.SetTimecodeEnabled(value is "1" or "true" or "on"),
                    "device" => SetTimecodeDeviceId(value),
                    "rate" => SettingsVerbs.SetTimecodeRate(RateIndex(value)),
                    "start" => SettingsVerbs.SetTimecodeStartHour(value is "1" or "01" or "01:00:00:00" ? 1 : 0),
                    "level" => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float db) && SettingsVerbs.SetTimecodeLevel(db),
                    "offset" => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ms) && SettingsVerbs.SetTimecodeOffset(ms),
                    _ => false,
                };
                if (!ok) return Inspect.Reply.Fail(id, name, "timecode do=set wants key=enabled|device|rate|start|level|offset and a value it accepts");
                // The output opens on a worker: give it a moment so the reply already reads it.
                for (int i = 0; i < 50 && !string.Equals(Playback.TimecodeOutput.WantedId(), Playback.TimecodeOutput.OpenId, StringComparison.OrdinalIgnoreCase); i++) Thread.Sleep(10);
                return Inspect.Reply.Ok(id, name, static w => WriteTimecode(w));
            }
            if (verb.Length > 0 && verb != "status") return Inspect.Reply.Fail(id, name, "timecode wants do=status|set, not '" + verb + "'");
            int samples = Math.Clamp(a.Int("samples", 1), 1, 2000);
            int every = Math.Clamp(a.Int("every", 200), 0, 60_000);
            if (samples == 1) return Inspect.Reply.Ok(id, name, static w => WriteTimecode(w));
            // The readings are taken first, then written: the reply is one object, and the wall clock between readings is the point.
            var readings = new (bool Live, Playback.Timeline.Snapshot Tap, AudioTimelineAnchor Anchor, long Now)[samples];
            for (int i = 0; i < samples; i++)
            {
                // Headless, the session renders into an endpoint paced by the FRAME clock, which moves only as frames run: a mounted
                // inspector advances it by frames (16 ms each); unmounted (or a real device) the wall clock does.
                if (i > 0 && every > 0)
                {
                    if (_shell is not null) RunFramesInRealTime(Math.Max(1, (int)Math.Round(every / 16.0)));
                    else Thread.Sleep(every);
                }
                long now = Playback.Timeline.NowQpc100ns();
                bool live = Playback.Timeline.TryRead(out var tap, out var anchor);
                readings[i] = (live, tap, anchor, now);
            }
            return Inspect.Reply.Ok(id, name, w =>
            {
                WriteTimecode(w);
                w.WriteStartArray("series");
                long t0 = readings[0].Now;
                foreach (var r in readings)
                {
                    w.WriteStartObject();
                    w.WriteNumber("tMs", (r.Now - t0) / 10_000.0);
                    WriteTap(w, r.Live, in r.Tap, r.Now);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
        }

        /// <summary>Run <paramref name="n"/> frames no faster than the wall clock. `frames` runs them back to back, so the frame clock
        /// leaps ahead of the session's feed and control threads (they run on the wall clock): the paced endpoint then starves and its
        /// played count loses the difference, and no anchor is published inside the burst. Held to real time, the frame-paced endpoint
        /// is fed as a device is and the tap reads what it would read live.</summary>
        void RunFramesInRealTime(int n)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                RunFrames(1);
                double due = (i + 1) * 16.0;   // the headless frame clock steps 16 ms a frame
                double spent = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (due > spent) Thread.Sleep(TimeSpan.FromMilliseconds(due - spent));
            }
        }

        static int RateIndex(string v) => v.ToLowerInvariant() switch
        {
            "24" => 0, "25" => 1, "29.97" or "2997" or "29.97df" or "df" => 2, "30" => 3, _ => -1,
        };

        /// <summary>The device by id, as the Settings row writes it. Headless there is no roster: any id stands for the silent endpoint.</summary>
        static bool SetTimecodeDeviceId(string id)
        {
            var devices = Playback.Audio.Devices.Peek();
            for (int i = 0; i < devices.Length; i++)
                if (string.Equals(devices[i].Id, id, StringComparison.OrdinalIgnoreCase)) return SettingsVerbs.SetTimecodeDevice(i + 1);
            if (!Playback.Audio.SilentEndpoint) return false;
            Platform.Settings.Set(Platform.Keys.TimecodeLtcDeviceId, id);
            Playback.TimecodeOutput.Apply();
            return true;
        }

        static void WriteTimecode(Utf8JsonWriter w)
        {
            long now = Playback.Timeline.NowQpc100ns();
            bool live = Playback.Timeline.TryRead(out var tap, out var anchor);
            w.WriteStartObject("tap");
            WriteTap(w, live, in tap, now);
            w.WriteNumber("changes", Playback.Timeline.ChangeCount);
            w.WriteEndObject();
            w.WriteStartObject("engine");
            PcmAudioSession? session = Playback.Timeline.Session;
            w.WriteBoolean("session", session is not null);
            if (live)
            {
                w.WriteNumber("contentFrame", Math.Round(anchor.ContentFrame, 1));
                w.WriteNumber("sourceFrame", Math.Round(anchor.SourceFrame, 1));
                w.WriteNumber("mixFrame", Math.Round(anchor.MixFrame, 1));
                w.WriteNumber("mixRate", anchor.MixRate);
                w.WriteNumber("voice", anchor.VoiceId);
                w.WriteBoolean("advancing", anchor.Advancing);
                w.WriteNumber("epoch", anchor.Epoch);
                w.WriteNumber("anchorAgeMs", Math.Round((now - anchor.Qpc100ns) / 10_000.0, 2));
            }
            if (session is not null)
            {
                AudioTimelineStats st = session.TimelineStats;
                w.WriteNumber("publishes", st.Publishes);
                w.WriteNumber("epochs", st.Epochs);
                w.WriteNumber("driftEpochs", st.DriftEpochs);
                w.WriteNumber("lastDriftMs", Math.Round(st.LastDriftMs, 3));
                w.WriteNumber("outputDelayMs", session.OutputDelayFrames * 1000.0 / Math.Max(1, session.Format.SampleRate));
                w.WriteNumber("sampleClock", session.SampleClock);
            }
            w.WriteEndObject();
            w.WriteStartObject("ltc");
            w.WriteString("status", Playback.TimecodeOutput.StatusLine());
            w.WriteString("device", Playback.TimecodeOutput.OpenId);
            if (Playback.TimecodeOutput.Output is { } output)
            {
                var os = output.Stats;
                w.WriteString("state", output.State.Peek().ToString());
                w.WriteNumber("auxRate", os.SampleRate);
                w.WriteNumber("blocks", os.Blocks);
                w.WriteNumber("underruns", os.Underruns);
                w.WriteNumber("auxLatencyMs", os.StreamLatencyFrames * 1000.0 / Math.Max(1, os.SampleRate));
            }
            LtcGenerator.Status g = Playback.TimecodeOutput.Generator.Read();
            w.WriteString("rate", g.Rate.ToString());
            w.WriteBoolean("locked", g.Locked);
            w.WriteString("sent", g.SentFrame >= 0 ? global::Wavee.Timecode.Format(g.SentFrame, g.Rate) : "");
            w.WriteString("read", g.ReadFrame >= 0 ? global::Wavee.Timecode.Format(g.ReadFrame, g.Rate) : "");
            w.WriteNumber("readCount", g.ReadCount);
            w.WriteNumber("matches", g.Matches);
            w.WriteNumber("mismatches", g.Mismatches);
            w.WriteNumber("errorMs", Math.Round(g.ErrorFrames * 1000.0 / global::Wavee.Timecode.FramesPerSecond(g.Rate), 3));
            w.WriteNumber("relocations", g.Relocations);
            w.WriteEndObject();
        }

        static void WriteTap(Utf8JsonWriter w, bool live, in Playback.Timeline.Snapshot tap, long now)
        {
            w.WriteBoolean("live", live);
            if (!live) return;
            w.WriteString("uri", tap.Track.Text);
            w.WriteNumber("positionMs", Math.Round(tap.PositionMicrosAt(now) / 1000.0, 2));
            w.WriteNumber("durationMs", tap.DurationMs);
            w.WriteNumber("rate", tap.Rate);
            w.WriteBoolean("playing", tap.Playing);
            w.WriteString("flags", tap.State.ToString());
            w.WriteString("epoch", (tap.Epoch >> 32).ToString(CultureInfo.InvariantCulture) + "." + (tap.Epoch & 0xFFFF_FFFFL).ToString(CultureInfo.InvariantCulture));
            if ((tap.State & Playback.Timeline.Flags.Transition) != 0)
            {
                w.WriteString("incoming", tap.Incoming.Text);
                w.WriteString("master", tap.MasterVoice.ToString());
                w.WriteNumber("transitionStartInMs", Math.Round((tap.TransitionStartQpc100ns - now) / 10_000.0, 1));
                w.WriteNumber("transitionEndInMs", Math.Round((tap.TransitionEndQpc100ns - now) / 10_000.0, 1));
            }
        }
    }
}
