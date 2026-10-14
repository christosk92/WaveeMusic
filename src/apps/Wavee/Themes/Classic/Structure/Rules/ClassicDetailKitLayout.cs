// ── Themes/Classic/Structure/Rules/ClassicDetailKitLayout.cs — the numbers of Classic's shared detail kit (themes plan 6.1, T9b) ──────────────────
//
// What `Entities\Detail.UI.cs` held as literals in the billed-artist line box (`RailArtistLineBox`) and the notice strip (`NoticeBar`), moved with those
// pictures into `Views\Detail\ClassicDetailKit.cs` and `ClassicDetailSeam.cs`. They are data, not decisions (WVTH003 allows literals only in `Rules\**`).
// The line's DECISIONS (`Detail.RailArtistLine`: the width ladder with its hysteresis, the reserved heights, the "+N" arithmetic) stay core and pinned.

namespace Wavee.Themes.Classic;

/// <summary>The constants of the shared detail kit's pictures.</summary>
public static class ClassicDetailKitLayout
{
    // ── the billed-artist line ──

    /// <summary>The names: size (the line box's own <c>RailArtistLine.TextLine</c> is the line height).</summary>
    public const float ArtistNamesSize = 14f;

    /// <summary>The Medium "+N": size and line height.</summary>
    public const float ArtistPlusSize = 12f, ArtistPlusLine = 16f;

    /// <summary>The trigger's side padding (its vertical padding is the rule's <c>RailArtistLine.PadY</c>).</summary>
    public const float ArtistTriggerPadX = 6f;

    // ── the notice strip ──

    /// <summary>The strip's padding: left and right, top, bottom.</summary>
    public const float NoticePadX = 16f, NoticePadTop = 8f, NoticePadBottom = 4f;
}
