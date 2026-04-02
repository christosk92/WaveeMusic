using System;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using LyricifyProviders = Lyricify.Lyrics.Providers.Web.Providers;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services.Lyrics.Providers;

public sealed class MusixmatchLyricsProvider : ILyricsProvider
{
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Id => "Musixmatch";
    public string DisplayName => "Musixmatch";

    public MusixmatchLyricsProvider(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        // Restore cached token
        var token = settingsService.Settings.LyricsProviders.MusixmatchToken;
        if (!string.IsNullOrEmpty(token))
            LyricifyProviders.MusixmatchApi.SetUserToken(token);
    }

    public async Task<LyricsSearchResult?> SearchAsync(
        string title, string artist, string? album,
        double durationMs, string? trackId, string? imageUri,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var durationSec = durationMs > 0 ? (int?)(durationMs / 1000) : null;

            // Search for the track
            var trackResult = await LyricifyProviders.MusixmatchApi.GetTrack(title, artist, durationSec);
            ct.ThrowIfCancellationRequested();

            if (trackResult == null)
                return null;

            // Get rich-synced lyrics
            var rawJson = await LyricifyProviders.MusixmatchApi.GetFullLyricsRaw(title, artist, durationSec);
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(rawJson))
                return null;

            var lyricsData = MusixmatchParser.Parse(rawJson);
            if (lyricsData?.Lines is not { Count: > 0 })
                return null;

            // Persist token for next session
            var newToken = LyricifyProviders.MusixmatchApi.GetUserToken();
            if (!string.IsNullOrEmpty(newToken))
                _settingsService.Update(s => s.LyricsProviders.MusixmatchToken = newToken);

            var (response, wordTimings) = LyricifyAdapter.ToWaveeResponse(lyricsData, DisplayName);

            // Map Musixmatch confidence to match score
            var confidence = trackResult.Message?.Header?.Confidence ?? 0;
            var matchScore = confidence switch
            {
                >= 1000 => 100,
                >= 950 => 99,
                >= 900 => 95,
                >= 750 => 90,
                >= 600 => 70,
                >= 400 => 30,
                >= 200 => 10,
                _ => 0,
            };

            return new LyricsSearchResult(response, wordTimings, matchScore);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _lock.Release(); }
    }
}
