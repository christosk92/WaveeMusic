// ── Themes/SwiftUI/Style/SwiftUIControls.cs — the engine controls in the SwiftUI spike's look (SPIKE, not for release) ────────────
//
// The definition's control style table: WinUI's table (handed in by the composition) with the controls restyled the way SwiftUI draws them on
// macOS 26 / iOS 26:
//   · Button       `.borderedProminent` = a systemBlue capsule with white ink; `.bordered` / `.glass` = a capsule with a quiet fill (white 12 % /
//                  black 6 %); `.borderless` = words that wash on hover.
//   · Toggle       the switch: a 40 x 24 capsule, the 20 white knob 2 in, systemBlue on (the macOS accent switch; iOS draws systemGreen).
//   · Picker       `.segmented`: a capsule track (white 10 % / black 6 %) with a raised capsule thumb under the selected item.
//   · Slider       a 4-DIP capsule track, a 20 white knob.
//   · Menu         a 16-corner GLASS plate (acrylic + rim + shadow), 5 inset, rows 28 high with 8-corner highlights, 13-DIP labels.
//   · TextField    a rounded 8 field on a quiet fill; the search field (`.searchable`) a capsule.
//   · Alert        a 20-corner plate, Title 3 bold title, capsule buttons side by side filling the row.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;

namespace Wavee.Themes.SwiftUI;

/// <summary>The control style table of the SwiftUI spike style.</summary>
public static class SwiftUIControls
{
    /// <summary>The table: <paramref name="baseTable"/> (WinUI's) with SwiftUI's controls.</summary>
    public static ControlStyleTable From(ControlStyleTable baseTable) => baseTable
        .With(ControlStyles.ButtonStyle, ButtonFor)
        .With(ControlStyles.ToggleButtonStyle, Chip)
        .With(ControlStyles.SegmentedStyle, SegmentedPicker)
        .With(ControlStyles.ToggleSwitchStyle, Switch)
        .With(ControlStyles.SliderStyle, CapsuleSlider)
        .With(ControlStyles.MenuFlyoutStyle, Menu)
        .With(ControlStyles.ContentDialogStyle, Alert)
        .With(ControlStyles.ComboBoxStyle, PopUp)
        .With(ControlStyles.TextBoxFieldStyle, RoundedField)
        .With(ControlStyles.AutoSuggestBoxFieldStyle, SearchField)
        .With(ControlStyles.AutoSuggestBoxSuggestionsPlate, GlassPlate)
        .With(ControlStyles.PopupPlateStyle, GlassPlate)
        .With(ControlStyles.ToolTipStyle, Tip)
        .With(ControlStyles.ProgressBarStyle, CapsuleProgress)
        .With(ControlStyles.SettingsCardStyle, GroupedRow);

    private static bool Dark => Tok.Theme == ThemeKind.Dark;

    private static ColorF Ink(float alpha) => Dark ? ColorF.FromRgba(0xFF, 0xFF, 0xFF) with { A = alpha } : ColorF.FromRgba(0, 0, 0) with { A = alpha };

    /// <summary>A quiet control fill (`.bordered`): white 12 % on dark, black 6 % on light.</summary>
    private static ColorF Quiet => Dark ? Ink(0.12f) : Ink(0.06f);
    private static ColorF QuietHover => Dark ? Ink(0.17f) : Ink(0.09f);
    private static ColorF QuietPressed => Dark ? Ink(0.22f) : Ink(0.12f);

    private static readonly ColorF White = ColorF.FromRgba(0xFF, 0xFF, 0xFF);

