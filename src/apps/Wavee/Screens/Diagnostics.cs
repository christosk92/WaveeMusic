// ── Screens/Diagnostics.cs ─────────────────────────────────────────────────────────────────────────────────────────
// LogView, the runtime diagnostics report (the seam records + BuildRuntimeReport), the module / release-notes report
// seams, the Connect traces, the crash-report file rule and frame parse, the frame-session totals and the sidebar-pane
// invariant edge, Diagnostics.StageRects, the lyrics inspector's pure half (LyricsReport)
//
// Role: CORE
// Owner: S
// Wave: 6
// Budget: 600 lines
// Spec: ch 27 §9.3, §8 (LogView, BuildReport), §7 DATA GAPS G7/G8/G14; ch 22 §9 (b), (d), W17-W19b; gaps G-094, G-153,
//       G-183, G-197
//
// ENGINE-FREE (one Signal for the Connect traces' version). Every decision the Logs page, the two diagnostics pages,
// the lyrics inspector and the frame watch render is a function here with a fact in Wavee.Tests; `Diagnostics.UI.cs` only
// lays them out and `Diagnostics.Host.cs` only touches the disk, the process and the engine.
//
// NOT HERE, ON PURPOSE: ch 27 §8's `GpuSummary` / `ClassifySharedIgpu` / `FormatBytes` / `FormatUptime` landed first as
// `Settings.Receipts` (owner R, `Screens/Settings.cs`) — one copy, read by both owners. `LogCapturePolicy` and the pure
// half of `WaveeLogSessions` are `Platform/Platform.Settings.cs` (the log's level fold runs in `Platform.Boot`). The probe
// arms' pure half (`ProbeOptions`, `SimRules`, `Bench`, `QrPng`) sits in its own section of `Diagnostics.Probe.Arms.cs`,
// because this file would otherwise pass its 600 by more than 30 %.

