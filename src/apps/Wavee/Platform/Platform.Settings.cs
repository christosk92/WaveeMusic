// ── Platform/Platform.Settings.cs ──────────────────────────────────────────────────────────────────────────────────
// the settings-backing selection, the developer-mode signals, NetworkPolicy (the decisions + the signals),
// LogCapturePolicy, WaveeLogSessions (the line and session parse), WaveeVersionInfo, RunMarker + CrashPromptPolicy, the
// factory-reset marker plan, the ambient power verdict, ProvisioningOutcome, the Wave-6 settings keys
//
// Role: CORE
// Owner: S
// Wave: 6
// Budget: 700 lines
// Spec: ch 29 §2 W9/W25, §5.4, §8 (NetworkPolicy, ReadPlugged); ch 27 §8 (LogCapturePolicy, WaveeLogSessions), §9.3
//       item 3 (DeveloperMode); ch 22 §9 (b) (StageRects); ch 28 DATA GAP D3 (ProvisioningOutcome); gaps G-094, G-198,
//       G-199, the settings-isolation row (orchestrator, 2026-09-13)
//
// THE NAMED PARTIAL `Platform.cs` NEEDS (G-199): that file's 1,100 is spent by the Wave-0 first cut, so everything owner
// S adds to the Platform CORE lands here. ENGINE-FREE apart from three plain value types the decisions are phrased in
// (`NetworkCost`, `PowerStatus`, `Signal<T>`). The SHELL halves — the NLM subscription, the power poll, the crash
// writers, the file stores, the factory-reset wipe — are `Platform.Host.cs` and `Screens/Diagnostics.Host.cs`.
//
// WHAT IS DELIBERATELY NOT HERE. ch 28 D3's `RuntimeStatus`, `ProgressFraction` and `ShortHash` already landed ONCE, in
// `Screens/Setup.cs` (`Setup.RuntimeFacts`, `Setup.RuntimeRules`, owner I, A18); a second copy would be two answers to one
// question. Only `ProvisioningOutcome` — the enum the diagnostics report names — is new. The About receipts' formatters
// (`GpuSummary`/`FormatBytes`/`FormatUptime`) are `Settings.Receipts` (owner R), for the same reason.

using System.Globalization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Network;
using FluentGpu.WindowsApi.Power;

namespace Wavee;

// ── 1. the Wave-6 settings keys ──────────────────────────────────────────────────────────────────────────────────────

public static partial class Platform
{
    public static partial class Keys
    {
        /// <summary>The immersive lyrics stage's geometry log (ch 22 §9 (b): `FG_STAGE_RECTS` retired into a Developer
        /// row). Off: the seven OnBoundsChanged callbacks stay null, which is what keeps the cost at zero.</summary>
        public static readonly SettingKey<bool> StageRects = new("diag.stageRects", false);
    }
}

// ── 2. which store backs the settings (the isolation rule) ───────────────────────────────────────────────────────────

/// <summary>Where this launch's settings live. <see cref="Memory"/> never reads or writes the registry (a `--fake` demo
/// is deterministic: the real profile's sidebar design must not leak into it, and a pane drag must not leak out of it);
/// <see cref="ProfileFile"/> keeps a `--profile &lt;dir&gt;` run's settings inside that folder; only a normal launch
/// reads and writes HKCU.</summary>
public enum SettingsBacking : byte { Registry, ProfileFile, Memory }

public static partial class Platform
{
    /// <summary>The file a <see cref="SettingsBacking.ProfileFile"/> store keeps, inside the profile folder.</summary>
    public const string ProfileSettingsFileName = "settings.json";

    /// <summary>THE selection. `--fake` wins over `--profile` (a demo never persists, not even into a scratch folder);
    /// a non-empty <see cref="ProfileRoot"/> keeps the store inside it; otherwise the registry.</summary>
    public static SettingsBacking SettingsBackingFor(bool fake, string? profileRoot)
        => fake ? SettingsBacking.Memory
         : !string.IsNullOrEmpty(profileRoot) ? SettingsBacking.ProfileFile
         : SettingsBacking.Registry;

    /// <summary>This launch's backing, resolved in <see cref="Boot"/>.</summary>
    public static SettingsBacking Backing { get; private set; } = SettingsBacking.Registry;
}

/// <summary>A store with nothing in it: every read is the key's default, every write is dropped. The `--fake` base under
/// the headless overlay (<c>Diagnostics.Headless.OverlaySettings</c>), which is what keeps a demo's writes in memory.</summary>
public sealed class DefaultsOnlySettings : IAppSettings
{
    public static readonly DefaultsOnlySettings Instance = new();
    DefaultsOnlySettings() { }
    public T Get<T>(SettingKey<T> key) => key.Default;
    public void Set<T>(SettingKey<T> key, T value) { }
}

// ── 3. developer mode (ch 27 §9.3 item 3) ────────────────────────────────────────────────────────────────────────────

