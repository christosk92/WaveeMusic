using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

// Explainability for the lyrics aggregator (the "why did this song find no/that lyric" debug surface). A per-search
// LyricsProbe flows (AsyncLocal) into every parallel source task so each source can leave breadcrumbs (query, hit count,
// where it bailed); the aggregator times each source, classifies the outcome, folds in the reranker's per-candidate
// decision, and Publishes one LyricsSearchReport to the process-wide LyricsDiagnostics store the debug panel reads.
//
// TWO STORES, deliberately different sizes. The REPORT store is pure metadata (outcome/timing/score strings) and is cheap
// enough to keep for the last 24 searches / 256 distinct tracks. The INSPECTION store is the heavy half — the untouched
// provider payloads and every candidate's PARSED document — and is what the lyrics-source inspector dialog reads to
// answer "is this desync the provider's or our parser's?". It keeps only the last few tracks (see InspectionCap), and
// each captured payload is length-capped, because those strings/documents would otherwise be garbage the moment the
// fan-out returns.

public enum LyricsOutcome { Pending, Hit, Miss, Timeout, Error, Skipped }

/// <summary>One source's result within a single track search — outcome + timing + a human "why" (its probe breadcrumbs),
/// plus the reranker's verdict once ranked (score/winner/reason).</summary>
public sealed record LyricsSourceTrace(
    string SourceId,
    LyricsOutcome Outcome,
    long ElapsedMs,
    string Detail,
    LyricsSyncKind Sync,
    int LineCount,
    double Score,
    bool Winner,
    string RerankReason,
    // The reranker's score BREAKDOWN. A bare 0.885-vs-0.740 says who won but not why, and "why" is always the actual
    // question — a word-synced candidate losing to a line-synced one is a completely different problem depending on
    // whether it lost on text agreement or on the sync tier being worth too little. Defaulted so a trace built for a
    // source that never ranked (a miss, a skip, a disk hit) needs no values.
    double Text = 0d,
    double Coverage = 0d,
    double Timing = 0d,
    double SyncScore = 0d);

/// <summary>The full explainable record of ONE GetLyricsAsync call: the request metadata the sources searched with, the
/// per-source traces, and a one-line summary.</summary>
public sealed record LyricsSearchReport(
    string TrackId,
    string Title,
    string Artist,
    string Album,
    long DurationMs,
    string? Isrc,
    long WhenUnixMs,
    string Summary,
    IReadOnlyList<LyricsSourceTrace> Sources);

/// <summary>One provider payload captured EXACTLY as it arrived — the HTTP body, or, for the encrypted CJK formats, the
/// decrypted text that is what the parser actually sees. <see cref="Label"/> is the (credential-redacted) URL or a short
/// description of the step; <see cref="Format"/> is the wire format (<c>json</c>/<c>ttml</c>/<c>lrc</c>/<c>krc</c>/…).
/// <see cref="Text"/> may be truncated to the capture cap — <see cref="OriginalLength"/> is the real size.</summary>
public sealed record LyricsRawPayload(
    string SourceId,
    string Label,
    string Format,
    string Text,
    int OriginalLength)
{
    public bool Truncated => OriginalLength > Text.Length;
}

/// <summary>One source's PARSED document, kept alongside the raw payload it came from so the inspector can show the two
/// side by side. This is the candidate BEFORE the reranker's offset correction (see
/// <see cref="LyricsInspection.Final"/> for what the UI actually receives).</summary>
public sealed record LyricsParsedCandidate(string SourceId, MatchBasis Basis, double Prior, LyricsDocument Document);

/// <summary>The heavy half of one track's explainability record: every provider payload captured verbatim, every
/// candidate's parsed document, and the document the UI ended up with. Republished (replacing the previous entry) each
/// time the aggregator finishes a pass for the track, so a background upgrade's superset wins.</summary>
public sealed record LyricsInspection(
    string TrackId,
    long WhenUnixMs,
    string Note,
    IReadOnlyList<LyricsRawPayload> Raw,
    IReadOnlyList<LyricsParsedCandidate> Candidates,
    LyricsDocument? Final);

