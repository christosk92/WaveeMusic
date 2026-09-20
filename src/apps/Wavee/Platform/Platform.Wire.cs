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

using System.Diagnostics;

namespace Wavee;

/// <summary>The pure half: what a request is called in the log, and when a run of them is a storm.</summary>
public static class WireRules
{
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

    /// <summary>Wrap <paramref name="inner"/> so every request through it is logged under <paramref name="client"/>.
    /// <paramref name="storms"/> is false for the audio cdn alone: a track IS dozens of range reads of one path a
    /// minute, which is its job and not a loop (each is still logged).</summary>
    public static HttpMessageHandler Handler(string client, HttpMessageHandler inner, bool storms = true) => new LoggingHandler(client, inner, storms);

    sealed class LoggingHandler(string client, HttpMessageHandler inner, bool storms) : DelegatingHandler(inner)
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                HttpResponseMessage response = base.Send(request, ct);
                Note(client, storms, request, (int)response.StatusCode, response.Content.Headers.ContentLength, start, null);
                return response;
            }
            catch (Exception ex) { Note(client, storms, request, 0, null, start, ex); throw; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                HttpResponseMessage response = await base.SendAsync(request, ct).ConfigureAwait(false);
                Note(client, storms, request, (int)response.StatusCode, response.Content.Headers.ContentLength, start, null);
                return response;
            }
            catch (Exception ex) { Note(client, storms, request, 0, null, start, ex); throw; }
        }
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

    static void Note(string client, bool storms, HttpRequestMessage request, int status, long? responseBytes, long start, Exception? error)
    {
        Uri? uri = request.RequestUri;
        string method = request.Method.Method;
        string host = uri?.Host ?? "-";
        string path = uri?.AbsolutePath ?? "-";                 // NEVER the query: signed cdn tokens, ids, search text
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long sent = request.Content?.Headers.ContentLength ?? 0;
        string range = request.Headers.Range is { } r ? " range=" + r.ToString() : "";
        string tail = error is null ? "" : " error=" + error.GetType().Name;

        Log.Info("wire", $"wire.call client={client} {method} {host}{path} status={status} ms={ms:F0} sent={sent} recv={responseBytes?.ToString() ?? "?"}{range}{tail}");

        if (!storms) return;
        Storm(WireRules.EndpointOf(method, host, path));
    }
}
