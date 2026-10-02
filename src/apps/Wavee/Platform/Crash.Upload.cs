// ── Platform/Crash.Upload.cs ───────────────────────────────────────────────────────────────────────────────────────
// The crash & diagnostics pipeline's PACKER + UPLOADER (WP-C, half 2): `Crash.Bundle.Pack` turns one scrubbed bundle
// into the exact multipart/form-data body the Worker ingest contract expects (pure, byte-exact — tested directly and
// pinned against `ops/crash/contract/*.multipart`); `Crash.UploadPolicy` is the pure status → next-step table every
// upload answer goes through; `Crash.Uploader` is the host-side outbox — enqueue, drain at launch / on every online
// edge / on its own backoff timer, single-flight with re-loop, offline-safe — plus the right-to-erasure client (§J).
//
// Role: CORE (Bundle, UploadPolicy, UploadToasts, OutboxMeta) + SHELL (Uploader — network, disk, a Signal<int>)
// Owner: WP-C
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §B.5, §F ("C · Scrub + upload"), §I, §J (erasure);
//       docs/plans/wavee/crash-production-readiness-implementation.md W3b + appendix A4 (#165)
//
// OUTBOX SHAPE: `<LogFolder>\crash\outbox\<reportId>.bundle` (the packed multipart body, written once) beside
// `<reportId>.meta` (a small flat `key=value` sidecar — bundleDir/attempts/includeDump/lastTryUtc/launches; not JSON,
// so no new source-generated JSON context is needed here). `attempts` counts SERVER retries (429/5xx) only — they drive
// the backoff; `launches` counts the launches that saw a server retry — the third one gives up. A network failure
// (offline, DNS, timeout) never backs off and never gives up: the next drain simply tries again.
//
// THREADING: every send.json write, every Signal<T> write and every `settled` callback goes through the UI poster
// handed to `Install` (FIFO, so a report's states land in the order they happened); without one (the engine-free
// recovery dialog) they run inline.

