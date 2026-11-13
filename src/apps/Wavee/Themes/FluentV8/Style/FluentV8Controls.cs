// ── Themes/FluentV8/Style/FluentV8Controls.cs — the engine controls in Fluent UI React v8's look (SPIKE) ───────────────────────────────
//
// The deltas from the stock (WinUI 3) controls, each read off the v8 component styles:
//   · Button       DefaultButton: white, 1-px neutralSecondaryAlt border, hover neutralLighter, pressed neutralLight, semibold 14, 32 tall,
//                  padding 0 16, roundedCorner2. PrimaryButton: themePrimary, hover themeDarkAlt, pressed themeDark, no border. Subtle
//                  (CommandBarButton / ActionButton): transparent, hover neutralLighter, pressed neutralLight. No elevation-gradient strokes.
//   · ContextualMenu  a solid white plate (no acrylic, no ring), depth8, roundedCorner2, rows 36 edge to edge, hover neutralLighter with no
//                  rounded backplate.
//   · Pivot        (SelectorBar) neutralPrimary words, the hover fill neutralLighter, 44-tall links.
//   · TextField / SearchBox  white, 1-px neutralSecondary ring (inputBorder), roundedCorner2, the 2-px themePrimary focus bar.
//   · Callout      the popup plate: solid, depth8, roundedCorner2, no ring.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;

namespace Wavee.Themes.FluentV8;

/// <summary>The control style table of the FluentV8 style.</summary>
public static class FluentV8Controls
{
    /// <summary>The table the FluentV8 definition carries.</summary>
    public static ControlStyleTable Table { get; } = ControlStyleTable.FluentDefault
        .With(ControlStyles.ButtonStyle, ButtonFor)
        .With(ControlStyles.MenuFlyoutStyle, Menu)
        .With(ControlStyles.SelectorBarStyle, Pivot)
        .With(ControlStyles.TextBoxFieldStyle, Field)
        .With(ControlStyles.AutoSuggestBoxFieldStyle, Field)
        .With(ControlStyles.PopupPlateStyle, CalloutPlate)
        .With(ControlStyles.AutoSuggestBoxSuggestionsPlate, CalloutPlate)
        .With(ControlStyles.PersonPictureStyle, Persona)
        .With(ControlStyles.SliderStyle, V8Slider)
        .With(ControlStyles.ContextMenuStyle, ContextualMenu);

    /// <summary>The ContextualMenu: the model's primary verbs become plain v8 menu items (a themePrimary 16 icon, the 14 label, a check column for
    /// a checked toggle) inside the solid callout plate, instead of the Windows 11 Explorer command-bar strip on acrylic.</summary>
    public static ContextMenu.Style ContextualMenu() => new() { PrimaryStrip = VerbRows, MinWidth = 260f };

