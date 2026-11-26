// ── Themes/Zune/Structure/Rules/ZuneLocalFilesRules.cs — the numbers of Zune's Local Files empty state (#240 UX S4) ─────────────────────
//
// Data, not decisions (`ZuneLocalFiles.cs` reads them). Zune's empty state is the empty playlist lead's voice (`ZunePlaylistExtras.EmptyLead`): a
// large lowercase heading on the list's inset, the line under it, the verbs as words, then the drop band. The tier's own numbers (the target, the
// heading's size, the top air) are the playlist layout record's (`ZunePlaylistTiers`).

namespace Wavee.Themes.Zune;

/// <summary>The constants of Zune's Local Files empty state.</summary>
internal static class ZuneLocalFilesRules
{
    public const string Key = "local-empty:body", TitleKey = "local-empty:title", AddMusicKey = "local-empty:add-music", ChooseKey = "local-empty:choose",
        CaptionKey = "local-empty:caption", DropKey = "local-empty:drop";

    /// <summary>The line's measure, the gap under the heading, above the verbs (the words' own padding takes the rest), above the caption and
    /// above the drop band.</summary>
    public const float Measure = 560f, LineGap = 4f, VerbsGap = 14f, CaptionGap = 2f, DropGap = 32f, Bottom = 48f;

    /// <summary>A verb word's padding across (its first word stands on the line's inset by pulling back this much), and the gap between the words.</summary>
    public const float WordPad = 8f, WordGap = 8f;

    /// <summary>The drop band: its height, the glyph and the gap between its lines.</summary>
    public const float DropHeight = 120f, DropGlyph = 24f, DropLineGap = 4f, DropPadX = 16f;

    // ── "find music on this pc" (#240, 0dm) ──

    public const string FindKey = "local-find:card", FindTitleKey = "local-find:title", FindRowsKey = "local-find:rows", FindRowKey = "local-find:row:",
        FindCountKey = "local-find:count:", FindAddKey = "local-find:add", FindChooseKey = "local-find:choose";

    /// <summary>The line to the card, the card's words to its rows, between rows, the tick to its count, where a row's lines start (under the tick's
    /// label: its 20 box and 8 gap).</summary>
    public const float FindGap = 14f, FindRowsGap = 10f, FindRowGap = 6f, FindCountGap = 16f, FindPathInset = 28f;

    /// <summary>A tier whose target is taller than this (Compact's touch target) stacks a row's count under its name.</summary>
    public const float FindStackTarget = 36f;
}
