using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Backend.Lyrics.Sources;

/// <summary>AMLL TTML DB (github.com/amll-dev/amll-ttml-db) — a community word-by-word (SYLLABLE) TTML library indexed
/// directly by Spotify track id under <c>/spotify-lyrics/{id}.ttml</c>, no auth (docs plan §6). The cleanest word-synced
/// source and the highest provider prior: a direct identity match, so when present it's the best karaoke candidate — this
/// is what gives per-syllable timing (LRCLIB/Spotify-native are line-only). A miss (no file → 404 → null) just drops it
/// from the fan-out. URL template is configurable (the repo also publishes .lrc/.lys/.yrc/.qrc/.eslrc per id; we take the
/// richest, .ttml). Verified live: id 002FHVgG4btehE → 39 word-by-word lines.</summary>
public sealed class AmllTtmlDbSource : ILyricCandidateSource
{
    public const string DefaultTemplate =
        "https://raw.githubusercontent.com/amll-dev/amll-ttml-db/main/spotify-lyrics/{0}.ttml";

    readonly ILyricHttp _http;
    readonly string _template;
    readonly ConcurrentDictionary<string, byte> _misses = new(StringComparer.Ordinal);

    public AmllTtmlDbSource(ILyricHttp http, string? urlTemplate = null)
    {
        _http = http;
        _template = urlTemplate ?? DefaultTemplate;
    }

    public string Id => "amll";
    public bool Enabled => true;
    public double Prior => 0.9;   // top prior (a curated, identity-matched, word-synced source)

    public async Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.TrackId)) return null;
        if (req.HasSpotifyLyrics == true) return null;
        if (_misses.ContainsKey(req.TrackId)) return null;
        string url = string.Format(_template, req.TrackId);
        string? ttml;
        if (_http is ILyricHttpWithStatus statusHttp)
        {
            var result = await statusHttp.GetAsync(url, null, ct).ConfigureAwait(false);
            if (result.Status == 404)
            {
                _misses.TryAdd(req.TrackId, 0);
                return null;
            }
            if (!result.IsSuccess) return null;
            ttml = result.Body;
        }
        else
        {
            ttml = await _http.GetStringAsync(url, null, ct).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(ttml))
        {
            _misses.TryAdd(req.TrackId, 0);
            return null;
        }
        LyricsProbe.CaptureRaw(Id, LyricsProbe.Redact(url), "ttml", ttml);

        var doc = LyricsText.ParseTtml(ttml!, req.TrackId, Id);
        if (doc.Lines.Count == 0)
        {
            _misses.TryAdd(req.TrackId, 0);
            return null;
        }
        return new LyricsCandidate(Id, Prior, MatchBasis.Identity, doc);
    }
}