/// <summary>Implemented by the aggregator (and forwarded by the switchable facade): drop every cached answer for ONE
/// track and fetch it again from the providers. The inspector needs it because the winner cache — memory AND disk — means
/// a track played before answers with no round-trip at all, so there is no raw payload to show.</summary>
public interface ILyricsRefetch
{
    Task<LyricsDocument?> RefetchAsync(string trackId, CancellationToken ct = default);
}

/// <summary>Ambient (AsyncLocal) per-search breadcrumb collector. The aggregator sets <see cref="Current"/> before the
/// fan-out; each source calls the static <see cref="Note"/> at its decision points (query, result count, miss reason)
/// and the static <see cref="CaptureRaw"/> with each payload it receives. Because the fan-out tasks are started while
/// Current is set, the value flows into each task; the probe object itself is shared, so notes and payloads from all
/// sources land in one place (thread-safe).</summary>
public sealed class LyricsProbe
{
    public static readonly AsyncLocal<LyricsProbe?> Current = new();

    // Capture caps. A word-synced TTML/richsync body is the big one (low hundreds of KB); the search responses are
    // small. The per-probe TOTAL is the cap that actually bounds the store — InspectionCap tracks × this budget is the
    // whole memory cost of the feature.
    const int MaxPayloadChars = 128_000;
    const int MaxPayloadsPerSource = 6;
    const int MaxTotalChars = 640_000;

    readonly object _gate = new();
    readonly Dictionary<string, List<string>> _notes = new(StringComparer.Ordinal);
    readonly List<LyricsRawPayload> _raw = new();
    int _rawChars;

    /// <summary>Record a breadcrumb for <paramref name="sourceId"/> (no-op if no probe is active, e.g. a unit test).</summary>
    public static void Note(string sourceId, string message)
    {
        var p = Current.Value;
        if (p is null) return;
        lock (p._gate)
        {
            if (!p._notes.TryGetValue(sourceId, out var list)) p._notes[sourceId] = list = new List<string>();
            list.Add(message);
        }
    }

    /// <summary>Keep one payload verbatim for the inspector. No-op when no probe is active (unit tests, the disk-hit
    /// path) or when this probe has spent its capture budget. Pass the URL through <see cref="Redact"/> before using it
    /// as <paramref name="label"/> — the captured text is copied to the clipboard by a human.</summary>
    public static void CaptureRaw(string sourceId, string label, string format, string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return;
        var p = Current.Value;
        if (p is null) return;
        lock (p._gate)
        {
            int room = MaxTotalChars - p._rawChars;
            if (room <= 0) return;
            int perSource = 0;
            foreach (var r in p._raw) if (StringComparer.Ordinal.Equals(r.SourceId, sourceId)) perSource++;
            if (perSource >= MaxPayloadsPerSource) return;

            int keep = Math.Min(Math.Min(MaxPayloadChars, room), payload!.Length);
            string text = keep == payload.Length ? payload : payload[..keep];
            p._rawChars += text.Length;
            p._raw.Add(new LyricsRawPayload(sourceId, label, format, text, payload.Length));
        }
    }

    public string NotesFor(string sourceId)
    {
        lock (_gate) return _notes.TryGetValue(sourceId, out var list) ? string.Join("; ", list) : "";
    }

    public IReadOnlyList<LyricsRawPayload> RawPayloads()
    {
        lock (_gate) return _raw.Count == 0 ? Array.Empty<LyricsRawPayload>() : _raw.ToArray();
    }

    // Query keys whose VALUE is a credential. Musixmatch's macro URL carries a usertoken and Kugou's download URL an
    // accesskey; both would otherwise ride along into whatever the user pastes into a bug report.
    static readonly string[] SecretKeys =
        { "usertoken", "user_token", "accesskey", "access_token", "token", "api_key", "apikey", "auth", "authorization", "signature", "sign" };

