// ── Themes/Zune/Style/ZuneControls.cs — the engine controls in Zune's look (themes plan 6.3, Z1; engine EP15 / ZE keys) ────────
//
// The definition's control style table (`ThemeDefinition.Controls`, docs\design\subsystems\theming.md §16.2). Each entry restyles one
// engine control from its built-in Fluent default (`X.FluentStyle() with { ... }`), so a member Zune does not mention keeps the engine's
// mechanics. Every provider is a static method: the table compares by provider, so an unchanged table stays equal across rebuilds, and
// every colour is a live token read at render (`Tok.*`), so an accent pick or a light/dark flip repaints without a new table.
//
// THE LOOK (Z-Tokens, Z-Overlays, Z-Dialogs, Z-Part-TitleBar, Z-Part-PlayBar, Z-Theme-Options):
//   · buttons     Accent is the round metro disc: an accent fill with the on-accent ink (black on dark, white on light), fully round, 36
//                 (Z-Tokens, Z-Dialogs; Small 32 is the track menu's verb strip, Large 40 is the transport and options-page play disc:
//                 the board wins per surface). Outline is the 2-DIP ring of the same size. Standard and Subtle are WORDS: no plate, no
//                 ring, square, the 5 % wash on hover. The disc's lowercase label beside it is the structure's composition, not a style.
//   · chips       ToggleButton / Segmented: flat rectangles, 6 % ink at rest, the accent fill with the on-accent ink when on, 32 tall.
//   · switch      40 x 20, a 16 knob 2 in from the edge, the accent fill on, 14 % ink off with a white knob.
//   · slider      a 2-DIP line: 12 % ink rail, accent value; the 12 accent thumb shows only while the pointer is over the control.
//   · check box   an 18 square: accent fill and on-accent check when on, a tertiary ring when off.
//   · menus       square #242424 plates (white on light) with no ring, the flyout shadow, 36 rows inset 16, an 8 % hairline 6 above and below.
//   · dialog      a square 548 plate (#1f1f1f on dark), 24 in, a 26/300 title, commands as words with the primary as the accent disc.
//   · fields      a text box is a rule (1 DIP at 18 % ink, the ink when focused); the search box is the one rounded field: the 16-corner pill
//                 with an 18 % ring.
//   · status      tool tips and info bars square; the progress ring with flat caps; the progress bar a 2-DIP line.
//   · pivots      every SelectorBar is the sub-pivot row (14/20 words, 400 / 600, no pill): `R.Type.FacetCompactStyle`.
//   · toasts      the lane's cards are 360 wide (Z-Overlays).
//
// What the table cannot say yet (engine gaps, reported): the switch's track and knob are always round (`ToggleSwitch` has no corner knob,
// so the 40 x 20 RECTANGLE is a rounded 40 x 20); the focused search pill's white ring (`TextFieldStyle` has no focused border colour); the
// focus ring's 4 corner (the engine draws it concentric with the control). The context menu's header and round-verb strip are presenter parts drawn by
// the overlays lane (`ZuneContextMenu`, Z8a): the track menu entry hands them to the engine.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;

namespace Wavee.Themes.Zune;

/// <summary>The control style table of the Zune style.</summary>
public static class ZuneControls
{
    /// <summary>A fully round corner (clamped to half the box at record time).</summary>
    public const float Round = 999f;

    /// <summary>The disc of each control size: Small 32 (the track menu's verb strip), Medium 36 (Z-Tokens' round metro button, the dialogs),
    /// Large 40 (the transport and the options page's play disc).</summary>
    public static float Disc(ControlSize size) => size switch
    {
        ControlSize.Small => 32f,
        ControlSize.Large => 40f,
        _ => 36f,
    };

