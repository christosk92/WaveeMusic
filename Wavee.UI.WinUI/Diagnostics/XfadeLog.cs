namespace Wavee.UI.WinUI.Diagnostics;

/// <summary>
/// Helpers for the temporary <c>[xfade]</c> diagnostic stream that traces
/// AlbumPage / PlaylistPage / ProfilePage / ArtistPage shimmer→content fade
/// state transitions. Gated by <c>VerboseLoggingEnabled</c> at the Serilog
/// level switch, so all call sites use <c>LogDebug</c> and cost nothing at
/// Information level.
/// </summary>
internal static class XfadeLog
{
    /// <summary>
    /// Last-5-char tag for a Spotify URI so log lines stay grep-able without
    /// dragging the full URI into every event. Returns "-" for null/empty.
    /// </summary>
    public static string Tag(string? id)
        => string.IsNullOrEmpty(id) ? "-" : id.Length <= 5 ? id : id[^5..];
}
