// ── Playback/Timecode.Host.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// The LTC output's lifetime (#154 phase 3): the timecode settings -> the generator's configuration and the aux output open or
// closed on the chosen endpoint.
//
// Role: SHELL. `Apply` is called by every timecode verb and once at the first load; it configures the generator at once (a dial is
// heard on the next block) and opens or closes the output on a worker (an open probes the device, a close drains ~100 ms), one at
// a time. The output is never the endpoint the music plays on: a timecode device that became the music's output is not opened.
// Headless (`--inspect`, `--silent`) the endpoint is the paced silent one, so the generator and its reader run with no device.

using System.Threading;

using FluentGpu.Localization;
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
        static Timer? s_statusTimer;
        static string s_lastStatus = "";
        static long s_relocLogged;

        /// <summary>The generator (its status, the reader lease).</summary>
        public static LtcGenerator Generator => s_generator;

        /// <summary>The open aux output, or null.</summary>
        public static AuxAudioOutput? Output { get { lock (s_gate) return s_output; } }

        /// <summary>The endpoint the open output was asked for ("" when none is open).</summary>
        public static string OpenId { get { lock (s_gate) return s_openId; } }

        /// <summary>The Settings row's words: Off, Opening the output, Not responding, Waiting for playback, or "Timecode at 30 fps". The
        /// running timecode itself is on Diagnostics › Playback runtime (the row changes only when the state does). Any thread.</summary>
        public static string StatusLine()
        {
            if (!Platform.Settings.Get(Platform.Keys.TimecodeLtcEnabled)) return Loc.Get(Strings.Settings.Sound.Timecode.StatusOff);
            AuxAudioOutput? output = Output;
            if (output is null)
                return Loc.Get(SettingsVerbs.TimecodeDeviceIsMusic() ? Strings.Settings.Sound.Timecode.IsMusic : Strings.Settings.Sound.Timecode.StatusOpening);
            switch (output.State.Peek())
            {
                case AudioDeviceState.Retrying or AudioDeviceState.Faulted: return Loc.Get(Strings.Settings.Sound.Timecode.StatusNotResponding);
                case AudioDeviceState.Building: return Loc.Get(Strings.Settings.Sound.Timecode.StatusOpening);
            }
            LtcGenerator.Status st = s_generator.Read();
            if (!st.Locked) return Loc.Get(Strings.Settings.Sound.Timecode.StatusWaiting);
            return Strings.Settings.Sound.Timecode.StatusSending(Loc.Get(RateKey(st.Rate)));
        }

        /// <summary>The loc key of a rate's words ("30 fps").</summary>
        public static string RateKey(LtcRate rate) => rate switch
        {
            LtcRate.Fps24 => Strings.Settings.Sound.Timecode.Fps24,
            LtcRate.Fps25 => Strings.Settings.Sound.Timecode.Fps25,
            LtcRate.Fps2997Drop => Strings.Settings.Sound.Timecode.Fps2997,
            _ => Strings.Settings.Sound.Timecode.Fps30,
        };

        /// <summary>While an output is open, once a second: a changed status phrase re-reads the Settings page (on the UI thread).</summary>
        static void WatchStatus(bool on)
        {
            if (on) s_statusTimer ??= new Timer(static _ =>
            {
                LogRelocations();
                string now = StatusLine();
                if (string.Equals(now, s_lastStatus, StringComparison.Ordinal)) return;
                s_lastStatus = now;
                Playback.ToUi(static () => Settings.Bump());
            }, null, 1000, 1000);
            else { s_statusTimer?.Dispose(); s_statusTimer = null; }
            Playback.ToUi(static () => Settings.Bump());
        }

        /// <summary>The always-on log of the generator's relocations (category <c>timecode</c>): each with the instant it is heard, the label
        /// and the timeline epoch that caused it. Read on the status timer (the aux thread never logs).</summary>
        static void LogRelocations()
        {
            long n = s_generator.Read().Relocations;
            for (long i = Math.Max(s_relocLogged, n - 8); i < n; i++)
                if (s_generator.TryGetRelocation(i, out long qpc, out long frame, out long epoch))
                    Log.Info("timecode", "ltc relocate #" + (i + 1) + " at=" + qpc + " label=" + Timecode.Format(frame, s_generator.Current.Rate)
                        + " epoch=" + (epoch >> 32) + "." + (epoch & 0xFFFF_FFFFL));
            s_relocLogged = n;
        }

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

        /// <summary>Set one timecode dial by name through the Settings rows' own verbs (what a click writes): the inspector's
        /// <c>timecode do=set</c> and the developer link <c>wavee://diag?cmd=timecode</c>. <paramref name="key"/>: enabled, device,
        /// rate, start, level, offset. A device id not in the roster is taken as given where the music is on no device (the silent
        /// endpoint, `--fake`). False for a key or value it does not accept.</summary>
        public static bool Set(string key, string value)
        {
            value ??= "";
            switch ((key ?? "").ToLowerInvariant())
            {
                case "enabled": return SettingsVerbs.SetTimecodeEnabled(value is "1" or "true" or "on");
                case "device":
                {
                    var devices = Audio.Devices.Peek();
                    for (int i = 0; i < devices.Length; i++)
                        if (string.Equals(devices[i].Id, value, StringComparison.OrdinalIgnoreCase)) return SettingsVerbs.SetTimecodeDevice(i + 1);
                    if (Audio.MusicEndpointId() is not null || value.Length == 0) return false;
                    Platform.Settings.Set(Platform.Keys.TimecodeLtcDeviceId, value);
                    Apply();
                    return true;
                }
                case "rate":
                    return SettingsVerbs.SetTimecodeRate(value.ToLowerInvariant() switch
                    {
                        "24" => 0, "25" => 1, "29.97" or "2997" or "29.97df" or "df" => 2, "30" => 3, _ => -1,
                    });
                case "start": return SettingsVerbs.SetTimecodeStartHour(value is "1" or "01" or "01:00:00:00" ? 1 : 0);
                case "level":
                    return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float db)
                           && SettingsVerbs.SetTimecodeLevel(db);
                case "offset":
                    return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ms)
                           && SettingsVerbs.SetTimecodeOffset(ms);
                default: return false;
            }
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
                    if (want.Length == 0) { WatchStatus(false); continue; }
                    AuxAudioOutput output = Audio.OpenAux(want, s_generator);
                    output.Start();
                    lock (s_gate) { s_output = output; s_openId = want; }
                    WatchStatus(true);
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
            s_statusTimer?.Dispose();
            s_statusTimer = null;
            try { close?.Dispose(); } catch { }
        }
    }
}
