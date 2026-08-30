using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Features.Detail;

namespace Wavee;

// The one primary call-to-action skin: a styled stock engine Button, NOT a hand-rolled box. Every LABELED Play/Resume/
// Shuffle CTA on a MEDIA surface routes through here, so it inherits Button's internals verbatim — the keyboard focus
// ring (FocusVisualMargin −3), AutomationRole.Button, the Space/Enter key mechanics, the ButtonPalette color seam that
// carries an artwork-derived accent, and the 83ms WinUI brush ramp on every state flip.
//
// What this skin substitutes on top of those internals is the media PERSONALITY, and only these five values:
//   · CornerRadius Radii.Full  — a capsule (the engine clamps to half the box, so 18 at the 36 height)
//   · MinHeight 36             — one step above the 32 control ladder, matching the Follow pill it sits beside
//   · Padding 18,6,18,7        — the wider capsule waist; the 1px bottom bias is the optical baseline nudge
//   · Bold label               — Style exposes no numeric weight, only Bold (=700); a Regular 400 label does not read
//                                as the page's primary action next to 32-48px hero type
//   · HoverScale/PressScale/Cursor.Hand — WinUI Button has NO scale cue and keeps the arrow; a media CTA over artwork
//                                does, and that divergence is CONFINED to this skin.
// UTILITY surfaces (settings, dialogs, empty-state actions) must keep stock Fluent rectangles by calling Button.*
// directly — this pill is for media primaries only.
//
// This is also NOT the skin for circular play FABs sitting ON artwork: those keep their round geometry and their own
// scale cues (MediaCard.PlayFab, the album-expand / episode / search circles, the hero Shuffle Fab, video overlays).
//
// ── THE ICON-BUTTON GEOMETRY TABLE ────────────────────────────────────────────────────────────────────────────────
// A button with no label has no text to say what it is, so its SHAPE has to. The app had five shapes doing that job
// (26 circles, 28 circles, 30 pills, 32 squares, 36 pills) and they carried no distinct meanings — a 26 circle in the
// queue and a 32 square in the toolbar were the same affordance drawn two ways. There are now exactly THREE rows, and
// a new icon button must be one of them:
//
//   ┌─ geometry ──────────────┬─ where ─────────────────────────────────────────────────────────────────────────────┐
//   │ 32 × 32, Radii.Control  │ THE standard icon button. Toolbars, panels, rows, flyouts, dialogs — every icon      │
//   │ (stock IconButton)      │ affordance on a NORMAL surface. If you are unsure, it is this one.                   │
//   │ 36 × 36, Radii.Full     │ The icon-only arm OF THIS PILL (<see cref="Icon"/>) — and only inside a CTA cluster  │
//   │                         │ where it stands beside labeled 36 capsules and must read as their equal.             │
//   │ circle, any diameter    │ A FAB, and ONLY ON MEDIA: floated over artwork or video, where the round plate is    │
//   │                         │ what separates the control from the picture (MediaCard.PlayFab, the player           │
//   │                         │ transport, video overlays). A circle on a flat panel is off-table.                   │
//   └─────────────────────────┴─────────────────────────────────────────────────────────────────────────────────────┘
//
// ── THE TEXT ACTION, AND ITS FENCE ────────────────────────────────────────────────────────────────────────────────
// <see cref="TextAction"/> is a THIRD grammar — a plateless, bold, 14px word that is a button — and a third grammar is
// exactly the kind of thing this file exists to prevent. It is sanctioned for ONE surface and fenced to it: the
// text-chrome CONTEXT BAND (Wavee's one sticky page header — see ContextBandLayout / ContextBand). That band is
// typography and nothing else: no thumbnail, no plates, no shadow. Putting a capsule in it would make the capsule the
// loudest object in a bar whose entire premise is that it is quiet, and putting an icon button in it would reintroduce
// the floating-glyph chrome the band replaced.
//
// It may NOT be used as a general low-emphasis button. A quiet action anywhere else is `Button.Create(...,
// ButtonAppearance.Subtle)`; a navigational word is the stock HyperlinkButton (which this is NOT — a hyperlink says
// "this goes somewhere", a text action says "this does something here"). If a second surface ever wants this, the
// question to answer first is why it is not a context band.
static class WaveeCta
{
    /// <summary>Row 1 of the geometry table: the standard 32-square icon button's edge. Equal to the control ladder's
    /// <c>WaveeSize.ControlH</c> by construction — an icon button is a control, so it is control-height.</summary>
    public const float IconButtonSize = WaveeSize.ControlH;

