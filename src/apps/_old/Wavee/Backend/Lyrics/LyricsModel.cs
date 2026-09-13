using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

// The aggregator's working model (docs/lyrics-aggregator-reranker-plan.md §4, §7). LyricsDocument/LyricLine themselves
// live in Wavee.Core (the display contract the UI binds); these are backend-only request/candidate shapes that never
// leak to Core.

/// <summary>Everything a candidate source needs to look a track up: the Spotify identity (track id / uri) for the
/// identity-matched sources (AMLL, Spotify-native) and the human metadata (title/artists/album/duration/ISRC) for the
/// search-matched ones (LRCLIB, the CJK providers). <see cref="HasSpotifyLyrics"/> only gates the Spotify-native source —
/// every other source is still queried when it is false/unknown.</summary>
public sealed record LyricsRequest(
    string TrackId,
    string Uri,
    string Title,
    IReadOnlyList<string> Artists,
    string Album,
    long DurationMs,
    string? Isrc = null,
    string Market = "from_token",
    bool? HasSpotifyLyrics = null)
{
    public string ArtistsJoined => string.Join(", ", Artists);
    public string PrimaryArtist => Artists.Count > 0 ? Artists[0] : "";
}

/// <summary>How a candidate was matched to the request — feeds the reranker's confidence (an identity/ISRC match is
/// trusted more than a fuzzy metadata search).</summary>
public enum MatchBasis { Identity, Isrc, MetadataSearch, LocalFile, Consensus }

/// <summary>One source's answer, pre-rank. The <see cref="Document"/> is the normalized display lyrics; the reranker
/// derives comparison text from it on demand. <see cref="Prior"/> is the provider's tiebreak weight (correctness and
/// timing dominate the score — the prior only breaks ties).</summary>
public sealed record LyricsCandidate(
    string ProviderId,
    double Prior,
    MatchBasis Basis,
    LyricsDocument Document)
{
    public LyricsSyncKind Sync => Document.Sync;
    public int LineCount => Document.Lines.Count;
}

/// <summary>A candidate source: fetch + parse + normalize one provider's lyrics into a <see cref="LyricsCandidate"/>.
/// Returns null on a miss (NOT an exception — a miss must not fail the aggregate). Implementations own their own HTTP and
/// per-source negative caching.</summary>
public interface ILyricCandidateSource
{
    string Id { get; }
    bool Enabled { get; }
    double Prior { get; }
    Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct);
}

/// <summary>A tiny GET seam so the public sources (LRCLIB, AMLL) are unit-testable with a fake (no network in tests).
/// The real implementation wraps the shared <see cref="SharedHttp"/> client.</summary>
public interface ILyricHttp
{
    Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct);
}

public readonly record struct LyricHttpResult(int Status, string? Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

public interface ILyricHttpWithStatus : ILyricHttp
{
    Task<LyricHttpResult> GetAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct);
}

/// <summary>The real <see cref="ILyricHttp"/> over the process-shared HttpClient (pooled connections, AOT-clean). Returns
/// null on any non-success / transport error so a source miss is a clean null, never a throw that aborts the fan-out.</summary>
public sealed class SharedHttpLyricFetch : ILyricHttpWithStatus
{
    readonly string _userAgent;
    public SharedHttpLyricFetch(string userAgent = "Wavee/1.0 (https://github.com/christosk92/Wavee)") => _userAgent = userAgent;

    public async Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var result = await GetAsync(url, headers, ct).ConfigureAwait(false);
        return result.IsSuccess ? result.Body : null;
    }

    public async Task<LyricHttpResult> GetAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (headers is not null)
                foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            using var resp = await Wavee.Backend.Spotify.HttpPools.Get(Wavee.Backend.Spotify.HttpPool.ThirdParty).SendAsync(req, ct).ConfigureAwait(false);
            var body = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
            return new LyricHttpResult((int)resp.StatusCode, body);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new LyricHttpResult(0, null); }
    }
}

/// <summary>Aggregator configuration — which sources run by default and the per-source budget. The grey CJK/Musixmatch
/// sources are OFF by default (docs plan §6); only AMLL + Spotify-native + LRCLIB are clean-by-default.</summary>
public sealed record LyricsOptions(
    bool EnableGreyProviders = false,
    int PerSourceTimeoutMs = 6000,
    int TotalTimeoutMs = 9000,
    int FirstHitGraceMs = 2000)
{
    public static LyricsOptions Default { get; } = new();
}
