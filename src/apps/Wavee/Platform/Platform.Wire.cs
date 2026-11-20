// ── Platform/Platform.Wire.cs ───────────────────────────────────────────────────────────────────────────────────────
// EVERY outbound HTTP request the app makes, in the log. One line per call, always on (CLAUDE.md: no switches), plus a
// storm verdict when one endpoint is hit far more often than any honest client would.
//
// Role: SHELL (the handler) + CORE (`WireRules`, pure, tested)
//
// WHY THIS EXISTS. 2026-09-18: a discarded library delta re-asked itself with the same sync token forever —
// `/collection/v2/delta`, hundreds of 200s a minute — and NOTHING in the log said so: the api layer logged failures
// only, so a loop of successes was invisible. It was found in Fiddler, by accident. A request that succeeds is still a
// request; the log has to be able to answer "what is this app sending, and how often" without a proxy.
//
// HOW. `Wire.Handler(client, inner)` wraps the transport of EVERY `HttpClient` the app builds (api, session, audio cdn,
// lyrics, update, release notes). It is a `DelegatingHandler`, so a new call site cannot forget to log — only a new
// `HttpClient` can, and those are six lines in six files. The QUERY STRING IS NEVER LOGGED: cdn urls carry signed
// tokens and the api carries ids the log does not need; host + path names the endpoint.
//
// REALTIME CAPTURE (unit 4, docs/plans/wavee/realtime-capture-implementation.md §3.3). This IS the one choke point
// every `HttpClient` the app builds funnels through, so it is where the plan's `HttpCall` capture lives: a
// `Capture.Begin` at send, a `Capture.End` at the response/exception. The cause id rides on
// `HttpRequestMessage.Options` (`Wire.CauseIdOption`) — §2.2's explicit answer to "how does a caller's cause cross
// `SendAsync`'s await": no thread-static, no `AsyncLocal`, just a field on the one type built for exactly this. A
// request nobody tagged reads back 0 ("unknown cause") and becomes its own root — never a crash, never a dropped
// capture. Bodies are captured ONLY when `Capture.Enabled` (zero cost off), ONLY when `Content-Length` is known and
// small (`MaxCapturedBodyBytes`) — an unknown-length or large body (the audio cdn's range reads, in particular) is
// never buffered just to satisfy an off-by-default capture — and run through `Redactor.RedactJsonBody` first: a
// non-JSON or unredactable body is never captured raw, it is simply dropped with `Truncated = true` on the header.
//
// TWO EXCEPTIONS, BOTH FOR A REFUSED COMMAND (remote-play plan 0.2, 2026-11-20). The 403 the gateway gave a forwarded Liked
// Songs play kept nothing: the request is gzipped (`X-Transfer-Encoding: gzip`, so not JSON on the wire) and the answer's
// length is unknown (the handler inflates it and drops `Content-Length`: `recv=?`), so capture records #89/#90 held
// payload 0 and the 403's text is still unknown (gap analysis 1.3, §6 question 1). So: (1) a gzipped request body is
// inflated before the redaction, and a `player/command` body may be as large as `MaxCapturedCommandBodyBytes` (a play
// that inlines a playlist page is about 130 KB before gzip, all.saz 1169); (2) a 4xx answer of unknown length is read up
// to `MaxCapturedBodyBytes`, and the caller still gets every byte (a longer one is handed on as the bytes read, then the
// rest of the stream). Media keeps the 8 KB cap and its unknown-length 2xx reads are never buffered.

using System.Diagnostics;
using System.IO.Compression;

namespace Wavee;

/// <summary>The pure half: what a request is called in the log, and when a run of them is a storm.</summary>
public static class WireRules
{
    /// <summary>A call this slow (ms) is not routine and keeps its own wire.call line.</summary>
    public const double SlowCallMs = 1500;

    /// <summary>Routine = a 2xx/3xx answer with no transport error and not slow: it only counts toward the minute's wire.summary.</summary>
    public static bool IsRoutine(int status, bool hadError, double ms) => !hadError && status is >= 200 and < 400 && ms < SlowCallMs;

