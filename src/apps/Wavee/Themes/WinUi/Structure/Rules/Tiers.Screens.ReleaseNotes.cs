// ── Themes/WinUi/Structure/Rules/Tiers.Screens.ReleaseNotes.cs — WinUI's release-notes overlays per tier (themes plan 3.6, TC8) ─────────────
//
// The OVERLAYS' record, per WINDOW tier (the highlight viewer and the after-update plate size against the window), with a Short variant of each
// (`ctx.Tier().Height`, 330-679 tall). A view reads `WinUiReleaseNotesTiers.Overlay(ctx.Tier())` and never compares a width with a number (WVTH013).
// At Compact the after-update plate fills the window less 16 with its cards as ROW cards in one column and a stacked footer, its body scrolling; a
// Short window scrolls it too. Every control of the viewer and the plate is >= 40 at Compact. The What's-new PAGE is Classic's (its record is
// `ClassicReleaseNotesTiers`). Huge 4K reads the Huge record.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>What the highlight viewer and the after-update plate are in one WINDOW tier.</summary>
public sealed record WinUiReleaseOverlayLayout
{
    /// <summary>The window tier the record is for.</summary>
    public required WidthTier Tier { get; init; }

    /// <summary>The window is Short (330-679 tall).</summary>
    public required bool Short { get; init; }

    /// <summary>The least hit target of every control (40 at Compact, 0 elsewhere).</summary>
    public required float Target { get; init; }

    /// <summary>The viewer's chrome circles (36; 40 at Compact).</summary>
    public required float ChromeCircle { get; init; }

    /// <summary>The pager row's height (24; 40 at Compact, where each pip carries a 40-wide hit box).</summary>
    public required float PagerRowH { get; init; }

    /// <summary>The window margin the plate keeps on each side (16).</summary>
    public required float PlateMargin { get; init; }

    /// <summary>The plate's side padding (26; 16 at Compact).</summary>
    public required float PlatePadX { get; init; }

    /// <summary>The plate's cards are ROW cards in one column (Compact) instead of one row of poster cards.</summary>
    public required bool CardsColumn { get; init; }

    /// <summary>The plate's footer stacks: the check box over a two-column grid of 40-high buttons (Compact).</summary>
    public required bool FooterStacked { get; init; }

    /// <summary>The plate's body (hero and cards) scrolls above a pinned footer (Compact, and any Short window).</summary>
    public required bool ScrollBody { get; init; }

    /// <summary>The plate's width in a window <paramref name="viewportWidth"/> wide: its 720, never wider than the window less the margins.</summary>
    public float PlateWidth(float viewportWidth)
        => MathF.Min(WinUiReleaseNotesRules.Card.PlateWidth, MathF.Max(1f, viewportWidth - 2f * PlateMargin));

    /// <summary>The plate's tallest in a window <paramref name="viewportHeight"/> tall: its 620 cap, never taller than the window less the margins.</summary>
    public float PlateMaxHeight(float viewportHeight)
        => MathF.Min(WinUiReleaseNotesRules.Card.PlateMaxHeight, MathF.Max(1f, viewportHeight - 2f * PlateMargin));
}

/// <summary>Classic's release-notes records.</summary>
public static class WinUiReleaseNotesTiers
{
    // ── the overlays ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The overlay record for <paramref name="facts"/>: the window tier's, the Short variant when the window is Short. Standard's where no frame resolved the tiers.</summary>
    public static WinUiReleaseOverlayLayout Overlay(in TierFacts facts)
        => facts.Policy is null ? OverlayFor(WidthTier.Standard, short_: false) : OverlayFor(facts.Window, facts.Height == HeightTier.Short);

    /// <summary>The overlay record for window tier <paramref name="window"/> (Huge 4K reads Huge's).</summary>
    public static WinUiReleaseOverlayLayout OverlayFor(WidthTier window, bool short_)
        => s_overlays[Math.Min((int)window, (int)WidthTier.Huge) * 2 + (short_ ? 1 : 0)];

    static WinUiReleaseOverlayLayout MakeOverlay(WidthTier tier, bool short_)
    {
        bool compact = tier == WidthTier.Compact;
        return new WinUiReleaseOverlayLayout
        {
            Tier = tier, Short = short_,
            Target = compact ? 40f : 0f,
            ChromeCircle = compact ? 40f : WinUiReleaseNotesRules.Viewer.ChromeCircle,
            PagerRowH = compact ? 40f : WinUiReleaseNotesRules.PagerRowHeight,
            PlateMargin = 16f,
            PlatePadX = compact ? 16f : WinUiReleaseNotesRules.Card.PlatePadX,
            CardsColumn = compact, FooterStacked = compact, ScrollBody = compact || short_,
        };
    }

    static readonly WinUiReleaseOverlayLayout[] s_overlays =
    [
        MakeOverlay(WidthTier.Compact, false), MakeOverlay(WidthTier.Compact, true),
        MakeOverlay(WidthTier.Small, false), MakeOverlay(WidthTier.Small, true),
        MakeOverlay(WidthTier.Standard, false), MakeOverlay(WidthTier.Standard, true),
        MakeOverlay(WidthTier.Huge, false), MakeOverlay(WidthTier.Huge, true),
    ];
}