    /// <summary>The table the Zune definition carries.</summary>
    public static ControlStyleTable Table { get; } = ControlStyleTable.Empty
        .With(ControlStyles.ButtonStyle, ButtonFor)
        .With(ControlStyles.ToggleButtonStyle, Chip)
        .With(ControlStyles.SegmentedStyle, ChipRow)
        .With(ControlStyles.ToggleSwitchStyle, Switch)
        .With(ControlStyles.SliderStyle, LineSlider)
        .With(ControlStyles.CheckBoxStyle, SquareBox)
        .With(ControlStyles.MenuFlyoutStyle, Menu)
        .With(ControlStyles.ContextMenuStyle, TrackMenu)
        .With(ControlStyles.ContentDialogStyle, Dialog)
        .With(ControlStyles.ComboBoxStyle, Combo)
        .With(ControlStyles.TextBoxFieldStyle, RuledField)
        .With(ControlStyles.AutoSuggestBoxFieldStyle, PillField)
        .With(ControlStyles.InfoBarStyle, Bar)
        .With(ControlStyles.ToolTipStyle, Tip)
        .With(ControlStyles.ProgressRingStyle, Ring)
        .With(ControlStyles.ProgressBarStyle, LineProgress)
        .With(ControlStyles.SelectorBarStyle, SubPivots)
        .With(ControlStyles.ToastLaneStyle, Toasts);

    // ── the live inks every provider shares ─────────────────────────────────────────────────────────────────────────────────

    private static ColorF Ink(float alpha) => Tok.TextPrimary with { A = alpha };

    // ── buttons ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Button provider: the accent disc, the outline ring, and the words.</summary>
    public static Button.Style? ButtonFor(ButtonAppearance appearance, ControlSize size)
    {
        var b = Button.FluentStyle(appearance, size);
        float disc = Disc(size);
        // An icon-only disc is exactly `disc` wide: the side padding centres the size's icon.
        float side = MathF.Max(0f, (disc - ControlMetrics.For(size).IconSize) * 0.5f);
        switch (appearance)
        {
            case ButtonAppearance.Accent:
                return b with
                {
                    Background = Tok.AccentDefault, HoverBackground = Tok.AccentSecondary, PressedBackground = Tok.AccentTertiary,
                    DisabledBackground = Tok.AccentDisabled,
                    Foreground = Tok.TextOnAccentPrimary, HoverForeground = Tok.TextOnAccentPrimary,
                    PressedForeground = Tok.TextOnAccentSecondary, DisabledForeground = Tok.TextOnAccentDisabled,
                    BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null, DisabledBorderBrush = null, BorderWidth = 0f,
                    CornerRadius = Round, MinHeight = disc, Padding = new Edges4(side, 0f, side, 0f),
                };
            case ButtonAppearance.Outline:
                return b with
                {
                    Background = ColorF.Transparent, HoverBackground = Ink(ZuneTokens.HoverA), PressedBackground = Ink(ZuneTokens.PressedA),
                    DisabledBackground = ColorF.Transparent,
                    Foreground = Tok.TextPrimary, HoverForeground = Tok.TextPrimary, PressedForeground = Tok.TextSecondary,
                    DisabledForeground = Tok.TextTertiary,
                    BorderBrush = GradientSpec.Solid(Tok.TextPrimary), HoverBorderBrush = GradientSpec.Solid(Tok.TextPrimary),
                    PressedBorderBrush = GradientSpec.Solid(Tok.TextSecondary), DisabledBorderBrush = GradientSpec.Solid(Tok.TextTertiary),
                    BorderWidth = 2f, CornerRadius = Round, MinHeight = disc, Padding = new Edges4(side, 0f, side, 0f),
                };
            default:
                // Standard and Subtle: a word. Zune writes its secondary commands as words (Z-Dialogs "not now", Z-Overlays "mark all read").
                return b with
                {
                    Background = ColorF.Transparent, HoverBackground = Ink(ZuneTokens.HoverA), PressedBackground = Ink(ZuneTokens.PressedA),
                    DisabledBackground = ColorF.Transparent,
                    Foreground = Tok.TextPrimary, HoverForeground = Tok.TextPrimary, PressedForeground = Tok.TextSecondary,
                    DisabledForeground = Tok.TextDisabled,
                    BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null, DisabledBorderBrush = null, BorderWidth = 0f,
                    CornerRadius = 0f, Padding = new Edges4(8f, 0f, 8f, 0f),
                };
        }
    }