    /// <summary>The media pill's height. One step above the 32-DIP control ladder so a labeled media primary reads as
    /// the page's dominant action; also the height of the Follow pill it shares hero rows with.</summary>
    public const float PillHeight = 36f;

    // The pill's scale cue is no longer authored here: this skin's 1.04 hover IS the Standard rung of
    // WaveeMotion.ScaleStandard (a labeled media primary is the canonical "discrete aim target"), and its press
    // deepened 0.97 -> the rung's 0.96 so the ladder has one press value per tier. Reading the tier also makes the
    // pill reduced-motion-safe, which the two local consts never were.

    /// <summary>The primary Play CTA on an artwork-derived <paramref name="accent"/>. <paramref name="label"/> defaults
    /// to the shared detail-surface "Play" string; surfaces with their own wording pass it.</summary>
    public static BoxEl Play(ColorF accent, Action onClick, string? label = null)
        => Accent(label ?? Loc.Get(Strings.Detail.Play), accent, onClick);

    /// <summary>A labeled primary CTA on an arbitrary (usually artwork-derived) fill. <paramref name="ink"/> overrides
    /// the WCAG-picked on-fill ink for a surface that has already resolved it.</summary>
    public static BoxEl Accent(string label, ColorF accent, Action onClick, string? glyph = null, ColorF? ink = null,
        float minHeight = PillHeight)
        => Pill(label, onClick, palette: Palette(accent, ink), glyph: glyph ?? Icons.Play, minHeight: minHeight);

    /// <summary>The media pill on an explicit palette (a photo-local white ramp, an immersive white-on-media pair) or on
    /// a stock appearance ramp when <paramref name="palette"/> is null — a labeled neutral secondary passes
    /// <see cref="ButtonAppearance.Standard"/> and no palette. <paramref name="minHeight"/> lets a surface that owns its
    /// own control ladder (the vertical hero's three orientations) keep its slot height: <c>Style.MinHeight</c> is a
    /// FLOOR, so a call site declaring <c>Height</c> below the default 36 would otherwise still measure 36.</summary>
    public static BoxEl Pill(string label, Action onClick, ButtonAppearance appearance = ButtonAppearance.Accent,
        Button.ButtonPalette? palette = null, string? glyph = null, float minHeight = PillHeight)
        => Button.Create(label, onClick, appearance, glyph: glyph,
            // Style and palette are NOT independent parameters on Button.Create — a supplied style WINS and the palette
            // argument is dropped (Button.cs: `style ?? DefaultStyle(appearance, cs, palette)`). So the palette must ride
            // INSIDE the style: DefaultStyle folds it into the 24-member record, and the pill geometry is a `with` on
            // top. Everything not listed stays on the stock ladder (BorderWidth 1, 14px, center alignment, focus
            // margin −3, 83ms brush).
            style: Button.DefaultStyle(appearance, palette: palette) with
            {
                CornerRadius = Radii.Full,
                MinHeight = minHeight,
                Padding = new Edges4(18f, 6f, 18f, 7f),
                Bold = true,
            }) with
        {
            HoverScale = WaveeMotion.ScaleStandard.Hover,
            PressScale = WaveeMotion.ScaleStandard.Press,
            Cursor = CursorId.Hand,
        };

