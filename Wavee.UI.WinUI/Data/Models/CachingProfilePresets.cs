using System;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Data.Models;

/// <summary>
/// Maps <see cref="CachingProfile"/> to concrete cache capacities and provides
/// memory-estimate helpers used by the Settings UI.
///
/// <para>
/// The <see cref="Capacities"/> record is the single source of truth for every
/// tunable cap in the app. All cache services read their capacities from an
/// instance of this record at DI construction time. Never hard-code a capacity
/// elsewhere — add it here and add a row to <see cref="Get"/>.
/// </para>
///
/// <para>
/// <b>Medium</b> exactly reproduces the pre-profile hard-coded defaults so
/// the baseline experience of users who don't touch the setting is unchanged.
/// </para>
/// </summary>
public static class CachingProfilePresets
{
    /// <summary>
    /// All tunable cache capacities, per profile. One instance per profile,
    /// constructed by <see cref="Get"/> at startup and consumed by DI registrations.
    /// </summary>
    public sealed record Capacities(
        // UI-layer LRU caches
        int LyricsMemoryCacheCapacity,
        int AlbumTracksHotCacheCapacity,
        int ImageCacheMaxSize,
        // Wavee core hot caches (WaveeCacheOptions)
        int TrackHotCacheSize,
        int AlbumHotCacheSize,
        int ArtistHotCacheSize,
        int PlaylistHotCacheSize,
        int ShowHotCacheSize,
        int EpisodeHotCacheSize,
        int UserHotCacheSize,
        int ContextCacheSize,
        int DatabaseHotCacheSize,
        // CacheService aux caches (audio keys, CDN URLs, head data)
        int AudioAuxCacheSize);

    /// <summary>
    /// Returns the capacity table for a given profile. All callers go through this
    /// method so the mapping lives in one place.
    /// </summary>
    public static Capacities Get(CachingProfile profile) => profile switch
    {
        CachingProfile.Low => new Capacities(
            LyricsMemoryCacheCapacity:   30,
            AlbumTracksHotCacheCapacity: 10,
            ImageCacheMaxSize:           25,
            TrackHotCacheSize:           2_000,
            AlbumHotCacheSize:           400,
            ArtistHotCacheSize:          200,
            PlaylistHotCacheSize:        100,
            ShowHotCacheSize:            100,
            EpisodeHotCacheSize:         400,
            UserHotCacheSize:            100,
            ContextCacheSize:            10,
            DatabaseHotCacheSize:        200,
            AudioAuxCacheSize:           200),

        CachingProfile.Medium => new Capacities(
            LyricsMemoryCacheCapacity:   150,
            AlbumTracksHotCacheCapacity: 50,
            ImageCacheMaxSize:           60,
            TrackHotCacheSize:           10_000,
            AlbumHotCacheSize:           2_000,
            ArtistHotCacheSize:          1_000,
            PlaylistHotCacheSize:        500,
            ShowHotCacheSize:            500,
            EpisodeHotCacheSize:         2_000,
            UserHotCacheSize:            500,
            ContextCacheSize:            50,
            DatabaseHotCacheSize:        1_000,
            AudioAuxCacheSize:           1_000),

        CachingProfile.High => new Capacities(
            LyricsMemoryCacheCapacity:   400,
            AlbumTracksHotCacheCapacity: 150,
            ImageCacheMaxSize:           150,
            TrackHotCacheSize:           20_000,
            AlbumHotCacheSize:           5_000,
            ArtistHotCacheSize:          2_500,
            PlaylistHotCacheSize:        1_250,
            ShowHotCacheSize:            1_250,
            EpisodeHotCacheSize:         5_000,
            UserHotCacheSize:            1_250,
            ContextCacheSize:            150,
            DatabaseHotCacheSize:        2_500,
            AudioAuxCacheSize:           2_500),

        CachingProfile.VeryAggressive => new Capacities(
            LyricsMemoryCacheCapacity:   1_000,
            AlbumTracksHotCacheCapacity: 500,
            // Lowered from 400 → 150. Bitmap cache is the largest single
            // contributor to working set on this profile (each entry holds a
            // decoded BitmapImage with unmanaged bitmap data on the GPU side),
            // and 400 was hitting diminishing returns long before saving any
            // measurable hit-rate. 150 keeps the player bar + sidebar + the
            // currently visible page comfortably resident.
            ImageCacheMaxSize:           150,
            TrackHotCacheSize:           50_000,
            AlbumHotCacheSize:           12_000,
            ArtistHotCacheSize:          6_000,
            PlaylistHotCacheSize:        3_000,
            ShowHotCacheSize:            3_000,
            EpisodeHotCacheSize:         12_000,
            UserHotCacheSize:            3_000,
            ContextCacheSize:            300,
            DatabaseHotCacheSize:        6_000,
            AudioAuxCacheSize:           6_000),

        _ => Get(CachingProfile.Medium),
    };

