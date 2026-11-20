// ── Themes/Classic/Structure/Rules/ClassicHintRules.cs — the numbers of Classic's in-app hints (settings rework phase 6, design 7.1) ──────────
//
// Data, not decisions: `Views\Overlays\ClassicHint.cs` reads them (WVTH003 allows literals only here, in `Rules\**`). The strip is the board's
// (C-InContext): 44 tall at least, 12 in before the glyph and 4 after the close glyph, 10 between its parts, the 16 glyph and the 12/16 line.

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's hint strip.</summary>
public static class ClassicHintRules
{
    /// <summary>The strip's least height, its padding and the gap between its parts (C-InContext).</summary>
    public const float StripMinHeight = 44f, StripGap = 10f;

    /// <summary>The strip's padding: 12 before the glyph, 4 after the close glyph, 6 above and below.</summary>
    public static readonly Edges4 StripPadding = new(12f, 6f, 4f, 6f);

    /// <summary>The lightbulb and the line (the lyrics footer's own 12/16 voice).</summary>
    public const float Glyph = 16f, TextSize = 12f, TextLine = 16f;

    /// <summary>An in-place strip's two rows: the gap between them, and the action row's indent onto the line's start (the glyph and its gap).</summary>
    public const float RowGap = 2f;
    public static readonly Edges4 ActionsIndent = new(Glyph + StripGap, 0f, 0f, 0f);

    /// <summary>The lyrics pane's strip: on the lyrics' own gutter (the core insets the slot by the surface's side pad), 12 above the pane's foot
    /// (C-InContext), none above (the footer's line sits there).</summary>
    public static readonly Edges4 LyricsMargin = new(0f, 0f, 0f, 12f);

    /// <summary>The devices flyout's strip under the menu, inset to the rows' plate.</summary>
    public static readonly Edges4 FlyoutMargin = new(4f, 2f, 4f, 4f);

    /// <summary>The bell panel's strip under its header: on the header's start inset and the feed's right.</summary>
    public static readonly Edges4 BellMargin = new(14f, 0f, 8f, 8f);

    /// <summary>The Home banner never grows wider than the runtime banner's lane cell.</summary>
    public const float BannerMaxWidth = ClassicOverlayRules.BannerMaxWidth;

    /// <summary>A strip that floats by a control (the player bar's, the queue's, the page head's) is a reading line, not the window.</summary>
    public const float FloatMaxWidth = 420f;

    /// <summary>A floating strip keeps this far from its surface's edges (the player bar's).</summary>
    public const float FloatInset = 12f;

    /// <summary>Above the player bar a floating strip keeps this far over the bar's top edge (a flyout's own gap): it lies over the page,
    /// never over the bar's rows.</summary>
    public const float FloatGap = 4f;

    /// <summary>In the queue panel it keeps to the panel's content column (the panel's own 14 side padding).</summary>
    public const float QueueInset = 14f;

    /// <summary>A floating strip's ring (the notice card's 1-DIP surface stroke).</summary>
    public const float FloatBorder = 1f;

    /// <summary>The entrance: the strip rises this far as it fades in (the notification card's own rise).</summary>
    public const float EnterDy = ClassicNotificationRules.CardEnterDy;
}