    /// <summary>More than this many calls to ONE endpoint inside <see cref="StormWindowMs"/> is a storm.</summary>
    public const int StormCalls = 40;
    public const int StormWindowMs = 60_000;

    /// <summary>The endpoint's NAME for counting: the path with its id-shaped segments folded to <c>{id}</c>, so 300
    /// different track reads are one endpoint and a loop on one path is not hidden among them. A segment is id-shaped
    /// when it is 16+ characters of letters and digits (a base62 id, a hex gid, a file id) or all digits. PURE.</summary>
    public static string EndpointOf(string method, string host, string path)
    {
        var sb = new System.Text.StringBuilder(method.Length + host.Length + path.Length + 2);
        sb.Append(method).Append(' ').Append(host);
        int i = 0;
        while (i < path.Length)
        {
            int next = path.IndexOf('/', i + 1);
            if (next < 0) next = path.Length;
            ReadOnlySpan<char> segment = path.AsSpan(i + (path[i] == '/' ? 1 : 0), next - i - (path[i] == '/' ? 1 : 0));
            sb.Append('/');
            if (IsIdShaped(segment)) sb.Append("{id}"); else sb.Append(segment);
            i = next;
        }
        return sb.ToString();
    }

    static bool IsIdShaped(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        bool digits = true;
        foreach (char c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c)) return false;
            if (!char.IsAsciiDigit(c)) digits = false;
        }
        return digits || s.Length >= 16;
    }

    /// <summary>One endpoint's sliding window. <see cref="Note"/> returns the count inside the window when THIS call
    /// is the one that crosses <see cref="StormCalls"/> — and again at every further multiple of it, so a storm that
    /// keeps going keeps saying so without a line per call — else 0. PURE (the clock is passed in).</summary>
    public sealed class Window
    {
        readonly long[] _at = new long[StormCalls];
        int _head, _count;
        long _total;

        public long Total => _total;

        public int Note(long nowMs)
        {
            _total++;
            _at[_head] = nowMs;
            _head = (_head + 1) % StormCalls;
            if (_count < StormCalls) _count++;
            if (_count < StormCalls) return 0;
            long oldest = _at[_head];                        // the slot about to be overwritten is the oldest kept
            bool storming = nowMs - oldest <= StormWindowMs;
            return storming && _total % StormCalls == 0 ? StormCalls : 0;
        }
    }
}

public static class Wire
{
    static readonly Dictionary<string, WireRules.Window> s_windows = new(StringComparer.Ordinal);
    static readonly object s_gate = new();

    /// <summary>Carries a capture cause id across `SendAsync`'s transport boundary (§2.2): a call site that knows why
    /// it is making this request sets it — <c>request.Options.Set(Wire.CauseIdOption, causeId)</c> — before handing
    /// the message to the client; <see cref="LoggingHandler"/> reads it back. Absent (0) means "unknown cause" — the
    /// resulting <see cref="CaptureKind.HttpCall"/> record becomes its own root (<see cref="CausalityRules.NewEvent"/>),
    /// never a crash or a silently-dropped correlation. No signature change to `RequestArgs`/`Api.Send` (plan §2.2) —
    /// this is the one field `HttpRequestMessage` carries for exactly this purpose.</summary>
    public static readonly HttpRequestOptionsKey<long> CauseIdOption = new("Wavee.Capture.CauseId");

    /// <summary>A request/response body is captured only up to this many bytes, and only when `Content-Length` is
    /// known ahead of the read — an unknown-length or large body (the audio cdn's range reads chief among them) is
    /// never buffered just to satisfy an off-by-default capture (§3.2's "may drop a body, never the header").</summary>
    const int MaxCapturedBodyBytes = 8 * 1024;

    /// <summary>The cap for a <c>player/command</c> REQUEST body, inflated (remote-play plan 0.2): the one route whose body
    /// is the question when the owner refuses a verb. A thin command is under 1 KB, but the official play inlines a playlist
    /// page (all.saz 1169: about 130 KB before gzip) and the plan's later steps send the same, so this route alone gets
    /// room for it; every other route, media first, keeps <see cref="MaxCapturedBodyBytes"/>.</summary>
    const int MaxCapturedCommandBodyBytes = 256 * 1024;

