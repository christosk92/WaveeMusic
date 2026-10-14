// ── Platform/Controls.Cta.cs ───────────────────────────────────────────────────────────────────────────────────────
// WaveeCta and the CTA ladders
//
// Role: UI
// Owner: L
// Wave: 4
// Budget: 1000 lines
// Spec: DERIVED
//
// ── THE ONE PRIMARY CALL-TO-ACTION SKIN ──────────────────────────────────────────────────────────────────────────────
//
// A styled stock engine Button, NOT a hand-rolled box. Every LABELED Play/Resume/Shuffle CTA on a MEDIA surface routes
// through `PrimaryButton` / `PlayButton`, so it inherits the Button's internals verbatim: the keyboard focus ring, the
// automation role, the Space/Enter mechanics, the palette colour seam that carries an artwork-derived accent
// (`Button.ButtonPalette.ForAccent`), and the 83 ms brush ramp on every state flip. It sits on the 32/r4 control ladder
// with a 600 label and a floor width, nothing more.
//
// UTILITY surfaces (settings, dialogs, empty-state actions) keep stock Fluent rectangles by calling the engine's
// `Button.*` directly. Circular play FABs sitting ON artwork are not this skin either: they keep their round geometry
// and their own scale cues.
//
// ── THE ICON-BUTTON GEOMETRY TABLE ───────────────────────────────────────────────────────────────────────────────────
//
// A button with no label has no text to say what it is, so its SHAPE has to. The app had FIVE shapes doing that job
// (26 circles, 28 circles, 30 pills, 32 squares, 36 pills) carrying no distinct meanings — a 26 circle in the queue and
// a 32 square in the toolbar were the same affordance drawn two ways. There are now exactly TWO rows, and a new icon
// button must be one of them:
//
//   ┌─ geometry ──────────────┬─ where ────────────────────────────────────────────────────────────────────────────┐
//   │ 32 × 32, Radii.Control  │ THE standard icon button. Toolbars, panels, rows, flyouts, dialogs — every icon     │
//   │ (stock IconButton)      │ affordance on a NORMAL surface. If you are unsure, it is this one.                  │
//   │ circle, any diameter    │ A FAB, and ONLY ON MEDIA: floated over artwork or video, where the round plate is   │
//   │                         │ what separates the control from the picture. A circle on a flat panel is off-table. │
//   └─────────────────────────┴────────────────────────────────────────────────────────────────────────────────────┘
//
// ── THE TEXT ACTION, AND ITS FENCE ───────────────────────────────────────────────────────────────────────────────────
//
// <see cref="Controls.TextAction"/> is a THIRD grammar — a plateless, bold, 14-px word that is a button — and a third
// grammar is exactly the kind of thing this file exists to prevent. It is sanctioned for ONE surface and fenced to it:
// the text-chrome CONTEXT BAND, the app's one sticky page header. That band is typography and nothing else: no
// thumbnail, no plates, no shadow. A capsule in it would be the loudest object in a bar whose entire premise is that it
// is quiet, and an icon button would reintroduce the floating-glyph chrome the band replaced.
//
// It may NOT be used as a general low-emphasis button. A quiet action anywhere else is the stock SUBTLE button; a
// navigational word is the stock hyperlink (which this is NOT — a hyperlink says "this goes somewhere", a text action
// says "this does something here"). If a second surface ever wants this, the question to answer first is why it is not
// a context band.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee;

public static partial class Controls
{
    // ══ 1. THE GEOMETRY LADDER ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Row 1 of the geometry table: the standard 32-square icon button's edge. Equal to the control ladder's
    /// height BY CONSTRUCTION — an icon button is a control, so it is control-height.</summary>
    public const float IconButtonSize = Design.Size.ControlH;

    /// <summary>The NEW button-standardisation ladder's one height (docs/plans/wavee/home-redesign-implementation.md
    /// Workstream B): every labeled/icon control in the grammar table — Primary, Secondary, icon-only, Follow/Pre-save,
    /// filter toggles — is 32 tall. Media exceptions (FABs, poster play) stay round and off this ladder.</summary>
    public const float ButtonHeight = 32f;

