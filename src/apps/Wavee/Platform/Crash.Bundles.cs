// ── Platform/Crash.Bundles.cs ──────────────────────────────────────────────────────────────────────────────────────
// The on-disk bundle writer/reader BOTH processes share: `Crash.Host` (the parent, Managed/Native) and
// `Crash.Handler` (the child, Hang/ExitCode/UncleanExit) call the exact same `Create`/`WriteSummary`/`WriteReport`/
// `WriteTail`/`WriteSend` sequence, so a bundle looks identical on disk regardless of which process wrote it. Every
// method is best-effort file I/O with no engine dependency — the one exception the file layout itself makes for
// (`WriteTail` scrubbing through `Feedback.ReportRedactor`) is engine-free too.
//
// Role: CORE (engine-free)
// Owner: WP-B
// Wave: crash-diagnostics
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §B.3 (write order, prune), §I ("used by BOTH processes")
//
// WRITE ORDER IS THE CONTRACT (§B.3): summary.json FIRST (disk-full safe — a bundle with only a summary still names
// what happened), then report.txt, then log-tail.txt, then the dump (written by the caller, `Crash.Handler`, AFTER
// this file's three writes return). A reader (`Read`) tolerates a bundle stopped at any of those points: a missing
// report/tail is not fatal, only a missing summary.json makes a directory not a bundle at all.

using System.Text;
using System.Text.Json;

namespace Wavee;

public static partial class Crash
{
    public static class Bundles
    {
        /// <summary>The "a bundle was written this run" marker: <c>logs\crash\pending</c> holding the bundle folder.
        /// A FILE, not a setting — a crash handler runs on whichever thread faulted and must never write settings.</summary>
        public static void MarkPending(string logFolder, string bundleDir)
        {
            try { File.WriteAllText(Path.Combine(Files.Root(logFolder), "pending"), bundleDir); } catch { }
        }

        /// <summary><c>logs\crashootfailures</c>: the consecutive-launches-that-died-before-the-first-frame streak. A FILE,
        /// not a setting: it must survive a crash (settings may never flush), a demo profile (in-memory settings) and a
        /// launch whose settings failed to open — exactly the launch that needs it.</summary>
        public static int ReadBootFailures(string logFolder)
        {
            try { return int.TryParse(File.ReadAllText(Path.Combine(Files.Root(logFolder), "bootfailures")).Trim(), out int n) && n >= 0 ? n : 0; }
            catch { return 0; }
        }

        public static void WriteBootFailures(string logFolder, int count)
        {
            try
            {
                string root = Files.Root(logFolder);
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "bootfailures"), count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
        }

        /// <summary>The marker's folder (or "") and its removal — the next launch's <c>BeginGuiRun</c>, UI thread.</summary>
        public static string TakePending(string logFolder)
        {
            string path = Path.Combine(Files.Root(logFolder), "pending");
            try
            {
                if (!File.Exists(path)) return "";
                string dir = File.ReadAllText(path).Trim();
                File.Delete(path);
                return Directory.Exists(dir) ? dir : "";
            }
            catch { return ""; }
        }

        /// <summary>A minidump over this size is deleted right after it is written (§B.3: "Sizes measured in the
        /// packaged probe run before MaxDumpBytes is fixed — start at 25 MB"). Deleting it, rather than truncating or
        /// refusing to write it, is what keeps <see cref="BundleInfo.HasDump"/> — computed from the file's presence,
        /// never from a stored bit — honest without a second write.</summary>
        public const long MaxDumpBytes = 25L << 20;