public static partial class Platform
{
    /// <summary>The ONE switch that reveals every tooling surface, plus the two developer toggles that ride it. Signals, so
    /// a flip re-renders every reader live (never next launch); written persist-then-publish, so a reader that re-reads the
    /// setting instead of the signal still agrees. Seeded once in <see cref="Boot"/>, before the first mount, so no surface
    /// paints its developer form for a frame and then removes it.</summary>
    public static class Developer
    {
        public static readonly Signal<bool> Enabled = new(false);
        public static readonly Signal<bool> FpsOverlay = new(false);
        /// <summary>`Diagnostics.StageRects` in ch 22 §9 (b): the stage reads it with <c>.Value</c> inside Render.</summary>
        public static readonly Signal<bool> StageRects = new(false);

        public static void Load(IAppSettings settings)
        {
            Enabled.Value = settings.Get(Keys.DeveloperMode);
            FpsOverlay.Value = settings.Get(Keys.FpsOverlay);
            StageRects.Value = settings.Get(Keys.StageRects);
        }

        public static void Set(bool on) { Settings.Set(Keys.DeveloperMode, on); Enabled.Value = on; }
        public static void SetFpsOverlay(bool on) { Settings.Set(Keys.FpsOverlay, on); FpsOverlay.Value = on; }
        public static void SetStageRects(bool on) { Settings.Set(Keys.StageRects, on); StageRects.Value = on; }

        /// <summary>The HUD shows only when BOTH switches are on (ch 27 W26). Pure, so the gate is a fact.</summary>
        public static bool ShowsFpsOverlay(bool developerMode, bool overlay) => developerMode && overlay;
    }
}

// ── 4. NetworkPolicy (ch 29 W9, §8) ──────────────────────────────────────────────────────────────────────────────────

public static partial class Platform
{
    /// <summary>The connection-cost policy: caps streaming quality on a metered link and defers prefetch. Fail-soft to
    /// <see cref="NetworkCost.Unknown"/> — unmetered-conservative, a probe failure never throttles playback.
    ///
    /// <para>THE PORT TRAP ch 29 W9 names: 0.2.9's <c>EffectiveQuality(int, int)</c> read a mutable static, so it could
    /// not be tested. Here every decision takes the cost as a PARAMETER; the no-argument conveniences read the live
    /// snapshot the host keeps. Unlimited is spelled 0 on the way IN (a stored video cap) and int.MaxValue on the way
    /// OUT, as 0.2.9 did.</para>
    ///
    /// <para>The rung ceiling is <see cref="QualityMax"/> = 3 (Lossless, decision D21): the metered cap caps the lossless
    /// rung exactly as it caps 320.</para></summary>
    public static partial class Network
    {
        public const int QualityMin = 0, QualityMax = 3;

        /// <summary>The kind + limit/roaming bits, for the settings status line. UI-thread writes only.</summary>
        public static readonly Signal<NetworkCost> Cost = new(NetworkCost.Unknown);
        /// <summary>The bool projection of <see cref="Cost"/>.</summary>
        public static readonly Signal<bool> Metered = new(false);
        /// <summary>The persisted metered cap (0..3), seeded at install; the Settings combo binds it live (ch 27 33b).</summary>
        public static readonly Signal<int> MeteredQualityCap = new(Keys.MeteredQualityCap.Default);
        /// <summary>The protected-video Auto height cap on a metered link; 0 = unlimited.</summary>
        public static readonly Signal<int> MeteredVideoMaxHeight = new(Keys.VideoMeteredMaxHeight.Default);

        // A packed snapshot for the non-UI readers (the audio open, the update scheduler): one int, one volatile read.
        static int s_packed;

        /// <summary><c>min(user, cap)</c> when metered, else the user's rung; both clamped. Unknown is unmetered.</summary>
        public static int EffectiveQuality(int userQuality, int meteredCap, in NetworkCost cost)
        {
            int q = Math.Clamp(userQuality, QualityMin, QualityMax);
            int cap = Math.Clamp(meteredCap, QualityMin, QualityMax);
            return cost.IsMetered ? Math.Min(q, cap) : q;
        }

        /// <summary>The protected-video Auto height cap: int.MaxValue unless metered AND a positive cap is stored.</summary>
        public static int EffectiveVideoMaxHeight(int storedCap, in NetworkCost cost)
            => !cost.IsMetered || storedCap <= 0 ? int.MaxValue : storedCap;

        /// <summary>Prefetch and CDN warm-up wait on a metered link; an unknown cost does not defer.</summary>
        public static bool ShouldDeferPrefetch(in NetworkCost cost) => cost.IsMetered;

        /// <summary>The live snapshot (any thread).</summary>
        public static NetworkCost Current => Unpack(Volatile.Read(ref s_packed));
        public static bool IsMetered => Current.IsMetered;

