using System;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using LyricifyProviders = Lyricify.Lyrics.Providers.Web.Providers;
using Lyricify.Lyrics.Searchers;

namespace Wavee.UI.WinUI.Services.Lyrics.Providers;

public sealed class NeteaseLyricsProvider : ILyricsProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Id => "Netease";
    public string DisplayName => "Netease Cloud Music";

    public async Task<LyricsSearchResult?> SearchAsync(
        string title, string artist, string? album,
        double durationMs, string? trackId, string? imageUri,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var metadata = new TrackMultiArtistMetadata
            {
                Title = title,
                Artist = artist,
                Album = album,
                DurationMs = (int)durationMs,
            };

            var searcher = new NeteaseSearcher();
            var result = await searcher.SearchForResult(metadata);
            ct.ThrowIfCancellationRequested();

            if (result is not NeteaseSearchResult neteaseResult)
                return null;

            // Try new API first (supports word-level YRC), fall back to legacy
            var lyricResult = await LyricifyProviders.NeteaseApi.GetLyricNew(neteaseResult.Id);
            ct.ThrowIfCancellationRequested();

            lyricResult ??= await LyricifyProviders.NeteaseApi.GetLyric(neteaseResult.Id);
            ct.ThrowIfCancellationRequested();

            if (lyricResult == null)
                return null;

            LyricsData? lyricsData = null;

            // Prefer YRC (word-level), then LRC (line-level)
            if (!string.IsNullOrEmpty(lyricResult.Yrc?.Lyric))
                lyricsData = YrcParser.Parse(lyricResult.Yrc.Lyric);

            if (lyricsData?.Lines is not { Count: > 0 } && !string.IsNullOrEmpty(lyricResult.Lrc?.Lyric))
                lyricsData = LrcParser.Parse(lyricResult.Lrc.Lyric);

            if (lyricsData?.Lines is not { Count: > 0 })
                return null;

            var (response, wordTimings) = LyricifyAdapter.ToWaveeResponse(lyricsData, DisplayName);
            var matchScore = MatchScoreHelper.ToScore(result.MatchType);

            return new LyricsSearchResult(response, wordTimings, matchScore);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _lock.Release(); }
    }
}