    /// <summary>Replace the value of every credential-bearing query parameter with a placeholder. The path and every
    /// other parameter (the actual search terms — the interesting part) survive intact.</summary>
    public static string Redact(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        int q = url.IndexOf('?');
        if (q < 0) return url;

        var sb = new StringBuilder(url.Length);
        sb.Append(url, 0, q + 1);
        string[] pairs = url[(q + 1)..].Split('&');
        for (int i = 0; i < pairs.Length; i++)
        {
            if (i > 0) sb.Append('&');
            int eq = pairs[i].IndexOf('=');
            if (eq <= 0) { sb.Append(pairs[i]); continue; }
            string key = pairs[i][..eq];
            bool secret = false;
            foreach (string s in SecretKeys)
                if (string.Equals(key, s, StringComparison.OrdinalIgnoreCase)) { secret = true; break; }
            sb.Append(key).Append('=').Append(secret ? "***redacted***" : pairs[i][(eq + 1)..]);
        }
        return sb.ToString();
    }
}

/// <summary>Process-wide store of the most recent lyric searches, read by the debug panel. Keyed by track id (latest per
/// track) plus a small recency ring. <see cref="Version"/> bumps on every publish so an open panel can refresh live.</summary>
public static class LyricsDiagnostics
{
    const int Cap = 24;
    const int TrackCap = 256;   // bound the per-track store — it is published on EVERY search (the env-gate only hides the panel), so a long session would otherwise accumulate a report per distinct track forever
    /// <summary>How many tracks keep their heavy <see cref="LyricsInspection"/> (raw payloads + every candidate's parsed
    /// document). Two orders of magnitude below <see cref="TrackCap"/> on purpose: this store holds real payload bytes,
    /// and the only track anyone inspects is the one playing (plus the one or two before it).</summary>
    const int InspectionCap = 3;
    static readonly object _gate = new();
    static readonly LinkedList<LyricsSearchReport> _recent = new();
    static readonly Dictionary<string, LyricsSearchReport> _byTrack = new(StringComparer.Ordinal);
    static readonly Queue<string> _order = new();   // first-seen order of distinct track ids, for FIFO eviction of _byTrack
    static readonly Dictionary<string, LyricsInspection> _inspections = new(StringComparer.Ordinal);
    static readonly List<string> _inspectionLru = new();   // MRU at the end
    static long _version;

    /// <summary>Monotonic publish counter — read it to detect a fresh report without holding the lock.</summary>
    public static long Version => Interlocked.Read(ref _version);

    public static void Publish(LyricsSearchReport report)
    {
        lock (_gate)
        {
            if (_byTrack.TryAdd(report.TrackId, report)) _order.Enqueue(report.TrackId);   // new track → track its first-seen order
            else _byTrack[report.TrackId] = report;                                         // re-publish → update in place, keep its order
            _recent.AddFirst(report);
            while (_recent.Count > Cap) _recent.RemoveLast();
            while (_byTrack.Count > TrackCap && _order.Count > 0) _byTrack.Remove(_order.Dequeue());   // FIFO-evict the oldest distinct track
        }
        Interlocked.Increment(ref _version);
    }

    public static LyricsSearchReport? ForTrack(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        lock (_gate) return _byTrack.TryGetValue(trackId, out var r) ? r : null;
    }

    public static IReadOnlyList<LyricsSearchReport> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    /// <summary>Record (replacing any previous entry for the track) the raw payloads + parsed candidates of one pass.
    /// A later pass for the same track always carries a superset, so replace — never merge.</summary>
    public static void PublishInspection(LyricsInspection inspection)
    {
        if (string.IsNullOrEmpty(inspection.TrackId)) return;
        lock (_gate)
        {
            _inspections[inspection.TrackId] = inspection;
            _inspectionLru.Remove(inspection.TrackId);
            _inspectionLru.Add(inspection.TrackId);
            while (_inspectionLru.Count > InspectionCap)
            {
                _inspections.Remove(_inspectionLru[0]);
                _inspectionLru.RemoveAt(0);
            }
        }
        Interlocked.Increment(ref _version);
    }

    public static LyricsInspection? InspectionFor(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        lock (_gate) return _inspections.TryGetValue(trackId, out var i) ? i : null;
    }
}
