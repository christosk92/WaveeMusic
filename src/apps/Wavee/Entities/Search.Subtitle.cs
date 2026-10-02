// ── Entities/Search.Subtitle.cs ────────────────────────────────────────────────────────────────────────────────────
// The one search-subtitle composer: the kind word, then " · ", then what the target knows. Shared by the search page's
// rows (Search.UI.SubtitleOf switches to it in WP6) and the omnibar flyout's rows.
//
// Role: UI
// Owner: P
// Wave: interaction-consistency (WP7)
// Budget: 30 lines

namespace Wavee;

public readonly partial struct Search
{
    /// <summary>"Song · A, B" / "Album · A" / "Artist": the kind word alone when there is no detail (null, empty or
    /// whitespace), else the word, <see cref="TextJoin.Sep"/>, the detail. Pure.</summary>
    public static string SubtitleText(string kindWord, string? detail)
        => string.IsNullOrWhiteSpace(detail) ? kindWord : kindWord + TextJoin.Sep + detail;
}
