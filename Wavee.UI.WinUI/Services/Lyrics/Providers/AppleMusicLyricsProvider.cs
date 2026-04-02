using System;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using LyricifyProviders = Lyricify.Lyrics.Providers.Web.Providers;
using Lyricify.Lyrics.Searchers;

namespace Wavee.UI.WinUI.Services.Lyrics.Providers;

public sealed class AppleMusicLyricsProvider : ILyricsProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Id => "AppleMusic";
    public string DisplayName => "Apple Music";

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

            var searcher = new AppleMusicSearcher();
            var result = await searcher.SearchForResult(metadata);
            ct.ThrowIfCancellationRequested();

            if (result is not AppleMusicSearchResult appleResult)
                return null;

            // Fetch TTML syllable lyrics
            var lyricResponse = await LyricifyProviders.AppleMusicApi.GetLyrics(appleResult.Id);
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(lyricResponse?.Ttml))
                return null;

            var lyricsData = TtmlParser.Parse(lyricResponse.Ttml);
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
