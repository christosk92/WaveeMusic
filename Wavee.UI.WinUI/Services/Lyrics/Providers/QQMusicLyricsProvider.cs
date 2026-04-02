using System;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using LyricifyProviders = Lyricify.Lyrics.Providers.Web.Providers;
using Lyricify.Lyrics.Searchers;

namespace Wavee.UI.WinUI.Services.Lyrics.Providers;

public sealed class QQMusicLyricsProvider : ILyricsProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Id => "QQMusic";
    public string DisplayName => "QQ Music";

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

            var searcher = new QQMusicSearcher();
            var result = await searcher.SearchForResult(metadata);
            ct.ThrowIfCancellationRequested();

            if (result is not QQMusicSearchResult qqResult)
                return null;

            // Fetch encrypted QRC lyrics (syllable-level)
            var lyricsResponse = await LyricifyProviders.QQMusicApi.GetLyricsAsync(qqResult.Id);
            ct.ThrowIfCancellationRequested();

            LyricsData? lyricsData = null;

            // Try QRC (syllable-level) first
            if (!string.IsNullOrEmpty(lyricsResponse?.Lyrics))
                lyricsData = QrcParser.Parse(lyricsResponse.Lyrics);

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