        /// <summary>The rung the audio open should aim at, from the settings and the live cost (any thread). THE
        /// composition site for `Spotify.Audio.PreferredQuality` (owner B4) and the headless `quality` verb.</summary>
        public static int EffectiveQuality()
        {
            var cost = Current;
            return EffectiveQuality(Settings.Get(Keys.PlaybackQuality), Settings.Get(Keys.MeteredQualityCap), in cost);
        }

        /// <summary>The live video cap (any thread) — the second composition site (`Playback.Video`, owner B7).</summary>
        public static int EffectiveVideoMaxHeight()
        {
            var cost = Current;
            return EffectiveVideoMaxHeight(Settings.Get(Keys.VideoMeteredMaxHeight), in cost);
        }

        /// <summary>UI THREAD. Apply a polled or pushed snapshot; only a CHANGE writes the signals or logs, so the 60 s
        /// poll republishing the same cost is silent. Returns whether anything changed.</summary>
        public static bool Apply(NetworkCost cost)
        {
            int packed = Pack(in cost);
            if (Volatile.Read(ref s_packed) == packed && Cost.Peek() == cost) return false;
            Volatile.Write(ref s_packed, packed);
            Log.Event(WaveeLogLevel.Info, "network", "network.cost.changed", "", null, -1, null,
                WaveeLogField.Of("kind", cost.Kind.ToString()),
                WaveeLogField.Of("metered", cost.IsMetered),
                WaveeLogField.Of("overLimit", cost.OverDataLimit),
                WaveeLogField.Of("roaming", cost.Roaming));
            Cost.Value = cost;
            if (Metered.Peek() != cost.IsMetered) Metered.Value = cost.IsMetered;
            return true;
        }

        /// <summary>UI THREAD. Seed the two cap signals from the store (install time).</summary>
        public static void SeedCaps(IAppSettings settings)
        {
            MeteredQualityCap.Value = Math.Clamp(settings.Get(Keys.MeteredQualityCap), QualityMin, QualityMax);
            MeteredVideoMaxHeight.Value = Math.Max(0, settings.Get(Keys.VideoMeteredMaxHeight));
        }

        /// <summary>The Settings combo's writers: persist, then publish.</summary>
        public static void SetMeteredQualityCap(int rung)
        {
            int v = Math.Clamp(rung, QualityMin, QualityMax);
            Settings.Set(Keys.MeteredQualityCap, v);
            MeteredQualityCap.Value = v;
        }

        public static void SetMeteredVideoMaxHeight(int height)
        {
            int v = Math.Max(0, height);
            Settings.Set(Keys.VideoMeteredMaxHeight, v);
            MeteredVideoMaxHeight.Value = v;
        }

        internal static int Pack(in NetworkCost c) => 1 << 8 | (int)c.Kind | (c.OverDataLimit ? 1 << 4 : 0)
            | (c.ApproachingDataLimit ? 1 << 5 : 0) | (c.Roaming ? 1 << 6 : 0);

        internal static NetworkCost Unpack(int p) => (p & 1 << 8) == 0 ? NetworkCost.Unknown
            : new NetworkCost((NetworkCostKind)(p & 0xF), (p & 1 << 4) != 0, (p & 1 << 5) != 0, (p & 1 << 6) != 0);
    }
}

// ── 5. the ambient power verdict (ch 29 W25, §5.4, §8) ───────────────────────────────────────────────────────────────

public static partial class Platform
{
    /// <summary>The POLICY half of the ambient cadence: the 2 s poll, the two-read hold window and the verdict. The
    /// values it writes are `Design.Cadence`'s; the poll and the host writes are `Platform.Host.cs`.</summary>
    public static class AmbientPower
    {
        /// <summary>How long a new reading must hold before it may change the cadence — equal to the poll on purpose, so
        /// a real transition costs exactly two reads and a blip shorter than one interval is usually never sampled.</summary>
        public const int PollMs = 2000;
        public const double DebounceSeconds = 2.0;

        /// <summary>"Is this machine on wall power?" Energy saver ⇒ NOT plugged (the OS asks for less work). No battery,
        /// an unknown line status or a FAILED read ⇒ plugged, or every desktop would run permanently half-capped.</summary>
        public static bool Plugged(bool readOk, in PowerStatus status)
        {
            if (!readOk) return true;
            if (status.EnergySaverOn) return false;
            return !status.HasBattery || status.Source != PowerSource.Dc;
        }

        /// <summary>The loop rate a cadence-less `loop: true` row runs at for a verdict.</summary>
        public static float LoopHzFor(bool plugged) => plugged ? Design.Cadence.PluggedLoopHz : Design.Cadence.BatteryLoopHz;

        /// <summary>The debounce state: the APPLIED verdict and the most recent READ one still inside its window.</summary>
        public struct Debounce
        {
            public bool Applied, Pending;
            public long PendingSince;

