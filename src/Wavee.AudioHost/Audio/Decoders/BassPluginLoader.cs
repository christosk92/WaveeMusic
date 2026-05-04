using ManagedBass;
using Microsoft.Extensions.Logging;

namespace Wavee.AudioHost.Audio.Decoders;

/// <summary>
/// Best-effort loader for BASS plug-in DLLs that extend the core decoder set.
///
/// <para>
/// BASS itself ships with native MP3, WAV, AIFF, and basic MP4/AAC support.
/// FLAC, Opus, WMA, and full AAC handling live in side-by-side plug-ins
/// (<c>bassflac.dll</c>, <c>bass_aac.dll</c>, <c>bassopus.dll</c>, <c>basswma.dll</c>),
/// licensed separately by un4seen and not redistributable from this repo.
/// </para>
/// <para>
/// At startup we look for a <c>bass-plugins/</c> folder next to the running
/// AudioHost executable and call <see cref="Bass.PluginLoad(string)"/> on every
/// <c>bass*.dll</c> found. Missing plug-ins are logged at Information and
/// playback simply fails for the affected formats — MP3/WAV/AIFF always work
/// even when the plug-in folder is empty.
/// </para>
/// </summary>
internal static class BassPluginLoader
{
    private static readonly object Gate = new();
    private static bool _attempted;

    public static void TryLoadAllOnce(ILogger? logger)
    {
        lock (Gate)
        {
            if (_attempted) return;
            _attempted = true;
        }

        try
        {
            var baseDir = AppContext.BaseDirectory;
            var pluginDir = Path.Combine(baseDir, "bass-plugins");
            if (!Directory.Exists(pluginDir))
            {
                logger?.LogInformation(
                    "BASS plugin folder not found at {PluginDir}; FLAC/AAC/Opus/WMA local playback will be unavailable. " +
                    "Drop bassflac.dll / bass_aac.dll / bassopus.dll / basswma.dll there to enable.",
                    pluginDir);
                return;
            }

            var loaded = 0;
            foreach (var dll in Directory.EnumerateFiles(pluginDir, "bass*.dll", SearchOption.TopDirectoryOnly))
            {
                var handle = Bass.PluginLoad(dll);
                if (handle == 0)
                {
                    logger?.LogWarning(
                        "BASS plugin failed to load: {Plugin} ({Error})",
                        Path.GetFileName(dll), Bass.LastError);
                }
                else
                {
                    loaded++;
                    logger?.LogInformation("BASS plugin loaded: {Plugin}", Path.GetFileName(dll));
                }
            }

            if (loaded == 0)
            {
                logger?.LogInformation("BASS plugin folder {PluginDir} contained no loadable plug-ins.", pluginDir);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "BASS plugin enumeration failed; built-in formats only.");
        }
    }
}