    /// <summary>The primary CTA's floor width (<see cref="ButtonRules.PrimaryWidthNominal"/> — named here too so a
    /// `with`-tweak call site never has to import <c>ButtonRules</c> just to read the number back).</summary>
    public const float PrimaryMinWidth = ButtonRules.PrimaryWidthNominal;

    /// <summary>The capsule Follow pill's height (<see cref="FollowButton"/> and its skeleton): one step above the 32-DIP
    /// control ladder.</summary>
    public const float PillHeight = 36f;

    // ══ 1b. THE NEW 32/r4 GRAMMAR (Workstream B) ═════════════════════════════════════════════════════════════════════
    //
    // A styled STOCK `Button`/`IconButton`/`ToggleButton`, never a hand-rolled box, so every one of these inherits the
    // control's own internals verbatim (focus ring, automation role, Space/Enter mechanics, the 83ms brush ramp) instead
    // of re-deriving them. No `HoverScale`/`PressScale`/hand cursor anywhere in this section: the grammar table's whole
    // point is that these controls behave like ordinary WinUI controls, not like media chrome.
    // All accent maths routes through `Button.ButtonPalette.ForAccent` (E1) — this file carries no copy of the tier
    // arithmetic, only geometry.

    /// <summary>The ONE primary call-to-action on a surface (Play/Resume) — a stock <c>Button</c> Accent-appearance,
    /// tinted by <paramref name="accent"/> via <see cref="Button.ButtonPalette.ForAccent"/>, 32 tall/r4, 14 semibold
    /// label, floored at <see cref="PrimaryMinWidth"/> so a short label ("Play") never reads as an icon button.
    /// <paramref name="wrap"/> lets a narrow CTA row take a two-line label (the show page's) instead of the default single trimmed line.</summary>
    public static BoxEl PrimaryButton(string label, Action onClick, ColorF accent, string? glyph = null, bool wrap = false)
    {
        var parts = new TemplateParts();
        parts.Set<TextEl>(Button.PartLabel, t => t with
        {
            Weight = 600,
            MaxLines = wrap ? 2 : 1,
            Wrap = wrap ? TextWrap.Wrap : TextWrap.NoWrap,
        });
        return Button.Create(label, onClick, ButtonAppearance.Accent, glyph: glyph,
            style: Button.DefaultStyle(ButtonAppearance.Accent, palette: Button.ButtonPalette.ForAccent(accent)) with
            {
                MinHeight = ButtonHeight,
            },
            parts: parts) with { MinWidth = PrimaryMinWidth };
    }

    /// <summary>The PLAY primary — <see cref="PrimaryButton"/> pre-glyphed and pre-labeled. <paramref name="accent"/>
    /// is either a resolved fill or a THUNK invoked every render, so a palette that lands after the page's cover
    /// decodes (a daylist card, a hero whose accent extracts asynchronously) re-tints the fill instead of freezing the
    /// mount-time default — reading it inside the caller's own <c>Render</c> is what subscribes; this overload does not
    /// itself hold state (component props freeze at mount, so the LIVE seam is the caller's render, not a field here).</summary>
    public static BoxEl PlayButton(ColorF accent, Action onClick, string? label = null, string? glyph = null)
        => PrimaryButton(label ?? Loc.Get(Strings.Detail.Play), onClick, accent, glyph ?? Icons.Play);

    /// <inheritdoc cref="PlayButton(ColorF, Action, string?, string?)"/>
    public static BoxEl PlayButton(Func<ColorF> accent, Action onClick, string? label = null, string? glyph = null)
        => PlayButton(accent(), onClick, label, glyph);

