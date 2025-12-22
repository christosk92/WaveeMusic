using Microsoft.Extensions.Logging;
using Wavee.Connect.Commands;
using Wavee.Connect.Events;
using Wavee.Connect.Playback.Abstractions;
using Wavee.Connect.Playback.Decoders;
using Wavee.Connect.Playback.Processors;
using Wavee.Connect.Playback.Sinks;
using Wavee.Connect.Playback.Sources;
using Wavee.Core.Audio;
using Wavee.Core.Audio.Cache;
using Wavee.Core.Http;
using Wavee.Core.Library;
using Wavee.Core.Session;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Connect.Playback;

/// <summary>
/// Factory for creating fully configured audio pipelines.
/// </summary>
public static class AudioPipelineFactory
{
    /// <summary>
    /// Creates a fully configured audio pipeline for Spotify playback.
    /// </summary>
    /// <param name="session">Active Spotify session.</param>
    /// <param name="spClient">SpClient for metadata requests.</param>
    /// <param name="httpClient">HTTP client for CDN/head file requests.</param>
    /// <param name="options">Pipeline configuration options.</param>
    /// <param name="metadataDatabase">Optional metadata database for caching extended metadata.</param>
    /// <param name="cacheService">Optional cache service (should be singleton). Created if metadataDatabase provided but cacheService is null.</param>
    /// <param name="deviceId">Device ID for event reporting.</param>
    /// <param name="eventService">Optional event service for playback reporting.</param>
    /// <param name="commandHandler">Optional command handler for remote control. If provided, pipeline auto-subscribes to commands.</param>
    /// <param name="deviceStateManager">Optional device state manager for activation on playback.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>Configured audio pipeline ready for playback.</returns>
    public static AudioPipeline CreateSpotifyPipeline(
        Session session,
        SpClient spClient,
        HttpClient httpClient,
        AudioPipelineOptions? options = null,
        IMetadataDatabase? metadataDatabase = null,
        ICacheService? cacheService = null,
        string deviceId = "",
        EventService? eventService = null,
        ConnectCommandHandler? commandHandler = null,
        DeviceStateManager? deviceStateManager = null,
        ILogger? logger = null)
    {
        options ??= AudioPipelineOptions.Default;

        // Create components
        var sourceRegistry = CreateTrackSourceRegistry(session, spClient, httpClient, options, metadataDatabase, logger);
        var decoderRegistry = CreateDecoderRegistry(logger);
        var audioSink = CreateAudioSink(options.AudioSinkType, logger);
        var processingChain = CreateProcessingChain(options, logger);

        // Create context resolver for playlist/album loading
        ContextResolver? contextResolver = null;
        if (metadataDatabase != null)
        {
            var spClientBaseUrl = session.SpClientUrl ?? "spclient.wg.spotify.com";
            var extendedMetadataClient = new ExtendedMetadataClient(
                session,
                httpClient,
                spClientBaseUrl,
                metadataDatabase,
                logger);

            // Use provided cache service or create one (caller should ideally provide singleton)
            cacheService ??= new CacheService(metadataDatabase, hotCacheSize: 10_000, logger);

            contextResolver = new ContextResolver(
                spClient,
                extendedMetadataClient,
                cacheService,
                logger);

            logger?.LogDebug("ContextResolver created - album/playlist playback enabled");
        }
        else
        {
            logger?.LogWarning("ContextResolver not created - IMetadataDatabase not provided. " +
                "Album and playlist playback will not work.");
        }

        // Use constructor with commandHandler if provided (enables auto-subscription to commands)
        if (commandHandler != null)
        {
            return new AudioPipeline(
                sourceRegistry,
                decoderRegistry,
                audioSink,
                processingChain,
                commandHandler,
                deviceStateManager,
                deviceId,
                eventService,
                contextResolver,
                options.EventReporting,
                logger);
        }

        return new AudioPipeline(
            sourceRegistry,
            decoderRegistry,
            audioSink,
            processingChain,
            deviceId,
            eventService,
            contextResolver,
            options.EventReporting,
            logger);
    }