    /// <summary>The ICON-ONLY arm of the pill — row 2 of the geometry table. Same height, same appearance ramp, same
    /// hover/press rung and the same hand cursor as its labeled siblings, but square, so <see cref="Radii.Full"/>
    /// resolves to a circle and the cluster reads as "three capsules, one of which happens to be round" rather than as
    /// two grammars parked next to each other.
    /// <para>Built on the stock <see cref="IconButton"/> (focus ring, Space/Enter mechanics, AutomationRole, the 83ms
    /// brush ramp) wearing the BUTTON appearance ramp rather than the icon-button's own subtle one — beside a filled
    /// standard capsule a transparent-until-hover square disappears. The inner glyph's AnimatedIcon scale is switched
    /// OFF (<c>IconHoverScale = 1</c>): this skin's documented divergence is that the whole CAPSULE scales, and running
    /// both would compound to a 1.12 hover.</para>
    /// <para><paramref name="requestsContext"/> is the overflow "…" case: it re-enters the engine's context funnel to
    /// find the surface's ATTACHED menu instead of carrying a handler of its own. OnClick and ClickRequestsContext are
    /// mutually exclusive in the reconciler, so the handler is dropped when it is set.</para></summary>
    public static BoxEl Icon(string glyph, Action? onClick, ButtonAppearance appearance = ButtonAppearance.Standard,
        Button.ButtonPalette? palette = null, float size = PillHeight, bool requestsContext = false)
    {
        var s = Button.DefaultStyle(appearance, palette: palette);
        var box = IconButton.Create(glyph, onClick ?? NoOp, style: IconButton.DefaultStyle with
        {
            Size = size,
            CornerRadius = Radii.Full,
            Foreground = s.Foreground,
            HoverForeground = s.HoverForeground,
            PressedForeground = s.PressedForeground,
            DisabledForeground = s.DisabledForeground,
            Fill = s.Background,
            HoverFill = s.HoverBackground,
            PressedFill = s.PressedBackground,
            DisabledFill = s.DisabledBackground,
            IconHoverScale = 1f,
            IconPressScale = 1f,
        }) with
        {
            // The button ramp's hairline, which IconButton.Style has no knob for — without it the round arm is the one
            // control in the cluster with no edge.
            BorderBrush = s.BorderBrush,
            HoverBorderBrush = s.HoverBorderBrush,
            PressedBorderBrush = s.PressedBorderBrush,
            BorderWidth = s.BorderWidth,
            HoverScale = WaveeMotion.ScaleStandard.Hover,
            PressScale = WaveeMotion.ScaleStandard.Press,
            Cursor = CursorId.Hand,
        };
        return requestsContext ? box with { OnClick = null, ClickRequestsContext = true } : box;
    }

    static readonly Action NoOp = static () => { };

    // ── the text action (context bands only — see the fence in the file header) ───────────────────────────────────

    /// <summary>The context band's action rung: 14 / 600, no plate, no border, no scale cue.</summary>
    public const float TextActionSize = 14f;
    public const ushort TextActionWeight = 600;
    public const float TextActionLineHeight = 20f;

