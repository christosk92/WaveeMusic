// ── Themes/Zune/Structure/Views/Overlays/ZuneOverlayParts.cs — the parts Zune's overlays and dialogs share (themes plan 6.3, P5 Z8a) ───────────────────
//
// A glyph from an engine icon reference, the hairline, the quiet skeleton bar, the accent word (a lowercase command that turns to the ink on hover) and the
// round verb (a ring, or the accent disc for the play verb). Zune's overlays are flat plates with words on them: the parts are the same few shapes everywhere.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>The shared parts of Zune's overlays.</summary>
internal static class ZuneOverlayParts
{
    /// <summary>An <see cref="IconRef"/> as an element: the layered vector when it is registered, else the glyph in its font.</summary>
    public static Element GlyphOf(IconRef icon, float size, ColorF ink, bool glyphFirst = false, bool solid = false)
    {
        // A round verb draws the icon's plain glyph in ONE ink (the board's white line glyph; the on-accent ink on the disc): the themed layered icon is a coloured
        // badge (an accent disc with its symbol), which vanishes into the accent disc and shows pink inside a white ring.
        if (glyphFirst && icon.Glyph is { Length: > 0 } plain) return Icon(solid && plain == Icons.Play ? ZunePlayerLayout.PlaySolid : plain, size, ink, icon.Font);
        if (icon.ThemedName is { Length: > 0 } themed && ThemedIconRegistry.Has(themed)) return ThemedIcon.Create(themed, size);
        return icon.Glyph is { Length: > 0 } glyph ? Icon(glyph, size, ink, icon.Font) : new BoxEl { Width = size, Height = size, Shrink = 0f };
    }