        /// <summary>Creates <c>&lt;logFolder&gt;\crash\&lt;stamp&gt;-&lt;kind&gt;\</c> and returns it. The caller
        /// writes into it next; this method's only job is the folder existing.</summary>
        public static string Create(string logFolder, Kind kind, DateTimeOffset local)
        {
            string dir = Path.Combine(Files.Root(logFolder), Files.BundleName(local, kind));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void WriteSummary(string dir, Summary summary)
        {
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(summary, CrashJson.Default.Summary);
            File.WriteAllText(Path.Combine(dir, Files.SummaryName), json, new UTF8Encoding(false));
        }

        public static void WriteReport(string dir, string text)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Files.ReportName), text, new UTF8Encoding(false));
        }

        /// <summary>The last <paramref name="maxLines"/> lines of <paramref name="datedLogPath"/>, each scrubbed
        /// through <see cref="Feedback.ReportRedactor.Redact"/> with <paramref name="rules"/> — <c>RedactionRules.None</c>
        /// from a caller with no session to gather rules from (the child, every Hang/ExitCode/UncleanExit bundle).
        /// Reads with <c>FileShare.ReadWrite | FileShare.Delete</c>, the same share mode the live log's own append
        /// sink needs, so a tail read never contends with the writer it is reading. <paramref name="datedLogPath"/>
        /// of <c>null</c>/<c>""</c>/<c>"-"</c> (the child's "no log path" arg) writes an empty tail rather than
        /// failing the bundle.</summary>
        public static void WriteTail(string dir, string? datedLogPath, int maxLines, Feedback.RedactionRules rules)
        {
            Directory.CreateDirectory(dir);
            var lines = new Queue<string>(Math.Max(1, maxLines));
            if (!string.IsNullOrEmpty(datedLogPath) && datedLogPath != "-")
            {
                try
                {
                    if (File.Exists(datedLogPath))
                    {
                        using var fs = new FileStream(datedLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(fs);
                        while (reader.ReadLine() is { } line)
                        {
                            if (lines.Count == maxLines) lines.Dequeue();
                            lines.Enqueue(line);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var sb = new StringBuilder(lines.Count * 96);
            foreach (string line in lines) sb.Append(Feedback.ReportRedactor.Redact(line, rules)).Append('\n');
            File.WriteAllText(Path.Combine(dir, Files.TailName), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Records a bundle's send state — but only into a bundle that still EXISTS. An upload that settles
        /// after the user deleted the report (or the pruner did) must not resurrect its folder as a <c>send.json</c>
        /// orphan (#165): a missing folder, before or during the write, makes this a no-op.</summary>
        public static void WriteSend(string dir, SendRecord record)
        {
            if (!Directory.Exists(dir)) return;
            string json = JsonSerializer.Serialize(record, CrashJson.Default.SendRecord);
            try { File.WriteAllText(Path.Combine(dir, Files.SendName), json, new UTF8Encoding(false)); }
            catch (DirectoryNotFoundException) { }   // deleted between the check and the write — same answer
        }

        /// <summary>The bundle's <c>summary.json</c>, or null when it is missing or unreadable.</summary>
        public static Summary? ReadSummary(string dir)
        {
            try
            {
                string path = Path.Combine(dir, Files.SummaryName);
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize(File.ReadAllText(path), CrashJson.Default.Summary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }

        /// <summary>Read-modify-write of an existing bundle's <c>summary.json</c> — the child's one finalizing write after
        /// a dump (hasDump/dumpBytes/faultModule/faultOffset, #165 W3a). Never creates anything: a missing folder or
        /// summary returns false. The new text goes to a sibling temp file first and replaces the old one in a single
        /// rename, so an interrupted write never leaves a bundle without the one file that makes it a bundle (§B.3).</summary>
        public static bool UpdateSummary(string dir, Func<Summary, Summary> update)
        {
            if (!Directory.Exists(dir) || ReadSummary(dir) is not { } current) return false;
            string path = Path.Combine(dir, Files.SummaryName), temp = path + ".tmp";
            try
            {
                string json = JsonSerializer.Serialize(update(current), CrashJson.Default.Summary);
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                return false;
            }
        }

        /// <summary>Appends <paramref name="line"/> to an existing bundle's <c>report.txt</c> as its own paragraph (a
        /// blank line before it, a newline after) so a trailer such as the child's <c>fault=&lt;module&gt;+0x&lt;offset&gt;</c>
        /// never reads as one more entry of the "Frames (RVA)" list above it. A missing folder is a no-op.</summary>
        public static void AppendReportLine(string dir, string line)
        {
            if (!Directory.Exists(dir)) return;
            try { File.AppendAllText(Path.Combine(dir, Files.ReportName), "\n" + line + "\n", new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        public static SendRecord? ReadSend(string dir)
        {
            try
            {
                string path = Path.Combine(dir, Files.SendName);
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize(File.ReadAllText(path), CrashJson.Default.SendRecord);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }

        /// <summary>A dump over <see cref="MaxDumpBytes"/> is deleted right after the writer closes the file handle —
        /// called by <c>Crash.Handler</c> immediately after <c>MiniDumpWriteDump</c> returns. Returns whether a dump
        /// file exists AFTER this call (false either way it was never written or it was just deleted for size).</summary>
        public static bool EnforceDumpCap(string dir)
        {
            string path = Path.Combine(dir, Files.DumpName);
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                if (fi.Length > MaxDumpBytes) { fi.Delete(); return false; }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return File.Exists(path); }
        }

        /// <summary>One bundle, or null when <paramref name="dir"/> has no (or an unreadable) <c>summary.json</c> —
        /// the only file whose absence disqualifies a folder from being a bundle at all (§B.3's write order).</summary>
        public static BundleInfo? Read(string dir)
        {
            try
            {
                string summaryPath = Path.Combine(dir, Files.SummaryName);
                if (!File.Exists(summaryPath)) return null;
                var summary = JsonSerializer.Deserialize(File.ReadAllText(summaryPath), CrashJson.Default.Summary);
                if (summary is null) return null;
                var send = ReadSend(dir) ?? new SendRecord(SendState.NotSent, null, null, 0, false);
                string dumpPath = Path.Combine(dir, Files.DumpName);
                bool hasDump = File.Exists(dumpPath);
                long dumpBytes = 0;
                if (hasDump) { try { dumpBytes = new FileInfo(dumpPath).Length; } catch { hasDump = false; } }
                DateTime stampLocal = Files.TryParse(Path.GetFileName(dir), out var stamp, out _) ? stamp : SafeCreationTime(dir);
                return new BundleInfo(dir, summary, stampLocal, send, hasDump, dumpBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }

        static DateTime SafeCreationTime(string dir)
        {
            try { return Directory.GetCreationTime(dir); } catch { return DateTime.MinValue; }
        }

        /// <summary>Every bundle under <c>&lt;logFolder&gt;\crash\</c> (the outbox folder excluded), newest first —
        /// the Reports list's whole input. Best-effort: a folder that fails to parse or read is skipped, never
        /// fatal to the rest of the listing.</summary>
        public static List<BundleInfo> List(string logFolder)
        {
            var result = new List<BundleInfo>();
            try
            {
                string root = Files.Root(logFolder);
                if (!Directory.Exists(root)) return result;
                var dirs = new List<string>();
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (name == Files.Outbox || !Files.TryParse(name, out _, out _)) continue;
                    dirs.Add(dir);
                }
                dirs.Sort(Files.NewestFirst);
                foreach (string dir in dirs)
                    if (Read(dir) is { } info) result.Add(info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return result;
        }

        /// <summary>Enforces <see cref="Files.KeepBundles"/> / <see cref="Files.MaxFolderBytes"/> over
        /// <c>&lt;logFolder&gt;\crash\</c> right now (called after every bundle write). Best-effort: a folder that
        /// cannot be deleted (open handle, permissions) is skipped rather than aborting the rest of the prune.</summary>
        public static void PruneNow(string logFolder)
        {
            try
            {
                string root = Files.Root(logFolder);
                if (!Directory.Exists(root)) return;
                var entries = new List<(string Path, long Bytes)>();
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (name == Files.Outbox || !Files.TryParse(name, out _, out _)) continue;
                    long bytes = 0;
                    try { foreach (string f in Directory.GetFiles(dir)) bytes += new FileInfo(f).Length; } catch { }
                    entries.Add((dir, bytes));
                }
                entries.Sort((a, b) => Files.NewestFirst(a.Path, b.Path));
                foreach (string toDelete in Files.Prune(entries, Files.KeepBundles, Files.MaxFolderBytes))
                {
                    try { Directory.Delete(toDelete, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