    /// <summary>A PLATELESS labelled action for a <see cref="ContextBand"/> row.
    ///
    /// <para><b>Ink is the whole state model.</b> Rest <c>TextSecondary</c> → hover <c>TextPrimary</c>, eased by the
    /// engine's own HoverT through <see cref="TextEl.HoverColor"/> — no fill, no border, no hover plate, and
    /// deliberately no <c>HoverScale</c>: a word that grows under the pointer inside a 56-DIP bar shoves its
    /// neighbours, and the band's premise is that it is still. <paramref name="primary"/> (the ONE per band) and
    /// <paramref name="toggledOn"/> (a latched toggle, e.g. Following) both take ACCENT ink on the stock hyperlink
    /// ramp — <c>AccentTextPrimary</c> → <c>AccentTextSecondary</c> → <c>AccentTextTertiary</c>, the same ladder
    /// <c>HyperlinkButton</c> rides — so accent ink in this band always means either "the primary verb" or "this is
    /// on", and never decoration.</para>
    ///
    /// <para><b>The hover boundary is the ACTION's own box</b>, never the row that contains it. <c>HoverColor</c>
    /// interpolates against the nearest interactive ancestor's HoverT, so a handler one level up would light every
    /// action in the cluster at once (the hover-container trap that produced the "all the shelf cards popped"
    /// class of bug).</para>
    ///
    /// <para>Everything a button owes is still here: <see cref="AutomationRole.Button"/>, <c>Focusable</c> with the
    /// engine's keyboard ring, the hand cursor, and SENTENCE case — the label is passed through verbatim, because a
    /// caps transform over a localized string mangles Turkish dotted i and expands German ß, and lowercase-stylized
    /// chrome is on the rejected-Zune list.</para></summary>
    public static BoxEl TextAction(string label, Action? onClick, bool primary = false, bool toggledOn = false,
                                   string? glyph = null)
    {
        bool accent = primary || toggledOn;
        ColorF rest = accent ? Tok.AccentTextPrimary : Tok.TextSecondary;
        ColorF hover = accent ? Tok.AccentTextSecondary : Tok.TextPrimary;
        ColorF pressed = accent ? Tok.AccentTextTertiary : Tok.TextSecondary;

        var kids = new System.Collections.Generic.List<Element>(2);
        if (glyph is { Length: > 0 })
            kids.Add(new TextEl(glyph)
            {
                Size = 14f, FontFamily = Theme.IconFont,
                Color = rest, HoverColor = hover, PressedColor = pressed,
            });
        kids.Add(new TextEl(label)
        {
            Size = TextActionSize, LineHeight = TextActionLineHeight, Weight = TextActionWeight,
            Color = rest, HoverColor = hover, PressedColor = pressed,
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
        });

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Height = ContextBandLayout.Height - 2f * Spacing.M,
            Padding = new Edges4(ContextBandLayout.ActionPadX, 0f, ContextBandLayout.ActionPadX, 0f),
            // A focus ring needs a shape to draw; 4 is the control ladder's radius, and it is invisible at rest
            // because nothing is filled.
            Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = onClick,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The stock <c>ButtonPalette.For(ButtonAppearance.Accent)</c> ramp with <paramref name="fill"/>
    /// substituted for AccentFillColorDefault. Shades mirror Tok's AccentFillShade exactly (the SAME color at @0.90 /
    /// @0.80 alpha for Secondary/Tertiary); the disabled legs stay on the system tokens, because a disabled control
    /// must not advertise the page's accent. Border = AccentControlElevationBorder for rest/hover, transparent for
    /// pressed/disabled; BackgroundSizing = OuterBorderEdge — the AccentButtonStyle setters.</summary>
    public static Button.ButtonPalette Palette(ColorF fill, ColorF? ink = null, GradientSpec? border = null)
    {
        ColorF fg = ink ?? ColorContrast.PickContrast(fill);
        GradientSpec? rest = border ?? Tok.AccentControlElevationBorder;
        var transparent = GradientSpec.Solid(ColorF.Transparent);
        return new Button.ButtonPalette(
            Background: new StateBrush(fill, fill with { A = 0.90f }, fill with { A = 0.80f }, Tok.AccentDisabled),
            Foreground: new StateBrush(fg, fg, fg with { A = OnFillSecondaryAlpha(fg) }, Tok.TextOnAccentDisabled),
            Border: new Button.BorderRamp(rest, rest, transparent, transparent),
            Sizing: BackgroundSizing.OuterBorderEdge);
    }

    // WinUI TextOnAccentFillColorSecondary (the PRESSED label): the primary on-accent ink dimmed — 0x80 where that ink
    // is dark (PaletteBuilder's near-black stop), 0xB3 where it is light (white). Keyed off the ink's LUMINANCE, not off
    // Tok.Theme (an artwork accent can invert the ink against the theme) and not off token equality (callers may pass an
    // explicit ink, e.g. pure black, that is not the NearBlackInk constant).
    static float OnFillSecondaryAlpha(in ColorF ink)
        => ColorContrast.RelativeLuminance(ink) < 0.5f ? 0x80 / 255f : 0xB3 / 255f;
}