    // ── buttons ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Button provider: every button is a capsule (Tahoe's regular and large controls).</summary>
    public static Button.Style? ButtonFor(ButtonAppearance appearance, ControlSize size)
    {
        var b = Button.FluentStyle(appearance, size);
        float capsule = Radii.Full;
        switch (appearance)
        {
            case ButtonAppearance.Accent:
                return b with
                {
                    Background = Tok.AccentDefault, HoverBackground = Tok.AccentSecondary, PressedBackground = Tok.AccentTertiary,
                    DisabledBackground = Tok.AccentDisabled,
                    Foreground = White, HoverForeground = White, PressedForeground = White with { A = 0.85f },
                    DisabledForeground = Tok.TextOnAccentDisabled,
                    BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null, DisabledBorderBrush = null, BorderWidth = 0f,
                    CornerRadius = capsule,
                };
            case ButtonAppearance.Subtle:
                return b with
                {
                    Background = ColorF.Transparent, HoverBackground = Quiet, PressedBackground = QuietHover,
                    BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null, DisabledBorderBrush = null, BorderWidth = 0f,
                    CornerRadius = capsule,
                };
            default:
                return b with
                {
                    Background = Quiet, HoverBackground = QuietHover, PressedBackground = QuietPressed, DisabledBackground = Ink(0.05f),
                    Foreground = Tok.TextPrimary, HoverForeground = Tok.TextPrimary, PressedForeground = Tok.TextSecondary,
                    BorderBrush = null, HoverBorderBrush = null, PressedBorderBrush = null, DisabledBorderBrush = null, BorderWidth = 0f,
                    CornerRadius = capsule,
                };
        }
    }

    /// <summary>A toggle chip: a quiet capsule off, the accent capsule with white ink on.</summary>
    public static ToggleButton.Style Chip() => ToggleButton.FluentStyle() with
    {
        CornerRadius = Radii.Full, BorderWidth = 0f,
        OffBackground = Quiet, OffHover = QuietHover, OffPressed = QuietPressed,
        OffForeground = Tok.TextPrimary, OffPressedForeground = Tok.TextSecondary,
        OnBackground = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary,
        OnForeground = White, OnPressedForeground = White with { A = 0.85f },
        OffBorder = null, OffHoverBorder = null, OffPressedBorder = null, OffDisabledBorder = null,
        OnBorder = null, OnHoverBorder = null, OnPressedBorder = null, OnDisabledBorder = null,
    };

    /// <summary>`.pickerStyle(.segmented)`: a 28 capsule track, 2 in, the selected item a raised capsule (white 24 % on dark, white on light).</summary>
    public static Segmented.Style SegmentedPicker() => Segmented.FluentStyle() with
    {
        Height = 28f, CornerRadius = Radii.Full, ItemCornerRadius = Radii.Full, Padding = new Edges4(2f, 2f, 2f, 2f),
        Background = Dark ? Ink(0.10f) : Ink(0.06f), Border = ColorF.Transparent,
        ItemHover = Ink(0.05f), ItemPressed = Ink(0.08f),
        SelectedBackground = Dark ? Ink(0.24f) : White, SelectedHover = Dark ? Ink(0.28f) : White, SelectedPressed = Dark ? Ink(0.20f) : White,
        SelectedBorder = null, SelectionPill = ColorF.Transparent,
        Foreground = Tok.TextPrimary, SelectedForeground = Tok.TextPrimary, SelectedFontWeight = 600,
        ItemGap = 0f, ItemPadding = new Edges4(12f, 0f, 12f, 0f), FontSize = 13f,
    };

    /// <summary>The switch: a 40 x 24 capsule, a 20 white knob 2 in (travel 16), systemBlue on, a 16 % / 9 % ink track off.</summary>
    public static ToggleSwitch.Style Switch() => ToggleSwitch.FluentStyle() with
    {
        TrackWidth = 40f, TrackHeight = 24f, KnobHostSize = 24f,
        KnobRestSize = 20f, KnobHoverSize = 20f, KnobPressedWidth = 24f, KnobPressedHeight = 20f, KnobTravel = 16f, PressedKnobEdgeMargin = 2f,
        OffFill = Dark ? Ink(0.16f) : Ink(0.09f), OffHover = Dark ? Ink(0.20f) : Ink(0.12f), OffPressed = Dark ? Ink(0.24f) : Ink(0.15f),
        OffBorder = ColorF.Transparent, OffKnob = White,
        OnFill = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary, OnKnob = White,
        KnobStrokeOn = null, KnobStrokeOnWidth = 0f,
        OffBorderDisabled = ColorF.Transparent,
    };

