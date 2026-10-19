// ── Themes/WinUi/Style/WinUiControls.cs — the engine controls in WinUI's look (themes plan 6.2, W1; engine EP15) ─────────────────
//
// The definition's control style table (`ThemeDefinition.Controls`). The engine's built-in Fluent defaults ARE the WinUI look, so the table is the
// empty FluentDefault table plus the deltas the boards draw. Each delta restyles one control from its built-in default (`X.FluentStyle() with
// { ... }`); every provider is a static method (the table compares by provider) and every colour a live token read at render, so an accent pick
// or a light/dark flip repaints without a new table.
//
// What the boards draw and the stock control already does (W-Tokens "Controls", W-RowStates, W-Dialog, W-ContextMenu): the accent button 32 r4
// with the on-accent ink, the standard button (control fill, 1-px elevation stroke) and the subtle one, the ToggleButton chip, the 40 x 20 switch
// with its 12 knob, the 20 check box, the radio, the text field with its 2-px accent underline when focused, the slider's 4-px track with the
// 18 thumb and 12 accent core, the InfoBar per severity, the ContentDialog, the acrylic r8 MenuFlyout, the ProgressRing.
//
// THE DELTAS:
//   · MenuFlyout   the rows are 36 apart, edge to edge in the plate (W-Flyouts: "Items 36 high"): the engine's 36-high row plus its 2 + 2 margin is 40
//                  apart, so the margin goes vertically and the Large (touch) row keeps its 40.
//   · SelectorBar  the words rest in Text 2 and the selected one is Text 1 (W-Tokens: "Overview" #fff over its pill, "Albums" #c5c5c5); the items
//                  are 40 tall (the SubPivotRow). The stock bar inks every word Text 1.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;

namespace Wavee.Themes.WinUi;

/// <summary>The control style table of the WinUI style.</summary>
public static class WinUiControls
{
    /// <summary>A SelectorBar item's height (the SubPivotRow, W-Tokens).</summary>
    public const float SelectorItemHeight = 40f;

    /// <summary>The table the WinUI definition carries.</summary>
    public static ControlStyleTable Table { get; } = ControlStyleTable.FluentDefault
        .With(ControlStyles.SelectorBarStyle, Selector)
        .With(ControlStyles.MenuFlyoutStyle, Menu);

    /// <summary>The MenuFlyout: rows 36 apart (no vertical margin), the 4 side inset kept.</summary>
    public static MenuFlyout.Style Menu() => MenuFlyout.FluentStyle() with { ItemMargin = new Edges4(4f, 0f, 4f, 0f) };

    /// <summary>The SelectorBar: Text 2 at rest, Text 1 selected, 40-tall items, the stock pill.</summary>
    public static SelectorBarStyle? Selector() => new()
    {
        RestColor = Tok.TextSecondary,
        SelectedColor = Tok.TextPrimary,
        HoverColor = Tok.TextPrimary,
        ItemHeight = SelectorItemHeight,
    };
}
