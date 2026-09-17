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
// ENGINE-FREE (one Signal for the Connect traces' version). Every decision the logs panel, the two diagnostics pages,
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

    // ══ 5. CRASH REPORTS — the pure half (G-094) ════════════════════════════════════════════════════════════════════

    public static class CrashFiles
    {
        public const string Prefix = "crash-report-", Suffix = ".txt", StampFormat = "yyyyMMdd-HHmmss";

        /// <summary>A report's file name for a local instant — a millisecond stamp, so two crashes a second apart never
        /// overwrite each other.</summary>
        public static string NameFor(DateTimeOffset local) =>
            Prefix + local.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + Suffix;

        /// <summary>The <c>yyyyMMdd-HHmmss</c> stamp of a report name (an optional -fff tail is ignored); any other shape
        /// fails rather than being guessed at.</summary>
        public static bool TryStamp(string fileName, out DateTime stamp)
        {
            stamp = default;
            if (!fileName.StartsWith(Prefix, StringComparison.Ordinal) || !fileName.EndsWith(Suffix, StringComparison.Ordinal)
                || fileName.Length < Prefix.Length + Suffix.Length + StampFormat.Length) return false;
            var span = fileName.AsSpan(Prefix.Length, fileName.Length - Prefix.Length - Suffix.Length);
            if (span.Length != StampFormat.Length && (span.Length != StampFormat.Length + 4 || span[StampFormat.Length] != '-')) return false;
            return DateTime.TryParseExact(span[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp);
        }

        /// <summary>Newest NAME first — the order the card lists and the pruner keeps (a copy or restore resets mtimes).</summary>
        public static int NewestFirst(string a, string b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a));

        /// <summary>Every <c>&lt;module&gt;!&lt;BaseAddress&gt;+0x&lt;hex&gt;</c> offset in a NativeAOT trace, innermost first,
        /// duplicates kept. Any other frame shape contributes nothing; never throws.</summary>
        public static List<long> ParseRvas(string? stackTrace)
        {
            const string Marker = "!<BaseAddress>+0x";
            var found = new List<long>();
            if (string.IsNullOrEmpty(stackTrace)) return found;
            int at = 0;
            while ((at = stackTrace.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0)
            {
                int start = at + Marker.Length, end = start;
                while (end < stackTrace.Length && char.IsAsciiHexDigit(stackTrace[end])) end++;
                at = end;
                int digits = end - start;
                if (digits is 0 or > 15) continue;
                if (long.TryParse(stackTrace.AsSpan(start, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long rva))
                    found.Add(rva);
            }
            return found;
        }
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

        /// <summary>Why this provider is (not) the one you are listening to — the line the whole report exists for.</summary>
        public static string Verdict(Lyrics.SourceTrace t, double winnerScore)
        {
            string reason = t.RerankReason.Length > 0 ? " (" + t.RerankReason + ")" : "";
            if (t.Winner) return "★ CHOSEN — reranker score " + t.Score.ToString("F2", Inv) + reason;
            return t.Outcome switch
            {
                Lyrics.Outcome.Hit => "not chosen — it returned lyrics but lost the rerank: score " + t.Score.ToString("F2", Inv)
                                      + " against the winner's " + winnerScore.ToString("F2", Inv) + reason,
                Lyrics.Outcome.Miss => "not chosen — the provider had nothing for this track",
                Lyrics.Outcome.Timeout => "not chosen — it did not answer inside the per-source budget",
                Lyrics.Outcome.Error => "not chosen — the request failed",
                Lyrics.Outcome.Skipped => "not chosen — it never ran to completion (a faster match closed the window)",
                _ => "not chosen",
            };
        }

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

        /// <summary>The full report ("Copy full report", and <c>report.txt</c> in a bundle).</summary>
        public static string BuildReport(string trackId, Lyrics.SearchReport? r, Lyrics.Inspection? insp)
        {
            var sb = new StringBuilder(4096);
            sb.Append("# Wavee lyrics source report\n").Append("track:   ").Append(trackId).Append('\n');
            if (r is not null)
            {
                sb.Append("title:   ").Append(Or(r.Title, "-")).Append("  —  ").Append(Or(r.Artist, "-")).Append('\n');
                sb.Append("album:   ").Append(Or(r.Album, "-")).Append("   duration=").Append(r.DurationMs.ToString(Inv))
                  .Append("ms   isrc=").Append(Or(r.Isrc, "-")).Append('\n');
                sb.Append("summary: ").Append(r.Summary).Append('\n');
            }
            else sb.Append("summary: (no search recorded)\n");
            if (insp is not null) sb.Append("note:    ").Append(insp.Note).Append('\n');

            double winnerScore = WinnerScore(r);
            sb.Append("\n## providers\n");
            if (r is null || r.Sources.Count == 0) sb.Append("(none)\n");
            else
                foreach (var t in r.Sources)
                {
                    sb.Append("- ").Append(t.SourceId).Append("  ").Append(t.Outcome).Append("  ").Append(t.ElapsedMs.ToString(Inv)).Append("ms")
                      .Append("  sync=").Append(t.Sync).Append("  lines=").Append(t.LineCount.ToString(Inv))
                      .Append("  score=").Append(t.Score.ToString("F3", Inv)).Append('\n');
                    sb.Append("    verdict: ").Append(Verdict(t, winnerScore)).Append('\n');
                    // The breakdown, not just the total: "0.885 vs 0.740" says who won, "sync 0.60 vs 1.00" says why.
                    if (t.Score > 0d)
                        sb.Append("    score:   ").Append(t.Score.ToString("F3", Inv)).Append("  =  text ").Append(t.Text.ToString("F2", Inv))
                          .Append(" × .40  +  sync ").Append(t.SyncScore.ToString("F2", Inv)).Append(" × .25  +  timing ")
                          .Append(t.Timing.ToString("F2", Inv)).Append(" × .20  +  coverage ").Append(t.Coverage.ToString("F2", Inv))
                          .Append(" × .10  +  prior × .05\n");
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
