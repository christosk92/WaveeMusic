using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            return new MetadataDatabase(opts.DatabasePath, opts.DatabaseHotCacheSize, logger);
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
            return new CacheService(database, trackHotCache, logger);
        });

        return services;
    }
}
