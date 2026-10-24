// ── Themes/Zune/Structure/Rules/ZuneSelectionDockRules.cs — the docked selection bar's numbers (P5 Zune fidelity round 2; Z-State-Rows) ────────────
//
// Z-State-Rows: a 60-tall bar docked above the player bar: "3 selected" in the 22 light display voice, the verbs as 32 rings with their lowercase words,
// select all and done at the right end. The frame reserves the bar's height under the page while a selection shows it (an animated reservation: the last
// rows are never covered). Z-State-Rows-Compact: a header row (count, select all, done) over a row of ring columns with the word under each ring.
// Numbers are WVTH003's: the views read this record, never a literal.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The docked selection bar's geometry per window tier.</summary>
public static class ZuneSelectionDockRules
{
    /// <summary>The frame slot's key and the bar's.</summary>
    public const string SlotKey = "zune.selection-dock", BarKey = "zune.selection-dock:bar";

    /// <summary>The ring's disc (32; 36 in the Compact columns) and its glyph stroke.</summary>
    public const float Ring = 32f, ColumnRing = 36f;

    /// <summary>The gap between two verbs, and between the count and the first verb (Z-State-Rows: 22).</summary>
    public const float VerbGap = 22f, CountGap = 12f;

    /// <summary>The Compact column's gap between its ring and its word, and its hit height (the 40 rule: the column is the whole target).</summary>
    public const float ColumnGap = 4f, ColumnHeight = 64f;

    /// <summary>A text command's hit height (select all, done): the 40 target at every tier.</summary>
    public const float TextTarget = 40f;

    /// <summary>The bar's height: 60 (72 at Huge 4K, where the frame's bars are taller); at Compact the header row and the column row.</summary>
    public static float HeightFor(WidthTier tier) => tier switch
    {
        WidthTier.Compact => CompactHeader + CompactColumns,
        WidthTier.Huge4K => 72f,
        _ => 60f,
    };

    /// <summary>The Compact header row and its ring-column row.</summary>
    public const float CompactHeader = 48f, CompactColumns = 80f;

    /// <summary>Does the verb show its word beside its ring? Standard and up; Small keeps the rings (named by tooltips) so the bar fits 640 wide.</summary>
    public static bool Labels(WidthTier tier) => tier >= WidthTier.Standard;

    /// <summary>The bar's reservation under the page: a reflow on the height axis, the slide the title bar and the player slot use.</summary>
    public static readonly LayoutTransition HeightMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing, Axes: SizeAxes.Height);

    /// <summary>The bar's enter and exit while the reservation moves (opacity only).</summary>
    public static readonly LayoutTransition Fade = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(ZuneMotion.GridCrossFadeMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>A disabled verb's opacity.</summary>
    public const float DisabledOpacity = 0.45f;
}
