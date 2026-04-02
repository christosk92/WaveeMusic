using System;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using LyricifyProviders = Lyricify.Lyrics.Providers.Web.Providers;
using Lyricify.Lyrics.Searchers;

namespace Wavee.UI.WinUI.Services.Lyrics.Providers;

public sealed class KugouLyricsProvider : ILyricsProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Id => "Kugou";
    public string DisplayName => "Kugou Music";

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

            var searcher = new KugouSearcher();
            var result = await searcher.SearchForResult(metadata);
            ct.ThrowIfCancellationRequested();

            if (result is not KugouSearchResult kugouResult)
                return null;

            // Search for lyrics by hash
            var searchLyrics = await LyricifyProviders.KugouApi.GetSearchLyrics(
                keywords: $"{title} {artist}",
                duration: (int)(durationMs / 1000),
                hash: kugouResult.Hash);
            ct.ThrowIfCancellationRequested();

            if (searchLyrics?.Candidates is not { Count: > 0 })
                return null;

            var candidate = searchLyrics.Candidates[0];

            // Download and decrypt KRC lyrics
            var decryptedKrc = await Lyricify.Lyrics.Decrypter.Krc.Helper.GetLyricsAsync(
                candidate.Id, candidate.AccessKey);
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(decryptedKrc))
                return null;

            var lyricsData = KrcParser.Parse(decryptedKrc);
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