    /// <summary>
    /// Creates a track source registry with Spotify support.
    /// </summary>
    public static TrackSourceRegistry CreateTrackSourceRegistry(
        Session session,
        SpClient spClient,
        HttpClient httpClient,
        AudioPipelineOptions? options = null,
        IMetadataDatabase? metadataDatabase = null,
        ILogger? logger = null)
    {
        options ??= AudioPipelineOptions.Default;

        var registry = new TrackSourceRegistry();

        // Create head file client
        var headFileClient = new HeadFileClient(httpClient, logger);

        // Create cache manager if enabled
        AudioCacheManager? cacheManager = null;
        if (options.EnableCaching)
        {
            cacheManager = new AudioCacheManager(options.CacheConfig, logger);
        }

        // Create extended metadata client if database is provided
        ExtendedMetadataClient? extendedMetadataClient = null;
        if (metadataDatabase != null)
        {
            // Get the spclient base URL from the session
            var spClientBaseUrl = session.SpClientUrl ?? "spclient.wg.spotify.com";
            extendedMetadataClient = new ExtendedMetadataClient(
                session,
                httpClient,
                spClientBaseUrl,
                metadataDatabase,
                logger);
        }

        // Create Spotify track source
        var spotifySource = new SpotifyTrackSource(
            session,
            spClient,
            headFileClient,
            httpClient,
            options.PreferredQuality,
            cacheManager,
            extendedMetadataClient,
            logger);

        registry.Register(spotifySource);

        // Register local file source if enabled
        if (options.EnableLocalFiles)
        {
            var localFileSource = CreateLocalFileTrackSource(options.LocalFileCacheDirectory, logger);
            registry.Register(localFileSource);
        }

        // Register HTTP stream source if enabled
        if (options.EnableHttpStreams)
        {
            var httpStreamSource = new HttpStreamTrackSource(httpClient, logger);
            registry.Register(httpStreamSource);
        }

        return registry;
    }

    /// <summary>
    /// Creates a local file track source for playing audio files from disk.
    /// </summary>
    /// <param name="cacheDirectory">Directory for caching album art. If null, uses temp directory.</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>Configured local file track source.</returns>
    public static LocalFileTrackSource CreateLocalFileTrackSource(
        string? cacheDirectory = null,
        ILogger? logger = null)
    {
        // Use temp directory if not specified
        cacheDirectory ??= Path.Combine(Path.GetTempPath(), "Wavee", "cache");

        var metadataExtractor = new MetadataExtractor();
        var albumArtCache = new AlbumArtCache(cacheDirectory);

        return new LocalFileTrackSource(metadataExtractor, albumArtCache);
    }

    /// <summary>
    /// Creates a decoder registry with Vorbis and BASS support.
    /// </summary>
    public static AudioDecoderRegistry CreateDecoderRegistry(ILogger? logger = null)
    {
        var registry = new AudioDecoderRegistry();

        // Register Vorbis decoder (primary format for Spotify OGG files)
        registry.Register(new VorbisDecoder(logger));

        // Register BASS decoder (MP3, FLAC, WAV, AAC, etc.)
        registry.Register(new BassDecoder(logger));

        return registry;
    }

    /// <summary>
    /// Creates the appropriate audio sink for the platform.
    /// </summary>
    public static IAudioSink CreateAudioSink(AudioSinkType sinkType, ILogger? logger = null)
    {
        return AudioSinkFactory.Create(sinkType, logger);
    }

    /// <summary>
    /// Creates an audio processing chain with standard processors.
    /// </summary>
    public static AudioProcessingChain CreateProcessingChain(
        AudioPipelineOptions? options = null,
        ILogger? logger = null)
    {
        options ??= AudioPipelineOptions.Default;

        var chain = new AudioProcessingChain();

        // Add normalization processor if enabled
        if (options.EnableNormalization)
        {
            var normProcessor = new NormalizationProcessor
            {
                IsEnabled = true,
                PreAmpDb = options.NormalizationTargetLufs + 14f // Adjust relative to -14 LUFS reference
            };
            chain.AddProcessor(normProcessor);
        }

        // Add volume processor
        var volumeProcessor = new VolumeProcessor
        {
            Volume = options.InitialVolume
        };
        chain.AddProcessor(volumeProcessor);

        // Add equalizer if enabled
        if (options.EnableEqualizer)
        {
            chain.AddProcessor(new EqualizerProcessor());
        }

        return chain;
    }
}

