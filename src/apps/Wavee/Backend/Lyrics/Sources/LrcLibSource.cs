using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Lyrics.Sources;

/// <summary>LRCLIB (lrclib.net) — a clean, free, no-auth synced-lyrics database (docs plan §6). Primary lookup is the
/// exact <c>/api/get</c> (artist+track+album+duration); on a miss it falls back to <c>/api/search</c> and picks the
/// closest result by duration. Synced payloads are LRC (parsed to line/word timing); plain payloads become unsynced.
/// Matched by metadata, so the reranker validates it against the Spotify-native reference before trusting it.</summary>
public sealed class LrcLibSource : ILyricCandidateSource
{
    readonly ILyricHttp _http;
    public LrcLibSource(ILyricHttp http) => _http = http;

    public string Id => "lrclib";
    public bool Enabled => true;
    public double Prior => 0.45;

    public async Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
    {
        long sec = req.DurationMs > 0 ? req.DurationMs / 1000 : 0;
        string get = "https://lrclib.net/api/get"
            + "?artist_name=" + Uri.EscapeDataString(req.PrimaryArtist)
            + "&track_name=" + Uri.EscapeDataString(req.Title)
            + "&album_name=" + Uri.EscapeDataString(req.Album)
            + (sec > 0 ? "&duration=" + sec : "");
        string? body = await _http.GetStringAsync(get, null, ct).ConfigureAwait(false);
        LyricsProbe.CaptureRaw(Id, LyricsProbe.Redact(get), "json", body);

        LyricsDocument? doc = body is not null ? FromObject(body, req) : null;
        LyricsProbe.Note(Id, doc is not null ? "exact /api/get hit" : "exact /api/get miss → /api/search");
        doc ??= await SearchAsync(req, ct).ConfigureAwait(false);
        if (doc is null || doc.Lines.Count == 0) return null;
        return new LyricsCandidate(Id, Prior, MatchBasis.MetadataSearch, doc);
    }

    // /api/search with the query-variant ladder: full "title/artist" → feat-stripped "title/artist" → "title" only.
    // Returns the first variant that yields a usable doc (each variant keeps the per-result duration-closest pick + the
    // >5s reject as the wrong-song guardrail).
    async Task<LyricsDocument?> SearchAsync(LyricsRequest req, CancellationToken ct)
    {
        foreach (var (title, artist) in LyricsQuery.TitleArtistVariants(req))
        {
            var doc = await SearchOnce(title, artist, req, ct).ConfigureAwait(false);
            if (doc is { Lines.Count: > 0 })
            {
                LyricsProbe.Note(Id, $"/api/search hit on '{title}'" + (artist.Length > 0 ? $" / '{artist}'" : " (title-only)"));
                return doc;
            }
            ct.ThrowIfCancellationRequested();
        }
        return null;
    }

    async Task<LyricsDocument?> SearchOnce(string title, string artist, LyricsRequest req, CancellationToken ct)
    {
        string search = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title)
            + (artist.Length > 0 ? "&artist_name=" + Uri.EscapeDataString(artist) : "");
        string? json = await _http.GetStringAsync(search, null, ct).ConfigureAwait(false);
        LyricsProbe.CaptureRaw(Id, LyricsProbe.Redact(search), "json", json);
        if (json is null) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.ValueKind != JsonValueKind.Array) return null;
            string? bestSynced = null, bestPlain = null; long bestDelta = long.MaxValue; int n = 0;
            foreach (var el in d.RootElement.EnumerateArray())
            {
                n++;
                if (el.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True) continue;
                string? sl = Str(el, "syncedLyrics");
                string? pl = Str(el, "plainLyrics");
                if (string.IsNullOrWhiteSpace(sl) && string.IsNullOrWhiteSpace(pl)) continue;
                long dur = el.TryGetProperty("duration", out var du) && du.TryGetDouble(out var ds) ? (long)(ds * 1000) : 0;
                long delta = req.DurationMs > 0 && dur > 0 ? Math.Abs(dur - req.DurationMs) : 0;
                if (delta < bestDelta) { bestDelta = delta; bestSynced = sl; bestPlain = pl; }   // GetString → owned copy, safe post-dispose
            }
            LyricsProbe.Note(Id, $"/api/search '{title}' → {n} results" + (bestDelta != long.MaxValue ? $" (best Δ{bestDelta}ms)" : ""));
            if (req.DurationMs > 0 && bestDelta > 5000) { LyricsProbe.Note(Id, "closest result's duration > 5s off — rejected"); return null; }
            if (!string.IsNullOrWhiteSpace(bestSynced)) return LyricsText.ParseLrc(bestSynced!, req.TrackId, Id);
            if (!string.IsNullOrWhiteSpace(bestPlain)) return Unsynced(bestPlain!, req.TrackId);
            return null;
        }
        catch { return null; }
    }

    LyricsDocument? FromObject(string json, LyricsRequest req)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var el = d.RootElement;
            if (el.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True) return null;
            string? synced = Str(el, "syncedLyrics");
            if (!string.IsNullOrWhiteSpace(synced)) return LyricsText.ParseLrc(synced!, req.TrackId, Id);
            string? plain = Str(el, "plainLyrics");
            if (!string.IsNullOrWhiteSpace(plain)) return Unsynced(plain!, req.TrackId);
            return null;
        }
        catch { return null; }
    }

    static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static LyricsDocument Unsynced(string plain, string trackId)
    {
        var lines = plain.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Where(l => l.Trim().Length > 0)
            .Select(l => new LyricLine(0, l.Trim(), Array.Empty<LyricSyllable>()))
            .ToList();
        return new LyricsDocument(trackId, false, lines, LyricsSyncKind.Unsynced, "lrclib");
    }
}