using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Crash
{
    // ── 1. Bundle.Pack — pure, byte-exact multipart (§I) ────────────────────────────────────────────────────────────

    public static class Bundle
    {
        /// <summary>The Worker's request cap (§B.5); a dump that would push the body past it is dropped, not the
        /// whole report.</summary>
        public const long CapBytes = 20L << 20;

        static readonly byte[] Crlf = "\r\n"u8.ToArray();

        /// <summary>The multipart boundary a report is packed AND posted with — one per report id, so the outbox body
        /// written at enqueue time and the <c>Content-Type</c> header sent at drain time can never disagree, and the
        /// contract fixtures (<c>ops/crash/contract/*.multipart</c>) are reproducible byte for byte.</summary>
        public static string BoundaryFor(string reportId) => "wavee-" + reportId;

        /// <summary>Builds the <c>multipart/form-data</c> body the Worker ingest contract (§I) expects: parts
        /// <c>summary</c> (<c>application/json</c>, camelCase via <see cref="CrashJson"/>), <c>report</c>/<c>tail</c>
        /// (<c>text/plain; charset=utf-8</c>), and — only when it fits under <paramref name="capBytes"/> —
        /// <c>dump</c> (<c>application/octet-stream</c>, filename <c>minidump.dmp</c>). Deterministic for a given
        /// <paramref name="boundary"/>: same input, same bytes, every time (byte-exact multipart tests depend on this).</summary>
        public static byte[] Pack(ScrubbedBundle b, ReadOnlyMemory<byte>? dump, long capBytes, string boundary, out bool dumpDropped)
        {
            byte[] summaryJson = JsonSerializer.SerializeToUtf8Bytes(b.Summary, CrashJson.Default.Summary);
            byte[] reportBytes = Encoding.UTF8.GetBytes(b.ReportTxt);
            byte[] tailBytes = Encoding.UTF8.GetBytes(b.TailTxt);

            if (dump is { } d)
            {
                byte[] withDump = Build(boundary, summaryJson, reportBytes, tailBytes, d.Span);
                if (withDump.LongLength <= capBytes) { dumpDropped = false; return withDump; }
                dumpDropped = true;
                return Build(boundary, summaryJson, reportBytes, tailBytes, null);
            }
            dumpDropped = false;
            return Build(boundary, summaryJson, reportBytes, tailBytes, null);
        }

        static byte[] Build(string boundary, byte[] summaryJson, byte[] report, byte[] tail, ReadOnlySpan<byte> dump)
        {
            using var ms = new MemoryStream(summaryJson.Length + report.Length + tail.Length + dump.Length + 512);
            WritePart(ms, boundary, "summary", "application/json", null, summaryJson);
            WritePart(ms, boundary, "report", "text/plain; charset=utf-8", null, report);
            WritePart(ms, boundary, "tail", "text/plain; charset=utf-8", null, tail);
            if (dump.Length > 0) WritePart(ms, boundary, "dump", "application/octet-stream", "minidump.dmp", dump);
            WriteAscii(ms, "--" + boundary + "--");
            ms.Write(Crlf);
            return ms.ToArray();
        }

        static void WritePart(MemoryStream ms, string boundary, string name, string contentType, string? filename, ReadOnlySpan<byte> content)
        {
            WriteAscii(ms, "--" + boundary);
            ms.Write(Crlf);
            WriteAscii(ms, filename is null
                ? "Content-Disposition: form-data; name=\"" + name + "\""
                : "Content-Disposition: form-data; name=\"" + name + "\"; filename=\"" + filename + "\"");
            ms.Write(Crlf);
            WriteAscii(ms, "Content-Type: " + contentType);
            ms.Write(Crlf);
            ms.Write(Crlf);
            ms.Write(content);
            ms.Write(Crlf);
        }

        static void WriteAscii(MemoryStream ms, string s)
        {
            byte[] buf = Encoding.ASCII.GetBytes(s);
            ms.Write(buf, 0, buf.Length);
        }
    }

    /// <summary>The short report id a person reads, says and pastes — <c>3f9c-2b1a</c> for <c>3f9c2b1a…</c>: the
    /// Reports list rows, the "sent" toast, and the GitHub crash form's <c>report-id</c> field. The dashboard's search
    /// strips the dash and matches it as an id prefix. An id shorter than 8 characters is shown as it is.</summary>
    public static string ShortId(string reportId) => reportId.Length >= 8 ? reportId[..4] + "-" + reportId[4..8] : reportId;

    // ── 2. UploadPolicy — pure: one HTTP answer → what happens to the outbox entry (appendix A4) ─────────────────────

    /// <summary>What one upload attempt's answer means. <see cref="AlreadySent"/> is the Worker's 409 for a report id it
    /// already holds — the report IS on the service, so it settles as Sent; <see cref="Erased"/> is the 410 tombstone of
    /// an install the user erased (§J).</summary>
    public enum UploadOutcome : byte { Sent, AlreadySent, Rejected, Erased, RetryServer, RetryNetwork }

    public static class UploadPolicy
    {
        /// <summary>The launch on which a still-failing SERVER retry gives up (the third launch that saw one).</summary>
        public const int GiveUpAfterServerRetryLaunches = 3;

        /// <summary>The server-retry backoff never waits longer than this.</summary>
        public const int MaxBackoffMinutes = 60;

        /// <summary><c>null</c> = no HTTP answer at all (offline, DNS, reset, timeout).</summary>
        public static UploadOutcome Classify(int? http) => http switch
        {
            null => UploadOutcome.RetryNetwork,
            >= 200 and < 300 => UploadOutcome.Sent,
            409 => UploadOutcome.AlreadySent,
            410 => UploadOutcome.Erased,
            429 or >= 500 => UploadOutcome.RetryServer,
            _ => UploadOutcome.Rejected,
        };

        /// <summary>The next step: drop the outbox entry or keep it, and the state <c>send.json</c> records.</summary>
        public readonly record struct Step(bool DeleteEntry, SendState State, string? Error);

        /// <param name="serverRetryLaunches">How many launches (this one included) have seen a server retry for this
        /// report — only meaningful for <see cref="UploadOutcome.RetryServer"/>.</param>
        /// <param name="status">The answer as text (<c>"HTTP 503"</c>) — the error a rejected / retried report keeps.</param>
        public static Step Next(UploadOutcome o, int serverRetryLaunches, string status) => o switch
        {
            UploadOutcome.Sent or UploadOutcome.AlreadySent => new(true, SendState.Sent, null),
            UploadOutcome.Erased => new(true, SendState.Failed, "install erased"),
            UploadOutcome.Rejected => new(true, SendState.Failed, status),
            UploadOutcome.RetryServer when serverRetryLaunches >= GiveUpAfterServerRetryLaunches => new(true, SendState.Failed, "gave up"),
            UploadOutcome.RetryServer => new(false, SendState.Queued, status),
            _ => new(false, SendState.Queued, "offline"),   // a network failure never gives up
        };

        /// <summary>2^n minutes after the n-th server retry, capped at <see cref="MaxBackoffMinutes"/>.</summary>
        public static TimeSpan Backoff(int serverAttempts) =>
            TimeSpan.FromMinutes(Math.Min(MaxBackoffMinutes, Math.Pow(2, Math.Max(0, serverAttempts))));

        /// <summary>Whether an entry may be tried now: never server-retried, no recorded try, or its backoff elapsed.</summary>
        public static bool Due(int serverAttempts, DateTime? lastTryUtc, DateTime nowUtc) =>
            serverAttempts <= 0 || lastTryUtc is not { } t || nowUtc - t >= Backoff(serverAttempts);

        /// <summary>Per-request timeout: 30 s plus a second per 50 KB (a 20 MB dump on a slow uplink), at most 10 min.
        /// The HttpClient itself has no timeout — a fixed one either cut big dumps off or let small posts hang.</summary>
        public static TimeSpan Timeout(long bodyBytes) => TimeSpan.FromSeconds(Math.Clamp(30 + bodyBytes / 50_000, 30, 600));

        /// <summary>An outbox entry whose bundle folder is gone (the user deleted the report): dropped, never sent.
        /// An entry with no recorded folder is not an orphan — it is still sent, it just cannot record the outcome.</summary>
        public static bool IsOrphan(string bundleDir, bool exists) => bundleDir.Length > 0 && !exists;
    }

    /// <summary>Which toast one upload state earns. Every card for one report shares the dedupe key <c>crash:&lt;id&gt;</c>
    /// (Screens/Crash.UI.cs); "queued" is said once per report, never once per retry.</summary>
    public static class UploadToasts
    {
        public enum Toast : byte { None, Sent, Queued, Failed }

        public static Toast For(SendState s, bool queuedAlreadyShown) => s switch
        {
            SendState.Sent => Toast.Sent,
            SendState.Failed => Toast.Failed,
            SendState.Queued => queuedAlreadyShown ? Toast.None : Toast.Queued,
            _ => Toast.None,
        };
    }

    // ── 3. the outbox sidecar (flat text, not JSON — see the file header) ──────────────────────────────────────────

    /// <summary>The <c>&lt;reportId&gt;.meta</c> sidecar's parsed form. <see cref="Attempts"/> = server retries so far
    /// (the backoff exponent); <see cref="Launches"/> = launches that saw a server retry (the give-up counter).</summary>
    public sealed record OutboxMeta(string BundleDir, int Attempts, bool IncludeDump, DateTime? LastTryUtc, int Launches)
    {
        public static readonly OutboxMeta Empty = new("", 0, true, null, 0);

        public static OutboxMeta Parse(string text)
        {
            string dir = "";
            int attempts = 0, launches = 0;
            bool includeDump = true;
            DateTime? lastTry = null;
            foreach (string line in text.Split('\n'))
            {
                string l = line.TrimEnd('\r');
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                string key = l[..eq], value = l[(eq + 1)..];
                switch (key)
                {
                    case "bundleDir": dir = value; break;
                    case "attempts": _ = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out attempts); break;
                    case "includeDump": includeDump = value == "true"; break;
                    case "launches": _ = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out launches); break;
                    case "lastTryUtc":
                        // RoundtripKind cannot be combined with Assume*/AdjustToUniversal (TryParse throws) — the "O" text
                        // carries its own offset; AssumeUniversal covers a bare stamp, AdjustToUniversal yields UTC.
                        if (value.Length > 0 && DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)) lastTry = dt;
                        break;
                }
            }
            return new OutboxMeta(dir, attempts, includeDump, lastTry, launches);
        }

        public string Format()
        {
            var sb = new StringBuilder(128);
            sb.Append("bundleDir=").Append(BundleDir).Append('\n');
            sb.Append("attempts=").Append(Attempts.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("includeDump=").Append(IncludeDump ? "true" : "false").Append('\n');
            sb.Append("lastTryUtc=").Append(LastTryUtc is { } t ? t.ToString("O", CultureInfo.InvariantCulture) : "").Append('\n');
            sb.Append("launches=").Append(Launches.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>The result of a right-to-erasure request (§J) — the confirm dialog's toast reads this directly.</summary>
    public sealed record DeleteResult(bool Ok, int Deleted, string? Error);

    // ── 4. Uploader — the host-side outbox ──────────────────────────────────────────────────────────────────────────

    public static class Uploader
    {
        /// <summary>Never touches the network when this is false (an unstamped <c>dotnet run</c>, the E2E package, or a
        /// build stamped without the key or the quad — see <see cref="WaveeVersionInfo.CrashReportingAvailable"/>).</summary>
        public static bool Configured => Platform.Version.CrashReportingAvailable;

        /// <summary>Bumps on every outbox change (enqueue, sent, dropped, discarded, forgotten) — the Reports list's
        /// "N queued" row and the Settings "Send now"/"Discard" buttons read it.</summary>
        public static readonly Signal<int> OutboxVersion = new(0);

        static Action<Action>? s_post;
        static int s_draining, s_drainRequested, s_unconfiguredLogged;
        static IDisposable? s_onlineSub;
        static System.Threading.Timer? s_retryTimer;
        static readonly object s_gate = new();
        /// <summary>Report ids whose server-retry launch was already counted by THIS process (see <see cref="OutboxMeta.Launches"/>).</summary>
        static readonly HashSet<string> s_serverRetryCounted = new(StringComparer.Ordinal);
        /// <summary>The <see cref="Enqueue"/> callers waiting on a report's outcome, by report id (in memory only).</summary>
        static readonly Dictionary<string, Action<SendRecord>> s_waiters = new(StringComparer.Ordinal);

        static readonly Lazy<HttpClient> s_http = new(BuildClient);

        /// <summary>Wired once at boot (App.cs): subscribes to the OS's own connectivity connection point (the same NLM
        /// pillar <c>Platform.Network.Install</c> uses) and drains on every online edge, then runs the LAUNCH drain — a
        /// report queued by an earlier run goes out now, not on the next connectivity change. <paramref name="post"/> is
        /// the UI-thread marshaller every send.json write, Signal write and settled callback goes through.</summary>
        public static void Install(Action<Action> post)
        {
            s_post = post;
            try
            {
                s_onlineSub = FluentGpu.WindowsApi.Network.NetworkStatus.Subscribe(static online => { if (online) Drain(); });
            }
            catch (Exception ex)
            {
                Log.Warn("crash", "upload.network.subscribe.failed", ex);
                s_onlineSub = null;
            }
            if (Configured) Drain();
        }

        public static int QueuedCount()
        {
            try
            {
                string dir = Files.OutboxDir(Host.LogFolder);
                return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bundle", SearchOption.TopDirectoryOnly).Length : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        /// <summary>Off-thread: scrub → pack → write the meta sidecar, mark <c>send.json</c> Queued, then publish
        /// <c>outbox\&lt;reportId&gt;.bundle</c> → <see cref="Drain"/>. Fire-and-forget by design.
        /// <paramref name="settled"/> (optional) hears this report's upload outcomes ON THE UI THREAD: a Queued record
        /// after an attempt that will be retried (offline, 429/5xx), then exactly one final Sent or Failed record, after
        /// which it is dropped. A later <see cref="Enqueue"/> of the same report replaces it.</summary>
        public static void Enqueue(BundleInfo b, bool includeDump, Action<SendRecord>? settled = null)
        {
            if (settled is not null) lock (s_gate) s_waiters[b.Summary.ReportId] = settled;
            _ = Task.Run(() => EnqueueCore(b, includeDump));
        }

        static void EnqueueCore(BundleInfo b, bool includeDump)
        {
            string reportId = b.Summary.ReportId;
            try
            {
                var scrubbed = ScrubForUpload(b, includeDump, out ReadOnlyMemory<byte>? dump);
                byte[] packed = Bundle.Pack(scrubbed, dump, Bundle.CapBytes, Bundle.BoundaryFor(reportId), out bool dumpDropped);
                bool dumpIncluded = dump is not null && !dumpDropped;

                string dir = Files.OutboxDir(Host.LogFolder);
                Directory.CreateDirectory(dir);
                // Order matters: the meta first and the Queued record posted BEFORE the .bundle appears, so a drain that
                // is already running can never pick up a body without its sidecar, nor have its "sent" overwritten by
                // this "queued". The body is written beside and renamed in, so a drain never reads half of it.
                WriteMeta(dir, reportId, new OutboxMeta(b.Dir, 0, dumpIncluded, null, 0));
                WriteSendPosted(b.Dir, new SendRecord(SendState.Queued, null, null, 0, dumpIncluded));
                string bundlePath = Path.Combine(dir, reportId + ".bundle"), tmp = bundlePath + ".tmp";
                File.WriteAllBytes(tmp, packed);
                File.Move(tmp, bundlePath, overwrite: true);

                Log.Info("crash", "upload.queued id=" + reportId + " bytes=" + packed.Length.ToString(CultureInfo.InvariantCulture));
                Bump();
                Drain();
            }
            catch (Exception ex)
            {
                Log.Warn("crash", "upload.enqueue.failed id=" + reportId, ex);
                Settle(b.Dir, reportId, new SendRecord(SendState.Failed, null, "couldn't queue the report", 0, false), final: true);
            }
        }

        /// <summary>The engine-free recovery dialog's "Send": scrub → pack → POST synchronously, returning the record
        /// it also writes to <c>send.json</c>. A final answer (sent, already sent, rejected, erased) also removes the
        /// report's outbox entry, if it had one — otherwise a later drain's 409 would rewrite the outcome. A retryable
        /// answer is reported as not sent (Queued while the outbox still holds the report and will retry it itself,
        /// else Failed, with the reason).</summary>
        public static async Task<SendRecord> SendNow(BundleInfo b, bool includeDump, CancellationToken ct)
        {
            string reportId = b.Summary.ReportId;
            SendRecord rec;
            bool final = false;
            try
            {
                var scrubbed = ScrubForUpload(b, includeDump, out ReadOnlyMemory<byte>? dump);
                byte[] packed = Bundle.Pack(scrubbed, dump, Bundle.CapBytes, Bundle.BoundaryFor(reportId), out bool dumpDropped);
                bool dumpIncluded = dump is not null && !dumpDropped;
                var (code, error) = await PostAsync(packed, reportId, ct).ConfigureAwait(false);
                var outcome = UploadPolicy.Classify(code);
                string status = StatusText(code);
                var step = UploadPolicy.Next(outcome, 0, status);
                LogOutcome(reportId, outcome, step, status, error, packed.Length, 1);
                final = step.DeleteEntry;
                rec = final
                    ? new SendRecord(step.State, step.State == SendState.Sent ? NowIso() : null, step.Error, 1, dumpIncluded)
                    : new SendRecord(HasOutboxEntry(reportId) ? SendState.Queued : SendState.Failed, null,
                        code is null ? error ?? status : status, 1, dumpIncluded);
            }
            catch (Exception ex)
            {
                rec = new SendRecord(SendState.Failed, null, ex.Message, 1, false);
            }
            if (final)
            {
                try { DeleteOutboxEntry(Files.OutboxDir(Host.LogFolder), reportId); }
                catch (Exception ex) { Log.Warn("crash", "upload.sendnow.outbox.failed id=" + reportId, ex); }
                Bump();
            }
            Settle(b.Dir, reportId, rec, final);
            return rec;
        }

        /// <summary>Single-flight with re-loop: a call while a drain is running asks it for one more pass (so a bundle
        /// enqueued mid-drain, or an online edge that arrives mid-drain, is never stranded). Each pass walks the outbox
        /// oldest-first: drops orphans (the report was deleted), skips entries still inside their backoff, POSTs the
        /// rest and settles each through <see cref="UploadPolicy"/> (Classify → Next). A pass that leaves a server
        /// retry waiting arms a one-shot timer for when it comes due.</summary>
        public static void Drain()
        {
            if (!Configured) { LogUnconfiguredOnce(); return; }
            Volatile.Write(ref s_drainRequested, 1);
            if (Interlocked.CompareExchange(ref s_draining, 1, 0) != 0) return;
            _ = Task.Run(DrainLoopAsync);
        }

        static async Task DrainLoopAsync()
        {
            while (true)
            {
                try
                {
                    while (Interlocked.Exchange(ref s_drainRequested, 0) != 0)
                        await DrainPassAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Warn("crash", "upload.drain.failed", ex); }
                finally { Volatile.Write(ref s_draining, 0); }

                // A request that landed between the last check and the release above must not strand: take the flag
                // back and go round again (or leave it to the caller that already took it).
                if (Volatile.Read(ref s_drainRequested) == 0 || Interlocked.CompareExchange(ref s_draining, 1, 0) != 0) return;
            }
        }

        static async Task DrainPassAsync()
        {
            string dir = Files.OutboxDir(Host.LogFolder);
            if (!Directory.Exists(dir)) return;
            string[] bundles = Directory.GetFiles(dir, "*.bundle", SearchOption.TopDirectoryOnly);
            Array.Sort(bundles, static (a, b) => SafeCreationTicks(a).CompareTo(SafeCreationTicks(b)));

            DateTime? nextDueUtc = null;
            foreach (string bundlePath in bundles)
            {
                string reportId = Path.GetFileNameWithoutExtension(bundlePath);
                OutboxMeta meta = ReadMeta(Path.Combine(dir, reportId + ".meta"));

                if (UploadPolicy.IsOrphan(meta.BundleDir, meta.BundleDir.Length > 0 && Directory.Exists(meta.BundleDir)))
                {
                    Log.Info("crash", "upload.orphan id=" + reportId);
                    DeleteOutboxEntry(dir, reportId);
                    DropWaiter(reportId);
                    Bump();
                    continue;
                }

                if (!UploadPolicy.Due(meta.Attempts, meta.LastTryUtc, DateTime.UtcNow))
                {
                    if (meta.LastTryUtc is { } last) nextDueUtc = Earliest(nextDueUtc, last + UploadPolicy.Backoff(meta.Attempts));
                    continue;
                }

                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(bundlePath).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }   // deleted / forgotten meanwhile

                var (code, error) = await PostAsync(bytes, reportId).ConfigureAwait(false);
                var outcome = UploadPolicy.Classify(code);
                string status = StatusText(code);
                int launches = outcome == UploadOutcome.RetryServer ? CountServerRetryLaunch(reportId, meta.Launches) : meta.Launches;
                var step = UploadPolicy.Next(outcome, launches, status);
                int attempt = meta.Attempts + 1;
                LogOutcome(reportId, outcome, step, status, error, bytes.Length, attempt);

                if (step.DeleteEntry)
                {
                    DeleteOutboxEntry(dir, reportId);
                    Settle(meta.BundleDir, reportId,
                        new SendRecord(step.State, step.State == SendState.Sent ? NowIso() : null, step.Error, attempt, meta.IncludeDump), final: true);
                    Bump();
                    continue;
                }

                if (outcome == UploadOutcome.RetryServer)
                {
                    DateTime now = DateTime.UtcNow;
                    var next = meta with { Attempts = meta.Attempts + 1, LastTryUtc = now, Launches = launches };
                    WriteMeta(dir, reportId, next);
                    nextDueUtc = Earliest(nextDueUtc, now + UploadPolicy.Backoff(next.Attempts));
                }
                Settle(meta.BundleDir, reportId, new SendRecord(SendState.Queued, null, step.Error, attempt, meta.IncludeDump), final: false);
            }
            ArmRetry(nextDueUtc);
        }

        /// <summary>Settings' "Discard": clears the outbox without sending, marking every bundle it referenced
        /// <see cref="SendState.NotSent"/> so the Reports list stops showing "queued". Does NOT touch the remote
        /// service — for that, see <see cref="DeleteRemote"/>.</summary>
        public static void DiscardQueue()
        {
            try
            {
                string dir = Files.OutboxDir(Host.LogFolder);
                if (!Directory.Exists(dir)) return;
                foreach (string bundlePath in Directory.GetFiles(dir, "*.bundle", SearchOption.TopDirectoryOnly))
                {
                    string reportId = Path.GetFileNameWithoutExtension(bundlePath);
                    OutboxMeta meta = ReadMeta(Path.Combine(dir, reportId + ".meta"));
                    DeleteOutboxEntry(dir, reportId);
                    DropWaiter(reportId);
                    if (meta.BundleDir.Length > 0)
                        WriteSendPosted(meta.BundleDir, new SendRecord(SendState.NotSent, null, null, meta.Attempts, meta.IncludeDump));
                }
                Bump();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("crash", "upload.discard.failed", ex);
            }
        }

        /// <summary>A report folder is about to be deleted (<c>Crash.Host.Delete</c> calls this FIRST): every outbox
        /// entry whose meta points at <paramref name="bundleDir"/> is removed, so a deleted report is never uploaded
        /// afterwards. A drain already mid-POST for it settles into a folder that no longer exists, which records
        /// nothing (<c>Bundles.WriteSend</c> never recreates a folder).</summary>
        public static void ForgetBundle(string bundleDir)
        {
            if (string.IsNullOrEmpty(bundleDir)) return;
            try
            {
                string dir = Files.OutboxDir(Host.LogFolder);
                if (!Directory.Exists(dir)) return;
                string target = NormalizeDir(bundleDir);
                bool any = false;
                foreach (string metaPath in Directory.GetFiles(dir, "*.meta", SearchOption.TopDirectoryOnly))
                {
                    OutboxMeta meta = ReadMeta(metaPath);
                    if (meta.BundleDir.Length == 0 || !string.Equals(NormalizeDir(meta.BundleDir), target, StringComparison.OrdinalIgnoreCase)) continue;
                    string reportId = Path.GetFileNameWithoutExtension(metaPath);
                    DeleteOutboxEntry(dir, reportId);
                    DropWaiter(reportId);
                    Log.Info("crash", "upload.forgotten id=" + reportId);
                    any = true;
                }
                if (any) Bump();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("crash", "upload.forget.failed", ex);
            }
        }

        // ── §J. Right to erasure ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>"Delete my data…": clears the local outbox first (nothing already queued gets re-sent), then
        /// <c>DELETE {ingest}/v1/installs/&lt;installId&gt;</c>. 200 and 404 both count as success (404 = the server
        /// never had anything for this install, which is the desired end state either way); a network failure or a
        /// 5xx is NOT success — the caller shows an error and the install id stays as it is. On success the install id
        /// is ROTATED, so any report sent later (if reporting stays on) cannot be linked back to the deleted ones.</summary>
        public static async Task<DeleteResult> DeleteRemote(CancellationToken ct)
        {
            DiscardQueue();
            if (!Configured) return new DeleteResult(false, 0, "crash reporting is not configured on this build");

            string installId = Platform.Settings.Get(Platform.Keys.CrashInstallId);
            if (installId.Length == 0) return new DeleteResult(true, 0, null);   // nothing was ever sent under an id

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(UploadPolicy.Timeout(0));
                string url = EraseUrl(Platform.Version.CrashIngestUrl, installId);
                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                using var resp = await s_http.Value.SendAsync(req, timeout.Token).ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound)
                {
                    int deleted = 0;
                    try
                    {
                        string body = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.Number)
                            deleted = d.GetInt32();
                    }
                    catch (Exception) { /* a malformed body still means the delete happened per the status code */ }

                    string newId = Guid.NewGuid().ToString("N");
                    Platform.Settings.Set(Platform.Keys.CrashInstallId, newId);
                    Log.Info("crash", "erase.done deleted=" + deleted.ToString(CultureInfo.InvariantCulture));
                    return new DeleteResult(true, deleted, null);
                }

                string error = "HTTP " + (int)resp.StatusCode;
                Log.Warn("crash", "erase.failed status=" + error);
                return new DeleteResult(false, 0, error);
            }
            catch (Exception ex)
            {
                Log.Warn("crash", "erase.failed", ex);
                return new DeleteResult(false, 0, "Couldn't reach the crash service - try again later");
            }
        }

        /// <summary>The erase endpoint's URL, pure so <see cref="DeleteRemote"/>'s request shape is a fact without a
        /// network call: <c>{ingest}/v1/installs/&lt;installId&gt;</c>, tolerating a trailing slash on the ingest base
        /// and percent-encoding the install id (it is a GUID "N" string today, but this never assumes that).</summary>
        public static string EraseUrl(string ingestBaseUrl, string installId) =>
            ingestBaseUrl.TrimEnd('/') + "/v1/installs/" + Uri.EscapeDataString(installId);

        // ── internals ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Reads report/tail/dump off disk and scrubs them. <paramref name="dump"/> is <c>null</c> exactly
        /// when no dump should be attempted (or there is none on disk) — NOT an empty span — so
        /// <see cref="Bundle.Pack"/> can tell "there is no dump" from "there is a zero-byte dump".</summary>
        static ScrubbedBundle ScrubForUpload(BundleInfo b, bool includeDump, out ReadOnlyMemory<byte>? dump)
        {
            string reportTxt = SafeReadText(b.ReportTxt);
            string[] tailLines = SafeReadLines(b.TailTxt);
            var rules = Scrubber.RulesNow();
            byte[]? bytes = includeDump ? SafeReadBytes(b.DumpPath) : null;
            dump = bytes is null ? null : new ReadOnlyMemory<byte>(bytes);
            return Scrubber.Scrub(b.Summary, reportTxt, tailLines, rules);
        }

        /// <summary>One POST. <c>Status</c> is the HTTP status code, or <c>null</c> when there was no answer at all
        /// (offline, DNS, reset, the per-request <see cref="UploadPolicy.Timeout"/>, a cancelled <paramref name="ct"/>)
        /// — then <c>Error</c> says why. Every decision about the answer is <see cref="UploadPolicy"/>'s.</summary>
        static async Task<(int? Status, string? Error)> PostAsync(byte[] body, string reportId, CancellationToken ct = default)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(UploadPolicy.Timeout(body.LongLength));
                using var content = new ByteArrayContent(body);
                content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + Bundle.BoundaryFor(reportId));
                string url = Platform.Version.CrashIngestUrl.TrimEnd('/') + "/v1/report";
                using var resp = await s_http.Value.PostAsync(url, content, timeout.Token).ConfigureAwait(false);
                return ((int)resp.StatusCode, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, "timed out");
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        static string StatusText(int? code) => code is int c ? "HTTP " + c.ToString(CultureInfo.InvariantCulture) : "network error";

        static string NowIso() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        static void LogOutcome(string reportId, UploadOutcome outcome, UploadPolicy.Step step, string status, string? error, int bytes, int attempt)
        {
            string tail = " id=" + reportId + " status=" + status + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture)
                + " attempt=" + attempt.ToString(CultureInfo.InvariantCulture);
            switch (outcome)
            {
                case UploadOutcome.Sent: Log.Info("crash", "upload.sent" + tail); break;
                case UploadOutcome.AlreadySent: Log.Info("crash", "upload.sent" + tail + " (already on the service)"); break;
                case UploadOutcome.Erased: Log.Warn("crash", "upload.erased" + tail); break;
                case UploadOutcome.Rejected: Log.Warn("crash", "upload.rejected" + tail); break;
                case UploadOutcome.RetryServer when step.DeleteEntry: Log.Warn("crash", "upload.giveup" + tail); break;
                case UploadOutcome.RetryServer: Log.Warn("crash", "upload.retry" + tail); break;
                default: Log.Info("crash", "upload.offline" + tail + " error=" + (error ?? "")); break;
            }
        }

        static HttpClient BuildClient()
        {
            var http = new HttpClient(Wire.Handler("crash", new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                MaxConnectionsPerServer = 4,
            })) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };   // every request carries its own UploadPolicy.Timeout
            try { http.DefaultRequestHeaders.TryAddWithoutValidation("X-Wavee-Ingest", Platform.Version.CrashIngestKey); } catch { }
            try { http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Platform.Version.UserAgent(RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString())); } catch { }
            return http;
        }

        static void LogUnconfiguredOnce()
        {
            if (Interlocked.Exchange(ref s_unconfiguredLogged, 1) == 0) Log.Info("crash", "upload.unconfigured");
        }

        static void Post(Action a)
        {
            if (s_post is { } p) p(a);
            else a();
        }

        static void Bump() => Post(static () => OutboxVersion.Update(static v => v + 1));

        /// <summary>Writes <c>send.json</c> (through <c>Crash.Host.WriteSend</c>, which also bumps the Reports list) and
        /// then hands the record to the report's waiter, both on the UI poster, in that order. A final record drops the
        /// waiter.</summary>
        static void Settle(string bundleDir, string reportId, SendRecord rec, bool final)
        {
            Action<SendRecord>? waiter;
            lock (s_gate)
            {
                if (s_waiters.TryGetValue(reportId, out waiter) && final) s_waiters.Remove(reportId);
            }
            Post(() =>
            {
                if (bundleDir.Length > 0)
                {
                    try { Host.WriteSend(bundleDir, rec); } catch (Exception ex) { Log.Warn("crash", "upload.writesend.failed", ex); }
                }
                if (waiter is not null)
                {
                    try { waiter(rec); } catch (Exception ex) { Log.Warn("crash", "upload.settled.callback.failed id=" + reportId, ex); }
                }
            });
        }

        static void WriteSendPosted(string bundleDir, SendRecord rec)
        {
            if (bundleDir.Length == 0) return;
            Post(() =>
            {
                try { Host.WriteSend(bundleDir, rec); } catch (Exception ex) { Log.Warn("crash", "upload.writesend.failed", ex); }
            });
        }

        static void DropWaiter(string reportId)
        {
            lock (s_gate) s_waiters.Remove(reportId);
        }

        /// <summary>A launch is charged against a report's give-up budget at most once per PROCESS, and only when the
        /// server (not the network) asked for a retry: a bundle that sees several 5xx in one run costs one launch.</summary>
        static int CountServerRetryLaunch(string reportId, int launches)
        {
            lock (s_gate) return s_serverRetryCounted.Add(reportId) ? launches + 1 : launches;
        }

        static DateTime? Earliest(DateTime? a, DateTime b) => a is { } x && x <= b ? x : b;

        /// <summary>One-shot: drain again when the earliest server retry comes due (clamped to 1 s … the backoff cap).</summary>
        static void ArmRetry(DateTime? dueUtc)
        {
            if (dueUtc is not { } due) return;
            double ms = Math.Clamp((due - DateTime.UtcNow).TotalMilliseconds, 1_000, (UploadPolicy.MaxBackoffMinutes + 1) * 60_000.0);
            lock (s_gate)
            {
                s_retryTimer ??= new System.Threading.Timer(static _ => Drain(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                s_retryTimer.Change((long)ms, System.Threading.Timeout.Infinite);
            }
        }

        static bool HasOutboxEntry(string reportId)
        {
            try { return File.Exists(Path.Combine(Files.OutboxDir(Host.LogFolder), reportId + ".bundle")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        static string NormalizeDir(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (Exception) { return path.TrimEnd('\\', '/'); }
        }

        static void WriteMeta(string dir, string reportId, OutboxMeta meta)
        {
            try { File.WriteAllText(Path.Combine(dir, reportId + ".meta"), meta.Format(), new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("crash", "upload.meta.write.failed id=" + reportId, ex); }
        }

        static OutboxMeta ReadMeta(string path)
        {
            try { return File.Exists(path) ? OutboxMeta.Parse(File.ReadAllText(path)) : OutboxMeta.Empty; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return OutboxMeta.Empty; }
        }

        static void DeleteOutboxEntry(string dir, string reportId)
        {
            try { File.Delete(Path.Combine(dir, reportId + ".bundle")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { File.Delete(Path.Combine(dir, reportId + ".meta")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        static long SafeCreationTicks(string path)
        {
            try { return File.GetCreationTimeUtc(path).Ticks; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        static string SafeReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
        }

        static string[] SafeReadLines(string path)
        {
            try { return File.Exists(path) ? File.ReadAllLines(path) : []; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }

        static byte[]? SafeReadBytes(string? path)
        {
            if (path is not { Length: > 0 }) return null;
            try { return File.Exists(path) ? File.ReadAllBytes(path) : null; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }
}
