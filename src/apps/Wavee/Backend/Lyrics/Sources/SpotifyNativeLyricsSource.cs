using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Lyrics.Sources;

/// <summary>Spotify-native color-lyrics (docs plan §10). Primarily the reranker's TEXT/TIMING REFERENCE (Spotify knows the
/// exact track), but also a line-synced candidate in its own right. Uses the live spclient via an injected authed GET
/// delegate (so this stays testable + decoupled from the transport). Skipped when the wire says
/// <c>HasSpotifyLyrics == false</c>, but that never suppresses the other sources.</summary>
public sealed class SpotifyNativeLyricsSource : ILyricCandidateSource
{
    readonly Func<string, CancellationToken, Task<string?>> _get;
    readonly Func<string> _baseUrl;

    /// <param name="get">Authed GET against the spclient (returns the JSON body, or null on miss/error).</param>
    /// <param name="baseUrl">The resolved spclient base URL (e.g. https://gae2-spclient.spotify.com).</param>
    public SpotifyNativeLyricsSource(Func<string, CancellationToken, Task<string?>> get, Func<string> baseUrl)
    {
        _get = get;
        _baseUrl = baseUrl;
    }

    public string Id => "spotify";
    public bool Enabled => true;
    public double Prior => 0.55;

    public async Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
    {
        if (req.HasSpotifyLyrics == false) return null;   // wire explicitly says none → skip THIS source only
        if (string.IsNullOrEmpty(req.TrackId)) return null;
        string url = _baseUrl().TrimEnd('/') + "/color-lyrics/v2/track/" + req.TrackId
            + "?format=json&vocalRemoval=false&market=" + req.Market;
        string? json = await _get(url, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return null;
        LyricsProbe.CaptureRaw(Id, LyricsProbe.Redact(url), "json", json);

        var doc = Parse(json!, req.TrackId);
        if (doc is null || doc.Lines.Count == 0) return null;
        return new LyricsCandidate(Id, Prior, MatchBasis.Identity, doc);
    }

    /// <summary>Parse the <c>color-lyrics/v2</c> JSON: <c>lyrics.syncType</c> + <c>lyrics.lines[].startTimeMs/words</c>
    /// (startTimeMs is a STRING on the wire). Syllables are normally empty → line-synced.</summary>
    public static LyricsDocument? Parse(string json, string trackId)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            if (!d.RootElement.TryGetProperty("lyrics", out var lyr) || lyr.ValueKind != JsonValueKind.Object) return null;
            string syncType = lyr.TryGetProperty("syncType", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "";
            bool synced = syncType.IndexOf("SYNCED", StringComparison.OrdinalIgnoreCase) >= 0
                && !syncType.Equals("UNSYNCED", StringComparison.OrdinalIgnoreCase);
            if (!lyr.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return null;

            var outLines = new List<LyricLine>();
            foreach (var ln in lines.EnumerateArray())
            {
                long start = 0;
                if (ln.TryGetProperty("startTimeMs", out var sm))
                {
                    if (sm.ValueKind == JsonValueKind.String) long.TryParse(sm.GetString(), out start);
                    else if (sm.ValueKind == JsonValueKind.Number) start = sm.GetInt64();
                }
                string words = ln.TryGetProperty("words", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString()! : "";
                outLines.Add(new LyricLine(start, words, Array.Empty<LyricSyllable>()));
            }
            if (outLines.Count == 0) return null;
            return new LyricsDocument(trackId, synced, outLines, synced ? LyricsSyncKind.Line : LyricsSyncKind.Unsynced, "spotify");
        }
        catch { return null; }
    }
}
