using Wavee.Sdk.Streams;

namespace Wavee.Backend.Audio;

/// <summary>
/// The app-side boundary for the SDK's <see cref="ChunkDiskCache"/> (the sparse, SHA-256-verified 64 KiB chunk cache of
/// the encrypted CDN body — moved to <c>Wavee.Sdk.Streams</c> so playback modules share ONE implementation with the app).
/// Everything here is what the SDK deliberately does not know: where Wavee's cache lives, and how the user's settings
/// map onto a <see cref="ChunkCachePolicy"/>.
/// </summary>
public static class AudioBodyDiskCache
{
    /// <summary>The cache's fixed chunk granularity — re-exported so call sites do not need the SDK type.</summary>
    public const int ChunkBytes = ChunkDiskCache.ChunkBytes;

    /// <summary>A cache whose root/budget follow the user's storage settings live (re-read on every operation).</summary>
    public static ChunkDiskCache FromSettings(IAppSettings settings, WaveeLogger log = default) => new(() =>
    {
        string directory = ResolveDirectory(settings.Get(WaveeSettings.AudioBodyCacheBasePath));
        var mode = (AudioCacheBudgetMode)Math.Clamp(settings.Get(WaveeSettings.AudioBodyCacheBudgetMode), 0, 2);
        return new ChunkCachePolicy(
            settings.Get(WaveeSettings.AudioBodyCacheEnabled),
            directory,
            mode,
            Math.Max(ChunkDiskCache.MinBudgetBytes, settings.Get(WaveeSettings.AudioBodyCacheBudgetBytes)),
            Math.Clamp(settings.Get(WaveeSettings.AudioBodyCacheBudgetPercent), 0, 90));
    }, log, DefaultDirectory());

    /// <summary>Wavee's canonical cache root under the per-user app-data cache folder.</summary>
    public static string DefaultDirectory()
    {
        try { return Path.Combine(FluentGpu.WindowsApi.Storage.AppDataStore.ForUnpackaged("Wavee", "Wavee").CacheFolder, "audio"); }
        catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "Cache", "audio"); }
    }

    /// <summary>The picker stores a parent directory. Wavee owns only this dedicated child beneath it.</summary>
    public static string ResolveDirectory(string? selectedBasePath) =>
        ChunkDiskCache.ResolveDirectory(selectedBasePath, DefaultDirectory());
}

/// <summary>Maps the SDK's source-neutral stream failure vocabulary onto the app's audio failure reasons — the ONE
/// place a <see cref="AudioRangeFetchException"/> / <see cref="CdnPermanentException"/> reason crosses the boundary.</summary>
public static class StreamFailureReasons
{
    /// <summary>The app-facing twin of an SDK <see cref="StreamFailureReason"/>.</summary>
    public static AudioKeyFailureReason ToAudioKeyFailureReason(this StreamFailureReason reason) => reason switch
    {
        StreamFailureReason.Network => AudioKeyFailureReason.Network,
        StreamFailureReason.Restricted => AudioKeyFailureReason.Restricted,
        StreamFailureReason.ProtocolFault => AudioKeyFailureReason.EmulationFault,
        _ => AudioKeyFailureReason.None,
    };
}
