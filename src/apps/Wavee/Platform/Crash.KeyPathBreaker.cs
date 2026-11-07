// ── Platform/Crash.KeyPathBreaker.cs ───────────────────────────────────────────────────────────────────────────────
// A circuit breaker for the private key path. A hardware fault in native code the key path runs cannot be caught: the
// process dies, and the next launch plays a track, derives a key and dies again. So a native fault that lands while a
// thread is inside the key-path region (`Crash.AppRegion.KeyPath`, set around the seam call in `Spotify.Audio.Key`) is
// written down — by the native hook in the dying process, or by the handler child when the process died by exit code
// with a thread inside the region — and the next launch decides:
//
//   * two key-path faults within the last three launches of THIS version → the breaker OPENS for this launch and later
//     ones: the seam answers as if no deriver were installed (`Spotify.Audio.KeyDeriver` reads null, `CanDerive` false),
//     so playback that needs the key path shows the existing "unavailable on this build" outcome (`Fault.NoDeriver`)
//     instead of crash-looping. Tracks the regular key service serves keep playing.
//   * it CLOSES on an app update (a new version starts clean) or after a quiet period (seven days open), so a fixed
//     driver, a fixed VM or a one-off is retried.
//
// State is one small key=value file, `crash\keypath-breaker.txt`, beside the boot-failure streak (never settings: the
// dying process must not write settings). Pure rules in `Decide` / `WithFault`; the file I/O around them never throws.

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class Crash
{
    public static class KeyPathBreaker
    {
        public const string FileName = "keypath-breaker.txt";

        /// <summary>Faults that open the breaker, and how many recent launches they must fall within.</summary>
        public const int FaultsToOpen = 2, WithinLaunches = 3;

        /// <summary>How long an open breaker stays open, and how long a recorded fault counts (the quiet period).</summary>
        public const long QuietMs = 7L * 24 * 60 * 60 * 1000;

        const int MaxFaults = 8;

        /// <summary>One fault: when (unix ms) and during which launch (the state's launch counter then).</summary>
        public readonly record struct Fault(long AtMs, int Launch);

        /// <summary>The persisted state. <see cref="OpenedAtMs"/> is 0 while closed.</summary>
        public sealed record State(string Version, int Launches, long OpenedAtMs, Fault[] Faults)
        {
            public static readonly State Empty = new("", 0, 0, []);
            public bool IsOpen => OpenedAtMs != 0;
        }

        static volatile bool s_open;

        /// <summary>Exit codes that mean "died of a fault", for the handler child's exit-code record: an access violation,
        /// an illegal instruction, an in-page error, heap corruption, and the fail-fasts a fault turns into (NativeAOT's
        /// 0xC0000602 / 0xC0000409, CoreCLR's 0x80131623 in a Debug build). A plain non-zero exit is not evidence.</summary>
        public static bool IsCrashExit(uint code)
            => code is 0xC0000005 or 0xC000001D or 0xC0000006 or 0xC0000374 or 0xC0000602 or 0xC0000409 or 0x80131623;

        /// <summary>True when this launch skips the key path. Read by the seam on every call; set once by <see cref="Evaluate"/>.</summary>
        public static bool Open => s_open;

        /// <summary>A launch: the state after counting it. A new version starts clean; faults older than the quiet period are
        /// forgotten; an open breaker closes after the quiet period; a closed one opens on <see cref="FaultsToOpen"/> faults
        /// within the last <see cref="WithinLaunches"/> launches. PURE.</summary>
        public static State Decide(State previous, string version, long nowMs)
        {
            State s = previous.Version == version ? previous : State.Empty with { Version = version };
            int launch = s.Launches + 1;
            var faults = s.Faults.Where(f => nowMs - f.AtMs <= QuietMs).ToArray();
            long openedAt = s.OpenedAtMs;
            if (openedAt != 0 && nowMs - openedAt > QuietMs) { openedAt = 0; faults = []; }
            if (openedAt == 0 && faults.Count(f => f.Launch > launch - 1 - WithinLaunches) >= FaultsToOpen) openedAt = nowMs;
            return new State(version, launch, openedAt, faults);
        }

        /// <summary>The state after a key-path fault during the current launch. A fault from another version than the state's
        /// starts a fresh state for that version. PURE.</summary>
        public static State WithFault(State s, string version, long nowMs)
        {
            if (s.Version != version) s = State.Empty with { Version = version, Launches = 1 };
            var faults = s.Faults.Append(new Fault(nowMs, s.Launches)).TakeLast(MaxFaults).ToArray();
            return s with { Faults = faults };
        }

        /// <summary>Once per GUI launch (<c>Crash.Host.BeginGuiRun</c>): counts the launch, decides, persists, and opens the
        /// breaker for this process when the rules say so (logged, and in the crash trail).</summary>
        public static void Evaluate(string logFolder, string version)
        {
            try
            {
                var next = Decide(Read(logFolder), version, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Write(logFolder, next);
                s_open = next.IsOpen;
                if (next.IsOpen)
                {
                    Trail.Note(AppTrail.KeyPathBreakerOpen, next.Faults.Length);
                    Log.Warn("crash", "keypath.breaker open: the private key path faulted " + next.Faults.Length.ToString(CultureInfo.InvariantCulture)
                        + " times recently; this launch skips it (playback that needs it shows as unavailable) until an update or "
                        + (QuietMs / 86_400_000).ToString(CultureInfo.InvariantCulture) + " quiet days");
                }
            }
            catch (Exception ex) { Log.Warn("crash", "keypath.breaker.evaluate.failed", ex); }
        }

        /// <summary>A native fault inside the key-path region (the dying process's hook, or the handler child for an exit
        /// code). Never throws; allocates a little (it runs after the fault, on a thread that can).</summary>
        public static void RecordFault(string logFolder, string version, string via)
        {
            try
            {
                Write(logFolder, WithFault(Read(logFolder), version, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Trail.Note(AppTrail.KeyPathFault, via == "native" ? 1 : 2);
            }
            catch (Exception) { }
        }

        // ── the file ────────────────────────────────────────────────────────────────────────────────────────────────

        static string PathFor(string logFolder) => Path.Combine(Files.Root(logFolder), FileName);

        public static State Read(string logFolder)
        {
            try
            {
                string path = PathFor(logFolder);
                return File.Exists(path) ? Parse(File.ReadAllText(path)) : State.Empty;
            }
            catch (Exception) { return State.Empty; }
        }

        static void Write(string logFolder, State s)
        {
            Directory.CreateDirectory(Files.Root(logFolder));
            File.WriteAllText(PathFor(logFolder), Format(s), new UTF8Encoding(false));
        }

        /// <summary><c>version=…</c>, <c>launches=N</c>, <c>openedAt=unixMs</c>, <c>fault=unixMs,launch</c> (one per line). PURE.</summary>
        public static string Format(State s)
        {
            var sb = new StringBuilder(128);
            sb.Append("version=").Append(s.Version).Append('\n')
              .Append("launches=").Append(s.Launches.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append("openedAt=").Append(s.OpenedAtMs.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var f in s.Faults)
                sb.Append("fault=").Append(f.AtMs.ToString(CultureInfo.InvariantCulture)).Append(',').Append(f.Launch.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        /// <summary>The inverse of <see cref="Format"/>; a malformed line is skipped, never thrown on. PURE.</summary>
        public static State Parse(string text)
        {
            string version = "";
            int launches = 0;
            long openedAt = 0;
            var faults = new List<Fault>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq], value = line[(eq + 1)..];
                switch (key)
                {
                    case "version": version = value; break;
                    case "launches": int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out launches); break;
                    case "openedAt": long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out openedAt); break;
                    case "fault":
                        int comma = value.IndexOf(',');
                        if (comma > 0 && long.TryParse(value.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out long at)
                            && int.TryParse(value.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int launch))
                            faults.Add(new Fault(at, launch));
                        break;
                }
            }
            return new State(version, launches, openedAt, faults.TakeLast(MaxFaults).ToArray());
        }
    }
}