    /// <summary>The slider: a 4 capsule track (white 20 % / black 10 % rail, accent value), a 20 white knob.</summary>
    public static Slider.Style CapsuleSlider() => Slider.FluentStyle() with
    {
        TrackHeight = 4f, TrackCornerRadius = 2f,
        RailFill = Dark ? Ink(0.20f) : Ink(0.10f), RailFillDisabled = Ink(0.06f),
        ValueFill = Tok.AccentDefault, ValueFillPointerOver = Tok.AccentDefault, ValueFillPressed = Tok.AccentDefault,
        ThumbRingDiameter = 20f, InnerThumbDiameter = 20f, ThumbCornerRadius = 10f,
        ThumbRing = White, ThumbBorder = null, ThumbBorderWidth = 0f,
        ThumbFill = White, ThumbFillPointerOver = White, ThumbFillPressed = White,
        InnerRestScale = 1f, InnerHoverScale = 1f, InnerPressScale = 1f, InnerDisabledScale = 1f,
    };

    // ── menus, popups, the alert ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A Liquid Glass popup plate: the glass acrylic over its fallback wash, the specular rim's top tone as the ring, a 16 corner, the
    /// floating shadow, 5 inset.</summary>
    public static PopupPlate GlassPlate() => new()
    {
        Fill = Dark ? ColorF.FromRgba(0x2A, 0x2A, 0x2C, 0xD9) : ColorF.FromRgba(0xF6, 0xF6, 0xF8, 0xD9),
        Acrylic = SwiftUITokens.Glass,
        Border = Dark ? Ink(0.16f) : Ink(0.08f), BorderWidth = 1f,
        Radius = 16f, Shadow = SwiftUITokens.GlassShadow, Padding = new Edges4(5f, 5f, 5f, 5f),
    };

    /// <summary>A menu (`Menu`, the context menu): the glass plate, rows 28 high with 8-corner highlights, 13 labels, a hairline separator.</summary>
    public static MenuFlyout.Style Menu() => MenuFlyout.FluentStyle() with
    {
        Plate = GlassPlate(),
        RowHeight = 28f,
        ItemMargin = new Edges4(0f, 0f, 0f, 0f),
        ItemPadding = new Edges4(10f, 4f, 10f, 4f),
        ItemRadius = 8f,
        ItemHoverFill = Dark ? Ink(0.12f) : Ink(0.07f), ItemPressedFill = Dark ? Ink(0.18f) : Ink(0.11f),
        LabelSize = 13f,
        SeparatorColor = Dark ? Ink(0.12f) : Ink(0.10f), SeparatorThickness = 1f,
        SeparatorMargin = new Edges4(10f, 4f, 10f, 4f),
    };

    /// <summary>The alert: a 20-corner raised plate, no ring, Title 3 bold (15/700... drawn 17 here for the desktop), 13 body, capsule commands
    /// side by side filling the row.</summary>
    public static ContentDialog.Style Alert() => ContentDialog.FluentStyle() with
    {
        PlateFill = Dark ? SwiftUITokens.DarkRaised : SwiftUITokens.LightGround, PlateBorderWidth = 0f, Radius = 20f, Shadow = SwiftUITokens.GlassShadow,
        ContentFill = ColorF.Transparent, CommandFill = ColorF.Transparent,
        TitleSize = 17f, TitleWeight = 700, TitleColor = Tok.TextPrimary, TitleGap = 8f,
        MessageSize = 13f, MessageColor = Tok.TextSecondary,
        SeparatorThickness = 0f,
        ContentPadding = new Edges4(20f, 20f, 20f, 16f), CommandPadding = new Edges4(20f, 0f, 20f, 20f), CommandGap = 8f,
        CommandsFill = true,
    };

