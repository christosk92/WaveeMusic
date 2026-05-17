using System.Globalization;

namespace Wavee.UI.Formatters;

/// <summary>
/// Compact "1.2M / 453K / 812" number formatting used across listener / follower /
/// play-count badges. Centralizes the rounding + decimal-point rules so every
/// surface uses the same threshold. Replaces the four inline reimplementations
/// audited across ArtistViewModel / TrackDetailsViewModel / PlaylistViewModel /
/// PodcastCommentViewModel.
/// </summary>
internal static class NumberFormatter
{
    /// <summary>Monthly listeners — never billions in practice; compact M/K/raw.</summary>
    public static string FormatListenerCount(long count) => FormatCompactCount(count, includeBillions: false);

    /// <summary>Playlist / artist followers — same scale as listeners.</summary>
    public static string FormatFollowerCount(long count) => FormatCompactCount(count, includeBillions: false);

    /// <summary>Track play counts — can overflow into billions for global hits.</summary>
    public static string FormatPlayCount(long count) => FormatCompactCount(count, includeBillions: true);

    /// <summary>
    /// Compact representation. Negative inputs are clamped to 0 (no UI surface
    /// has a meaningful negative count). Returns the bare count under 1000 with
    /// no thousands separator, "12.3K" / "453K" between 1K and 1M, "1.2M" /
    /// "8.5M" between 1M and 1B, and (when <paramref name="includeBillions"/>)
    /// "1.2B" past 1B.
    /// </summary>
    public static string FormatCompactCount(long count, bool includeBillions = false)
    {
        if (count <= 0) return "0";

        if (includeBillions && count >= 1_000_000_000L)
        {
            var b = count / 1_000_000_000d;
            return b >= 10
                ? b.ToString("0", CultureInfo.InvariantCulture) + "B"
                : b.ToString("0.#", CultureInfo.InvariantCulture) + "B";
        }

        if (count >= 1_000_000L)
        {
            var m = count / 1_000_000d;
            return m >= 10
                ? m.ToString("0", CultureInfo.InvariantCulture) + "M"
                : m.ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (count >= 1_000L)
        {
            var k = count / 1_000d;
            return k >= 10
                ? k.ToString("0", CultureInfo.InvariantCulture) + "K"
                : k.ToString("0.#", CultureInfo.InvariantCulture) + "K";
        }

        return count.ToString("0", CultureInfo.InvariantCulture);
    }
}
