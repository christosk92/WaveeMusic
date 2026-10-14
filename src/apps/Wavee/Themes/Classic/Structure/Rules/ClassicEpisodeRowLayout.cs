// ── Themes/Classic/Structure/Rules/ClassicEpisodeRowLayout.cs — the numbers of Classic's episode row (themes plan 6.1, T8c) ─────
//
// What `Entities/Episode.UI.cs` wrote as literals beside the row (the prototype's .erow), moved verbatim with the row's picture: the
// title rung, the gaps of the copy column and the meta line, the art's radius, the progress rule's corner, the equalizer's height and
// the length of the seed's figure-space runs. They are data, not decisions: `Lists\ClassicEpisodeRow.cs` reads them (WVTH003 allows
// literals only here, in `Rules\**`). The row's outer metrics (`Episode.RowArt`, `RowDisc`, `RowGap`, `RowPadY`, ...) stay public on
// `Episode`: the reader's extents and the show page's skeleton are computed from them.

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's episode row.</summary>
internal static class ClassicEpisodeRowLayout
{
    /// <summary>The title rung: 14 on a 19 line (600, up to two lines).</summary>
    public const float TitleSize = 14f, TitleLine = 19f;

    /// <summary>The gap between the copy column's lines (title, blurb, meta, rule).</summary>
    public const float CopyGap = 3f;

    /// <summary>The gap between the meta line's parts (the numeral, the chips, "new", the date, the length).</summary>
    public const float MetaGap = 10f;

    /// <summary>The art tile's corner radius.</summary>
    public const float ArtRadius = 6f;

    /// <summary>The progress rule's corner radius.</summary>
    public const float RuleCorner = 2f;

    /// <summary>The playing row's equalizer, before the title.</summary>
    public const float EqualizerHeight = 11f;

    /// <summary>The seed's figure-space runs: a derived shimmer draws a bar only for a run that MEASURES.</summary>
    public const int SeedTitleRun = 22, SeedDescriptionRun = 34;
}