            /// <summary>Launch: applied immediately, no debounce.</summary>
            public static Debounce Start(bool reading, long now) => new() { Applied = reading, Pending = reading, PendingSince = now };
        }

        /// <summary>One debounced sample. A changed reading only re-arms the window; the applied verdict flips when the new
        /// reading has held for <see cref="DebounceSeconds"/>. Returns true exactly when <see cref="Debounce.Applied"/>
        /// changed (the host then writes the loop rate).</summary>
        public static bool Step(ref Debounce d, bool reading, long now, long ticksPerSecond)
        {
            if (reading != d.Pending) { d.Pending = reading; d.PendingSince = now; return false; }
            if (reading == d.Applied) return false;
            if (now - d.PendingSince < (long)(DebounceSeconds * ticksPerSecond)) return false;
            d.Applied = reading;
            return true;
        }
    }
}

// ── 6. LogCapturePolicy (ch 27 §8) ───────────────────────────────────────────────────────────────────────────────────

/// <summary>The Verbose toggle + the two capture-level combos' decision core, and the ONE place that resolves a persisted
/// level (-1 = the build default) — `Platform.Host.cs`'s log open at boot and the logs panel at runtime both call through
/// here, so the two can never disagree about what "-1" means. CLAUDE.md forbids the 0.2.x env-var level switches.</summary>
public static class LogCapturePolicy
{
    /// <summary>The ring's default: full Debug detail in a Debug build, Info in Release.</summary>
    public static WaveeLogLevel BuildDefaultMinLevel =>
#if DEBUG
        WaveeLogLevel.Debug;
#else
        WaveeLogLevel.Info;
#endif

    /// <summary>The file's default in every build — a Debug run's verbose ring does not bloat the file.</summary>
    public const WaveeLogLevel BuildDefaultFileLevel = WaveeLogLevel.Info;

    /// <summary>-1 → the build default; anything else clamps into the user-selectable Trace..Error range.</summary>
    public static WaveeLogLevel Resolve(int setting, WaveeLogLevel buildDefault) =>
        setting < 0 ? buildDefault : (WaveeLogLevel)Math.Clamp(setting, (int)WaveeLogLevel.Trace, (int)WaveeLogLevel.Error);

    /// <summary>A level that IS the build default is stored as -1, so a later default change carries "never touched" forward.</summary>
    public static int ToSetting(WaveeLogLevel level, WaveeLogLevel buildDefault) => level == buildDefault ? -1 : (int)level;

    /// <summary>Verbose is "on" exactly when the ring captures Debug or below — derived, never a second persisted bool.</summary>
    public static bool IsVerbose(WaveeLogLevel min) => min <= WaveeLogLevel.Debug;

    /// <summary>On → Trace (the deepest level); off → the build default (a Debug build's "off" is still Debug).</summary>
    public static WaveeLogLevel VerboseTarget(bool on, WaveeLogLevel buildDefault) => on ? WaveeLogLevel.Trace : buildDefault;

    /// <summary>What actually reaches disk: the file filter is upward-only against the ring's MinLevel.</summary>
    public static WaveeLogLevel EffectiveFileLevel(WaveeLogLevel min, WaveeLogLevel file) => file < min ? min : file;

    /// <summary>The panel's only writers: apply to the running log AND persist, together.</summary>
    public static void SetMinLevel(IAppSettings settings, WaveeLogLevel level)
    {
        Log.MinLevel = level;
        settings.Set(Platform.Keys.LogMinLevel, ToSetting(level, BuildDefaultMinLevel));
    }

    public static void SetFileLevel(IAppSettings settings, WaveeLogLevel level)
    {
        Log.FileMinLevel = level;
        settings.Set(Platform.Keys.LogFileMinLevel, ToSetting(level, BuildDefaultFileLevel));
    }

    public static void SetVerbose(IAppSettings settings, bool on) => SetMinLevel(settings, VerboseTarget(on, BuildDefaultMinLevel));
}

// ── 7. WaveeLogSessions — the pure half (ch 27 §8; the disk walk is Diagnostics.Host.cs) ─────────────────────────────

/// <summary>Past app sessions re-read from the dated wavee-*.log set. A session opens where the run identity changes —
/// the <c>sid=</c> token when the line carries one, else 0.2.x's heuristic (a "Wavee starting" line or a sequence reset);
/// it may span a size roll, so the files are walked as ONE chronological stream. Parsing is LOSSY by design: the formatted
/// remainder becomes the Message verbatim, which is what the log viewer renders anyway. Every function here takes the
/// line reader as a parameter, so the whole walk is a fact over in-memory lines.</summary>
public static partial class WaveeLogSessions
{
    /// <summary>One session: a [start, end) line range over the chronological file list.</summary>
    public sealed record Info(string[] Files, int FileStart, int LineStart, int FileEnd, int LineEnd,
                              long StartUnixMs, int Pid, int EntryCount, string SessionId);