    /// <summary>The path every player verb to another device is POSTed under (<c>Spotify.Connect.PostCommand</c>).</summary>
    const string PlayerCommandPath = "/connect-state/v1/player/command/";

    /// <summary>How large a request body to <paramref name="path"/> may be and still be captured.</summary>
    static int RequestBodyCap(string path)
        => path.Contains(PlayerCommandPath, StringComparison.Ordinal) ? MaxCapturedCommandBodyBytes : MaxCapturedBodyBytes;

    /// <summary>Wrap <paramref name="inner"/> so every request through it is logged under <paramref name="client"/>.
    /// <paramref name="storms"/> is false for the audio cdn alone: a track IS dozens of range reads of one path a
    /// minute, which is its job and not a loop (each is still logged).</summary>
    public static HttpMessageHandler Handler(string client, HttpMessageHandler inner, bool storms = true) => new LoggingHandler(client, inner, storms);

    static int s_offline, s_refused;

    /// <summary>Refuse every request from now on, for the life of the process: each one fails as a dropped connection
    /// would (<see cref="HttpRequestException"/>) before any byte is sent, and logs one `wire.refused` line. Armed only by
    /// the shell test host (<c>Shell.Headless.Boot</c>): its `--fake` session still has the live transports behind every
    /// page the seed does not answer, and a test must never reach Spotify. The `--fake` demo itself stays online (it
    /// loads the real cover art).</summary>
    public static void GoOffline() => Volatile.Write(ref s_offline, 1);

    /// <summary>Whether <see cref="GoOffline"/> ran.</summary>
    public static bool IsOffline => Volatile.Read(ref s_offline) != 0;

    /// <summary>How many requests <see cref="GoOffline"/> has refused.</summary>
    public static int Refused => Volatile.Read(ref s_refused);

    static HttpRequestException Refuse(string client, HttpRequestMessage request)
    {
        Interlocked.Increment(ref s_refused);
        Log.Info("wire", "wire.refused client=" + client + " "
            + WireRules.EndpointOf(request.Method.Method, request.RequestUri?.Host ?? "", request.RequestUri?.AbsolutePath ?? "")
            + " (the shell test host is offline)");
        return new HttpRequestException("the shell test host is offline");
    }

