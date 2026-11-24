// ── Playback/Timecode.Host.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// The LTC output's lifetime (#154 phase 3): the timecode settings -> the generator's configuration and the aux output open or
// closed on the chosen endpoint.
//
// Role: SHELL. `Apply` is called by every timecode verb and once at the first load; it configures the generator at once (a dial is
// heard on the next block) and opens or closes the output on a worker (an open probes the device, a close drains ~100 ms), one at
// a time. The output is never the endpoint the music plays on: a timecode device that became the music's output is not opened.
// Headless (`--inspect`, `--silent`) the endpoint is the paced silent one, so the generator and its reader run with no device.

using System.Threading;

using FluentGpu.Media;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The LTC output (see the file header).</summary>
    public static class TimecodeOutput
    {
        static readonly LtcGenerator s_generator = new();
        static readonly Lock s_gate = new();
        static AuxAudioOutput? s_output;
        static string s_openId = "";
        static int s_applying, s_reapply, s_applied;

        /// <summary>The generator (its status, the reader lease).</summary>
        public static LtcGenerator Generator => s_generator;

        /// <summary>The open aux output, or null.</summary>
        public static AuxAudioOutput? Output { get { lock (s_gate) return s_output; } }

        /// <summary>The endpoint the open output was asked for ("" when none is open).</summary>
        public static string OpenId { get { lock (s_gate) return s_openId; } }

        /// <summary>The settings as a generator configuration.</summary>
        public static LtcGenerator.Config ConfigFromSettings()
        {
            int rate = Math.Clamp(Platform.Settings.Get(Platform.Keys.TimecodeLtcRate), 0, 3);
            int hour = Math.Clamp(Platform.Settings.Get(Platform.Keys.TimecodeStartHour), 0, 1);
            int db = Math.Clamp(Platform.Settings.Get(Platform.Keys.TimecodeLtcLevelDb), -24, 0);
            int offsetMs = Math.Clamp(Platform.Settings.Get(Platform.Keys.TimecodeOffsetMs), -500, 500);
            return new LtcGenerator.Config((LtcRate)rate, hour, MathF.Pow(10f, db / 20f), offsetMs * 10_000L);
        }

        /// <summary>The output the settings ask for: the chosen endpoint while the switch is on and it is not the music's output; else "".</summary>
        public static string WantedId()
        {
            if (!Platform.Settings.Get(Platform.Keys.TimecodeLtcEnabled)) return "";
            string id = Platform.Settings.Get(Platform.Keys.TimecodeLtcDeviceId);
            if (id.Length == 0) return "";
            return string.Equals(id, Audio.MusicEndpointId(), StringComparison.OrdinalIgnoreCase) ? "" : id;
        }

        /// <summary>The first load applies the saved settings once (a later call is a no-op).</summary>
        public static void ApplyOnce()
        {
            if (Interlocked.Exchange(ref s_applied, 1) == 0) Apply();
        }

        /// <summary>Configure the generator now and bring the output to what the settings ask for, on a worker. Any thread.</summary>
        public static void Apply()
        {
            Volatile.Write(ref s_applied, 1);
            s_generator.Configure(ConfigFromSettings());
            Interlocked.Exchange(ref s_reapply, 1);
            if (Interlocked.CompareExchange(ref s_applying, 1, 0) != 0) return;   // the running pass re-reads before it ends
            ThreadPool.UnsafeQueueUserWorkItem(static _ => Reconcile(), null);
        }

        static void Reconcile()
        {
            try
            {
                while (Interlocked.Exchange(ref s_reapply, 0) == 1)
                {
                    string want = WantedId();
                    AuxAudioOutput? close = null;
                    lock (s_gate)
                    {
                        if (string.Equals(want, s_openId, StringComparison.OrdinalIgnoreCase) && (want.Length == 0) == (s_output is null)) continue;
                        close = s_output;
                        s_output = null;
                        s_openId = "";
                    }
                    if (close is not null)
                    {
                        try { close.Dispose(); } catch (Exception ex) { Log.Warn("timecode", "ltc close failed", ex); }
                        Log.Info("timecode", "ltc close");
                    }
                    if (want.Length == 0) continue;
                    AuxAudioOutput output = Audio.OpenAux(want, s_generator);
                    output.Start();
                    lock (s_gate) { s_output = output; s_openId = want; }
                    Log.Info("timecode", "ltc open id=" + want + " rate=" + s_generator.Current.Rate + (Audio.SilentEndpoint ? " (silent endpoint)" : ""));
                }
            }
            catch (Exception ex) { Log.Warn("timecode", "ltc reconcile failed", ex); }
            finally
            {
                Volatile.Write(ref s_applying, 0);
                // A request that landed after the loop's last read and before the flag dropped runs now.
                if (Volatile.Read(ref s_reapply) == 1 && Interlocked.CompareExchange(ref s_applying, 1, 0) == 0)
                    ThreadPool.UnsafeQueueUserWorkItem(static _ => Reconcile(), null);
            }
        }

        /// <summary>Close the output (the exit tail). Blocks while the aux thread drains (bounded).</summary>
        public static void Close()
        {
            AuxAudioOutput? close;
            lock (s_gate) { close = s_output; s_output = null; s_openId = ""; }
            try { close?.Dispose(); } catch { }
        }
    }
}
