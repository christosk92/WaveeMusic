using System;
using System.IO;

namespace Wavee;

/// <summary>
/// Surfaces Windows Error Reporting crash dumps into the normal app log on the NEXT launch, so a hard crash leaves a
/// human-readable breadcrumb in Diagnostics even when the dying process cannot run managed handlers.
/// </summary>
static class CrashDumpProbe
{
    /// <returns>The newest dump path when this launch found a NEW dump it hadn't logged before; otherwise null
    /// (no CrashDumps dir, no dumps, the newest dump was already seen, or the probe itself failed).</returns>
    public static string? LogPendingCrashDump(IAppSettings settings, IWaveeLog log)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
            if (!Directory.Exists(dir)) return null;

            string[] dumps = Directory.GetFiles(dir, "Wavee.exe*.dmp");
            if (dumps.Length == 0) return null;

            string newestPath = dumps[0];
            DateTime newestWrite = File.GetLastWriteTimeUtc(newestPath);
            for (int i = 1; i < dumps.Length; i++)
            {
                DateTime write = File.GetLastWriteTimeUtc(dumps[i]);
                if (write > newestWrite)
                {
                    newestWrite = write;
                    newestPath = dumps[i];
                }
            }

            string seenPath = settings.Get(WaveeSettings.LastSeenCrashDumpPath);
            long seenTicks = settings.Get(WaveeSettings.LastSeenCrashDumpTicksUtc);
            if (string.Equals(seenPath, newestPath, StringComparison.OrdinalIgnoreCase) &&
                seenTicks == newestWrite.Ticks)
                return null;

            long size = 0;
            try { size = new FileInfo(newestPath).Length; } catch { }

            log.Event(WaveeLogLevel.Critical, "crash", "wer.dump.detected",
                "Previous run left a Windows crash dump",
                fields:
                [
                    WaveeLogField.Of("path", newestPath),
                    WaveeLogField.Of("writtenUtc", newestWrite.ToString("O")),
                    WaveeLogField.Of("sizeBytes", size),
                ]);

            settings.Set(WaveeSettings.LastSeenCrashDumpPath, newestPath);
            settings.Set(WaveeSettings.LastSeenCrashDumpTicksUtc, newestWrite.Ticks);
            return newestPath;
        }
        catch (Exception ex)
        {
            log.Error("crash", "wer.dump.detect.failed", "Failed to inspect CrashDumps", ex);
            return null;
        }
    }
}
