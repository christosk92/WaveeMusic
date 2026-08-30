using System;
using System.Globalization;
using System.IO;
using Wavee.Sdk.Streams;

namespace Wavee;

/// <summary>The Sound &amp; storage page's (page 6) pure arithmetic: streaming bitrate/GB-per-hour readouts, the audio
/// cache budget share the stage's <c>ProgressBar</c> draws, and the two combo-index lookups the page's crossfade
/// duration / metadata budget rows use. ENGINE-FREE BY CONSTRUCTION (System + <see cref="AudioCacheBudgetMode"/>
/// only — the same enum every other cache call site already uses, no <c>Loc</c>/<c>Signal</c>/<c>Element</c>), exactly
/// like <c>SetupGating</c>/<c>SidebarDesignGating</c>: this file is source-included by <c>Wavee.Tests</c> so
/// <c>SetupSoundFactsTests</c> drives the REAL numbers instead of a copy of them.
///
/// <para><b>Never</b> call <c>AudioBodyDiskCache.Status()</c> (or anything that walks the on-disk chunk map) from a
/// render path to feed <see cref="CacheShare"/> — the stage card's drive total comes from a try/caught
/// <c>new DriveInfo(root).TotalSize</c> at the call site instead (null on failure → share 0, bar hidden).</para></summary>
static class SetupSoundFacts
{
    /// <summary>The three enabled streaming-quality tiers' kbps, indexed by <c>PlaybackQuality</c> 0/1/2 (Normal/High/
    /// Very High). Lossless (index 3) is offered disabled on the page — it has no kbps figure here.</summary>
    public static readonly int[] KbpsByQuality = [96, 160, 320];

    /// <summary>The metadata-cache budget combo's four byte options, indexed 0-3 — the SAME table
    /// <c>MetaBudgetIndex</c> resolves a stored byte value back against.</summary>
    public static readonly long[] MetaBudgetBytes = [32L << 20, 64L << 20, 128L << 20, 256L << 20];

    /// <summary><paramref name="quality"/> clamped to 0..2 → its kbps figure.</summary>
    public static int Kbps(int quality) => KbpsByQuality[Math.Clamp(quality, 0, KbpsByQuality.Length - 1)];

    /// <summary>Streamed GB per hour at a given bitrate: kbps → bytes/sec (÷8) → bytes/hour (×3600) → GB (÷1e6, decimal
    /// GB — matches the "0.14 GB/hour" copy in <c>settings.playback.quality*Sub</c>, not GiB).</summary>
    public static double GbPerHour(int kbps) => kbps * 3600.0 / 8.0 / 1_000_000.0;

    /// <summary>Two decimal places, invariant culture — "0.14", "0.07", "0.04" for 320/160/96 kbps respectively (must
    /// equal the numbers baked into <c>settings.playback.quality*Sub</c>'s copy; <c>SetupSoundFactsTests</c> pins the
    /// round-trip so the two can never silently drift apart).</summary>
    public static string FormatGb(double gb) => gb.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The drive-letter root of a local path ("C:" for <c>C:\Users\...\Music</c>) — "" for a UNC path, a
    /// relative path, or anything <see cref="Path.GetPathRoot(string)"/> can't resolve, so the caller falls back to
    /// the generic <c>sound.thisDrive</c> ("this drive") copy instead of printing a wrong or empty drive letter.</summary>
    public static string DriveLabel(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        string? root;
        try { root = Path.GetPathRoot(dir); }
        catch { return ""; }
        if (string.IsNullOrEmpty(root)) return "";
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return "";   // UNC share, not a drive letter
        string trimmed = root.TrimEnd('\\', '/');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed : "";
    }

    /// <summary>The Drive-share mode's ACTUAL applied percentage: the stored value 0 means "Auto", which the cache
    /// itself resolves to a byte figure independently (<c>ChunkDiskCache</c>'s own <c>total/10</c> clamp) — for the
    /// stage's plain-English readout, "Auto" reads as 10%. Any explicit stored value clamps to the same 1..90 band the
    /// storage tab's own percent editor enforces (<c>SettingsPage.Storage.cs</c>'s <c>NumberBox</c> Minimum/Maximum).</summary>
    public static int EffectivePercent(int storedPercent) => storedPercent <= 0 ? 10 : Math.Clamp(storedPercent, 1, 90);

    /// <summary>The audio cache budget's share of the drive, 0..1, for the stage's <c>ProgressBar.Determinate</c> —
    /// Fixed = <paramref name="fixedBytes"/> / the drive's total size (0 when the total is unknown — never a wrong
    /// number from a stale drive), Drive share = <see cref="EffectivePercent"/> as a fraction (the drive's total is
    /// irrelevant to a percentage), Unlimited = the full bar (1).</summary>
    public static double CacheShare(AudioCacheBudgetMode mode, long fixedBytes, int percent, long? driveTotalBytes) => mode switch
    {
        AudioCacheBudgetMode.FixedBytes => driveTotalBytes is > 0
            ? Math.Clamp((double)fixedBytes / driveTotalBytes.Value, 0.0, 1.0)
            : 0.0,
        AudioCacheBudgetMode.DriveShare => EffectivePercent(percent) / 100.0,
        AudioCacheBudgetMode.Unlimited => 1.0,
        _ => 0.0,
    };

    /// <summary>A directory shortened for a 200-DIP row sub-label: the drive/UNC root verbatim, then (only once there
    /// are more than three path segments below it) an ellipsis and the last two segments — so a shallow path
    /// ("C:\Wavee\audio", ≤3 segments) prints whole while a deep app-data path collapses to
    /// "C:\…\Cache\audio" instead of overflowing the row.</summary>
    public static string ShortPath(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        string normalized = dir.Replace('/', '\\').TrimEnd('\\');
        if (normalized.Length == 0) return dir;

        string root;
        try { root = Path.GetPathRoot(normalized) ?? ""; }
        catch { return normalized; }

        string rest = normalized.Length >= root.Length ? normalized.Substring(root.Length) : normalized;
        string[] segments = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 3) return normalized;

        string tail = string.Join('\\', segments[^2..]);
        string rootTrimmed = root.TrimEnd('\\');
        return rootTrimmed.Length > 0 ? $"{rootTrimmed}\\…\\{tail}" : $"…\\{tail}";
    }

    /// <summary>The crossfade duration combo's nearest-option index for a persisted second count. Moved verbatim from
    /// the page (was a private <c>SecondsIndex</c>) — the four durations the combo itself offers.</summary>
    public static int SecondsIndex(double seconds)
    {
        double[] options = [2, 5, 8, 12];
        int best = 1;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < options.Length; i++)
        {
            double diff = Math.Abs(options[i] - seconds);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return best;
    }

    /// <summary>The metadata-cache budget combo's index for a persisted byte value, against <see cref="MetaBudgetBytes"/>
    /// — falls back to index 1 (64 MB) for a value that predates/doesn't match the table, exactly like the page's own
    /// <c>PlaybackQuality</c>/<c>MeteredQualityCap</c> clamps default to a safe middle option.</summary>
    public static int MetaBudgetIndex(long bytes)
    {
        int idx = Array.IndexOf(MetaBudgetBytes, bytes);
        return idx >= 0 ? idx : 1;
    }
}
