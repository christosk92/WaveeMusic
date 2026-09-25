// ── Platform/Crash.Upload.cs ───────────────────────────────────────────────────────────────────────────────────────
// The crash & diagnostics pipeline's PACKER + UPLOADER (WP-C, half 2): `Crash.Bundle.Pack` turns one scrubbed bundle
// into the exact multipart/form-data body the Worker ingest contract expects (pure, byte-exact — tested directly);
// `Crash.Uploader` is the host-side outbox — enqueue, drain with backoff, single-flight, offline-safe — plus the
// right-to-erasure client (§J).
//
// Role: CORE (Bundle) + SHELL (Uploader — network, disk, a Signal<int>)
// Owner: WP-C
// Wave: crash-diagnostics (post-0.3; independent of the migration waves — nothing here renders)
// Budget: n/a (new file)
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §B.5, §F ("C · Scrub + upload"), §I, §J (erasure)
//
// `Crash.Uploader` codes against `Crash.Host.LogFolder`, `Crash.Host.WriteSend(dir, SendRecord)` (WP-B, built
// concurrently) — this file does not implement the crash bundle writer or the Reports list, only what carries a
// bundle from the local outbox to the ingest endpoint and back.
//
// OUTBOX SHAPE: `<LogFolder>\crash\outbox\<reportId>.bundle` (the packed multipart body, written once) beside
// `<reportId>.meta` (a small flat `key=value` sidecar — bundleDir/attempts/includeDump/lastTryUtc/launches; not JSON,
// so no new source-generated JSON context is needed here — `Crash.CrashJson`, WP-A's, is not touched by this file).

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

    // ── 2. the outbox sidecar (flat text, not JSON — see the file header) ──────────────────────────────────────────

    /// <summary>The <c>&lt;reportId&gt;.meta</c> sidecar's parsed form. Internal: no other work package reads the
    /// outbox directly (the Reports list reads <c>send.json</c> via <c>Crash.Host</c>, WP-B).</summary>
    sealed record OutboxMeta(string BundleDir, int Attempts, bool IncludeDump, DateTime? LastTryUtc, int Launches)
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
                        if (value.Length > 0 && DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var dt)) lastTry = dt.ToUniversalTime();
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

    // ── 3. Uploader — the host-side outbox ──────────────────────────────────────────────────────────────────────────

    public static class Uploader
    {
        /// <summary>Never touches the network when this is false (an unstamped <c>dotnet run</c> or the E2E package) —
        /// logged once via <see cref="LogUnconfiguredOnce"/> rather than on every <see cref="Drain"/>.</summary>
        public static bool Configured => Platform.Version.CrashIngestUrl.Length > 0;

        /// <summary>Bumps on every outbox change (enqueue, sent, dropped, discarded) — the Reports list's "N queued"
        /// row and the Settings "Send now"/"Discard" buttons read it.</summary>
        public static readonly Signal<int> OutboxVersion = new(0);

        static Action<Action>? s_post;
        static int s_draining;
        static int s_unconfiguredLogged;
        static IDisposable? s_onlineSub;
        static readonly HashSet<string> s_launchCounted = new(StringComparer.Ordinal);
        static readonly object s_launchGate = new();

        static readonly Lazy<HttpClient> s_http = new(BuildClient);

        /// <summary>Wired once by the orchestrator after <c>Update.Host.Start</c>: subscribes to the OS's own
        /// connectivity connection point (the same NLM pillar <c>Platform.Network.Install</c> uses) and drains on
        /// every online edge. <paramref name="post"/> is the UI-thread marshaller (every <see cref="Signal{T}"/> write
        /// in this class goes through it — <see cref="Enqueue"/>/<see cref="Drain"/> both run off the UI thread).</summary>
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

        /// <summary>Off-thread: scrub → pack → write <c>outbox\&lt;reportId&gt;.bundle</c> + its meta sidecar → mark
        /// <c>send.json</c> Queued → <see cref="Drain"/>. Fire-and-forget by design (the caller is a UI click or a
        /// just-written crash bundle; nobody awaits this).</summary>
        public static void Enqueue(BundleInfo b, bool includeDump) => _ = Task.Run(() => EnqueueCore(b, includeDump));

        static void EnqueueCore(BundleInfo b, bool includeDump)
        {
            try
            {
                var scrubbed = ScrubForUpload(b, includeDump, out ReadOnlyMemory<byte>? dump);
                string reportId = b.Summary.ReportId;
                byte[] packed = Bundle.Pack(scrubbed, dump, Bundle.CapBytes, BoundaryFor(reportId), out bool dumpDropped);

                string dir = Files.OutboxDir(Host.LogFolder);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, reportId + ".bundle"), packed);
                WriteMeta(dir, reportId, new OutboxMeta(b.Dir, 0, includeDump && !dumpDropped, null, 0));

                Host.WriteSend(b.Dir, new SendRecord(SendState.Queued, null, null, 0, includeDump && !dumpDropped));
                Log.Info("crash", "upload.queued id=" + reportId);
                Bump();
                Drain();
            }
            catch (Exception ex)
            {
                Log.Warn("crash", "upload.enqueue.failed id=" + b.Summary.ReportId, ex);
            }
        }

        /// <summary>The recovery dialog's / Settings' "Send now": scrub → pack → POST synchronously, returning the
        /// record it also writes to <c>send.json</c>. Never touches the outbox file — a manual send is one-shot.</summary>
        public static async Task<SendRecord> SendNow(BundleInfo b, bool includeDump, CancellationToken ct)
        {
            SendRecord rec;
            try
            {
                var scrubbed = ScrubForUpload(b, includeDump, out ReadOnlyMemory<byte>? dump);
                string reportId = b.Summary.ReportId;
                byte[] packed = Bundle.Pack(scrubbed, dump, Bundle.CapBytes, BoundaryFor(reportId), out bool dumpDropped);
                var result = await PostAsync(packed, reportId, ct).ConfigureAwait(false);
                rec = result.Ok
                    ? new SendRecord(SendState.Sent, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), null, 1, includeDump && !dumpDropped)
                    : new SendRecord(SendState.Failed, null, result.Error ?? result.Status, 1, includeDump && !dumpDropped);
                if (result.Ok) Log.Info("crash", "upload.sent id=" + reportId + " status=" + result.Status + " bytes=" + packed.Length.ToString(CultureInfo.InvariantCulture));
                else Log.Warn("crash", "upload.rejected id=" + reportId + " status=" + result.Status + " bytes=" + packed.Length.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                rec = new SendRecord(SendState.Failed, null, ex.Message, 1, false);
            }
            try { Host.WriteSend(b.Dir, rec); } catch (Exception ex) { Log.Warn("crash", "upload.sendnow.writesend.failed", ex); }
            return rec;
        }

        /// <summary>Off-thread, single-flight (a second call while one is already running is a no-op — the running
        /// drain will see whatever the caller just enqueued because it re-lists the outbox directory each pass): walks
        /// the outbox oldest-first, POSTing each bundle. 2xx deletes; 4xx (not 429) deletes + logs; 410 (the install
        /// was erased server-side, §J) deletes with a distinct reason; 429/5xx/network keeps with exponential backoff,
        /// giving up after 3 launches.</summary>
        public static void Drain()
        {
            if (!Configured) { LogUnconfiguredOnce(); return; }
            if (Interlocked.CompareExchange(ref s_draining, 1, 0) != 0) return;
            _ = Task.Run(DrainCoreAsync);
        }

        static async Task DrainCoreAsync()
        {
            try
            {
                string dir = Files.OutboxDir(Host.LogFolder);
                if (!Directory.Exists(dir)) return;
                string[] bundles = Directory.GetFiles(dir, "*.bundle", SearchOption.TopDirectoryOnly);
                Array.Sort(bundles, static (a, b) => SafeCreationTicks(a).CompareTo(SafeCreationTicks(b)));

                foreach (string bundlePath in bundles)
                {
                    string reportId = Path.GetFileNameWithoutExtension(bundlePath);
                    string metaPath = Path.Combine(dir, reportId + ".meta");
                    OutboxMeta meta = ReadMeta(metaPath);
                    meta = CountLaunchOnce(reportId, metaPath, meta);

                    if (meta.LastTryUtc is { } last && meta.Attempts > 0)
                    {
                        double waitMinutes = Math.Pow(2, meta.Attempts);
                        if ((DateTime.UtcNow - last).TotalMinutes < waitMinutes) continue;
                    }

                    byte[] bytes;
                    try { bytes = await File.ReadAllBytesAsync(bundlePath).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                    var result = await PostAsync(bytes, reportId).ConfigureAwait(false);
                    int attempts = meta.Attempts + 1;
                    string bytesStr = bytes.Length.ToString(CultureInfo.InvariantCulture);

                    if (result.Ok)
                    {
                        Log.Info("crash", "upload.sent id=" + reportId + " status=" + result.Status + " bytes=" + bytesStr);
                        DeleteOutboxEntry(dir, reportId);
                        WriteSendSafe(meta.BundleDir, new SendRecord(SendState.Sent, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), null, attempts, meta.IncludeDump));
                        Bump();
                    }
                    else if (result.Status == "HTTP 410")
                    {
                        Log.Warn("crash", "upload.rejected id=" + reportId + " status=" + result.Status + " bytes=" + bytesStr);
                        DeleteOutboxEntry(dir, reportId);
                        WriteSendSafe(meta.BundleDir, new SendRecord(SendState.Failed, null, "install erased", attempts, meta.IncludeDump));
                        Bump();
                    }
                    else if (!result.Retry)
                    {
                        Log.Warn("crash", "upload.rejected id=" + reportId + " status=" + result.Status + " bytes=" + bytesStr);
                        DeleteOutboxEntry(dir, reportId);
                        WriteSendSafe(meta.BundleDir, new SendRecord(SendState.Failed, null, result.Error, attempts, meta.IncludeDump));
                        Bump();
                    }
                    else if (meta.Launches >= 3)
                    {
                        Log.Warn("crash", "upload.giveup id=" + reportId + " attempt=" + attempts.ToString(CultureInfo.InvariantCulture) + " status=" + result.Status);
                        DeleteOutboxEntry(dir, reportId);
                        WriteSendSafe(meta.BundleDir, new SendRecord(SendState.Failed, null, "gave up", attempts, meta.IncludeDump));
                        Bump();
                    }
                    else
                    {
                        Log.Warn("crash", "upload.retry id=" + reportId + " attempt=" + attempts.ToString(CultureInfo.InvariantCulture) + " status=" + result.Status);
                        WriteMeta(dir, reportId, meta with { Attempts = attempts, LastTryUtc = DateTime.UtcNow });
                        WriteSendSafe(meta.BundleDir, new SendRecord(SendState.Queued, null, result.Error, attempts, meta.IncludeDump));
                    }
                }
            }
            catch (Exception ex) { Log.Warn("crash", "upload.drain.failed", ex); }
            finally { Volatile.Write(ref s_draining, 0); }
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
                    if (meta.BundleDir.Length > 0)
                        WriteSendSafe(meta.BundleDir, new SendRecord(SendState.NotSent, null, null, meta.Attempts, meta.IncludeDump));
                    DeleteOutboxEntry(dir, reportId);
                }
                Bump();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("crash", "upload.discard.failed", ex);
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
                string url = EraseUrl(Platform.Version.CrashIngestUrl, installId);
                using var req = new HttpRequestMessage(HttpMethod.Delete, url);
                using var resp = await s_http.Value.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound)
                {
                    int deleted = 0;
                    try
                    {
                        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
        /// when no dump should be attempted — NOT an empty span — so <see cref="Bundle.Pack"/> can tell "there is no
        /// dump" from "there is a zero-byte dump" (the byte[]→<see cref="ReadOnlyMemory{T}"/> conversion is done here,
        /// explicitly, rather than left to an implicit array conversion at the call site).</summary>
        static ScrubbedBundle ScrubForUpload(BundleInfo b, bool includeDump, out ReadOnlyMemory<byte>? dump)
        {
            string reportTxt = SafeReadText(b.ReportTxt);
            string[] tailLines = SafeReadLines(b.TailTxt);
            var rules = Scrubber.RulesNow();
            byte[]? bytes = includeDump ? SafeReadBytes(b.DumpPath) : null;
            dump = bytes is null ? null : new ReadOnlyMemory<byte>(bytes);
            return Scrubber.Scrub(b.Summary, reportTxt, tailLines, rules);
        }

        static string BoundaryFor(string reportId) => "wavee-" + reportId;

        static async Task<(bool Ok, bool Retry, string Status, string? Error)> PostAsync(byte[] body, string reportId, CancellationToken ct = default)
        {
            try
            {
                using var content = new ByteArrayContent(body);
                content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + BoundaryFor(reportId));
                string url = Platform.Version.CrashIngestUrl.TrimEnd('/') + "/v1/report";
                using var resp = await s_http.Value.PostAsync(url, content, ct).ConfigureAwait(false);
                int code = (int)resp.StatusCode;
                string status = "HTTP " + code.ToString(CultureInfo.InvariantCulture);
                if (resp.IsSuccessStatusCode) return (true, false, status, null);
                bool retry = code == 429 || code >= 500;
                return (false, retry, status, status);
            }
            catch (Exception ex)
            {
                return (false, true, "network error", ex.Message);
            }
        }

        static HttpClient BuildClient()
        {
            var http = new HttpClient(Wire.Handler("crash", new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                MaxConnectionsPerServer = 4,
            })) { Timeout = TimeSpan.FromSeconds(30) };
            try { http.DefaultRequestHeaders.TryAddWithoutValidation("X-Wavee-Ingest", Platform.Version.CrashIngestKey); } catch { }
            try { http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Platform.Version.UserAgent(RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString())); } catch { }
            return http;
        }

        static void LogUnconfiguredOnce()
        {
            if (Interlocked.Exchange(ref s_unconfiguredLogged, 1) == 0) Log.Info("crash", "upload.unconfigured");
        }

        static void Bump()
        {
            if (s_post is { } p) p(static () => OutboxVersion.Update(static v => v + 1));
            else OutboxVersion.Update(static v => v + 1);
        }

        /// <summary>Bumps a bundle's <c>launches</c> counter at most once per PROCESS — a bundle that survives several
        /// <see cref="Drain"/> calls within the same run (multiple connectivity edges) must not be charged more than
        /// one "launch" for it.</summary>
        static OutboxMeta CountLaunchOnce(string reportId, string metaPath, OutboxMeta meta)
        {
            lock (s_launchGate)
            {
                if (!s_launchCounted.Add(reportId)) return meta;
            }
            var bumped = meta with { Launches = meta.Launches + 1 };
            WriteMeta(Path.GetDirectoryName(metaPath) ?? "", reportId, bumped);
            return bumped;
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

        static void WriteSendSafe(string bundleDir, SendRecord rec)
        {
            if (bundleDir.Length == 0) return;
            try { Host.WriteSend(bundleDir, rec); } catch (Exception ex) { Log.Warn("crash", "upload.writesend.failed", ex); }
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