/// <summary>
/// Configuration options for audio pipeline.
/// </summary>
public sealed record AudioPipelineOptions
{
    /// <summary>
    /// Preferred audio quality.
    /// </summary>
    public AudioQuality PreferredQuality { get; init; } = AudioQuality.VeryHigh;

    /// <summary>
    /// Audio sink type to use.
    /// </summary>
    public AudioSinkType AudioSinkType { get; init; } = GetDefaultSinkType();

    /// <summary>
    /// Whether to enable audio caching.
    /// </summary>
    public bool EnableCaching { get; init; } = true;

    /// <summary>
    /// Cache configuration.
    /// </summary>
    public AudioCacheConfig? CacheConfig { get; init; }

    /// <summary>
    /// Whether to enable normalization.
    /// </summary>
    public bool EnableNormalization { get; init; } = true;

    /// <summary>
    /// Target LUFS for normalization.
    /// </summary>
    public float NormalizationTargetLufs { get; init; } = -14f;

    /// <summary>
    /// Whether to enable equalizer.
    /// </summary>
    public bool EnableEqualizer { get; init; } = false;

    /// <summary>
    /// Initial volume (0.0 to 1.0).
    /// </summary>
    public float InitialVolume { get; init; } = 1.0f;

    /// <summary>
    /// Whether to enable local file playback support.
    /// </summary>
    public bool EnableLocalFiles { get; init; } = true;

    /// <summary>
    /// Directory for caching local file album art.
    /// If null, uses system temp directory.
    /// </summary>
    public string? LocalFileCacheDirectory { get; init; }

    /// <summary>
    /// Whether to enable HTTP/HTTPS stream playback support.
    /// Supports direct audio files and radio streams (Shoutcast/Icecast).
    /// </summary>
    public bool EnableHttpStreams { get; init; } = true;

    /// <summary>
    /// Event reporting configuration (controls what gets sent to Spotify).
    /// </summary>
    public EventReportingOptions EventReporting { get; init; } = EventReportingOptions.Default;

    /// <summary>
    /// Default options.
    /// </summary>
    public static AudioPipelineOptions Default { get; } = new();

    private static AudioSinkType GetDefaultSinkType()
    {
        // PortAudio is cross-platform (WASAPI/CoreAudio/ALSA)
        return AudioSinkType.PortAudio;
    }
}

/// <summary>
/// Configuration for playback event reporting to Spotify.
/// Controls which sources send playback events (play counts, history, etc.).
/// </summary>
public sealed record EventReportingOptions
{
    /// <summary>
    /// Report Spotify track playback events. Default: true.
    /// </summary>
    public bool ReportSpotifyTracks { get; init; } = true;

    /// <summary>
    /// Report local file playback events. Default: false.
    /// When enabled, local file plays are sent to Spotify (affects listening history).
    /// </summary>
    public bool ReportLocalFiles { get; init; } = true;

    /// <summary>
    /// Report HTTP stream playback events. Default: false.
    /// </summary>
    public bool ReportHttpStreams { get; init; } = false;

    /// <summary>
    /// Report podcast episode playback events. Default: true.
    /// Podcasts are typically Spotify content, so this is enabled by default.
    /// </summary>
    public bool ReportPodcasts { get; init; } = true;

    /// <summary>
    /// Default settings: report Spotify content only.
    /// </summary>
    public static EventReportingOptions Default { get; } = new();

    /// <summary>
    /// Report everything to Spotify.
    /// </summary>
    public static EventReportingOptions ReportAll { get; } = new()
    {
        ReportSpotifyTracks = true,
        ReportLocalFiles = true,
        ReportHttpStreams = true,
        ReportPodcasts = true
    };

    /// <summary>
    /// Report nothing to Spotify (private mode).
    /// </summary>
    public static EventReportingOptions ReportNone { get; } = new()
    {
        ReportSpotifyTracks = false,
        ReportLocalFiles = false,
        ReportHttpStreams = false,
        ReportPodcasts = false
    };
}