    /// <summary>The ONE on-media scrim button ramp (Workstream B) — a <see cref="Button.ButtonPalette"/> for a labeled
    /// or icon-only stock control sitting directly on artwork (a video poster's transport row, a cover-corner action
    /// that wants LABEL text instead of <see cref="CoverActionFab"/>'s bare glyph). Shares the ONE on-media scrim ramp
    /// (<see cref="R.OnMedia"/>) every other on-photo affordance already uses, so three controls on the same
    /// artwork read as one darkness instead of three.</summary>
    public static Button.ButtonPalette OnMediaPalette => new(
        Background: new StateBrush(R.OnMedia.ScrimRest, R.OnMedia.ScrimHover, R.OnMedia.ScrimPressed, R.OnMedia.ScrimRest),
        Foreground: new StateBrush(R.OnMedia.Ink, R.OnMedia.Ink,
                                   R.OnMedia.Ink with { A = OnFillSecondaryAlpha(R.OnMedia.Ink) }, R.OnMedia.Ink with { A = 0.4f }),
        Border: Button.BorderRamp.Flat(GradientSpec.Solid(R.OnMedia.Stroke)),
        Sizing: BackgroundSizing.InnerBorderEdge);

    /// <summary>A filter/mode <see cref="ToggleButton.Style"/> whose CHECKED state is <paramref name="fill"/> (the
    /// grammar table's "stock checked = accent" row) instead of the live <see cref="Tok.AccentDefault"/> — the OFF arm
    /// is <see cref="ToggleButton.DefaultStyle"/> verbatim, same convention as <see cref="ButtonRules.FollowStyle"/>.
    /// Solid, not translucent: a filter chip's "on" reads as the SAME accent-filled capsule WinUI's own ToggleButton
    /// draws, just re-tinted — unlike <see cref="ButtonRules.FollowTint"/>'s deliberately translucent Follow ramp.</summary>
    public static ToggleButton.Style AccentToggleStyle(ColorF fill)
    {
        ColorF ink = ColorContrast.PickContrast(fill);
        return ToggleButton.DefaultStyle with
        {
            OnBackground = fill,
            OnHover = fill with { A = 0.90f },
            OnPressed = fill with { A = 0.80f },
            OnBorder = Tok.AccentControlElevationBorder,
            OnHoverBorder = Tok.AccentControlElevationBorder,
            OnPressedBorder = GradientSpec.Solid(ColorF.Transparent),
            OnForeground = ink,
            OnPressedForeground = ink with { A = OnFillSecondaryAlpha(ink) },
        };
    }

    // ══ 3. THE STANDARD ICON BUTTON ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The DEFAULT: the standard 32 × 32, 4-radius icon button. Toolbars, panels, rows, flyouts, dialogs. If a
    /// new icon affordance is not obviously a FAB on media, it is this one.</summary>
    public static BoxEl IconAction(string glyph, Action? onClick, bool requestsContext = false,
                                   float size = IconButtonSize)
    {
        var box = IconButton.Create(glyph, onClick ?? NoOp,
            style: IconButton.DefaultStyle with { Size = size, CornerRadius = Radii.Control }) with
        {
            HoverScale = R.Motion.ScaleSubtle.Hover,
            PressScale = R.Motion.ScaleSubtle.Press,
            Cursor = CursorId.Hand,
        };
        return requestsContext ? box with { OnClick = null, ClickRequestsContext = true } : box;
    }

    /// <summary>Row 4 of the table in spirit: a FAB — a circle, ONLY over artwork or video. The plate is what separates
    /// the control from the picture, which is why this one is allowed a circle on any diameter and nothing on a flat
    /// panel is.
    /// <para>Two of the three defects ch 02 §9.12 says to FIX rather than port are fixed here: 0.2.9's play FAB had no
    /// automation role and no focus stop, so a keyboard user could not reach it. The third — an accessible NAME — has no
    /// property on an engine box to live on, so it rides the tooltip: wrap the FAB in <see cref="Named"/> at the mount
    /// site (the now-playing overlay does).</para></summary>
    public static BoxEl PlayFab(Action onClick, string? glyph = null, float size = 44f, ColorF? fill = null)
        => new()
        {
            Width = size, Height = size, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(size / 2f),
            Fill = fill ?? Tok.AccentDefault,
            Role = AutomationRole.Button, Focusable = true,
            FocusVisualMargin = Design.FocusInsetBordered,
            Cursor = CursorId.Hand,
            HoverScale = R.Motion.ScaleEmphatic.Hover,
            PressScale = R.Motion.ScaleEmphatic.Press,
            // Forget this and the FAB stops working the moment its row or card becomes a drag source.
            BlocksDragArm = true,
            OnClick = onClick,
            Children = [FabGlyph(glyph ?? Icons.Play, size * 0.42f,
                                 ColorContrast.PickContrast(fill ?? Tok.AccentDefault))],
        };