    private static Element VerbRows(ContextMenuStrip strip)
    {
        var rows = new List<Element>(strip.Commands.Count + 1);
        bool anyCheck = false;
        foreach (var c in strip.Commands) anyCheck |= c.Kind == AppBarCommandKind.ToggleButton;
        for (int i = 0; i < strip.Commands.Count; i++)
        {
            var c = strip.Commands[i];
            if (c.Kind == AppBarCommandKind.Separator) continue;
            int at = i;
            bool on = c.Kind == AppBarCommandKind.ToggleButton && c.IsChecked;
            var icon = on && c.CheckedIcon is { } filled ? filled : c.Icon;
            var kids = new List<Element>(3);
            if (anyCheck)
                kids.Add(new BoxEl
                {
                    Width = 16f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = on ? [Ui.Icon(Icons.Accept, 12f, Tok.TextPrimary)] : [],
                });
            kids.Add(new BoxEl
            {
                Width = 16f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = icon.Glyph is { Length: > 0 } g ? [Ui.Icon(g, 16f, Tok.AccentTextPrimary, icon.Font)] : [],
            });
            kids.Add(new TextEl(c.Label)
            {
                Size = 14f, LineHeight = 20f, Color = c.Enabled ? Tok.TextPrimary : Tok.TextDisabled,
                MinWidth = 0f, Shrink = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
            rows.Add(new BoxEl
            {
                Key = "fluentv8.menu.verb:" + i, Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Height = 36f,
                Padding = new Edges4(12f, 0f, 12f, 0f), MinWidth = 0f,
                Fill = ColorF.Transparent, HoverFill = FluentV8Palette.Hovered(Tok.Theme), PressedFill = FluentV8Palette.Checked(Tok.Theme),
                Role = AutomationRole.MenuItem, Cursor = CursorId.Hand, OnClick = () => strip.Invoke(at),
                Children = kids.ToArray(),
            });
        }
        rows.Add(new BoxEl { Height = 1f, Shrink = 0f, Margin = new Edges4(0f, 4f, 0f, 4f), Fill = Tok.StrokeDividerDefault });
        return new BoxEl { Key = "fluentv8.menu.verbs", Direction = 1, MinWidth = 0f, Children = rows.ToArray() };
    }

    /// <summary>The v8 Slider: a 4-px line (inactive neutralTertiaryAlt, active neutralSecondary, themePrimary under the pointer) and a 16 white
    /// thumb with a 2-px neutralSecondary ring that never scales.</summary>
    public static Slider.Style V8Slider()
    {
        var k = Tok.Theme;
        bool light = k == ThemeKind.Light;
        var ring = light ? FluentV8Palette.NeutralSecondary : FluentV8Palette.DarkNeutralSecondary;
        return Slider.FluentStyle() with
        {
            TrackHeight = 4f, TrackCornerRadius = 2f,
            RailFill = light ? FluentV8Palette.NeutralTertiaryAlt : FluentV8Palette.DarkNeutralTertiaryAlt,
            ValueFill = ring, ValueFillPointerOver = FluentV8Palette.Primary(k), ValueFillPressed = FluentV8Palette.PrimaryPressed(k),
            ThumbRingDiameter = 16f, InnerThumbDiameter = 12f, ThumbCornerRadius = 8f,
            ThumbRing = ring, ThumbFill = FluentV8Palette.Body(k), ThumbFillPointerOver = FluentV8Palette.Body(k), ThumbFillPressed = FluentV8Palette.Body(k),
            ThumbBorder = null, ThumbBorderWidth = 0f,
            InnerRestScale = 1f, InnerHoverScale = 1f, InnerPressScale = 1f,
        };
    }

    static readonly PersonPicture.Style s_persona = new() { InitialsFloor = 11f };

    /// <summary>Persona: the stock avatar, initials never under 11.</summary>
    public static PersonPicture.Style Persona() => s_persona;

    /// <summary>The v8 buttons.</summary>
    public static Button.Style? ButtonFor(ButtonAppearance appearance, ControlSize size)
    {
        var b = Button.FluentStyle(appearance, size);
        var k = Tok.Theme;
        var none = GradientSpec.Solid(ColorF.Transparent);
        switch (appearance)
        {
            case ButtonAppearance.Accent:
                return b with
                {
                    Background = FluentV8Palette.Primary(k), HoverBackground = FluentV8Palette.PrimaryHovered(k),
                    PressedBackground = FluentV8Palette.PrimaryPressed(k),
                    BorderBrush = none, HoverBorderBrush = none, PressedBorderBrush = none, DisabledBorderBrush = none,
                    CornerRadius = 2f, Bold = true,
                };
            case ButtonAppearance.Standard:
            case ButtonAppearance.Outline:
            {
                var ring = GradientSpec.Solid(FluentV8Palette.ButtonBorder(k));
                return b with
                {
                    Background = FluentV8Palette.Body(k), HoverBackground = FluentV8Palette.Hovered(k), PressedBackground = FluentV8Palette.Checked(k),
                    BorderBrush = ring, HoverBorderBrush = ring, PressedBorderBrush = ring,
                    DisabledBorderBrush = GradientSpec.Solid(FluentV8Palette.Hovered(k)),
                    CornerRadius = 2f, Bold = true,
                };
            }
            default:
                return b with
                {
                    HoverBackground = FluentV8Palette.Hovered(k), PressedBackground = FluentV8Palette.Checked(k),
                    BorderBrush = none, HoverBorderBrush = none, PressedBorderBrush = none, DisabledBorderBrush = none,
                    CornerRadius = 2f,
                };
        }
    }

    /// <summary>The callout / popup plate: solid bodyBackground, depth8, roundedCorner2, no ring.</summary>
    public static PopupPlate CalloutPlate() => new()
    {
        Fill = FluentV8Palette.Body(Tok.Theme),
        Acrylic = null,
        Border = ColorF.Transparent,
        BorderWidth = 0f,
        Radius = 2f,
        Shadow = Elevation.Flyout,
        Padding = default,
    };

    /// <summary>The ContextualMenu: the callout plate with the menu's 0,4 padding, rows 36 edge to edge, flat hover.</summary>
    public static MenuFlyout.Style Menu() => MenuFlyout.FluentStyle() with
    {
        Plate = CalloutPlate() with { Padding = new Edges4(0f, 4f, 0f, 4f) },
        ItemMargin = new Edges4(0f, 0f, 0f, 0f),
        ItemRadius = 0f,
        ItemHoverFill = FluentV8Palette.Hovered(Tok.Theme),
        ItemPressedFill = FluentV8Palette.Checked(Tok.Theme),
        SeparatorMargin = new Edges4(0f, 0f, 0f, 0f),
    };

    /// <summary>The Pivot: neutralPrimary words, the neutralLighter hover fill, 44-tall links.</summary>
    public static SelectorBarStyle? Pivot() => new()
    {
        RestColor = Tok.TextPrimary,
        SelectedColor = Tok.TextPrimary,
        HoverColor = Tok.TextPrimary,
        HoverFill = FluentV8Palette.Hovered(Tok.Theme),
        PressedFill = FluentV8Palette.Checked(Tok.Theme),
        ItemHeight = 44f,
    };

    /// <summary>The TextField / SearchBox: white, a 1-px inputBorder ring, roundedCorner2, the themePrimary focus bar.</summary>
    public static TextFieldStyle Field() => TextFieldStyle.Fluent() with
    {
        Radius = 2f,
        Fill = FluentV8Palette.Body(Tok.Theme),
        HoverFill = FluentV8Palette.Body(Tok.Theme),
        PressedFill = FluentV8Palette.Body(Tok.Theme),
        FocusedFill = FluentV8Palette.Body(Tok.Theme),
        BorderBrush = null,
        BorderColor = FluentV8Palette.InputBorder(Tok.Theme),
        BorderWidth = 1f,
        FocusBar = FluentV8Palette.Primary(Tok.Theme),
        FocusBarThickness = 2f,
    };
}
