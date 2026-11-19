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
//   · MenuFlyout   the rows take the ComboBox item's plate (owner, 2026-11-15, "combo-box spacing"): 32 high on the stock 2 + 2 margin, so 36 apart
//                  with a 4 gap between plates (W-Flyouts draws 36 rows edge to edge); the content padding is the combo item's 11,5,11,7, so a
//                  menu row and a combo row centre their line alike. The Large (touch) row keeps its 40 plate, 44 apart. A radio row marks its
//                  choice as the ComboBox item does (owner, 2026-11-15, over W-Flyouts' dot): the 3 x 16 accent pill on the plate's left edge
//                  over the Subtle Secondary fill (Tertiary under the pointer and the keyboard cursor, Secondary pressed), no check column.
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
        .With(ControlStyles.MenuFlyoutStyle, Menu)
        .With(ControlStyles.PersonPictureStyle, Avatar);

    /// <summary>The least initials size of an imageless avatar: WinUI's type floor (a 24 attribution portrait's 42 % is 10.08).</summary>
    public const float InitialsFloor = 11f;

    static readonly PersonPicture.Style s_avatar = new() { InitialsFloor = InitialsFloor };

    /// <summary>The PersonPicture: the stock avatar, its initials never under <see cref="InitialsFloor"/> (where that fits in half the circle).</summary>
    public static PersonPicture.Style Avatar() => s_avatar;

    /// <summary>The menu row's plate: the ComboBox item's (32 high, content padding 11,5,11,7; ComboBox_themeresources ComboBoxItemThemePadding).</summary>
    public const float MenuRowHeight = 32f;

    /// <summary>The MenuFlyout: the ComboBox item's 32 plate on the stock margin (36 apart, a 4 gap), and its selection (the pill over the subtle fill).</summary>
    public static MenuFlyout.Style Menu() => MenuFlyout.FluentStyle() with
    {
        RowHeight = MenuRowHeight, ItemPadding = new Edges4(11f, 5f, 11f, 7f),
        RadioMark = MenuRadioMark.Pill,
        SelectedFill = Tok.FillSubtleSecondary, SelectedHoverFill = Tok.FillSubtleTertiary, SelectedPressedFill = Tok.FillSubtleSecondary,
    };

    /// <summary>The SelectorBar: Text 2 at rest, Text 1 selected, 40-tall items, the stock pill.</summary>
    public static SelectorBarStyle? Selector() => new()
    {
        RestColor = Tok.TextSecondary,
        SelectedColor = Tok.TextPrimary,
        HoverColor = Tok.TextPrimary,
        ItemHeight = SelectorItemHeight,
    };
}