    /// <summary>A glyph in a square box, at the icon face. Its own box rather than a bare text node so the glyph
    /// centres optically inside a circle instead of on its own line box.</summary>
    public static Element FabGlyph(string glyph, float size, ColorF color) => new BoxEl
    {
        Width = size, Height = size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl(glyph) { Size = size, FontFamily = Tok.Definition.Fonts.Icon, Color = color }],
    };

    /// <summary>A scrim-plated on-media action (the cover corner's "…", a cover action). The plate is the ONE on-media
    /// scrim ladder, so three affordances on the same artwork share one darkness instead of advertising three.</summary>
    public static BoxEl CoverActionFab(string glyph, Action? onClick, float size = 30f, bool requestsContext = false)
    {
        var box = new BoxEl
        {
            Width = size, Height = size, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(size / 2f),
            Fill = R.OnMedia.ScrimRest,
            HoverFill = R.OnMedia.ScrimHover,
            PressedFill = R.OnMedia.ScrimPressed,
            BorderWidth = 1f, BorderColor = R.OnMedia.Stroke,
            Role = AutomationRole.Button, Focusable = true,
            FocusVisualMargin = Design.FocusInsetBordered,
            Cursor = CursorId.Hand,
            HoverScale = R.Motion.ScaleEmphatic.Hover,
            PressScale = R.Motion.ScaleEmphatic.Press,
            BlocksDragArm = true,
            OnClick = onClick,
            Children = [FabGlyph(glyph, size * 0.42f, R.OnMedia.Ink)],
        };
        return requestsContext ? box with { OnClick = null, ClickRequestsContext = true } : box;
    }

    /// <summary>Give a glyph-only affordance its NAME. The engine carries no accessible-name property on a box, so the
    /// name rides the tooltip — which is also what a sighted mouse user needs from a control with no label.</summary>
    public static Element Named(Element target, string name) => ToolTip.Wrap(target, name);

    static readonly Action NoOp = static () => { };

    // ══ 4. THE TEXT ACTION (context bands only — see the fence in the file header) ════════════════════════════════════

    /// <summary>The context band's action rung.</summary>
    public const float TextActionSize = 14f, TextActionLineHeight = 20f;
    /// <inheritdoc cref="TextActionSize"/>
    public const ushort TextActionWeight = 600;

    /// <summary>A PLATELESS labelled action for a context-band row.
    ///
    /// <para><b>Ink is the whole state model.</b> Rest secondary → hover primary, eased by the engine's own hover
    /// interpolation through the text node's hover colour — no fill, no border, no hover plate, and deliberately NO
    /// <c>HoverScale</c>: a word that grows under the pointer inside a 56-DIP bar shoves its neighbours, and the band's
    /// premise is that it is still. <paramref name="primary"/> (the ONE per band) and <paramref name="toggledOn"/> (a
    /// latched toggle) both take ACCENT ink on the stock hyperlink ramp, so accent ink in this band always means either
    /// "the primary verb" or "this is on", and never decoration.</para>
    ///
    /// <para><b>The hover boundary is the ACTION's own box</b>, never the row that contains it. A hover colour
    /// interpolates against the nearest interactive ANCESTOR, so a handler one level up would light every action in the
    /// cluster at once — the hover-container trap that produced the "all the shelf cards popped" class of bug.</para>
    ///
    /// <para><b>There is no disabled arm.</b> <paramref name="onClick"/> is nullable and a null handler leaves a fully
    /// live-looking word — a band action that can be unavailable must not be rendered, or must be rendered by something
    /// else.</para>
    ///
    /// <para>Everything a button owes is still here: the automation role, a focus stop with the engine's keyboard ring,
    /// the hand cursor, and SENTENCE case — the label passes through verbatim, because a caps transform over a localized
    /// string mangles Turkish dotted i and expands German ß.</para>
    ///
    /// <para><paramref name="height"/>/<paramref name="padX"/> come from the band's own layout (owner M's, A1), so this
    /// file carries none of the detail frame's arithmetic. The defaults are the 0.2.9 values: 56 − 2 × 12 = 32, and a
    /// 10-DIP waist.</para></summary>
    public static BoxEl TextAction(string label, Action? onClick, bool primary = false, bool toggledOn = false,
                                   string? glyph = null, float height = 32f, float padX = 10f)
    {
        bool accent = primary || toggledOn;
        ColorF rest = accent ? Tok.AccentTextPrimary : Tok.TextSecondary;
        ColorF hover = accent ? Tok.AccentTextSecondary : Tok.TextPrimary;
        ColorF pressed = accent ? Tok.AccentTextTertiary : Tok.TextSecondary;

        var kids = new System.Collections.Generic.List<Element>(2);
        // The optional LEADING glyph wears the SAME rest/hover/pressed ink triple as the word, so the two brighten
        // together rather than the glyph staying flat while the word lights.
        if (glyph is { Length: > 0 })
            kids.Add(new TextEl(glyph)
            {
                Size = 14f, FontFamily = Tok.Definition.Fonts.Icon,
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
            Height = height,
            Padding = new Edges4(padX, 0f, padX, 0f),
            // A focus ring needs a SHAPE to draw; 4 is the control ladder's radius, and it is invisible at rest because
            // nothing is filled.
            Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = onClick,
            Children = kids.ToArray(),
        };
    }

    // ══ 5. THE ON-FILL PRESSED INK ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The PRESSED label's alpha: the primary on-accent ink dimmed — 0x80 where that ink is DARK, 0xB3 where it
    /// is LIGHT.
    /// <para>Keyed off the ink's LUMINANCE, not off the theme and not off token equality. An artwork accent can invert
    /// the ink against the theme, and a caller may pass an explicit ink that is not the palette's own near-black
    /// constant, so neither a theme read nor an equality test would be correct here.</para></summary>
    public static float OnFillSecondaryAlpha(in ColorF ink)
        => ColorContrast.RelativeLuminance(ink) < 0.5f ? 0x80 / 255f : 0xB3 / 255f;

    // ══ THE CONTEXT PLAY BUTTON — the hero ▶ that says what a press will do ═════════════════════════════════════════════

    /// <summary>The detail hero's primary: <see cref="PlayButton(Func{ColorF}, Action, string?, string?)"/> whose glyph and
    /// label follow playback (Play ▶ → Pause ⏸ while this subject is the one playing). A small component so the
    /// play/pause flip re-renders THIS button and not the page rail around it; the props freeze at mount, so the subject
    /// is keyed by its uri.</summary>
    public static Element ContextPlayButton(string subjectUri, Func<ColorF> accent, Action onClick)
        => Embed.Comp(() => new ContextPlayButtonHost { Uri = subjectUri, Accent = accent, OnClick = onClick })
            with { Key = "ctxplay:" + subjectUri };

    sealed class ContextPlayButtonHost : Component
    {
        public required string Uri { get; init; }
        public required Func<ColorF> Accent { get; init; }
        public required Action OnClick { get; init; }
        EntityId _id;
        bool _parsed;

        public override Element Render()
        {
            if (!_parsed) { _id = EntityId.Parse(Uri); _parsed = true; }
            bool pause = PlayRelation.ShowsPause(_id);
            return pause
                ? PlayButton(Accent(), OnClick, Loc.Get(Strings.Home.Pause), Icons.Pause)
                : PlayButton(Accent(), OnClick);
        }
    }
}
