using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wavee.Local;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;
using Wavee.Core.Storage.Entities;

namespace Wavee.Core.DependencyInjection;

/// <summary>
/// Extension methods for registering Wavee cache services with dependency injection.
/// </summary>
public static class WaveeCacheServiceCollectionExtensions
{
    /// <summary>
    /// Adds Wavee cache services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddWaveeCache(
        this IServiceCollection services,
        Action<WaveeCacheOptions>? configureOptions = null)
    {
        // Configure options
        var options = new WaveeCacheOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        // Register metadata database (singleton)
        services.AddSingleton<IMetadataDatabase>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<MetadataDatabase>>();
            return new MetadataDatabase(opts.DatabasePath, opts.DatabaseHotCacheSize, opts.SpotifyMetadataLocale, logger);
        });

        // Register hot caches for each entity type (singleton)
        services.AddSingleton<IHotCache<TrackCacheEntry>>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<HotCache<TrackCacheEntry>>>();
            return new HotCache<TrackCacheEntry>(opts.TrackHotCacheSize, logger);
        });

        services.AddSingleton<IHotCache<AlbumCacheEntry>>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<HotCache<AlbumCacheEntry>>>();
            return new HotCache<AlbumCacheEntry>(opts.AlbumHotCacheSize, logger);
        });

        services.AddSingleton<IHotCache<ArtistCacheEntry>>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<HotCache<ArtistCacheEntry>>>();
            return new HotCache<ArtistCacheEntry>(opts.ArtistHotCacheSize, logger);
        });

        services.AddSingleton<IHotCache<PlaylistCacheEntry>>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<HotCache<PlaylistCacheEntry>>>();
            return new HotCache<PlaylistCacheEntry>(opts.PlaylistHotCacheSize, logger);
        });

        services.AddSingleton<IHotCache<ShowCacheEntry>>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<HotCache<ShowCacheEntry>>>();
            return new HotCache<ShowCacheEntry>(opts.ShowHotCacheSize, logger);
        });

        services.AddSingleton<IHotCache<EpisodeCacheEntry>>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<HotCache<EpisodeCacheEntry>>>();
            return new HotCache<EpisodeCacheEntry>(opts.EpisodeHotCacheSize, logger);
        });

        services.AddSingleton<IHotCache<UserCacheEntry>>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<HotCache<UserCacheEntry>>>();
            return new HotCache<UserCacheEntry>(opts.UserHotCacheSize, logger);
        });

        services.AddSingleton<IHotCache<ContextCacheEntry>>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<HotCache<ContextCacheEntry>>>();
            return new HotCache<ContextCacheEntry>(opts.ContextCacheSize, logger);
        });

        // Register unified cache service (singleton)
        services.AddSingleton<ICacheService>(sp =>
        {
            var database = sp.GetRequiredService<IMetadataDatabase>();
            var trackHotCache = sp.GetRequiredService<IHotCache<TrackCacheEntry>>();
            var logger = sp.GetService<ILogger<CacheService>>();
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            return new CacheService(database, trackHotCache, logger, opts.AudioAuxCacheSize);
        });

        // Register ICleanableCache for each HotCache instance
        services.AddSingleton<ICleanableCache>(sp => (ICleanableCache)sp.GetRequiredService<IHotCache<TrackCacheEntry>>());
        services.AddSingleton<ICleanableCache>(sp => (ICleanableCache)sp.GetRequiredService<IHotCache<AlbumCacheEntry>>());
        services.AddSingleton<ICleanableCache>(sp => (ICleanableCache)sp.GetRequiredService<IHotCache<ArtistCacheEntry>>());
        services.AddSingleton<ICleanableCache>(sp => (ICleanableCache)sp.GetRequiredService<IHotCache<PlaylistCacheEntry>>());
        services.AddSingleton<ICleanableCache>(sp => (ICleanableCache)sp.GetRequiredService<IHotCache<ShowCacheEntry>>());
        services.AddSingleton<ICleanableCache>(sp => (ICleanableCache)sp.GetRequiredService<IHotCache<EpisodeCacheEntry>>());
        services.AddSingleton<ICleanableCache>(sp => (ICleanableCache)sp.GetRequiredService<IHotCache<UserCacheEntry>>());
        services.AddSingleton<ICleanableCache>(sp => (ICleanableCache)sp.GetRequiredService<IHotCache<ContextCacheEntry>>());

        // Register CacheService as ICleanableCache
        services.AddSingleton<ICleanableCache>(sp => (ICleanableCache)sp.GetRequiredService<ICacheService>());

        // Register cleanup options and service
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            return new CacheCleanupOptions
            {
                CleanupInterval = opts.CleanupInterval,
                DefaultMaxAge = opts.DefaultMaxAge
            };
        });
        services.AddSingleton<CacheCleanupService>();

        // ── Local file library ──
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<LocalArtworkCache>>();
            return new LocalArtworkCache(opts.LocalArtworkDirectory, logger);
        });
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<ILogger<LocalMetadataExtractor>>();
            return new LocalMetadataExtractor(logger);
        });
        services.AddSingleton(sp =>
        {
            var extractor = sp.GetRequiredService<LocalMetadataExtractor>();
            var artwork = sp.GetRequiredService<LocalArtworkCache>();
            // Optional — only registered on platforms that supply one
            // (Wavee.UI.WinUI does via WindowsVideoThumbnailExtractor).
            // When null, the scanner skips the video-frame fallback and
            // simply doesn't write artwork for tag-less videos.
            var videoThumbnail = sp.GetService<IVideoThumbnailExtractor>();
            // Optional — only registered on Windows (Wavee.UI.WinUI provides
            // MediaFoundationEmbeddedTrackProber). Without it the scanner skips
            // the embedded-track index pass for video files.
            var embeddedTrackProber = sp.GetService<Wavee.Local.Subtitles.IEmbeddedTrackProber>();
            var logger = sp.GetService<ILogger<LocalFolderScanner>>();
            return new LocalFolderScanner(extractor, artwork, videoThumbnail, embeddedTrackProber, logger);
        });
        services.AddSingleton<ILocalLibraryService>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var scanner = sp.GetRequiredService<LocalFolderScanner>();
            var logger = sp.GetService<ILogger<LocalLibraryService>>();
            return new LocalLibraryService(opts.DatabasePath, scanner, logger);
        });
        // Concrete LocalLibraryService for components that need RunScanAsync
        // (the hosted service); ILocalLibraryService remains the public surface.
        services.AddSingleton(sp => (LocalLibraryService)sp.GetRequiredService<ILocalLibraryService>());
        services.AddSingleton(sp =>
        {
            var lib = sp.GetRequiredService<ILocalLibraryService>();
            var logger = sp.GetService<ILogger<LocalFolderWatcher>>();
            return new LocalFolderWatcher(lib, logger);
        });
        services.AddSingleton(sp =>
        {
            var lib = sp.GetRequiredService<LocalLibraryService>();
            var watcher = sp.GetRequiredService<LocalFolderWatcher>();
            var logger = sp.GetService<ILogger<LocalIndexerHostedService>>();
            return new LocalIndexerHostedService(lib, watcher, logger);
        });
        services.AddSingleton<ILocalLikeService>(sp =>
        {
            var opts = sp.GetRequiredService<WaveeCacheOptions>();
            var logger = sp.GetService<ILogger<LocalLikeService>>();
            return new LocalLikeService(opts.DatabasePath, logger);
        });

        return services;
    }
}