    // ── chips, the switch, the slider, the check box ────────────────────────────────────────────────────────────────────────

    /// <summary>A chip: a flat rectangle, 6 % ink at rest, the accent fill with the on-accent ink when on (Z-Tokens).</summary>
    public static ToggleButton.Style Chip() => ToggleButton.FluentStyle() with
    {
        CornerRadius = 0f, Padding = new Edges4(12f, 0f, 12f, 0f), MinHeight = 32f, BorderWidth = 0f,
        OffBackground = Ink(ZuneTokens.ChipA), OffHover = Ink(ZuneTokens.ChipA + ZuneTokens.HoverA), OffPressed = Ink(ZuneTokens.HoverA),
        OffForeground = Tok.TextPrimary, OffPressedForeground = Tok.TextSecondary,
        OnBackground = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary,
        OnForeground = Tok.TextOnAccentPrimary, OnPressedForeground = Tok.TextOnAccentSecondary,
        OffBorder = null, OffHoverBorder = null, OffPressedBorder = null, OffDisabledBorder = null,
        OnBorder = null, OnHoverBorder = null, OnPressedBorder = null, OnDisabledBorder = null,
    };

    /// <summary>A row of chips: no track, square items 8 apart, the selected one the accent fill (Z-Theme-Options).</summary>
    public static Segmented.Style ChipRow() => Segmented.FluentStyle() with
    {
        Height = 32f, CornerRadius = 0f, ItemCornerRadius = 0f, Padding = new Edges4(0f, 0f, 0f, 0f),
        Background = ColorF.Transparent, Border = ColorF.Transparent,
        ItemHover = Ink(ZuneTokens.HoverA), ItemPressed = Ink(ZuneTokens.PressedA),
        SelectedBackground = Tok.AccentDefault, SelectedHover = Tok.AccentSecondary, SelectedPressed = Tok.AccentTertiary,
        SelectedBorder = null, SelectionPill = Tok.AccentDefault,
        Foreground = Tok.TextPrimary, SelectedForeground = Tok.TextOnAccentPrimary,
        ItemGap = 8f, ItemPadding = new Edges4(12f, 0f, 12f, 0f),
    };

    /// <summary>The switch: 40 x 20 with a 16 knob 2 in, the accent fill and a black knob on, 14 % ink with a white knob off (Z-Tokens).</summary>
    public static ToggleSwitch.Style Switch() => ToggleSwitch.FluentStyle() with
    {
        TrackWidth = 40f, TrackHeight = 20f, KnobHostSize = 20f,
        KnobRestSize = 16f, KnobHoverSize = 16f, KnobPressedWidth = 16f, KnobPressedHeight = 16f, KnobTravel = 20f, PressedKnobEdgeMargin = 2f,
        OffFill = Ink(0.14f), OffHover = Ink(0.20f), OffPressed = Ink(0.10f), OffBorder = ColorF.Transparent, OffKnob = Tok.TextPrimary,
        OnFill = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary, OnKnob = Tok.TextOnAccentPrimary,
        KnobStrokeOn = null, KnobStrokeOnWidth = 0f,
        OffBorderDisabled = ColorF.Transparent,
    };

    /// <summary>The slider: a 2-DIP line (12 % ink rail, accent value) whose 12 accent thumb appears only while the pointer is over the control:
    /// the thumb's ring is clear and its dot is clear at rest and the accent on hover and press (the dot's hover follows the whole control).</summary>
    public static Slider.Style LineSlider() => Slider.FluentStyle() with
    {
        TrackHeight = 2f, TrackCornerRadius = 0f,
        RailFill = Ink(ZuneTokens.TrackA), RailFillDisabled = Ink(ZuneTokens.ChipA),
        ValueFill = Tok.AccentDefault, ValueFillPointerOver = Tok.AccentDefault, ValueFillPressed = Tok.AccentDefault,
        ThumbRingDiameter = 12f, InnerThumbDiameter = 12f, ThumbCornerRadius = 6f,
        ThumbRing = ColorF.Transparent, ThumbBorder = null, ThumbBorderWidth = 0f,
        ThumbFill = ColorF.Transparent, ThumbFillPointerOver = Tok.AccentDefault, ThumbFillPressed = Tok.AccentDefault,
        ThumbFillDisabled = ColorF.Transparent,
        InnerRestScale = 1f, InnerHoverScale = 1f, InnerPressScale = 1f, InnerDisabledScale = 1f,
    };