using System.Globalization;
using System.Text;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Diagnostics
{
    // ══ 1. THE LOG VIEW (ch 27 §8: `LogView`, 16 facts in 0.2.9) ════════════════════════════════════════════════════

    /// <summary>The Segmented level filter — index-compatible with the four-item strip.</summary>
    public enum LogLevelBucket : byte { All = 0, InfoPlus = 1, Warnings = 2, Errors = 3 }

    /// <summary>One rendered row: the entry that anchors it (the FIRST of a grouped run) and how many it stands for.</summary>
    public readonly record struct LogViewRow(WaveeLogEntry Entry, int Repeat);

    /// <summary>The filter/sort/cap state one build applies. A record so the panel can diff it for the remount Key.</summary>
    public sealed record LogViewQuery(
        LogLevelBucket Level = LogLevelBucket.All, string? Category = null, string Search = "",
        bool NewestFirst = true, bool GroupRepeats = true, int Cap = LogView.PageRows);

    /// <summary>The built view. <see cref="Total"/> and the two badge counts are over EVERY entry in the span — never the
    /// filtered subset — so the badges and the footer's "of N" describe the whole session.</summary>
    public sealed record LogViewResult(LogViewRow[] Rows, int Total, int WarningCount, int ErrorCount, bool Truncated)
    {
        public int Shown => Rows.Length;
        public static readonly LogViewResult Empty = new([], 0, 0, 0, false);
    }

    public static class LogView
    {
        /// <summary>The page size "Load more" grows the cap by, and the ceiling past which it stops offering.</summary>
        public const int PageRows = 500, MaxRows = 2000;

        /// <summary>The level combos' items, Trace..Error — index == (int)level; Critical is never user-selectable.</summary>
        public static readonly string[] LevelNames = ["Trace", "Debug", "Info", "Warning", "Error"];

        /// <summary>Filter → group → cap, oldest- or newest-first. Level/category/search run per entry, THEN adjacent
        /// repeats collapse (a run cannot straddle a filtered-out entry), THEN the cap stops the walk the moment one more
        /// row would pass it.</summary>
        public static LogViewResult Build(ReadOnlySpan<WaveeLogEntry> entries, LogViewQuery query)
        {
            if (entries.Length == 0) return LogViewResult.Empty;
            int warn = 0, err = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Level == WaveeLogLevel.Warning) warn++;
                else if (entries[i].Level >= WaveeLogLevel.Error) err++;
            }
            int cap = Math.Clamp(query.Cap, 1, MaxRows);
            var rows = new List<LogViewRow>(Math.Min(entries.Length, cap));
            int start = query.NewestFirst ? entries.Length - 1 : 0, end = query.NewestFirst ? -1 : entries.Length;
            int step = query.NewestFirst ? -1 : 1;
            bool truncated = false;
            for (int i = start; i != end; i += step)
            {
                var e = entries[i];
                if (!PassesLevel(e.Level, query.Level)) continue;
                if (query.Category is { Length: > 0 } cat && !string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase)) continue;
                if (query.Search.Length > 0 && !Matches(in e, query.Search)) continue;
                if (query.GroupRepeats && rows.Count > 0 && IsRepeatOf(rows[^1].Entry, in e))
                {
                    rows[^1] = rows[^1] with { Repeat = rows[^1].Repeat + 1 };
                    continue;
                }
                if (rows.Count >= cap) { truncated = true; break; }
                rows.Add(new LogViewRow(e, 1));
            }
            return new LogViewResult(rows.ToArray(), entries.Length, warn, err, truncated);
        }

        public static bool PassesLevel(WaveeLogLevel level, LogLevelBucket bucket) => bucket switch
        {
            LogLevelBucket.InfoPlus => level >= WaveeLogLevel.Info,
            LogLevelBucket.Warnings => level >= WaveeLogLevel.Warning,
            LogLevelBucket.Errors => level >= WaveeLogLevel.Error,
            _ => true,
        };

        /// <summary>OrdinalIgnoreCase over category, event id, message, operation id and every field's name and value.</summary>
        public static bool Matches(in WaveeLogEntry e, string query)
        {
            const StringComparison C = StringComparison.OrdinalIgnoreCase;
            if (e.Category.Contains(query, C) || e.EventId.Contains(query, C) || e.Message.Contains(query, C)) return true;
            if (e.OperationId?.Contains(query, C) == true) return true;
            if (e.Fields is { } fields)
                for (int i = 0; i < fields.Length; i++)
                    if (fields[i].Name.Contains(query, C) || fields[i].Value.Contains(query, C)) return true;
            return false;
        }

        /// <summary>Level, category, event id AND message all match — two messages at the same instant never merge.</summary>
        public static bool IsRepeatOf(in WaveeLogEntry a, in WaveeLogEntry b) =>
            a.Level == b.Level && a.Category == b.Category && a.EventId == b.EventId && a.Message == b.Message;

        /// <summary>Distinct categories, case-insensitively, sorted — the category combo's live item list.</summary>
        public static string[] Categories(ReadOnlySpan<WaveeLogEntry> entries)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Length; i++) if (entries[i].Category.Length > 0) set.Add(entries[i].Category);
            var result = new string[set.Count];
            set.CopyTo(result);
            return result;
        }

        /// <summary>The category combo's index for a name: 0 = "All categories", also for a name no longer present.</summary>
        public static int CategoryIndex(string[] categories, string? category)
        {
            if (string.IsNullOrEmpty(category)) return 0;
            for (int i = 0; i < categories.Length; i++)
                if (string.Equals(categories[i], category, StringComparison.OrdinalIgnoreCase)) return i + 1;
            return 0;
        }

        /// <summary>One "name=value" per line for the Fields block; empty (the block is skipped) when there are none.</summary>
        public static string FieldText(WaveeLogField[]? fields)
        {
            if (fields is not { Length: > 0 }) return "";
            var sb = new StringBuilder(fields.Length * 16);
            for (int i = 0; i < fields.Length; i++) { if (i > 0) sb.Append('\n'); fields[i].AppendTo(sb); }
            return sb.ToString();
        }

        /// <summary>"Copy visible" / "Export session": "seq=N " + the entry's own line, "(repeated N×)" for a group.</summary>
        public static string CopyText(ReadOnlySpan<LogViewRow> rows)
        {
            var sb = new StringBuilder(rows.Length * 96);
            for (int i = 0; i < rows.Length; i++)
            {
                var e = rows[i].Entry;
                sb.Append("seq=").Append(e.Sequence.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(e.Format());
                if (rows[i].Repeat > 1) sb.Append(" (repeated ").Append(rows[i].Repeat.ToString(CultureInfo.InvariantCulture)).Append("×)");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>"Export session" on the LIVE ring: every entry in the log FILE's own line shape
        /// (<see cref="Log.FormatFileLine"/>: <c>seq= tid= t= sid= pid=</c> and then the formatted entry), one per line,
        /// oldest first — the span is the ring snapshot, which already is. Independent of every filter and of grouping, so
        /// the file it becomes reads like a slice of the log file and a past session's export (raw file lines) is the same
        /// product. "Copy visible" stays <see cref="CopyText"/>, the filtered one.</summary>
        public static string ExportText(ReadOnlySpan<WaveeLogEntry> entries)
        {
            var sb = new StringBuilder(entries.Length * 128);
            for (int i = 0; i < entries.Length; i++) sb.Append(Log.FormatFileLine(in entries[i])).Append('\n');
            return sb.ToString();
        }

        /// <summary>"HH:mm:ss.fff" in the CALLER's offset (a host TZ read would be untestable); "—" for a line with no t=.</summary>
        public static string FormatTime(long unixMs, TimeSpan utcOffset) => unixMs <= 0 ? "—"
            : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(utcOffset).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        /// <summary>A past session's combo label: "MMM d · HH:mm · N events", else "pid P · N events" for a legacy file.</summary>
        public static string SessionLabel(long startUnixMs, int pid, int entryCount, TimeSpan utcOffset)
        {
            string head = startUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs).ToOffset(utcOffset).ToString("MMM d · HH:mm", CultureInfo.InvariantCulture)
                : "pid " + pid.ToString(CultureInfo.InvariantCulture);
            return head + " · " + entryCount.ToString(CultureInfo.InvariantCulture) + " events";
        }

        /// <summary>The live session's uptime tier: "N h M m" past an hour, "N min" past a minute, "just now" below.</summary>
        public static string Uptime(TimeSpan up)
        {
            var c = CultureInfo.InvariantCulture;
            if (up.TotalHours >= 1) return ((int)up.TotalHours).ToString(c) + " h " + up.Minutes.ToString(c) + " m";
            if (up.TotalMinutes >= 1) return ((int)up.TotalMinutes).ToString(c) + " min";
            return "just now";
        }

        /// <summary>"#812 · lyrics.fetch · tid 4 · op 7f2a · 41 ms" — each optional clause omitted when absent.</summary>
        public static string MetaLine(in WaveeLogEntry e)
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(64);
            sb.Append('#').Append(e.Sequence.ToString(c)).Append(" · ").Append(e.Category);
            if (e.EventId.Length > 0) sb.Append('.').Append(e.EventId);
            sb.Append(" · tid ").Append(e.ThreadId.ToString(c));
            if (e.OperationId is { Length: > 0 } op) sb.Append(" · op ").Append(op);
            if (e.ElapsedMs >= 0) sb.Append(" · ").Append(e.ElapsedMs.ToString(c)).Append(" ms");
            return sb.ToString();
        }

        public static int IndexOfSequence(LogViewRow[] rows, long seq)
        {
            for (int i = 0; i < rows.Length; i++) if (rows[i].Entry.Sequence == seq) return i;
            return -1;
        }

        /// <summary>The list remount Key: every input that changes the visible SET, plus the shown count (a growing tail
        /// with identical filters still needs a fresh key), and the FULL search text (equal lengths must never collide).</summary>
        public static string RemountKey(int session, LogViewQuery q, int shown) =>
            session.ToString(CultureInfo.InvariantCulture) + ":L" + (int)q.Level + ":C" + (q.Category ?? "")
            + ":N" + (q.NewestFirst ? 1 : 0) + ":G" + (q.GroupRepeats ? 1 : 0) + ":Q" + q.Search
            + ":n" + shown.ToString(CultureInfo.InvariantCulture);

        /// <summary>The Clear-view command is live only on the live ring; a past session is a file, not a ring.</summary>
        public static bool CanClear(int session) => session == 0;

        /// <summary>"Load more": the next cap, clamped at the ceiling.</summary>
        public static int NextCap(int cap) => Math.Min(MaxRows, cap + PageRows);
    }

    // ══ 2. THE RUNTIME DIAGNOSTICS REPORT (ch 27 W23, §7 G7, §8 BuildReport) ════════════════════════════════════════

    /// <summary>One place the provisioner looked for a runtime. <see cref="Source"/> is the STRING name of the private
    /// locate source, so no private type crosses the seam.</summary>
    public sealed record LocateCandidate(string Source, string RuntimeDir, bool DllPresent, bool ManifestPresent);

    /// <summary>The "why is local playback not ready?" report — 0.2.9's `PlaybackRuntimeDiagnostics` seam contract, as
    /// UI-safe data. The private provisioner fills <see cref="RuntimeReportSource"/>.</summary>
    public sealed record RuntimeDiagnostics(
        bool CompiledIn, IReadOnlyList<LocateCandidate> Candidates, ProvisioningOutcome LocateOutcome, string? LocateReason,
        ProvisioningOutcome? VerifyOutcome, string? VerifyDetail, Setup.RuntimeTrust? SignatureTrust, Setup.RuntimeSignature? Signature)
    {
        public static RuntimeDiagnostics NotCompiledIn(string reason) =>
            new(false, [], ProvisioningOutcome.RuntimeUnavailable, reason, null, null, null, null);

        /// <summary>Verify was never reached — the page's prose-absence arm.</summary>
        public bool VerifyNeverReached => VerifyOutcome is null && VerifyDetail is null && SignatureTrust is null && Signature is null;
    }

    /// <summary>The provisioner's own computed diagnostics. Null (or a null answer) = no playback session yet.</summary>
    public static Func<RuntimeDiagnostics?>? RuntimeReportSource;

    /// <summary>The clipboard / bug-report dump of the runtime page. The capture instant and the log path are parameters
    /// so the text is a fact.</summary>
    public static string BuildRuntimeReport(RuntimeDiagnostics? diag, Setup.RuntimeFacts? status, DateTimeOffset capturedLocal, string? logFile)
    {
        var sb = new StringBuilder(1024);
        sb.Append("Wavee — playback runtime diagnostics\n");
        sb.Append("captured : ").Append(Stamp(capturedLocal)).Append('\n');
        sb.Append("log file : ").Append(logFile ?? "(none)").Append("\n\n");
        if (status is { } s)
        {
            sb.Append("[status]\n");
            ReportLine(sb, "ready", s.IsReady ? "yes" : "no");
            ReportLine(sb, "issue", s.Issue.ToString());
            ReportLine(sb, "packId", s.PackId);
            ReportLine(sb, "spotifyVersion", s.Version);
            ReportLine(sb, "arch", s.Arch);
            ReportLine(sb, "runtimePath", s.Location);
            ReportLine(sb, "signatureTrust", s.Trust.ToString());
            ReportLine(sb, "trustedByPinnedFingerprint", s.PinnedFingerprint ? "yes" : "no");
            sb.Append('\n');
        }
        if (diag is null) return sb.Append("[diagnostics]\n  unavailable — no playback session is running.\n").ToString();

        sb.Append("[build]\n");
        ReportLine(sb, "localPlaybackCompiledIn", diag.CompiledIn ? "yes" : "no");
        sb.Append("\n[locate]\n");
        ReportLine(sb, "outcome", diag.LocateOutcome.ToString());
        ReportLine(sb, "reason", diag.LocateReason);
        sb.Append("\n[candidates] (").Append(diag.Candidates.Count.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        foreach (var c in diag.Candidates)
        {
            sb.Append("  - ").Append(c.Source).Append('\n');
            sb.Append("      dir      : ").Append(string.IsNullOrWhiteSpace(c.RuntimeDir) ? "(none)" : c.RuntimeDir).Append('\n');
            sb.Append("      dll      : ").Append(c.DllPresent ? "present" : "missing").Append('\n');
            sb.Append("      manifest : ").Append(c.ManifestPresent ? "present" : "missing").Append('\n');
        }
        sb.Append("\n[verify]\n");
        ReportLine(sb, "outcome", diag.VerifyOutcome?.ToString());
        ReportLine(sb, "detail", diag.VerifyDetail);
        ReportLine(sb, "signatureTrust", diag.SignatureTrust?.ToString());
        if (diag.Signature is { } sig)
        {
            ReportLine(sb, "publisher", sig.Subject);
            ReportLine(sb, "issuer", sig.Issuer);
            ReportLine(sb, "thumbprint", sig.Thumbprint);
            ReportLine(sb, "reason", sig.Reason);
            ReportLine(sb, "validFrom", Stamp(sig.ValidFrom));
            ReportLine(sb, "validTo", Stamp(sig.ValidTo));
            ReportLine(sb, "file", sig.FilePath);
        }
        return sb.ToString();
    }

    static void ReportLine(StringBuilder sb, string label, string? value) =>
        sb.Append("  ").Append(label).Append(" : ").Append(string.IsNullOrWhiteSpace(value) ? "(none)" : value).Append('\n');

    /// <summary>The report's timestamp shape, invariant: "yyyy-MM-dd HH:mm:ss zzz".</summary>
    public static string Stamp(DateTimeOffset t) => t.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    /// <summary>Every diagnostics Row prints "—" for a blank value (ch 27 W23): an unknown fact is a dash, never empty.</summary>
    public static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    /// <summary>The Connect page's millisecond stamps, in the caller's offset and invariant; 0 → "—".</summary>
    public static string StampMs(long unixMs, TimeSpan utcOffset) => unixMs <= 0 ? "—"
        : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(utcOffset).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    // ══ 3. THE OTHER OWNERS' REPORT SEAMS (ch 27 §7 G8, G14) ════════════════════════════════════════════════════════

    /// <summary>One playback module's receipts (owner T fills <see cref="ModulesReportSource"/> in Wave 6).</summary>
    public sealed record ModuleReport(
        string Id, string DisplayName, bool Bundled, string Version, int ProtocolVersion, string Publisher, string Directory,
        string? ProcessState, int? ProcessId, string Capabilities, bool HasStats, long Requests, long Failures, long Restarts,
        int P50Ms, int P95Ms, string? LastError, string? StatusCard)
    {
        /// <summary>A null process (never started), Ready, Stopped and Starting read as present; anything else critical.</summary>
        public bool ProcessHealthy => ProcessState is null or "Ready" or "Stopped" or "Starting";
        /// <summary>Retry is offered for Faulted OR Crashed — two states, not one (ch 27 69c).</summary>
        public bool CanRetry => ProcessState is "Faulted" or "Crashed";
    }

    public sealed record ModulesReport(string BundledRoot, string UserRoot, IReadOnlyList<ModuleReport> Installed,
        IReadOnlyList<(string Dir, string Reason)> Rejections, Action<string>? Retry);

    /// <summary>Null = this build has no playback-module host (ch 27 W23's fake-backend sentence).</summary>
    public static Func<ModulesReport?>? ModulesReportSource;

    /// <summary>The release-notes pipeline's receipts (owner R's `ReleaseNotes.Host.cs` fills the source).</summary>
    public sealed record NotesReport(string LastSource, string CacheRoot, string EmbeddedRoot, string FeedRelease,
        DateTimeOffset? LastFetchUtc, string LastFetchUrl, string LastFetchStatus, int IssueRequestsThisSession, int RateLimitRemaining);

    public static Func<NotesReport?>? NotesReportSource;

    // ══ 4. THE CONNECT TRACES (ch 27 W24) ═══════════════════════════════════════════════════════════════════════════

    public readonly record struct ClusterTrace(string ActiveId, string Origin, uint PutMsgId, int UpdateReason, string ChangedDevices,
        long ServerTsMs, string TrackUri, bool IsPlaying, bool IsPaused, long PositionAsOfMs);

    public readonly record struct PutTrace(uint MsgId, string Reason, bool IsActive, long StartedPlayingAtMs, long HasBeenPlayingForMs, long AtMs);

    public readonly record struct EchoTrace(uint MsgId, string ActiveId, long ServerTs, long ClusterStartedAtMs, bool Adopted, long AtMs);

    /// <summary>The last cluster the glue folded, the last put-state sent and its answer — a direct read, never computed by
    /// the page. The Connect glue (`Spotify.Connect.cs`) calls the notes ON THE UI THREAD (it posts); the page reads
    /// <see cref="Version"/> to re-render. Put and Echo have no absence state by design (six dashed rows each).</summary>
    public static class Connect
    {
        public static readonly Signal<int> Version = new(0);
        public static ClusterTrace? LastCluster { get; private set; }
        public static PutTrace LastPut { get; private set; }
        public static EchoTrace LastEcho { get; private set; }

        public static void NoteCluster(in ClusterTrace t) { LastCluster = t; Version.Value = Version.Peek() + 1; }
        public static void NotePut(in PutTrace t) { LastPut = t; Version.Value = Version.Peek() + 1; }
        public static void NoteEcho(in EchoTrace t) { LastEcho = t; Version.Value = Version.Peek() + 1; }
    }

    // ══ 6. THE FRAME WATCH'S PURE HALF (G-197) + THE SIDEBAR PANE INVARIANT EDGE (G-183) ═══════════════════════════

    /// <summary>The whole-session frame counters behind the `session.frames` line.</summary>
    public struct FrameSessionTotals
    {
        public long Frames, Over83, OverRefresh, Invalid;
        public double WorstMs;

        public void Add(double frameMs, double refreshMs)
        {
            Frames++;
            if (!double.IsFinite(frameMs) || frameMs < 0) { Invalid++; return; }
            if (frameMs > 8.3) Over83++;
            if (double.IsFinite(refreshMs) && refreshMs > 0) { if (frameMs > refreshMs) OverRefresh++; }
            else Invalid++;
            WorstMs = Math.Max(WorstMs, frameMs);
        }
    }

    /// <summary>The frame budget a frame was paced against; 60 Hz when the panel rate is unknown.</summary>
    public static double FrameBudgetMs(double refreshIntervalMs) => refreshIntervalMs > 0 ? refreshIntervalMs : 16.67;

    /// <summary>A route change restarts the navigation window only when the name OR the argument moved (album A → album B
    /// is a navigation; a repeat of the same key is not).</summary>
    public static string RouteWatchKey(string route, string? arg) => string.IsNullOrEmpty(arg) ? route : route + ":" + arg;

    /// <summary>The docked sidebar pane's settled-frame snapshot. The sidebar UI (owner J) assigns it; the frame watch
    /// samples it when a navigation window closes (the pane has long settled by then) and logs the FAULT EDGE.</summary>
    public static Func<SidebarPaneFrameSnapshot>? SidebarPaneFrame;

    /// <summary>Log only on an edge: a new or different fault. A persistent bad state logs once, not once per navigation.</summary>
    public static bool PaneFaultEdge(SidebarPaneInvariantFault previous, SidebarPaneInvariantFault now)
        => now != SidebarPaneInvariantFault.None && now != previous;

    // ══ 7. ch 22 §9 (b) ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>`Diagnostics.StageRects` by the name ch 22 gives it; the signal is <c>Platform.Developer.StageRects</c>.</summary>
    public static Signal<bool> StageRects => Platform.Developer.StageRects;

    // ══ 8. THE LYRICS INSPECTOR'S PURE HALF (A14; ch 22 W17-W19b) ═══════════════════════════════════════════════════
    // Ported from 0.2.9 `Backend/Lyrics/LyricsInspectionExport.cs` and the dialog's row rules. The dialog
    // (Diagnostics.UI.cs) lays these out, the bundle writer (Diagnostics.Host.cs) files them. Every number is
    // invariant-culture: the report is pasted into bug reports from every locale. The technical lines (verdicts, the
    // parsed/timing lines, the payload captions) are DATA, in English on purpose, like `Lyrics.Timing.Describe`.

    /// <summary>Turns one track's lyric search (<see cref="Lyrics.SearchReport"/> + <see cref="Lyrics.Inspection"/>) into
    /// the inspector's text, and decides the parsed tab's anomaly inks.</summary>
    public static class LyricsReport
    {
        /// <summary>How much of a payload the dialog RENDERS. Copy always hands over the whole capture.</summary>
        public const int OnScreenRawChars = 4000;
        /// <summary>How many parsed lines the dialog renders. Copy parsed has the rest.</summary>
        public const int OnScreenLines = 300;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The time column's ink: Bad = out of order or ends before it starts; Warn = a non-first line stamped
        /// 0 ms, or a squashed line.</summary>
        public enum TimeInk : byte { Normal, Warn, Bad }

        /// <summary>One parsed row's verdicts: the time column's ink, and whether the duration cell warns (squashed).</summary>
        public readonly record struct RowCheck(TimeInk Time, bool Squashed);

        public static RowCheck Check(Lyrics.Doc doc, int i)
        {
            var l = doc.Lines[i];
            bool outOfOrder = i > 0 && l.StartMs < doc.Lines[i - 1].StartMs;
            bool badEnd = l.EndMs is { } e && e < l.StartMs;
            bool squashed = Lyrics.Timing.IsSquashed(doc, i);
            bool noStamp = i > 0 && l.StartMs == 0;
            return new(outOfOrder || badEnd ? TimeInk.Bad : noStamp || squashed ? TimeInk.Warn : TimeInk.Normal, squashed);
        }

        /// <summary>"mm:ss.fff", clamped at 0.</summary>
        public static string Ts(long ms)
        {
            if (ms < 0) ms = 0;
            long m = ms / 60000, rest = ms % 60000;
            return m.ToString("00", Inv) + ":" + (rest / 1000).ToString("00", Inv) + "." + (rest % 1000).ToString("000", Inv);
        }

        /// <summary>"00:12.480→00:16.020"; a line with no end reads "00:12.480→  --:--.---".</summary>
        public static string TimeCell(Lyrics.Line l) => Ts(l.StartMs) + "→" + (l.EndMs is { } e ? Ts(e) : "  --:--.---");

        /// <summary>"217ms"; EMPTY (not "0ms") when the line has no end.</summary>
        public static string DurationCell(Lyrics.Line l) => l.EndMs is { } e ? (e - l.StartMs).ToString(Inv) + "ms" : "";

        /// <summary>"wa[12480-12610]  ter[12610-12980]"; a blank syllable prints "␣".</summary>
        public static string SyllableStrip(Lyrics.Line l)
        {
            var sb = new StringBuilder(l.Syllables.Count * 16);
            foreach (var s in l.Syllables)
            {
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(s.Text.Length == 0 ? "␣" : s.Text).Append('[').Append(s.StartMs.ToString(Inv)).Append('-')
                  .Append(s.EndMs.ToString(Inv)).Append(']');
            }
            return sb.ToString();
        }

        public static int SyllableCount(Lyrics.Doc doc)
        {
            int n = 0;
            foreach (var l in doc.Lines) n += l.Syllables.Count;
            return n;
        }

        public static int RawCount(Lyrics.Inspection? insp, string sourceId)
        {
            int n = 0;
            if (insp is not null) foreach (var r in insp.Raw) if (StringComparer.Ordinal.Equals(r.SourceId, sourceId)) n++;
            return n;
        }

        public static Lyrics.ParsedCandidate? CandidateFor(Lyrics.Inspection? insp, string sourceId)
        {
            if (insp is null || sourceId.Length == 0) return null;
            foreach (var c in insp.Candidates) if (StringComparer.Ordinal.Equals(c.SourceId, sourceId)) return c;
            return null;
        }

        /// <summary>The distinct sources that produced a payload, in capture order (the Raw tab's chips).</summary>
        public static List<string> RawSources(Lyrics.Inspection insp)
        {
            var sources = new List<string>(4);
            foreach (var r in insp.Raw)
            {
                bool seen = false;
                foreach (string s in sources) if (StringComparer.Ordinal.Equals(s, r.SourceId)) { seen = true; break; }
                if (!seen) sources.Add(r.SourceId);
            }
            return sources;
        }

        public static double WinnerScore(Lyrics.SearchReport? r)
        {
            if (r is not null) foreach (var t in r.Sources) if (t.Winner) return t.Score;
            return 0d;
        }

        public static string OutcomeLine(Lyrics.SourceTrace t)
            => t.Outcome.ToString().ToUpperInvariant() + " · " + t.ElapsedMs.ToString(Inv) + "ms";

        public static string IdentityLine(string trackId, Lyrics.SearchReport r)
            => "album " + Or(r.Album, "—") + " · " + (r.DurationMs / 1000).ToString(Inv) + "s · ISRC " + Or(r.Isrc, "—") + " · id " + trackId;

        public static string ParsedLine(Lyrics.ParsedCandidate c)
            => "parsed: " + c.Document.Sync + ", " + c.Document.Lines.Count.ToString(Inv) + " lines, "
             + SyllableCount(c.Document).ToString(Inv) + " syllables, matched by " + c.Basis + ", prior " + c.Prior.ToString("F2", Inv);

        public static string DocMeta(Lyrics.Doc d, string who)
            => "provider " + Or(d.Provider, who) + " · sync " + d.Sync + " · isSynced " + (d.IsSynced ? "True" : "False") + " · "
             + d.Lines.Count.ToString(Inv) + " lines · " + SyllableCount(d).ToString(Inv) + " syllables · offset applied "
             + d.OffsetMsApplied.ToString(Inv) + "ms";

        /// <summary>A payload's caption. TWO truncations, named separately on purpose: the store's capture cap
        /// ("CAPTURE-TRUNCATED to") and the layout's on-screen cap ("showing the first").</summary>
        public static string PayloadCaption(Lyrics.RawPayload p, int shownChars)
            => p.Format + " · " + p.OriginalLength.ToString("N0", Inv) + " chars"
             + (p.Truncated ? " · CAPTURE-TRUNCATED to " + p.Text.Length.ToString("N0", Inv) : "")
             + (shownChars < p.Text.Length ? " · showing the first " + shownChars.ToString("N0", Inv) : "");

        /// <summary>Why this provider is (not) the one you are listening to — the line the whole report exists for. The
        /// tie-break comes from <see cref="FactsOf"/>.</summary>
        public static string Verdict(Lyrics.SourceTrace t, double winnerScore) => Verdict(t, winnerScore, FactsOf(t).TieBreak);

        /// <summary>The verdict with an explicit stage-two tie-break (<see cref="Lyrics.Decision.TieBreak"/>): the winner
        /// reads "★ CHOSEN — reranker score 0.93, won on tier (syllable &gt; line) over qq (…)", a losing hit "… against
        /// the winner's 0.88 — lost to amll on tier (syllable &gt; line) (…)". A guarded source that never sent a request
        /// reads as the skip it was (<see cref="SkipPhrase"/>), and a breaker this fetch tripped is named.</summary>
        public static string Verdict(Lyrics.SourceTrace t, double winnerScore, string? tieBreak)
        {
            string reason = t.RerankReason.Length > 0 ? " (" + t.RerankReason + ")" : "";
            string tb = TieBreakPhrase(tieBreak);
            if (t.Winner) return "★ CHOSEN — reranker score " + t.Score.ToString("F2", Inv) + (tb.Length > 0 ? ", " + tb : "") + reason;
            string skip = SkipPhrase(t);
            if (skip.Length > 0) return skip;
            string trip = BreakerTripPhrase(t.Detail);
            if (trip.Length > 0) trip = "; " + trip;
            return t.Outcome switch
            {
                Lyrics.Outcome.Hit => "not chosen — it returned lyrics but lost the rerank: score " + t.Score.ToString("F2", Inv)
                                      + " against the winner's " + winnerScore.ToString("F2", Inv)
                                      + (tb.Length > 0 ? " — " + tb : "") + reason,
                Lyrics.Outcome.Miss => "not chosen — the provider had nothing for this track" + trip,
                Lyrics.Outcome.Timeout => "not chosen — it did not answer inside the per-source budget" + trip,
                Lyrics.Outcome.Error => "not chosen — the request failed" + trip,
                Lyrics.Outcome.Skipped => "not chosen — it never ran to completion (a faster match closed the window)",
                _ => "not chosen",
            };
        }

        // ── v2 (lyrics-grey-sources-implementation.md, W3-G): the reference, the stage-0 match, the rerank facts, the
        //    tie-break and the guard skips — every string the provider card and the export print is decided here ──

        /// <summary>The reranker's blend weights, mirrored for the score breakdown (<c>Lyrics.Reranker</c> keeps them
        /// private). <c>LyricsInspectorRulesTests</c> pins the mirror against a real <c>Reranker.Rank</c> score.</summary>
        public const double WText = 0.40, WSync = 0.25, WTiming = 0.20, WCoverage = 0.10, WPrior = 0.05;

        /// <summary>The prior term's input exactly as the reranker computes it: provider prior × match confidence.</summary>
        public static double PriorTerm(double prior, double confidence) => Math.Clamp(prior, 0d, 1d) * Math.Clamp(confidence, 0d, 1d);

        /// <summary>The reranker's blended score from its parts (same terms, same order).</summary>
        public static double Blend(double text, double sync, double timing, double coverage, double priorTerm)
            => WText * text + WSync * sync + WTiming * timing + WCoverage * coverage + WPrior * priorTerm;

        /// <summary>"0.912  =  text 0.95 × .40  +  sync 1.00 × .25  +  timing 0.88 × .20  +  coverage 0.97 × .10  +  prior
        /// 0.55 × conf 0.93 × .05". Without the provider prior (no parsed candidate recorded) the prior × confidence
        /// product is recovered from the score itself.</summary>
        public static string ScoreBreakdown(Lyrics.SourceTrace t, double? prior)
        {
            var sb = new StringBuilder(160);
            sb.Append(t.Score.ToString("F3", Inv)).Append("  =  text ").Append(F2(t.Text))
              .Append(" × .40  +  sync ").Append(F2(t.SyncScore)).Append(" × .25  +  timing ").Append(F2(t.Timing))
              .Append(" × .20  +  coverage ").Append(F2(t.Coverage)).Append(" × .10  +  ");
            if (prior is { } p)
                sb.Append("prior ").Append(F2(p)).Append(" × conf ").Append(F2(t.Confidence)).Append(" × .05");
            else
            {
                double residual = Math.Max(0d, (t.Score - Blend(t.Text, t.SyncScore, t.Timing, t.Coverage, 0d)) / WPrior);
                sb.Append("prior × conf ").Append(F2(residual)).Append(" × .05");
            }
            return sb.ToString();
        }

        /// <summary>What the reranker aligned every candidate against, as named by the report summary's
        /// <c>ref=</c> token. <see cref="ReferenceKind.Absent"/> = the summary carries no token (a disk hit, a
        /// no-match search) and the reference line is not shown at all.</summary>
        public enum ReferenceKind : byte { Absent, None, Source, Consensus }

        /// <summary><c>Sources</c>: the one source for <see cref="ReferenceKind.Source"/>; for
        /// <see cref="ReferenceKind.Consensus"/> the reference candidate first, then its agreeing peers.</summary>
        public sealed record ReferenceInfo(ReferenceKind Kind, IReadOnlyList<string> Sources);

        /// <summary>Parse "N/M returned; ref=spotify|consensus(a,b,…)|none; winner=…" into its reference.</summary>
        public static ReferenceInfo ReferenceOf(string? summary)
        {
            if (string.IsNullOrEmpty(summary)) return new(ReferenceKind.Absent, []);
            int at = -1;
            for (int i = summary.IndexOf("ref=", StringComparison.Ordinal); i >= 0; i = summary.IndexOf("ref=", i + 4, StringComparison.Ordinal))
                if (i == 0 || summary[i - 1] == ' ') { at = i + 4; break; }
            if (at < 0) return new(ReferenceKind.Absent, []);

            int end = summary.Length;
            int semi = summary.IndexOf(';', at);
            if (semi >= 0) end = semi;
            int dash = summary.IndexOf(" — ", at, StringComparison.Ordinal);
            if (dash >= 0 && dash < end) end = dash;
            string value = summary[at..end].Trim();

            if (value.Length == 0 || StringComparer.Ordinal.Equals(value, "none")) return new(ReferenceKind.None, []);
            const string Consensus = "consensus(";
            if (value.StartsWith(Consensus, StringComparison.Ordinal) && value.EndsWith(')'))
            {
                var ids = new List<string>(4);
                foreach (string part in value[Consensus.Length..^1].Split(','))
                {
                    string id = part.Trim();
                    if (id.Length > 0) ids.Add(id);
                }
                return ids.Count == 0 ? new(ReferenceKind.None, []) : new(ReferenceKind.Consensus, ids);
            }
            return new(ReferenceKind.Source, [value]);
        }

        /// <summary>"kugou, qq, netease".</summary>
        public static string JoinSources(IReadOnlyList<string> ids) => string.Join(", ", ids);

        /// <summary>A source's product name for prose ("spotify" → "Spotify"); an unknown id prints as itself.</summary>
        public static string SourceName(string id) => id switch
        {
            "spotify" => "Spotify",
            "amll" => "AMLL",
            "musixmatch" => "Musixmatch",
            "qq" => "QQ Music",
            "netease" => "NetEase",
            "kugou" => "Kugou",
            "lrclib" => "LRCLIB",
            _ => id,
        };

        /// <summary>"Reference: Spotify" / "Reference: consensus of kugou, qq, netease" / "No reference"; "" when the
        /// summary names none (the dialog localizes the same three shapes).</summary>
        public static string ReferenceLine(ReferenceInfo r) => r.Kind switch
        {
            ReferenceKind.Source => "Reference: " + SourceName(r.Sources[0]),
            ReferenceKind.Consensus => "Reference: consensus of " + JoinSources(r.Sources),
            ReferenceKind.None => "No reference",
            _ => "",
        };

        /// <summary>"0.93" — the band's confidence, invariant.</summary>
        public static string ConfidenceText(double confidence) => F2(confidence);

        /// <summary>The stage-0 metadata match of a SEARCH-matched source: "VeryHigh · conf 0.93 · a1b2 VeryHigh title=7
        /// …". "" for an identity/ISRC/local match (it carries no search) and for a source that produced no candidate.
        /// <paramref name="basis"/> is the parsed candidate's, when one was recorded.</summary>
        public static string MatchText(Lyrics.SourceTrace t, Lyrics.MatchBasis? basis = null)
        {
            bool searched = t.Match.Length > 0
                || basis == Lyrics.MatchBasis.MetadataSearch
                || (basis is null && t.Band is not (Lyrics.MatchBand.None or Lyrics.MatchBand.Perfect));
            if (!searched || (t.Band == Lyrics.MatchBand.None && t.Match.Length == 0)) return "";
            var sb = new StringBuilder(96);
            sb.Append(t.Band.ToString()).Append(" · conf ").Append(ConfidenceText(t.Confidence));
            if (t.Match.Length > 0) sb.Append(" · ").Append(t.Match);
            return sb.ToString();
        }

        /// <summary>The reranker's verdict facts for one source. <c>Ranked</c> = it produced a candidate the
        /// reranker scored; <c>Aligned</c> = it was aligned against a reference (recall and offset mean
        /// something); <c>CappedOffset</c> = the over-cap offset in seconds ("3.2") the reranker refused to
        /// apply.</summary>
        public readonly record struct RerankFacts(bool Ranked, bool Aligned, double Recall, int Support, long OffsetMs,
            bool OffsetCapped, string CappedOffset, string TieBreak);

        /// <summary>The facts a <see cref="Lyrics.SourceTrace"/> carries. The applied offset and the cap verdict are read
        /// off the reranker's own reason ("ref-align lcs=… off=320ms", "[offset 3.2s &gt; cap …]"); the tie-break is the
        /// trace's copy of <see cref="Lyrics.Decision.TieBreak"/>.</summary>
        public static RerankFacts FactsOf(Lyrics.SourceTrace t)
        {
            string r = t.RerankReason;
            bool ranked = t.Outcome == Lyrics.Outcome.Hit && (r.Length > 0 || t.Score > 0d);
            bool aligned = r.Contains("ref-align", StringComparison.Ordinal);
            string capped = CappedOffsetOf(r);
            return new RerankFacts(ranked, aligned, t.Recall, t.Support, aligned ? AppliedOffsetOf(r) : 0L,
                capped.Length > 0, capped, t.TieBreak);
        }

        /// <summary>"recall 0.95 · support 2 · offset +320 ms"; an over-cap offset reads "offset 3.2 s not applied
        /// [capped]", a partial document "recall 0.40 [&lt; 0.60]", no reference "recall n/a (no reference)". "" when
        /// the source was never ranked.</summary>
        public static string RerankText(in RerankFacts f)
        {
            if (!f.Ranked) return "";
            var sb = new StringBuilder(80);
            if (f.Aligned)
            {
                sb.Append("recall ").Append(F2(f.Recall));
                if (f.Recall < Lyrics.Reranker.TierRecall) sb.Append(" [< ").Append(F2(Lyrics.Reranker.TierRecall)).Append(']');
            }
            else sb.Append("recall n/a (no reference)");
            sb.Append(" · support ").Append(f.Support.ToString(Inv));
            if (f.OffsetCapped) sb.Append(" · offset ").Append(f.CappedOffset).Append(" s not applied [capped]");
            else if (f.Aligned) sb.Append(" · offset ").Append(SignedMs(f.OffsetMs));
            return sb.ToString();
        }

        /// <summary>"+320 ms" / "-1099 ms" / "0 ms".</summary>
        public static string SignedMs(long ms) => (ms > 0 ? "+" : "") + ms.ToString(Inv) + " ms";

        /// <summary>A stage-two tie-break in prose: "lost to amll on tier (syllable &gt; line)", "won on score (0.912 &gt;
        /// 0.880) over qq", "the only candidate", "lost to amll on provider id (a full tie)". "" for none.</summary>
        public static string TieBreakPhrase(string? tieBreak)
        {
            string tb = (tieBreak ?? "").Trim();
            if (tb.Length == 0) return "";
            if (StringComparer.Ordinal.Equals(tb, "only candidate")) return "the only candidate";
            const string Lost = "lost to ", Won = "won on ";
            if (tb.StartsWith(Lost, StringComparison.Ordinal))
            {
                int on = tb.IndexOf(" on ", Lost.Length, StringComparison.Ordinal);
                if (on > Lost.Length) return tb[..(on + 4)] + KeyPhrase(tb[(on + 4)..]);
            }
            else if (tb.StartsWith(Won, StringComparison.Ordinal))
            {
                int over = tb.LastIndexOf(" over ", StringComparison.Ordinal);
                if (over > Won.Length) return Won + KeyPhrase(tb[Won.Length..over]) + tb[over..];
            }
            return tb;
        }

        static string KeyPhrase(string key)
        {
            const string Tier = "tier ", Score = "score ", Prior = "prior×confidence ";
            if (key.StartsWith(Tier, StringComparison.Ordinal)) return "tier (" + key[Tier.Length..] + ")";
            if (key.StartsWith(Score, StringComparison.Ordinal)) return "score (" + key[Score.Length..] + ")";
            if (key.StartsWith(Prior, StringComparison.Ordinal)) return "prior × confidence (" + key[Prior.Length..] + ")";
            if (StringComparer.Ordinal.Equals(key, "verified")) return "verification";
            if (StringComparer.Ordinal.Equals(key, "provider id")) return "provider id (a full tie)";
            return key;
        }

        /// <summary>A source that never sent a request, read from the notes it wrote: "skipped — breaker open until 14:32
        /// (captcha)", "skipped — known miss (no lyric) 3 h ago", "skipped — no title to search with", or a disk hit's
        /// "skipped — not queried — served from the local lyrics cache". "" otherwise (and always for the winner).</summary>
        public static string SkipPhrase(Lyrics.SourceTrace t)
        {
            if (t.Winner) return "";
            string d = t.Detail;
            string open = NoteAfter(d, "skipped: breaker open until ");
            if (open.Length > 0) return "skipped — breaker open until " + open;
            string miss = NoteAfter(d, "cached miss ");
            if (miss.Length > 0)
            {
                int sp = miss.IndexOf(' ');
                string kind = sp > 0 ? miss[..sp] : miss;
                string age = sp > 0 ? miss[(sp + 1)..].Trim() : "";
                return "skipped — known miss (" + MissKindPhrase(kind) + ")" + (age.Length > 0 ? " " + SpacedUnit(age) : "");
            }
            if (t.Outcome == Lyrics.Outcome.Skipped && d.StartsWith("not queried", StringComparison.Ordinal)) return "skipped — " + d;
            if (d.Contains("no title to search with", StringComparison.Ordinal)) return "skipped — no title to search with";
            return "";
        }

        /// <summary>"it tripped the breaker for 2 h (captcha)" when this fetch opened the source's breaker; "" otherwise.</summary>
        public static string BreakerTripPhrase(string detail)
        {
            string trip = NoteAfter(detail, "breaker tripped for ");
            if (trip.Length == 0) return "";
            int colon = trip.IndexOf(": ", StringComparison.Ordinal);
            return colon > 0
                ? "it tripped the breaker for " + SpacedUnit(trip[..colon]) + " (" + trip[(colon + 2)..] + ")"
                : "it tripped the breaker for " + SpacedUnit(trip);
        }

        /// <summary>A remembered miss's kind in prose ("NoLyricForSong" → "no lyric").</summary>
        public static string MissKindPhrase(string kind) => kind switch
        {
            nameof(Lyrics.SourceGuard.MissKind.NoLyricForSong) => "no lyric",
            nameof(Lyrics.SourceGuard.MissKind.SongNotFound) => "song not found",
            _ => kind,
        };

        /// <summary>"3h ago" → "3 h ago", "10min" → "10 min": a space between a leading number and its unit.</summary>
        static string SpacedUnit(string s)
        {
            int n = 0;
            while (n < s.Length && char.IsAsciiDigit(s[n])) n++;
            return n > 0 && n < s.Length && char.IsAsciiLetter(s[n]) ? s[..n] + " " + s[n..] : s;
        }

        /// <summary>The note text after <paramref name="marker"/>, up to the next note ("; ") or the end; "" if absent.</summary>
        static string NoteAfter(string detail, string marker)
        {
            int at = detail.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return "";
            at += marker.Length;
            int end = detail.IndexOf("; ", at, StringComparison.Ordinal);
            return (end < 0 ? detail[at..] : detail[at..end]).Trim();
        }

        /// <summary>The applied offset in "… off=320ms …" (0 when absent or unparseable).</summary>
        static long AppliedOffsetOf(string reason)
        {
            int at = reason.IndexOf("off=", StringComparison.Ordinal);
            if (at < 0) return 0L;
            at += 4;
            int end = at;
            if (end < reason.Length && reason[end] == '-') end++;
            while (end < reason.Length && char.IsAsciiDigit(reason[end])) end++;
            return long.TryParse(reason.AsSpan(at, end - at), NumberStyles.AllowLeadingSign, Inv, out long ms) ? ms : 0L;
        }

        /// <summary>"3.2" from "[offset 3.2s &gt; cap (live/remix cut?)]"; "" when the offset was not capped.</summary>
        static string CappedOffsetOf(string reason)
        {
            const string Marker = "[offset ";
            for (int at = reason.IndexOf(Marker, StringComparison.Ordinal); at >= 0; at = reason.IndexOf(Marker, at + 1, StringComparison.Ordinal))
            {
                int start = at + Marker.Length;
                int cap = reason.IndexOf("s > cap", start, StringComparison.Ordinal);
                if (cap > start && cap - start <= 12) return reason[start..cap];
            }
            return "";
        }

        static string F2(double v) => v.ToString("0.00", Inv);

        /// <summary>One document as a TSV the bundle files and "Copy parsed" hands over.</summary>
        public static string BuildParsed(string who, Lyrics.Doc doc)
        {
            var sb = new StringBuilder(doc.Lines.Count * 48 + 256);
            sb.Append("# parsed lyrics — ").Append(who).Append('\n');
            sb.Append("provider:  ").Append(Or(doc.Provider, who)).Append('\n');
            sb.Append("track:     ").Append(doc.TrackId).Append('\n');
            sb.Append("sync:      ").Append(doc.Sync).Append("   isSynced=").Append(doc.IsSynced ? "True" : "False")
              .Append("   lines=").Append(doc.Lines.Count.ToString(Inv)).Append("   syllables=").Append(SyllableCount(doc).ToString(Inv))
              .Append("   offsetApplied=").Append(doc.OffsetMsApplied.ToString(Inv)).Append("ms\n");
            sb.Append("timing:    ").Append(Lyrics.Timing.Describe(doc)).Append("\n\n");
            sb.Append("idx\tstart\tend\tdurMs\tgapToNextMs\twordsPerSec\ttext\n");
            for (int i = 0; i < doc.Lines.Count; i++)
            {
                var l = doc.Lines[i];
                long gap = i + 1 < doc.Lines.Count ? doc.Lines[i + 1].StartMs - l.StartMs : 0;
                long span = Lyrics.Timing.WordSpanMs(l);
                int words = Lyrics.Timing.WordCount(l.Text);
                sb.Append(i.ToString(Inv)).Append('\t').Append(Ts(l.StartMs)).Append('\t')
                  .Append(l.EndMs is { } e ? Ts(e) : "-").Append('\t')
                  .Append(l.EndMs is { } e2 ? (e2 - l.StartMs).ToString(Inv) : "-").Append('\t')
                  .Append(gap > 0 ? gap.ToString(Inv) : "-").Append('\t')
                  .Append(span > 0 ? (words * 1000d / span).ToString("F1", Inv) : "-").Append('\t').Append(l.Text);
                if (l.Translation is { Length: > 0 } tr) sb.Append("\t[tr] ").Append(tr);
                if (l.Romanization is { Length: > 0 } ro) sb.Append("\t[ro] ").Append(ro);
                sb.Append('\n');
                foreach (var s in l.Syllables)
                    sb.Append("\t\t").Append(s.StartMs.ToString(Inv)).Append('-').Append(s.EndMs.ToString(Inv)).Append('\t').Append(s.Text).Append('\n');
            }
            return sb.ToString();
        }

        // ── on-device AI timing (the Providers tab's first card, and the report's "## on-device ai" section) ──

        /// <summary><see cref="AiFacts.ResultBytes"/> before the off-thread stat answered.</summary>
        public const long AiResultUnchecked = -2;
        /// <summary><see cref="AiFacts.ResultBytes"/> when there is no saved result on disk.</summary>
        public const long AiResultMissing = -1;

        /// <summary>The AI card's state dot.</summary>
        public enum AiInk : byte { Grey, Accent, Good, Bad }

        /// <summary>Which sentence the AI card's first line says (the dialog maps each to one loc key).</summary>
        public enum AiLine : byte { Off, NotReady, Idle, Waiting, Working, OnScreen, Cached, Outranked, Replaced, Skipped, Failed, Cancelled }

        /// <summary>One track's on-device AI facts, snapshotted on the UI thread. <see cref="Track"/> and <see cref="Job"/>
        /// are null unless they are about THIS track (the AI signals follow the playing track, the dialog does not).</summary>
        /// <param name="Doc">What the lyrics store holds for the track (the document on screen).</param>
        /// <param name="ResultBytes">The saved result's size; <see cref="AiResultMissing"/> or <see cref="AiResultUnchecked"/>.</param>
        public sealed record AiFacts(AiLyrics.Status Status, AiLyrics.TrackStatus? Track, AiLyrics.JobInfo? Job, Lyrics.Doc? Doc,
            string ResultPath, long ResultBytes);

        /// <summary>The facts about <paramref name="trackId"/>: the track state and the job are dropped when they belong to
        /// another track.</summary>
        public static AiFacts AiFactsFor(string trackId, AiLyrics.Status status, AiLyrics.TrackStatus track, AiLyrics.JobInfo? job,
            Lyrics.Doc? doc, string resultPath, long resultBytes)
            => new(status,
                track.TrackId is { Length: > 0 } t && t == trackId ? track : null,
                job is not null && job.TrackId == trackId ? job : null,
                doc, resultPath, resultBytes);

        /// <summary>The card shows while the feature is on, or when anything about this track came from it.</summary>
        public static bool ShowsAi(AiFacts f)
            => f.Status.Enabled || f.Job is not null || f.Doc is { Generated: true } || f.ResultBytes >= 0
            || f.Track is { Phase: not AiLyrics.TrackPhase.Idle };

        /// <summary>The first line, in this order: a running job → people-made word timing that outranks ours → a failure
        /// → our timing on screen (replayed or generated) → a finished job whose document was replaced → a skip → a
        /// cancel → the track state → the feature's own state.</summary>
        public static AiLine AiLineOf(AiFacts f)
        {
            string outcome = f.Job?.Outcome ?? "";
            if (outcome == "working") return AiLine.Working;
            if (outcome == "stopped" || f.Track is { Reason: AiLyrics.SkipReason.AlreadyWordByWord }) return AiLine.Outranked;
            if (outcome == "failed" || f.Track is { Reason: AiLyrics.SkipReason.Failed }) return AiLine.Failed;
            if (f.Doc is { Generated: true }) return outcome == "cached" ? AiLine.Cached : AiLine.OnScreen;
            if (outcome is "done" or "cached") return AiLine.Replaced;
            if (outcome == "skipped" || f.Track is { Phase: AiLyrics.TrackPhase.Skipped }) return AiLine.Skipped;
            if (outcome == "cancelled") return AiLine.Cancelled;
            if (f.Track is { Phase: AiLyrics.TrackPhase.Working }) return AiLine.Working;
            if (f.Track is { Phase: AiLyrics.TrackPhase.Waiting }) return AiLine.Waiting;
            if (!f.Status.Enabled) return AiLine.Off;
            if (f.Status.Phase != AiLyrics.SetupPhase.Ready) return AiLine.NotReady;
            return AiLine.Idle;
        }

        public static AiInk AiInkOf(AiLine line) => line switch
        {
            AiLine.Working => AiInk.Accent,
            AiLine.OnScreen or AiLine.Cached => AiInk.Good,
            AiLine.Failed => AiInk.Bad,
            _ => AiInk.Grey,
        };

        /// <summary>The provider whose lyrics are on screen instead of ours (the stopped job names it; else the document).</summary>
        public static string AiProvider(AiFacts f)
            => f.Job is { Outcome: "stopped", Detail.Length: > 0 } j ? j.Detail : Or(f.Doc?.Provider, "—");

        /// <summary>Why the track was skipped: the track state's reason, else the job's detail.</summary>
        public static string AiSkipReason(AiFacts f)
            => f.Track is { Reason: not AiLyrics.SkipReason.None } t ? t.Reason.ToString() : Or(f.Job?.Detail, "—");

        public static string AiFailure(AiFacts f) => Or(f.Job?.Detail, "—");

        /// <summary>The working line's numbers: lines ready / total, and seconds timed / the song's length.</summary>
        public readonly record struct AiProgress(string Ready, string Total, string Processed, string Duration);

        public static AiProgress AiProgressOf(AiFacts f)
        {
            int ready = f.Job?.LinesReady ?? f.Track?.LinesReady ?? 0, total = f.Job?.LineCount ?? f.Track?.LineCount ?? 0;
            double processed = f.Job?.ProcessedSeconds ?? f.Track?.ProcessedSeconds ?? 0;
            return new(ready.ToString(Inv), total.ToString(Inv), S1(processed), f.Job is { } j ? S1(j.DurationSeconds) : "—");
        }

        /// <summary>The meta line's numbers (stage times in seconds, "—" without a job) and the aligner language.</summary>
        public readonly record struct AiTimes(string Separate, string Align, string Total, string Language);

        public static AiTimes AiTimesOf(AiFacts f)
        {
            string language = Or(f.Job?.Language, Or(f.Track?.Language, Or(f.Doc is { Generated: true } d ? d.Language : null, "—")));
            return f.Job is { } j
                ? new(S1(j.SeparateSeconds), S1(j.AlignSeconds), S1(j.ElapsedMs / 1000d), language)
                : new("—", "—", "—", language);
        }

        /// <summary>The saved result's size ("1.2 KB"), "" when it is missing or not checked yet.</summary>
        public static string AiResultSize(long bytes) => bytes >= 0 ? AiLyrics.Rules.FormatBytes(bytes) : "";

        static string S1(double v) => v.ToString("0.0", Inv);

        /// <summary>The report's "## on-device ai" section: the same facts as the card, in English data like the rest.</summary>
        public static void AppendAi(StringBuilder sb, AiFacts f)
        {
            var line = AiLineOf(f);
            sb.Append("\n## on-device ai\n");
            sb.Append("state:   ").Append(line).Append("   setup=").Append(f.Status.Phase).Append("   enabled=")
              .Append(f.Status.Enabled ? "True" : "False").Append("   pack=v").Append(AiLyrics.PackVersion.ToString(Inv)).Append('\n');
            if (line == AiLine.Outranked || line == AiLine.Replaced) sb.Append("shown:   ").Append(AiProvider(f)).Append('\n');
            if (f.Track is { } t)
                sb.Append("track:   ").Append(t.Phase).Append("   reason=").Append(t.Reason).Append("   lines=")
                  .Append(t.LinesReady.ToString(Inv)).Append('/').Append(t.LineCount.ToString(Inv)).Append("   fromCache=")
                  .Append(t.FromCache ? "True" : "False").Append('\n');
            if (f.Job is { } j)
            {
                var times = AiTimesOf(f);
                sb.Append("job:     ").Append(j.Outcome).Append("   lines=").Append(j.LinesReady.ToString(Inv)).Append('/')
                  .Append(j.LineCount.ToString(Inv)).Append("   timed=").Append(S1(j.ProcessedSeconds)).Append("s of ")
                  .Append(S1(j.DurationSeconds)).Append("s   language=").Append(times.Language).Append('\n');
                sb.Append("times:   separate ").Append(times.Separate).Append("s · align ").Append(times.Align)
                  .Append("s · total ").Append(times.Total).Append("s   fromCache=").Append(j.FromCache ? "True" : "False").Append('\n');
                if (j.Detail.Length > 0) sb.Append("detail:  ").Append(j.Detail).Append('\n');
            }
            else sb.Append("job:     (none for this track)\n");
            sb.Append("npu:     ").Append(Or(f.Status.NpuName, "—")).Append("   driver=").Append(Or(f.Status.NpuDriver, "—")).Append('\n');
            sb.Append("result:  ").Append(Or(f.ResultPath, "—")).Append("   ").Append(f.ResultBytes switch
            {
                >= 0 => AiResultSize(f.ResultBytes),
                AiResultMissing => "(not saved)",
                _ => "(not checked)",
            }).Append('\n');
            if (f.Doc is { } d)
                sb.Append("doc:     provider ").Append(Or(d.Provider, "—")).Append("   sync=").Append(d.Sync).Append("   generated=")
                  .Append(d.Generated ? "True" : "False").Append('\n');
        }

        /// <summary>The full report ("Copy full report", and <c>report.txt</c> in a bundle). <paramref name="ai"/> adds the
        /// on-device AI section when the caller has its facts.</summary>
        public static string BuildReport(string trackId, Lyrics.SearchReport? r, Lyrics.Inspection? insp, AiFacts? ai = null)
        {
            var sb = new StringBuilder(4096);
            sb.Append("# Wavee lyrics source report\n").Append("track:   ").Append(trackId).Append('\n');
            if (r is not null)
            {
                sb.Append("title:   ").Append(Or(r.Title, "-")).Append("  —  ").Append(Or(r.Artist, "-")).Append('\n');
                sb.Append("album:   ").Append(Or(r.Album, "-")).Append("   duration=").Append(r.DurationMs.ToString(Inv))
                  .Append("ms   isrc=").Append(Or(r.Isrc, "-")).Append('\n');
                sb.Append("summary: ").Append(r.Summary).Append('\n');
                if (ReferenceLine(ReferenceOf(r.Summary)) is { Length: > 0 } reference) sb.Append("ref:     ").Append(reference).Append('\n');
            }
            else sb.Append("summary: (no search recorded)\n");
            if (insp is not null) sb.Append("note:    ").Append(insp.Note).Append('\n');
            if (ai is not null) AppendAi(sb, ai);

            double winnerScore = WinnerScore(r);
            sb.Append("\n## providers\n");
            if (r is null || r.Sources.Count == 0) sb.Append("(none)\n");
            else
                foreach (var t in r.Sources)
                {
                    sb.Append("- ").Append(t.SourceId).Append("  ").Append(t.Outcome).Append("  ").Append(t.ElapsedMs.ToString(Inv)).Append("ms")
                      .Append("  sync=").Append(t.Sync).Append("  lines=").Append(t.LineCount.ToString(Inv))
                      .Append("  score=").Append(t.Score.ToString("F3", Inv)).Append('\n');
                    var facts = FactsOf(t);
                    var parsed = CandidateFor(insp, t.SourceId);
                    sb.Append("    verdict: ").Append(Verdict(t, winnerScore, facts.TieBreak)).Append('\n');
                    if (MatchText(t, parsed?.Basis) is { Length: > 0 } match) sb.Append("    match:   ").Append(match).Append('\n');
                    if (RerankText(facts) is { Length: > 0 } rerank) sb.Append("    rerank:  ").Append(rerank).Append('\n');
                    // The breakdown, not just the total: "0.885 vs 0.740" says who won, "sync 0.60 vs 1.00" says why.
                    if (t.Score > 0d) sb.Append("    score:   ").Append(ScoreBreakdown(t, parsed?.Prior)).Append('\n');
                    if (t.Detail.Length > 0) sb.Append("    detail:  ").Append(t.Detail).Append('\n');
                }

            sb.Append("\n## captured payloads\n");
            if (insp is null || insp.Raw.Count == 0) sb.Append("(none — the answer came from a cache, so no provider was contacted)\n");
            else
                foreach (var p in insp.Raw)
                    sb.Append("- ").Append(p.SourceId).Append("  ").Append(p.Format).Append("  ").Append(p.OriginalLength.ToString(Inv))
                      .Append(" chars").Append(p.Truncated ? " (truncated)" : "").Append("  ").Append(p.Label).Append('\n');

            sb.Append("\n## parsed candidates\n");
            if (insp is null || insp.Candidates.Count == 0) sb.Append("(none)\n");
            else
                foreach (var c in insp.Candidates)
                    sb.Append("- ").Append(c.SourceId).Append("  ").Append(c.Document.Sync).Append("  ")
                      .Append(c.Document.Lines.Count.ToString(Inv)).Append(" lines  basis=").Append(c.Basis)
                      .Append("\n    timing: ").Append(Lyrics.Timing.Describe(c.Document, r?.DurationMs ?? 0)).Append('\n');

            if (insp?.Final is { } final) sb.Append('\n').Append(BuildParsed("final", final));
            return sb.ToString();
        }

        // ── the bundle's names (Diagnostics.Host.cs `SaveLyricsBundle` writes them) ──

        /// <summary>A path-safe segment: letters, digits, '-' and '_' survive; everything else is '_'.</summary>
        public static string SafeName(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
            return sb.Length == 0 ? "unknown" : sb.ToString();
        }

        /// <summary>"20260913-181500-4uLU6hMCjMI75M1A2tKUQC" — sortable by time, then the track.</summary>
        public static string BundleFolderName(string trackId, DateTime utc) => utc.ToString("yyyyMMdd-HHmmss", Inv) + "-" + SafeName(trackId);

        /// <summary>"raw-amll-1.ttml": the real extension, so an editor highlights it; the per-source counter keeps a query
        /// ladder's several responses distinct and in capture order.</summary>
        public static string RawFileName(string sourceId, int n, string format)
            => "raw-" + SafeName(sourceId) + "-" + n.ToString(Inv) + format switch
            {
                "json" => ".json", "ttml" => ".ttml", "xml" => ".xml", "lrc" => ".lrc",
                _ => ".txt",   // krc / qrc / yrc: decrypted plain text with no editor association
            };

        public static string ParsedFileName(string who) => "parsed-" + SafeName(who) + ".tsv";

        static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