    /// <summary>The rolled files in chronological order (their yyyyMMdd[-HHmmss] names sort ordinally), then the live
    /// file when it exists.</summary>
    public static string[] Chronological(string[] rolled, string? live)
    {
        var copy = (string[])rolled.Clone();
        Array.Sort(copy, StringComparer.Ordinal);
        if (live is null) return copy;
        var all = new string[copy.Length + 1];
        copy.CopyTo(all, 0);
        all[^1] = live;
        return all;
    }

    /// <summary>Every COMPLETED session, newest first. The trailing session that is THIS run (sid match, else pid for
    /// legacy sid-less files) is dropped — the live ring is the richer source for it. An unreadable file skips only
    /// itself; the boundary state machine carries across the gap.</summary>
    public static List<Info> Split(string[] files, Func<string, IEnumerable<string>> readLines, string currentSessionId, int currentPid)
    {
        var sessions = new List<Info>();
        long prevSeq = long.MaxValue;
        string prevSid = "";
        bool open = false;
        int curFile = 0, curLine = 0, curPid = 0, curCount = 0;
        long curStart = 0;
        string curSid = "";

        for (int fi = 0; fi < files.Length; fi++)
        {
            int li = 0;
            try
            {
                foreach (string line in readLines(files[fi]))
                {
                    if (TryParseLine(line, out var e, out bool isStart, out int pid, out string sid))
                    {
                        bool boundary = sid.Length > 0 ? sid != prevSid : isStart || e.Sequence < prevSeq;
                        if (boundary)
                        {
                            if (open) sessions.Add(new Info(files, curFile, curLine, fi, li, curStart, curPid, curCount, curSid));
                            open = true;
                            curFile = fi; curLine = li; curStart = e.UnixMs; curPid = pid; curCount = 0; curSid = sid;
                        }
                        prevSeq = e.Sequence;
                        prevSid = sid;
                        if (open) curCount++;
                    }
                    li++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (open) sessions.Add(new Info(files, curFile, curLine, files.Length - 1, int.MaxValue, curStart, curPid, curCount, curSid));

        if (sessions.Count > 0)
        {
            var last = sessions[^1];
            bool isCurrent = last.SessionId.Length > 0 ? last.SessionId == currentSessionId : last.Pid == currentPid;
            if (isCurrent) sessions.RemoveAt(sessions.Count - 1);
        }
        sessions.Reverse();
        return sessions;
    }

    /// <summary>A session's stable key for the report dialog: its sid, or pid + start for a legacy sid-less file.</summary>
    public static string KeyOf(Info s) => s.SessionId.Length > 0
        ? s.SessionId
        : "pid" + s.Pid.ToString(CultureInfo.InvariantCulture) + "@" + s.StartUnixMs.ToString(CultureInfo.InvariantCulture);

    /// <summary>The session <paramref name="key"/> names; a null key is the newest past session (the list is newest-first).</summary>
    public static Info? Find(List<Info> sessions, string? key)
    {
        if (key is null) return sessions.Count > 0 ? sessions[0] : null;
        foreach (var s in sessions) if (KeyOf(s) == key) return s;
        return null;
    }

    /// <summary>The session's raw lines (parsed or not), in file order.</summary>
    public static List<string> RawLines(Info info, Func<string, IEnumerable<string>> readLines)
    {
        var lines = new List<string>(Math.Max(info.EntryCount, 16));
        for (int fi = info.FileStart; fi <= info.FileEnd && fi < info.Files.Length; fi++)
        {
            int from = fi == info.FileStart ? info.LineStart : 0;
            int to = fi == info.FileEnd ? info.LineEnd : int.MaxValue;
            int li = 0;
            try
            {
                foreach (string line in readLines(info.Files[fi]))
                {
                    if (li >= to) break;
                    if (li >= from) lines.Add(line);
                    li++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return lines;
    }

    /// <summary>The session's entries, capped to the LAST <paramref name="maxEntries"/> (ring parity).</summary>
    public static WaveeLogEntry[] Load(Info info, Func<string, IEnumerable<string>> readLines, int maxEntries = 4096)
    {
        var raw = RawLines(info, readLines);
        var list = new List<WaveeLogEntry>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
            if (TryParseLine(raw[i], out var e, out _, out _, out _)) list.Add(e);
        if (list.Count > maxEntries) list.RemoveRange(0, list.Count - maxEntries);
        return list.ToArray();
    }

    /// <summary>"[ISO-UTC ]seq=N tid=M [t=U] [sid=S] [pid=P] L [category] rest" → a lossy entry. The ISO prefix is a
    /// pre-t= build's timestamp; a line with neither parses with UnixMs = 0.</summary>
    public static bool TryParseLine(string line, out WaveeLogEntry entry, out bool isStartMarker, out int pid, out string sid)
    {
        entry = default;
        isStartMarker = false;
        pid = 0;
        sid = "";
        int at = 0;
        long isoMs = 0;
        if (!line.StartsWith("seq=", StringComparison.Ordinal))
        {
            int idx = line.IndexOf(" seq=", StringComparison.Ordinal);
            if (idx <= 0 || idx > 33 || !char.IsAsciiDigit(line[0])) return false;
            if (!DateTimeOffset.TryParse(line.AsSpan(0, idx), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) return false;
            isoMs = ts.ToUnixTimeMilliseconds();
            at = idx + 1;
        }
        if (!Expect(line, ref at, "seq=") || !ReadLong(line, ref at, out long seq)) return false;
        if (!Expect(line, ref at, " tid=") || !ReadLong(line, ref at, out long tid)) return false;
        long unixMs = isoMs;
        if (Expect(line, ref at, " t=") && !ReadLong(line, ref at, out unixMs)) return false;
        if (Expect(line, ref at, " sid=")) { int s = at; while (at < line.Length && line[at] != ' ') at++; sid = line[s..at]; }
        if (Expect(line, ref at, " pid=") && ReadLong(line, ref at, out long p)) pid = (int)Math.Min(p, int.MaxValue);
        if (at + 3 >= line.Length || line[at] != ' ') return false;

        WaveeLogLevel level;
        switch (line[at + 1])
        {
            case 'T': level = WaveeLogLevel.Trace; break;
            case 'D': level = WaveeLogLevel.Debug; break;
            case 'I': level = WaveeLogLevel.Info; break;
            case 'W': level = WaveeLogLevel.Warning; break;
            case 'E': level = WaveeLogLevel.Error; break;
            case 'C': level = WaveeLogLevel.Critical; break;
            default: return false;
        }
        if (line[at + 2] != ' ' || line[at + 3] != '[') return false;
        int catStart = at + 4;
        int catEnd = line.IndexOf(']', catStart);
        if (catEnd < 0) return false;
        string category = line[catStart..catEnd];
        int restAt = catEnd + 1;
        if (restAt < line.Length && line[restAt] == ' ') restAt++;
        string rest = restAt < line.Length ? line[restAt..] : "";

        if (category == "app" && rest.StartsWith("startup - Wavee starting", StringComparison.Ordinal))
        {
            isStartMarker = true;
            if (pid == 0)
            {
                int q = rest.IndexOf(" pid=", StringComparison.Ordinal);
                if (q >= 0) { int k = q + 5; ReadLong(rest, ref k, out long v); pid = (int)Math.Min(v, int.MaxValue); }
            }
        }
        entry = new WaveeLogEntry(seq, unixMs, level, category, "", rest, null, (int)tid, -1, null, null);
        return true;
    }

    static bool Expect(string s, ref int at, string token)
    {
        if (at + token.Length > s.Length || string.CompareOrdinal(s, at, token, 0, token.Length) != 0) return false;
        at += token.Length;
        return true;
    }

    static bool ReadLong(string s, ref int at, out long value)
    {
        value = 0;
        int start = at;
        while (at < s.Length && char.IsAsciiDigit(s[at])) { value = value * 10 + (s[at] - '0'); at++; }
        return at > start;
    }
}

// ── 8. WaveeVersionInfo (G-198) ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>Everything the app knows about its own build, parsed once from the assembly metadata `Wavee.csproj` stamps.
/// Pure: the caller hands in the already-read pairs (`Platform.Version` in Platform.Host.cs does the read). Ported from
/// 0.2.9's `Wavee.Core.Versioning.WaveeVersionInfo`, verbatim in behaviour.</summary>
/// <param name="SemVer">"0.3.0-beta.1" · "0.3.0-dev" — the informational version with build metadata stripped.</param>
/// <param name="Core">"0.3.0" — SemVer without its pre-release suffix.</param>
/// <param name="Beta">1 for <c>-beta.1</c>, else null.</param>
/// <param name="Quad">"0.3.0.17" — the MSIX identity version; "" for an unstamped (dev) build.</param>
/// <param name="Codename">The per-minor codename; "" if unstamped.</param>
/// <param name="Channel">"stable" · "beta" · "dev" · "store".</param>
/// <param name="FeedRelease">The rolling release carrying the .appinstaller feed (build-time metadata, never a switch).</param>
/// <param name="UpdateBaseUrl">Where the feed's assets live; always ends in "/".</param>
public sealed record WaveeVersionInfo(
    string SemVer, string Core, int? Beta, string Quad, string Codename, string Channel, string Commit, string BuildDate,
    string FeedRelease = "wavee-stable", string UpdateBaseUrl = WaveeVersionInfo.DefaultUpdateBaseUrl, string StoreId = "")
{
    public const string DefaultUpdateBaseUrl = "https://github.com/christosk92/WaveeMusic/releases/download/";

    /// <summary>A Store build: the Store owns updates, "Update now" opens the product page.</summary>
    public bool IsStore => Channel == "store";

    /// <summary>A build Windows never installed: no quad, or an explicit dev channel. It never sees an update prompt.</summary>
    public bool IsDev => Channel == "dev" || Quad.Length == 0;

    /// <summary>Trim; empty → the default; guarantee the trailing slash (every caller concatenates a tail).</summary>
    public static string NormalizeUpdateBaseUrl(string? raw)
    {
        string s = raw?.Trim() ?? "";
        if (s.Length == 0) return DefaultUpdateBaseUrl;
        return s.EndsWith('/') ? s : s + "/";
    }

    /// <summary>The culture-free product name: <c>Wavee 0.3.0 “Crest”</c>; an unstamped codename drops the quotes; a dev
    /// build shows its raw semver. Crash headers and copied diagnostics use it; the UI renders a localized form.</summary>
    public string Display
    {
        get
        {
            if (IsDev) return "Wavee " + SemVer;
            string named = Codename.Length > 0 ? "Wavee " + Core + " “" + Codename + "”" : "Wavee " + Core;
            return Beta is int b ? named + " · Beta " + b.ToString(CultureInfo.InvariantCulture) : named;
        }
    }

    /// <summary>RFC 9110 product token — ThirdParty/GitHub clients only, never the Spotify-facing UA.</summary>
    public string UserAgent(string os, string arch) => "Wavee/" + Core + " (build " + Quad + "; " + Channel + "; " + os + "; " + arch + ")";

    /// <summary>The one-line build stamp for About / crash headers / copied diagnostics.</summary>
    public string OneLine(string arch) => Display + " · build " + Quad + " · " + Commit + " · " + BuildDate + " · " + arch;

    /// <summary>The value written to <c>app.lastRunVersion</c>: the quad; a dev build writes its semver so it never "updates".</summary>
    public string LastRunKey => IsDev ? SemVer : Quad;

    /// <summary>Informational version + the AssemblyMetadata pairs. Never throws: anything missing degrades to dev.</summary>
    public static WaveeVersionInfo Parse(string? informational, IReadOnlyDictionary<string, string>? metadata)
    {
        string inf = string.IsNullOrWhiteSpace(informational) ? "dev" : informational.Trim();
        int plus = inf.IndexOf('+');
        string semver = plus > 0 ? inf[..plus] : inf;
        int dash = semver.IndexOf('-');
        string core = dash > 0 ? semver[..dash] : semver;
        int? beta = null;
        if (dash > 0 && semver.AsSpan(dash + 1).StartsWith("beta.")
            && int.TryParse(semver.AsSpan(dash + 6), NumberStyles.None, CultureInfo.InvariantCulture, out int b)) beta = b;

        string Get(string k) => metadata is not null && metadata.TryGetValue(k, out var v) && v is not null ? v : "";
        string channel = Get("Channel");
        if (channel.Length == 0) channel = "dev";
        string feed = Get("FeedRelease");
        if (feed.Length == 0) feed = "wavee-stable";
        return new(semver, core, beta, Get("PackageVersion"), Get("Codename"), channel, Get("Commit"), Get("BuildDate"),
                   feed, NormalizeUpdateBaseUrl(Get("UpdateBaseUrl")), Get("StoreId"));
    }
}

// ── 9. the run marker and the crash prompt (G-094, S half) ───────────────────────────────────────────────────────────

/// <summary>The outcome of the PREVIOUS run, read at the start of this one.</summary>
public enum RunOutcome : byte { Unknown, Clean, Unclean }

/// <summary>A one-value "was the last run clean" breadcrumb bracketing every GUI launch: <see cref="Begin"/> reads what the
/// previous process left and overwrites it with "running"; <see cref="End"/> flips it to "clean" on an orderly exit. A
/// marker still reading "running" means the previous process never reached its shutdown — a crash, a kill, or an
/// OS-forced termination. "crashed" is written by a handler that already wrote its report and reads as Unclean, so a
/// report that failed to land still leaves the fallback prompt.</summary>
public static class RunMarker
{
    public const string Running = "running", Clean = "clean", Crashed = "crashed";

    public static RunOutcome Begin(IAppSettings s)
    {
        string prev = s.Get(Platform.Keys.RunMarker);
        s.Set(Platform.Keys.RunMarker, Running);
        return prev switch { "" => RunOutcome.Unknown, Running or Crashed => RunOutcome.Unclean, _ => RunOutcome.Clean };
    }

    /// <summary>Only downgrades our own "running" (never a "crashed" a handler wrote for THIS run); an orderly exit ends
    /// an unclean streak, so the next stale marker is offer-worthy again.</summary>
    public static void End(IAppSettings s)
    {
        if (s.Get(Platform.Keys.RunMarker) == Running) s.Set(Platform.Keys.RunMarker, Clean);
        s.Set(Platform.Keys.UncleanExitOffered, false);
    }

    public static void MarkCrashed(IAppSettings s)
    {
        s.Set(Platform.Keys.RunMarker, Crashed);
        s.Set(Platform.Keys.UncleanExitOffered, false);
    }
}

/// <summary>Where the evidence that the previous run crashed came from, strongest first.</summary>
public enum CrashSource : byte { None, ManagedReport, WerDump, UncleanExit }

/// <summary>How this launch surfaces the crash.</summary>
public enum CrashPromptMode : byte { None, Dialog, Toast }

public readonly record struct CrashPromptDecision(CrashPromptMode Mode, CrashSource Source, string? ReportPath, string? DumpPath);

/// <summary>Decides, once per launch, whether and how to tell the user the previous run crashed. The GUI run computes it
/// (`Diagnostics.Host.cs`) and latches it on <see cref="ThisLaunch"/>; the report chrome (`Feedback.UI.cs`, owner R)
/// consumes it once.</summary>
public static class CrashPromptPolicy
{
    /// <summary>Latched for this launch; the consumer resets it to default (one-shot).</summary>
    public static CrashPromptDecision ThisLaunch;

    /// <param name="versionChanged">The previous process was killed by an update deployment: suppresses ONLY the
    /// evidence-free UncleanExit, never a managed report or a WER dump.</param>
    /// <param name="uncleanExitOffered">An UncleanExit prompt was already offered in this unclean streak — a process
    /// stopped from Task Manager every run must not re-ask after every dismissal.</param>
    public static CrashPromptDecision Decide(string pendingReport, string? newDumpPath, RunOutcome previousRun, bool optOut,
        bool versionChanged, bool uncleanExitOffered)
    {
        CrashSource src = pendingReport.Length > 0 ? CrashSource.ManagedReport
                        // The dump folder is machine-wide (every Wavee.exe, any checkout or profile): a dump is evidence about THIS
                        // profile only when the profile has run before. A fresh profile (a --fake run, a new --profile) never prompts.
                        : newDumpPath is { Length: > 0 } && previousRun != RunOutcome.Unknown ? CrashSource.WerDump
                        : previousRun == RunOutcome.Unclean && !versionChanged && !uncleanExitOffered && !optOut ? CrashSource.UncleanExit
                        : CrashSource.None;
        if (src == CrashSource.None) return default;
        return new CrashPromptDecision(optOut ? CrashPromptMode.Toast : CrashPromptMode.Dialog, src,
            src == CrashSource.ManagedReport ? pendingReport : null, newDumpPath);
    }
}

// ── 10. the factory-reset marker plan (G-094, S half) ────────────────────────────────────────────────────────────────

/// <summary>A factory reset erases every local Wavee artifact on the NEXT process (the live one still holds library.db
/// and the instance mutex). The marker lives outside the wipe roots and lists only EXTRA roots (a relocated audio cache);
/// the defaults are recomputed at apply time. Pure path arithmetic; the disk half is `Platform.Host.cs`.</summary>
public static class FactoryResetPlan
{
    public const string MarkerFileName = "Wavee.factory-reset.pending";

    /// <summary>The marker's lines: every extra root as a full path, minus any already under a default root.</summary>
    public static List<string> MarkerLines(IEnumerable<string>? extraRoots, IReadOnlyList<string> defaultRoots)
    {
        var lines = new List<string>();
        if (extraRoots is null) return lines;
        foreach (string raw in extraRoots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path = Path.GetFullPath(raw);
            if (!IsUnderAny(path, defaultRoots)) lines.Add(path);
        }
        return lines;
    }

    /// <summary>What an apply wipes: the defaults, then every non-blank marker line.</summary>
    public static List<string> Roots(IReadOnlyList<string> defaultRoots, IReadOnlyList<string> markerLines)
    {
        var roots = new List<string>(defaultRoots.Count + markerLines.Count);
        roots.AddRange(defaultRoots);
        foreach (string line in markerLines)
        {
            string p = line.Trim();
            if (p.Length > 0) roots.Add(p);
        }
        return roots;
    }

    public static bool IsUnderAny(string path, IReadOnlyList<string> roots)
    {
        foreach (string root in roots) if (IsUnder(path, root)) return true;
        return false;
    }

    public static bool IsUnder(string path, string root)
    {
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

// ── 11. ProvisioningOutcome (ch 28 DATA GAP D3) ──────────────────────────────────────────────────────────────────────

/// <summary>How a local-playback provisioning pass ended — the enum the runtime diagnostics report names (0.2.9's public
/// seam contract, unchanged values). The card's own facts are `Setup.RuntimeFacts`.</summary>
public enum ProvisioningOutcome { Ready, NeverAttempted, RuntimeUnavailable, NoSupportedPack, PackDownloadFailed, HashMismatch, SignatureInvalid, ArchUnsupported }