    /// <summary>The check box: an 18 square, the accent fill and the on-accent check when on, a tertiary ring when off (Z-Dialogs).</summary>
    public static CheckBox.Style SquareBox() => CheckBox.FluentStyle() with
    {
        BoxSize = 18f, GlyphSize = 12f,
        OffFill = StateBrush.Flat(ColorF.Transparent),
        OffStroke = new StateBrush(Tok.TextTertiary, Tok.TextSecondary, Tok.TextSecondary, Tok.TextDisabled),
        OnFill = new StateBrush(Tok.AccentDefault, Tok.AccentSecondary, Tok.AccentTertiary, Tok.AccentDisabled),
        OnStroke = new StateBrush(Tok.AccentDefault, Tok.AccentSecondary, Tok.AccentTertiary, Tok.AccentDisabled),
        GlyphFg = StateBrush.Flat(Tok.TextOnAccentPrimary) with { Disabled = Tok.TextOnAccentDisabled },
    };

    // ── menus and the dialog ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The menu plate: the raised ground, square, no frost, the flyout shadow; the ring is the flyout stroke token (clear on dark,
    /// Z-Light-PlayBar's 6 % hairline on light); 8 above and below the rows (Z-Overlays).</summary>
    public static PopupPlate MenuPlate() => new()
    {
        Fill = Tok.FillSolidTertiary, Acrylic = null, Border = Tok.StrokeFlyoutDefault, BorderWidth = 1f,
        Radius = 0f, Shadow = Elevation.Flyout, Padding = new Edges4(0f, 8f, 0f, 8f),
    };

    /// <summary>A menu: the square plate, 36 rows inset 16 with no row margin and square hover, an 8 % hairline with 6 above and below.</summary>
    public static MenuFlyout.Style Menu() => MenuFlyout.FluentStyle() with
    {
        Plate = MenuPlate(),
        RowHeight = 36f,
        ItemMargin = new Edges4(0f, 0f, 0f, 0f),
        ItemPadding = new Edges4(16f, 8f, 16f, 9f),
        ItemRadius = 0f,
        ItemHoverFill = Ink(ZuneTokens.HoverA), ItemPressedFill = Ink(ZuneTokens.PressedA),
        LabelSize = 14f, ShowIcons = false, LabelCase = TextCase.Lower,   // Z-Overlays: a menu is lowercase words (a name, `MenuFlyoutItem.Literal`, keeps its case)
        SeparatorColor = Tok.StrokeDividerDefault, SeparatorThickness = 1f,
        SeparatorMargin = new Edges4(16f, 5f, 16f, 5f),
    };

    /// <summary>The track menu: 320 wide with no divider under its header (Z-Overlays); the header (a 40 tile, the title over the subtitle) and the strip of round verbs (the
    /// play disc and rings) are the overlays lane's presenters (`ZuneContextMenu`).</summary>
    public static ContextMenu.Style TrackMenu() => ContextMenu.FluentStyle() with
    {
        HeaderDivider = false, MinWidth = 320f, Header = ZuneContextMenu.Header, PrimaryStrip = ZuneContextMenu.Strip,
    };

    /// <summary>The dialog: a square plate on the dialog ground (#1f1f1f on dark), no ring, the dialog shadow, 24 in, a 26/300 title 14 above
    /// the body, the commands as words 20 apart, starting at the leading edge, the primary first (it is the accent disc).</summary>
    public static ContentDialog.Style Dialog() => ContentDialog.FluentStyle() with
    {
        PlateFill = Tok.FillLayerAlt, PlateBorderWidth = 0f, Radius = 0f, Shadow = Elevation.Dialog,
        MaxWidth = 548f,
        ContentPadding = new Edges4(24f, 24f, 24f, 24f), ContentFill = Tok.FillLayerAlt,
        TitleSize = 26f, TitleWeight = 300, TitleColor = Tok.TextPrimary, TitleGap = 14f, TitleCase = TextCase.Lower, CommandCase = TextCase.Lower,   // Z-Dialogs: "report a problem" (a title with a name sets ContentDialog.TitleLiteral)
        MessageSize = 14f, MessageColor = Tok.TextPrimary,
        SeparatorThickness = 0f,
        CommandPadding = new Edges4(24f, 4f, 24f, 24f), CommandFill = Tok.FillLayerAlt, CommandGap = 20f,
        CommandJustify = FlexJustify.Start, CommandsFill = false, LoneCommandTrailing = false,
    };

