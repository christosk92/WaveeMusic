using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Lyrics;

/// <summary>
/// JSON serialization context for lyrics types (AOT compatible).
/// </summary>
[JsonSerializable(typeof(LyricsResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class LyricsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Response from Spotify's color-lyrics API endpoint.
/// </summary>
public sealed record LyricsResponse
{
    /// <summary>
    /// Lyrics data including lines and sync type.
    /// </summary>
    public LyricsData? Lyrics { get; init; }

    /// <summary>
    /// Color scheme for lyrics display.
    /// </summary>
    public LyricsColors? Colors { get; init; }

    /// <summary>
    /// Whether vocal removal is available for this track.
    /// </summary>
    public bool HasVocalRemoval { get; init; }
}

/// <summary>
/// Lyrics data with timing information.
/// </summary>
public sealed record LyricsData
{
    /// <summary>
    /// Sync type: "LINE_SYNCED" for timed lyrics, "UNSYNCED" for static lyrics.
    /// </summary>
    public string SyncType { get; init; } = "";

    /// <summary>
    /// Timed lyrics lines.
    /// </summary>
    public IReadOnlyList<LyricsLine> Lines { get; init; } = [];

    /// <summary>
    /// Lyrics provider (e.g., "MusixMatch").
    /// </summary>
    public string Provider { get; init; } = "";

    /// <summary>
    /// Provider display name.
    /// </summary>
    public string ProviderDisplayName { get; init; } = "";

    /// <summary>
    /// Language code (e.g., "en", "ko").
    /// </summary>
    public string Language { get; init; } = "";

    /// <summary>
    /// Whether the language is right-to-left.
    /// </summary>
    public bool IsRtlLanguage { get; init; }

    /// <summary>
    /// Whether dense typeface should be used (for CJK languages).
    /// </summary>
    public bool IsDenseTypeface { get; init; }

    /// <summary>
    /// Whether lyrics are synced (LINE_SYNCED).
    /// </summary>
    public bool IsSynced => SyncType == "LINE_SYNCED";
}

/// <summary>
/// A single lyrics line with timing.
/// </summary>
public sealed record LyricsLine
{
    /// <summary>
    /// Start time in milliseconds (as string from API).
    /// </summary>
    public string StartTimeMs { get; init; } = "0";

    /// <summary>
    /// End time in milliseconds (often "0" for line-synced lyrics).
    /// </summary>
    public string EndTimeMs { get; init; } = "0";

    /// <summary>
    /// Lyrics text for this line.
    /// </summary>
    public string Words { get; init; } = "";

    /// <summary>
    /// Start time as long for easy comparison.
    /// </summary>
    public long StartTimeMilliseconds => long.TryParse(StartTimeMs, out var ms) ? ms : 0;

    /// <summary>
    /// Whether this line is empty/instrumental.
    /// </summary>
    public bool IsInstrumental => string.IsNullOrWhiteSpace(Words) || Words == "♪";
}

/// <summary>
/// Color scheme for lyrics display.
/// </summary>
public sealed record LyricsColors
{
    /// <summary>
    /// Background color (as signed int from API).
    /// </summary>
    public int Background { get; init; }

    /// <summary>
    /// Text color.
    /// </summary>
    public int Text { get; init; }

    /// <summary>
    /// Highlighted text color.
    /// </summary>
    public int HighlightText { get; init; }
}