    // ── Memory estimation ───────────────────────────────────────────────

    // Per-entry byte estimates. Conservative averages — real entries vary,
    // but the output is a display-only estimate labelled "~X MB".
    private const int LyricsBytesPerEntry = 60 * 1024;            // 60 KB
    private const int AlbumTracksBytesPerEntry = 6 * 1024;        // 6 KB
    private const int BitmapBytesPerEntry = (int)(1.5 * 1024 * 1024); // 1.5 MB unmanaged
    private const int TrackHotBytesPerEntry = 800;
    private const int AlbumHotBytesPerEntry = 1_024;
    private const int ArtistHotBytesPerEntry = 512;
    private const int PlaylistHotBytesPerEntry = 1_200;
    private const int ShowHotBytesPerEntry = 1_024;
    private const int EpisodeHotBytesPerEntry = 800;
    private const int UserHotBytesPerEntry = 300;
    private const int ContextHotBytesPerEntry = 50 * 1024;        // 50 KB
    private const int DatabaseHotBytesPerEntry = 2 * 1024;        // 2 KB
    private const int AudioAuxBytesPerEntry = 2 * 1024;           // 2 KB average across 3 caches

    /// <summary>
    /// Returns a rounded-to-nearest-10 MB estimate of the total memory these
    /// caches could consume if fully populated. Display-only — not a measurement.
    /// </summary>
    public static int EstimateMegabytes(Capacities c)
    {
        long totalBytes =
            (long)c.LyricsMemoryCacheCapacity * LyricsBytesPerEntry +
            (long)c.AlbumTracksHotCacheCapacity * AlbumTracksBytesPerEntry +
            (long)c.ImageCacheMaxSize * BitmapBytesPerEntry +
            (long)c.TrackHotCacheSize * TrackHotBytesPerEntry +
            (long)c.AlbumHotCacheSize * AlbumHotBytesPerEntry +
            (long)c.ArtistHotCacheSize * ArtistHotBytesPerEntry +
            (long)c.PlaylistHotCacheSize * PlaylistHotBytesPerEntry +
            (long)c.ShowHotCacheSize * ShowHotBytesPerEntry +
            (long)c.EpisodeHotCacheSize * EpisodeHotBytesPerEntry +
            (long)c.UserHotCacheSize * UserHotBytesPerEntry +
            (long)c.ContextCacheSize * ContextHotBytesPerEntry +
            (long)c.DatabaseHotCacheSize * DatabaseHotBytesPerEntry +
            (long)c.AudioAuxCacheSize * AudioAuxBytesPerEntry;

        int mb = (int)(totalBytes / (1024 * 1024));
        // Round to nearest 10 for presentational honesty — this is an estimate.
        return (int)(Math.Round(mb / 10.0) * 10);
    }

    /// <summary>
    /// Formatted estimate string for the Settings UI: "~120 MB".
    /// </summary>
    public static string FormatEstimate(CachingProfile profile) =>
        $"~{EstimateMegabytes(Get(profile))} MB";

    /// <summary>
    /// Human-readable profile name for tooltips and the summary line.
    /// </summary>
    public static string GetDisplayName(CachingProfile profile) => profile switch
    {
        CachingProfile.Low => AppLocalization.GetString("CachingProfile_Low"),
        CachingProfile.Medium => AppLocalization.GetString("CachingProfile_Medium"),
        CachingProfile.High => AppLocalization.GetString("CachingProfile_High"),
        CachingProfile.VeryAggressive => AppLocalization.GetString("CachingProfile_VeryAggressive"),
        _ => AppLocalization.GetString("CachingProfile_Medium"),
    };
}