    // ── fields ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A text field is a rule: no plate, no ring, a 1-DIP line at 18 % ink that turns to the ink while focused (Z-Dialogs).</summary>
    public static TextFieldStyle RuledField() => TextFieldStyle.Fluent() with
    {
        Radius = 0f,
        Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
        FocusedFill = ColorF.Transparent, DisabledFill = ColorF.Transparent,
        BorderBrush = null, BorderColor = ColorF.Transparent, DisabledBorderBrush = null, BorderWidth = 0f,
        UnderlineThickness = 1f, Underline = Ink(ZuneTokens.FieldRuleA),
        FocusBar = Tok.TextPrimary, FocusBarThickness = 1f,
    };

    /// <summary>The search box: the one rounded field, a 16-corner pill with an 18 % ring and no plate (Z-Part-TitleBar).</summary>
    public static TextFieldStyle PillField() => TextFieldStyle.Fluent() with
    {
        Radius = 16f,
        Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
        FocusedFill = ColorF.Transparent, DisabledFill = ColorF.Transparent,
        BorderBrush = null, BorderColor = Ink(ZuneTokens.FieldRuleA), DisabledBorderBrush = null, BorderWidth = 1f,
        UnderlineThickness = 0f, FocusBarThickness = 0f,
    };

    /// <summary>A combo box: the ruled field and the menu plate.</summary>
    public static ComboBox.Style Combo() => ComboBox.FluentStyle() with { Field = RuledField(), DropdownPlate = MenuPlate() };

    // ── status ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An info bar: square, no ring, the title at 400 (the severity tint stays the engine's).</summary>
    public static InfoBar.Style Bar() => InfoBar.FluentStyle() with { Radius = 0f, BorderWidth = 0f, TitleWeight = 400, CloseButtonRadius = 0f };

    /// <summary>A tool tip: the raised plate, square, no frost and no ring, the tool-tip shadow.</summary>
    public static ToolTip.Style Tip() => ToolTip.FluentStyle() with
    {
        Fill = Tok.FillSolidTertiary, Acrylic = null, BorderWidth = 0f, Radius = 0f, Shadow = Elevation.Tooltip,
        TextSize = 12f, TextColor = Tok.TextPrimary,
    };

    /// <summary>The progress ring: the accent arc with flat caps on no track.</summary>
    public static ProgressRing.Style Ring() => ProgressRing.FluentStyle() with
    {
        Foreground = Tok.AccentDefault, Track = ColorF.Transparent, RoundCaps = false,
    };

    /// <summary>The progress bar: a 2-DIP square line on a 12 % ink track (the play bar's seek line, Z-Part-PlayBar).</summary>
    public static ProgressBar.Style LineProgress() => ProgressBar.FluentStyle() with
    {
        TrackHeight = 2f, IndicatorRadius = 0f, TrackRadius = 0f, TrackFill = Ink(ZuneTokens.TrackA), Foreground = Tok.AccentDefault,
    };

    // ── pivots and toasts ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every SelectorBar is the band's second row: the sub-pivot words of the active roles (<c>R.Type.FacetCompactStyle</c>).</summary>
    public static SelectorBarStyle? SubPivots() => R.Type.FacetCompactStyle;

    /// <summary>The toast lane: cards 360 wide (Z-Overlays).</summary>
    public static ToastLaneStyle Toasts() => ToastLaneStyle.Default with { CardMinWidth = 360f, CardMaxWidth = 360f };
}