    sealed class LoggingHandler(string client, HttpMessageHandler inner, bool storms) : DelegatingHandler(inner)
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsOffline) throw Refuse(client, request);
            long start = Stopwatch.GetTimestamp();
            long captureId = CaptureBegin(request);
            try
            {
                HttpResponseMessage response = base.Send(request, ct);
                CaptureEnd(captureId, request, response);
                Note(client, storms, request, (int)response.StatusCode, response.Content.Headers.ContentLength, start, null);
                return response;
            }
            catch (Exception ex) { CaptureEnd(captureId, request, null); Note(client, storms, request, 0, null, start, ex); throw; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsOffline) throw Refuse(client, request);
            long start = Stopwatch.GetTimestamp();
            long captureId = CaptureBegin(request);
            try
            {
                HttpResponseMessage response = await base.SendAsync(request, ct).ConfigureAwait(false);
                CaptureEnd(captureId, request, response);
                Note(client, storms, request, (int)response.StatusCode, response.Content.Headers.ContentLength, start, null);
                return response;
            }
            catch (Exception ex) { CaptureEnd(captureId, request, null); Note(client, storms, request, 0, null, start, ex); throw; }
        }
    }

    // ── realtime capture (§3.3's HttpCall row) ──────────────────────────────────────────────────────────────────────
    //
    // Begin at send, End at response/exception — exactly the choke point the plan names. Guard-first throughout
    // (`Capture.Enabled` checked before any work, matching §2.4): a request made while capture is off costs one
    // volatile read and nothing else, because `CaptureBegin` returns 0 and every helper below treats 0 as "never
    // began" and does no further work.
    //
    // SECURITY (added on review, 2026-09-23): a credential/token endpoint's BODY is never captured, not even
    // redacted — `CaptureRules.IsAuthEndpoint` is checked before either body is ever read, so a login5/clienttoken/
    // accounts.spotify.com exchange (or anything path-shaped like one) records method/host/path/status/timing/size
    // only, tagged `b: "bodyOmitted=auth"` on the Begin record. `WireRules.EndpointOf` already folds the PATH ONLY
    // (never the query string, per this file's own header comment) into the endpoint label, so there is no raw URL
    // for `Redactor.RedactQueryString` to run on here — it is applied anyway, defensively, so a future call site
    // that starts passing a full URL through this same helper inherits the redaction rather than a silent gap.

    static long CaptureBegin(HttpRequestMessage request)
    {
        if (!Capture.Enabled) return 0;
        long causeId = request.Options.TryGetValue(CauseIdOption, out long id) ? id : 0;
        Uri? uri = request.RequestUri;
        string host = uri?.Host ?? "-";
        string path = uri?.AbsolutePath ?? "-";
        string endpoint = WireRules.EndpointOf(request.Method.Method, host, Redactor.RedactQueryString(path) ?? path);
        if (CaptureRules.IsAuthEndpoint(host, path))
            return Capture.Begin(CaptureKind.HttpCall, causeId, a: endpoint, b: "bodyOmitted=auth");
        return Capture.Begin(CaptureKind.HttpCall, causeId, a: endpoint, payload: CapturedRequestBody(request, RequestBodyCap(path)));
    }

    static void CaptureEnd(long id, HttpRequestMessage request, HttpResponseMessage? response)
    {
        if (id == 0) return; // never began (capture was off, or off by the time we got here) — nothing to close
        if (response is null) { Capture.End(id, n0: 0, truncated: true); return; } // transport failure — no status, no body

        int status = (int)response.StatusCode;
        Uri? uri = request.RequestUri;
        if (CaptureRules.IsAuthEndpoint(uri?.Host ?? "-", uri?.AbsolutePath ?? "-"))
        {
            Capture.End(id, n0: status); // never read/buffer an auth endpoint's response body — see file header
            return;
        }

        (ReadOnlyMemory<byte> payload, bool truncated) = CapturedResponseBody(response, status);
        Capture.End(id, n0: status, payload: payload, truncated: truncated);
    }

    /// <summary>Read-once, buffered content is safe to read again before the send (`ByteArrayContent`/`StringContent`,
    /// the only content kinds this app's HTTP call sites build) — a length-less or streamed request body (never true
    /// for this app's own requests, but a defensive default all the same) is left untouched. A gzipped body (the command
    /// route's <c>X-Transfer-Encoding: gzip</c>, extended-metadata's <c>Content-Encoding</c>) is inflated first, at most
    /// <paramref name="cap"/> bytes of it, so the JSON the app wrote is what the redaction reads.</summary>
    static ReadOnlyMemory<byte> CapturedRequestBody(HttpRequestMessage request, int cap)
    {
        HttpContent? content = request.Content;
        if (content is null) return default;
        long? len = content.Headers.ContentLength;
        if (len is not > 0 || len > cap) return default;
        try
        {
            byte[] raw = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (IsGzip(raw) && !TryGunzip(raw, cap, out raw)) return default;
            return TryRedactJson(raw, out byte[] redacted) ? redacted : default;
        }
        catch { return default; } // never let a capture-only read fault the real request
    }

    /// <summary>The gzip member header (<c>1f 8b</c>): no JSON body starts with it, so the bytes say it whatever header
    /// named the encoding. PURE.</summary>
    static bool IsGzip(ReadOnlySpan<byte> raw) => raw.Length > 2 && raw[0] == 0x1f && raw[1] == 0x8b;

    /// <summary>Inflate <paramref name="gzip"/>; false when it is not gzip after all or inflates past <paramref name="cap"/>
    /// (a capture-only read: the real request already left with the compressed bytes).</summary>
    static bool TryGunzip(byte[] gzip, int cap, out byte[] plain)
    {
        plain = [];
        try
        {
            using var input = new GZipStream(new MemoryStream(gzip, writable: false), CompressionMode.Decompress);
            byte[] buffer = new byte[Math.Min(cap, Math.Max(1024, gzip.Length * 8)) + 1];
            int n = 0, read;
            while ((read = input.Read(buffer, n, buffer.Length - n)) > 0)
            {
                n += read;
                if (n < buffer.Length) continue;
                if (buffer.Length > cap) return false;                       // more than the cap: not captured
                Array.Resize(ref buffer, Math.Min(cap, buffer.Length * 2) + 1);
            }
            plain = buffer.AsSpan(0, n).ToArray();
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>Buffers the response body into a fresh <see cref="ByteArrayContent"/> (same headers) so the real
    /// caller still reads exactly what the server sent — the capture's own copy is a SEPARATE array, never the one
    /// handed onward. Only when `Content-Length` is known and small: an unknown-length body (chunked, or the audio
    /// cdn's own range reads) is never buffered, so a media response is never at risk of this capture holding
    /// megabytes it does not need — bar a 4xx, whose unknown-length body is read up to the cap
    /// (<see cref="CapturedRefusalBody"/>).</summary>
    static (ReadOnlyMemory<byte> Payload, bool Truncated) CapturedResponseBody(HttpResponseMessage response, int status)
    {
        HttpContent? content = response.Content;
        if (content is null) return (default, false);
        long? len = content.Headers.ContentLength;
        if (len is null && status is >= 400 and < 500) return CapturedRefusalBody(response, content);
        if (len is not (> 0 and <= MaxCapturedBodyBytes)) return (default, len is null or 0 ? false : true);

        try
        {
            byte[] raw = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var replacement = new ByteArrayContent(raw);
            foreach (var header in content.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = replacement;
            return TryRedactJson(raw, out byte[] redacted) ? (redacted, false) : (default, raw.Length > 0);
        }
        catch { return (default, true); }
    }

    /// <summary>A 4xx answer whose length the transport does not know (remote-play plan 0.2: the handler inflates a gzipped
    /// answer and drops its <c>Content-Length</c>, so the 403 to a forwarded Liked play was <c>recv=?</c> and kept nothing).
    /// At most <see cref="MaxCapturedBodyBytes"/> + 1 bytes are read: a body that fits is captured (redacted) and handed on
    /// as a fresh buffered content; a longer one is handed on whole — the bytes read, then the rest of the stream
    /// (<see cref="PrefixedStream"/>) — and not captured. Either way the caller reads exactly what the server sent.</summary>
    static (ReadOnlyMemory<byte> Payload, bool Truncated) CapturedRefusalBody(HttpResponseMessage response, HttpContent content)
    {
        try
        {
            Stream stream = content.ReadAsStream();
            byte[] head = new byte[MaxCapturedBodyBytes + 1];
            int n = 0, read;
            while (n < head.Length && (read = stream.Read(head, n, head.Length - n)) > 0) n += read;
            bool fits = n <= MaxCapturedBodyBytes;
            HttpContent replacement = fits ? new ByteArrayContent(head, 0, n) : new StreamContent(new PrefixedStream(head, n, stream));
            foreach (var header in content.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = replacement;
            if (!fits) return (default, true);
            stream.Dispose();
            if (n == 0) return (default, false);
            return TryRedactJson(head.AsSpan(0, n), out byte[] redacted) ? (redacted, false) : (default, true);
        }
        catch { return (default, true); }
    }

    /// <summary>The bytes a capture already read off a response stream, then the rest of that stream: what a refusal body
    /// longer than the capture's cap is handed on as, so the caller's read is the server's body, byte for byte.</summary>
    sealed class PrefixedStream(byte[] head, int headLength, Stream rest) : Stream
    {
        int _at;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_at >= headLength) return rest.Read(buffer);
            int n = Math.Min(buffer.Length, headLength - _at);
            head.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _at >= headLength ? rest.ReadAsync(buffer, cancellationToken) : new ValueTask<int>(Read(buffer.Span));

        protected override void Dispose(bool disposing)
        {
            if (disposing) rest.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>PURE-ish (allocates, but only ever called on a capture-enabled path): redacts a JSON body's
    /// token/secret VALUES (§4, `Redactor.RedactJsonBody`) — never captured un-redacted. A body that is not
    /// well-formed JSON (a protobuf response, most of this app's own api traffic) is "cannot verify safety", so it is
    /// never captured raw: the caller marks the record `Truncated` instead of guessing it is safe.</summary>
    static bool TryRedactJson(ReadOnlySpan<byte> utf8, out byte[] redacted)
    {
        if (utf8.IsEmpty) { redacted = []; return true; }
        byte[] buffer = new byte[utf8.Length + 64]; // a redacted placeholder can be longer than the token it replaces
        int written = Redactor.RedactJsonBody(utf8, buffer);
        if (written < 0) { redacted = []; return false; }
        redacted = written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();
        return true;
    }

    /// <summary>The two NON-HTTP channels: an AP packet (<c>cmd=0x..</c>) or a dealer websocket text frame. Same log,
    /// same storm rule, keyed by channel + what — so an audio-key loop or a reply loop is as visible as an http one.</summary>
    public static void NoteSocket(string channel, string what, int bytes)
    {
        Log.Info("wire", $"wire.send channel={channel} {what} sent={bytes}");
        Storm(channel + " " + what);
    }

    /// <summary>A dealer frame's kind for the log — its <c>"type"</c> value (ping, pong, reply…) and nothing else of
    /// the body. PURE.</summary>
    public static string DealerKind(ReadOnlySpan<byte> utf8)
    {
        ReadOnlySpan<byte> key = "\"type\":\""u8;
        int at = utf8.IndexOf(key);
        if (at < 0) return "type=?";
        ReadOnlySpan<byte> rest = utf8[(at + key.Length)..];
        int end = rest.IndexOf((byte)'"');
        return end is > 0 and <= 24 ? "type=" + System.Text.Encoding.ASCII.GetString(rest[..end]) : "type=?";
    }

    static void Storm(string endpoint)
    {
        int storm;
        long total;
        lock (s_gate)
        {
            if (!s_windows.TryGetValue(endpoint, out WireRules.Window? window)) s_windows[endpoint] = window = new WireRules.Window();
            storm = window.Note(Environment.TickCount64);
            total = window.Total;
        }
        if (storm > 0)
            Log.Warn("wire", $"wire.storm {endpoint} — {storm}+ calls inside {WireRules.StormWindowMs / 1000}s ({total} this session): something is asking in a loop");
    }

    static readonly RoutineSummary Routine = RoutineSummary.Create("wire", "wire.summary");

    static void Note(string client, bool storms, HttpRequestMessage request, int status, long? responseBytes, long start, Exception? error)
    {
        Uri? uri = request.RequestUri;
        string method = request.Method.Method;
        string host = uri?.Host ?? "-";
        string path = uri?.AbsolutePath ?? "-";                 // NEVER the query: signed cdn tokens, ids, search text
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long sent = request.Content?.Headers.ContentLength ?? 0;

        // Steady state is one summary line a minute; a call that failed, errored or ran long keeps its own full line.
        if (!WireRules.IsRoutine(status, error is not null, ms))
        {
            string range = request.Headers.Range is { } r ? " range=" + r.ToString() : "";
            string tail = error is null ? "" : " error=" + error.GetType().Name;
            Log.Info("wire", $"wire.call client={client} {method} {host}{path} status={status} ms={ms:F0} sent={sent} recv={responseBytes?.ToString() ?? "?"}{range}{tail}");
        }
        else
            Routine.Note(sent + (responseBytes ?? 0), (long)ms);

        if (!storms) return;
        Storm(WireRules.EndpointOf(method, host, path));
    }
}