    /// <summary>A dialog's command as Zune draws it (Z-Dialogs): the default one is the accent ring disc with its arrow and its word ("open github", "send report"), every other
    /// a lowercase word in the secondary ink that turns to the primary on hover. The dialog keeps the order, the focus ranking and the cancelable close.</summary>
    public static BoxEl DialogCommand(ContentDialogCommandArgs c)
    {
        if (c.IsDefault)
            return (BoxEl)ZuneDiscoveryKit.Ring("zdialog:primary", Icons.Forward, c.Text, showLabel: true, primary: true, c.Invoke, ZuneOverlayRules.DialogRing)
                with { IsEnabled = c.IsEnabled, Opacity = c.IsEnabled ? 1f : ZuneOverlayRules.DialogDisabledOpacity };
        return new BoxEl
        {
            Key = "zdialog:command:" + (int)c.Command, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            MinHeight = ZuneOverlayRules.DialogWordHeight, Padding = new Edges4(0f, 0f, ZuneOverlayRules.DialogWordPadX, 0f), Shrink = 0f,
            Role = AutomationRole.Button, Focusable = c.IsEnabled, IsEnabled = c.IsEnabled, Cursor = c.IsEnabled ? CursorId.Hand : (CursorId?)null,
            OnClick = c.IsEnabled ? c.Invoke : null, Opacity = c.IsEnabled ? 1f : ZuneOverlayRules.DialogDisabledOpacity,
            Children = [ZuneText.Command(c.Text) with { Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
        };
    }

    /// <summary>The dialog's close mark (Z-Dialogs-Compact, -Huge): a 36 target with the close glyph in the secondary ink at the title row's end.</summary>
    public static Element CloseMark(Action close) => new BoxEl
    {
        Key = "zdialog:close", Width = ZuneOverlayRules.DialogClose, Height = ZuneOverlayRules.DialogClose, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = close,
        Children = [Icon(Icons.ChromeClose, ZuneOverlayRules.DialogCloseGlyph, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle);

    /// <summary>The menu's hairline: 16 in, 5 above and below (the menu style's separator).</summary>
    public static Element Divider() => new BoxEl
    {
        Height = 1f, Shrink = 0f, Margin = ZuneOverlayRules.SheetDividerMargin, Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
    };

    /// <summary>A quiet bar where a word is not known yet: it keeps the line's height.</summary>
    public static Element Skeleton(float width, float height) => new BoxEl
    {
        Width = width, Height = height, Shrink = 0f, Corners = CornerRadius4.All(ZuneOverlayRules.SkeletonBarRadius), Fill = Tok.FillSubtleSecondary,
    };

    /// <summary>A circle that stands for a picture not known yet.</summary>
    public static Element SkeletonCircle(float size) => new BoxEl
    {
        Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(size / 2f), Fill = Tok.FillSubtleSecondary,
    };

    /// <summary>The round verb of the context menu's strip and sheet: the accent disc for the play verb (the first, when it is a plain command), a 2-DIP ring for the others, the
    /// accent ring and the filled icon while a toggle is on. Its words are its tool tip and its automation name; a press runs the command and closes the menu.</summary>
    public static Element RoundVerb(string key, AppBarCommand command, bool disc, float size, float glyph, Action press)
    {
        bool on = command.Kind == AppBarCommandKind.ToggleButton && command.IsChecked;
        bool enabled = command.Enabled;
        IconRef icon = on && command.CheckedIcon is { } filled ? filled : command.Icon;
        ColorF ink = !enabled ? Tok.TextDisabled : disc ? Tok.TextOnAccentPrimary : on ? Tok.AccentTextPrimary : Tok.TextPrimary;
        var corners = CornerRadius4.All(size / 2f);
        BoxEl verb;
        if (disc)
        {
            verb = new BoxEl
            {
                Key = key, Width = size, Height = size, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = corners, Fill = enabled ? Tok.AccentDefault : Tok.AccentDisabled,
                HoverFill = enabled ? Tok.AccentSecondary : Tok.AccentDisabled, PressedFill = enabled ? Tok.AccentTertiary : Tok.AccentDisabled,
                Role = AutomationRole.Button, Focusable = enabled, Cursor = enabled ? CursorId.Hand : default, OnClick = enabled ? press : null,
                Children = [GlyphOf(icon, glyph, ink, glyphFirst: true, solid: true)],
            };
        }
        else
        {
            verb = new BoxEl
            {
                Key = key, Width = size, Height = size, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = corners, BorderWidth = ZuneOverlayRules.StripRing,
                BorderColor = !enabled ? Tok.TextTertiary : on ? Tok.AccentDefault : Tok.TextPrimary,
                Role = AutomationRole.Button, Focusable = enabled, OnClick = enabled ? press : null,
                Children = [GlyphOf(icon, glyph, ink, glyphFirst: true)],
            }.Interactive(Interaction.Subtle, isEnabled: enabled);
        }
        return ToolTip.Wrap(verb, command.Label);
    }

    /// <summary>An engine button drawn as an accent word (a lowercase command in the accent that turns lighter on hover): the action of an info bar, a banner, a toast card.</summary>
    public static BoxEl AccentWord(string label, Action onClick)
    {
        var parts = new TemplateParts();
        parts.Set<TextEl>(Button.PartLabel, t => ZuneText.Cased(t, LabelKind.Command));
        var style = Button.DefaultStyle(ButtonAppearance.Standard) with
        {
            Foreground = Tok.AccentDefault, HoverForeground = Tok.AccentSecondary, PressedForeground = Tok.AccentTertiary,
        };
        return Button.Standard(label, onClick, style, parts: parts);
    }

    /// <summary>A command word in <paramref name="ink"/> (it turns to the primary ink on hover), at least <paramref name="minHeight"/> tall: the retry word of an amber slab.</summary>
    public static BoxEl InkWord(string text, Action onClick, ColorF ink, float minHeight) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinHeight = minHeight, MinWidth = minHeight, Padding = ZuneSettingsRules.CmdPadding,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [ZuneText.Command(text) with { Color = ink, HoverColor = Tok.TextPrimary }],
    }.Interactive(Interaction.Subtle);
}