    /// <summary>A pop-up button (`Picker` in the menu style): the rounded field and the glass plate.</summary>
    public static ComboBox.Style PopUp() => ComboBox.FluentStyle() with { Field = RoundedField(), DropdownPlate = GlassPlate() };

    /// <summary>A text field: a rounded 8 field on the quiet fill, no ring, no underline; focus draws the accent bar under it (no focus ring
    /// in the engine's field).</summary>
    public static TextFieldStyle RoundedField() => TextFieldStyle.Fluent() with
    {
        Radius = 8f,
        Fill = Dark ? Ink(0.08f) : Ink(0.04f), HoverFill = Dark ? Ink(0.10f) : Ink(0.05f), PressedFill = Dark ? Ink(0.10f) : Ink(0.05f),
        FocusedFill = Dark ? Ink(0.06f) : White, DisabledFill = Ink(0.03f),
        BorderBrush = null, BorderColor = Dark ? Ink(0.10f) : Ink(0.10f), DisabledBorderBrush = null, BorderWidth = 1f,
        UnderlineThickness = 0f, FocusBar = Tok.AccentDefault, FocusBarThickness = 2f,
    };

    /// <summary>The search field (`.searchable` in a toolbar): a capsule on the quiet fill, no ring.</summary>
    public static TextFieldStyle SearchField() => RoundedField() with
    {
        Radius = Radii.Full, BorderWidth = 0f, BorderColor = ColorF.Transparent, FocusBarThickness = 0f,
        Fill = Dark ? Ink(0.10f) : Ink(0.05f), HoverFill = Dark ? Ink(0.13f) : Ink(0.07f), FocusedFill = Dark ? Ink(0.14f) : Ink(0.07f),
    };

    /// <summary>A help tag: the glass plate, an 8 corner, 12 ink.</summary>
    public static ToolTip.Style Tip() => ToolTip.FluentStyle() with
    {
        Fill = GlassPlate().Fill, Acrylic = SwiftUITokens.Glass, BorderWidth = 1f, Border = Dark ? Ink(0.16f) : Ink(0.08f), Radius = 8f,
        Shadow = SwiftUITokens.GlassShadow, TextSize = 12f, TextColor = Tok.TextPrimary,
    };

    /// <summary>A settings row (`Form` with `.formStyle(.grouped)`): the grouped row plate (white 6 % on dark, systemGroupedBackground #F2F2F7 on the white light ground), no ring, a 12 corner,
    /// 13 labels over 11 descriptions. The borrowed WinUI page keeps its 4 gap between rows, so each row reads as its own rounded plate rather than
    /// one inset-grouped section with hairlines (the real thing needs SwiftUI's own settings view).</summary>
    public static SettingsCard.Style GroupedRow() => SettingsCard.FluentStyle() with
    {
        Background = Dark ? Ink(0.06f) : SwiftUITokens.LightGrouped, BackgroundPointerOver = Dark ? Ink(0.09f) : ColorF.FromRgba(0xEC, 0xEC, 0xF1),
        BackgroundPressed = Dark ? Ink(0.11f) : ColorF.FromRgba(0xE6, 0xE6, 0xEB),
        Border = ColorF.Transparent, BorderWidth = 0f, BorderPressed = ColorF.Transparent, BorderPointerOver = null,
        CornerRadius = 12f, HeaderFontSize = 13f, DescriptionFontSize = 11f,
    };

    /// <summary>The progress bar: a 4 capsule.</summary>
    public static ProgressBar.Style CapsuleProgress() => ProgressBar.FluentStyle() with
    {
        TrackHeight = 4f, IndicatorRadius = 2f, TrackRadius = 2f, TrackFill = Dark ? Ink(0.16f) : Ink(0.08f), Foreground = Tok.AccentDefault,
    };
}
