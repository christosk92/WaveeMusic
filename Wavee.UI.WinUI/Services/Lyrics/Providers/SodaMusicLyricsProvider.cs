using System;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using LyricifyProviders = Lyricify.Lyrics.Providers.Web.Providers;
using Lyricify.Lyrics.Searchers;

namespace Wavee.UI.WinUI.Services.Lyrics.Providers;

public sealed class SodaMusicLyricsProvider : ILyricsProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Id => "SodaMusic";
    public string DisplayName => "Soda Music";

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

            var searcher = new SodaMusicSearcher();
            var result = await searcher.SearchForResult(metadata);
            ct.ThrowIfCancellationRequested();

            if (result is not SodaMusicSearchResult sodaResult)
                return null;

            // Fetch track detail with embedded lyrics
            var detail = await LyricifyProviders.SodaMusicApi.GetDetail(sodaResult.Id);
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(detail?.Lyric?.Content))
                return null;

            var lyricsData = LrcParser.Parse(detail.Lyric.Content);
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
