using System.Collections.Generic;

namespace Wavee.UI.WinUI.Data.Models;

public sealed class LyricsProviderSettings
{
    /// <summary>
    /// Search strategy: "Sequential" (try in order, first match wins) or "BestMatch" (parallel, highest score wins).
    /// </summary>
    public string SearchStrategy { get; set; } = "Sequential";

    /// <summary>
    /// Default minimum match score (0-100) a provider must meet. Per-provider overrides take precedence.
    /// </summary>
    public int DefaultMatchThreshold { get; set; } = 70;

    /// <summary>
    /// Cached Musixmatch user token (auto-generated, refreshed on expiry).
    /// </summary>
    public string? MusixmatchToken { get; set; }

    /// <summary>
    /// Ordered list of lyrics providers. Order determines priority in Sequential mode.
    /// </summary>
    public List<LyricsProviderEntry> Providers { get; set; } = GetDefaults();

    public static List<LyricsProviderEntry> GetDefaults() =>
    [
        new() { Id = "Musixmatch", IsEnabled = true },
        new() { Id = "QQMusic", IsEnabled = true },
        new() { Id = "Netease", IsEnabled = true },
        new() { Id = "LRCLIB", IsEnabled = true },
        new() { Id = "Spotify", IsEnabled = true },
        new() { Id = "AppleMusic", IsEnabled = true },
        new() { Id = "Kugou", IsEnabled = true },
        new() { Id = "SodaMusic", IsEnabled = false },
    ];
}

public sealed class LyricsProviderEntry
{
    public string Id { get; set; } = "";
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Per-provider match threshold override. Null = use <see cref="LyricsProviderSettings.DefaultMatchThreshold"/>.
    /// </summary>
    public int? MatchThreshold { get; set; }
}
