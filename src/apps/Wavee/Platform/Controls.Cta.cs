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
// through here, so it inherits the Button's internals verbatim: the keyboard focus ring, the automation role, the
// Space/Enter mechanics, the palette colour seam that carries an artwork-derived accent, and the 83 ms brush ramp on
// every state flip.
//
// What this skin substitutes on top of those internals is the media PERSONALITY, and only these FIVE values:
//   · CornerRadius = Radii.Full — a capsule (the engine clamps to half the box, so 18 at the 36 height)
//   · MinHeight 36              — one step above the 32-DIP control ladder, matching the Follow pill it sits beside
//   · Padding 18/6/18/7         — the wider capsule waist; the 1-px bottom bias is the optical baseline nudge
//   · Bold label                — the style exposes no numeric weight, only Bold (= 700); a Regular label does not read
//                                 as the page's primary action next to 32-48 px hero type
//   · HoverScale/PressScale/hand cursor — WinUI's Button has NO scale cue and keeps the arrow; a media CTA over artwork
//                                 does, and that divergence is CONFINED to this skin.
//
// UTILITY surfaces (settings, dialogs, empty-state actions) must keep stock Fluent rectangles by calling the engine's
// `Button.*` directly. This pill is for MEDIA PRIMARIES only. It is also NOT the skin for circular play FABs sitting ON
// artwork: those keep their round geometry and their own scale cues.
//
// ── THE ICON-BUTTON GEOMETRY TABLE ───────────────────────────────────────────────────────────────────────────────────
//
// A button with no label has no text to say what it is, so its SHAPE has to. The app had FIVE shapes doing that job
// (26 circles, 28 circles, 30 pills, 32 squares, 36 pills) carrying no distinct meanings — a 26 circle in the queue and
// a 32 square in the toolbar were the same affordance drawn two ways. There are now exactly THREE rows, and a new icon
// button must be one of them:
//
//   ┌─ geometry ──────────────┬─ where ────────────────────────────────────────────────────────────────────────────┐
//   │ 32 × 32, Radii.Control  │ THE standard icon button. Toolbars, panels, rows, flyouts, dialogs — every icon     │
//   │ (stock IconButton)      │ affordance on a NORMAL surface. If you are unsure, it is this one.                  │
//   │ 36 × 36, Radii.Full     │ The icon-only arm OF THIS PILL — and ONLY inside a CTA cluster where it stands      │
//   │                         │ beside labeled 36 capsules and must read as their equal.                            │
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

    /// <summary>The media pill's height. One step above the 32-DIP control ladder so a labeled media primary reads as
    /// the page's dominant action; also the height of the Follow pill it shares hero rows with.
    /// <para><c>// DELETE in Wave 5 (buttons)</c> — the whole labeled-pill grammar (this constant, <see cref="Play"/>,
    /// <see cref="Accent"/>, <see cref="Pill"/>, <see cref="IconPill"/>, <see cref="CtaPalette"/>) is replaced by
    /// <see cref="PrimaryButton"/>/<see cref="PlayButton"/> below. Migration groups B–D move their call sites first;
    /// this file's helpers stay compiling until every caller has moved.</para></summary>
    public const float PillHeight = 36f;

    // ══ 1b. THE NEW 32/r4 GRAMMAR (Workstream B) ═════════════════════════════════════════════════════════════════════
    //
    // The replacement for the whole capsule ladder above: a styled STOCK `Button`/`IconButton`/`ToggleButton`, never a
    // hand-rolled box, so every one of these inherits the control's own internals verbatim (focus ring, automation
    // role, Space/Enter mechanics, the 83ms brush ramp) instead of re-deriving them. No `HoverScale`/`PressScale`/hand
    // cursor anywhere in this section — that divergence stayed CONFINED to the old media pill above, on purpose; the
    // grammar table's whole point is that these controls behave like ordinary WinUI controls, not like media chrome.
    // All accent maths routes through `Button.ButtonPalette.ForAccent` (E1) — this file carries no copy of the tier
    // arithmetic, only geometry.

    /// <summary>The ONE primary call-to-action on a surface (Play/Resume) — a stock <c>Button</c> Accent-appearance,
    /// tinted by <paramref name="accent"/> via <see cref="Button.ButtonPalette.ForAccent"/>, 32 tall/r4, 14 semibold
    /// label, floored at <see cref="PrimaryMinWidth"/> so a short label ("Play") never reads as an icon button.
    /// <paramref name="wrap"/> lets a narrow CTA row take a two-line label (`Show.PrimaryPill`'s wrap deviation)
    /// instead of the default single trimmed line.</summary>
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

    /// <summary>An icon-only accent affordance on the new 32/r4 ladder (the compact reader band's tinted actions) —
    /// row 3's geometry (<see cref="IconButtonSize"/>/<see cref="Radii.Control"/>) wearing <paramref name="accent"/>'s
    /// <see cref="Button.ButtonPalette.ForAccent"/> ramp instead of the neutral icon-button ramp.</summary>
    public static BoxEl AccentIconButton(ColorF accent, string glyph, Action onClick)
    {
        var s = Button.DefaultStyle(ButtonAppearance.Accent, palette: Button.ButtonPalette.ForAccent(accent));
        return IconButton.Create(glyph, onClick, style: IconButton.DefaultStyle with
        {
            Size = IconButtonSize,
            CornerRadius = Radii.Control,
            Foreground = s.Foreground,
            HoverForeground = s.HoverForeground,
            PressedForeground = s.PressedForeground,
            DisabledForeground = s.DisabledForeground,
            Fill = s.Background,
            HoverFill = s.HoverBackground,
            PressedFill = s.PressedBackground,
            DisabledFill = s.DisabledBackground,
        });
    }

    /// <summary>The ONE on-media scrim button ramp (Workstream B) — a <see cref="Button.ButtonPalette"/> for a labeled
    /// or icon-only stock control sitting directly on artwork (a video poster's transport row, a cover-corner action
    /// that wants LABEL text instead of <see cref="CoverActionFab"/>'s bare glyph). Shares the ONE on-media scrim ramp
    /// (<see cref="Design.OnMedia"/>) every other on-photo affordance already uses, so three controls on the same
    /// artwork read as one darkness instead of three.</summary>
    public static Button.ButtonPalette OnMediaPalette => new(
        Background: new StateBrush(Design.OnMedia.ScrimRest, Design.OnMedia.ScrimHover, Design.OnMedia.ScrimPressed, Design.OnMedia.ScrimRest),
        Foreground: new StateBrush(Design.OnMedia.Ink, Design.OnMedia.Ink,
                                   Design.OnMedia.Ink with { A = OnFillSecondaryAlpha(Design.OnMedia.Ink) }, Design.OnMedia.Ink with { A = 0.4f }),
        Border: Button.BorderRamp.Flat(GradientSpec.Solid(Design.OnMedia.Stroke)),
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

    // ══ 2. THE LABELED MEDIA PILL (// DELETE in Wave 5 (buttons) — see the note on PillHeight above) ═════════════════

    /// <summary>The primary PLAY call to action on an artwork-derived <paramref name="accent"/>. <paramref name="label"/>
    /// defaults to the shared detail-surface word; surfaces with their own wording pass it (an artist page, a home hero,
    /// a podcast's "Resume").
    /// <para><c>// DELETE in Wave 5 (buttons)</c> — replaced by <see cref="PlayButton(ColorF, Action, string?, string?)"/>.</para></summary>
    public static BoxEl Play(ColorF accent, Action onClick, string? label = null)
        => Accent(label ?? Loc.Get(Strings.Detail.Play), accent, onClick);

    /// <summary>A labeled primary CTA on an arbitrary (usually artwork-derived) fill. <paramref name="ink"/> overrides
    /// the contrast-picked on-fill ink for a surface that has already resolved it.
    /// <para><c>// DELETE in Wave 5 (buttons)</c> — replaced by <see cref="PrimaryButton"/>.</para></summary>
    public static BoxEl Accent(string label, ColorF accent, Action onClick, string? glyph = null, ColorF? ink = null,
                               float minHeight = PillHeight)
        => Pill(label, onClick, palette: CtaPalette(accent, ink), glyph: glyph ?? Icons.Play, minHeight: minHeight);

    /// <summary>The media pill on an explicit palette (a photo-local white ramp, an immersive white-on-media pair), or
    /// on a stock appearance ramp when <paramref name="palette"/> is null — a labeled NEUTRAL secondary passes the
    /// standard appearance and no palette.
    ///
    /// <para><paramref name="minHeight"/> lets a surface that owns its own control ladder keep its slot height.
    /// <b><c>MinHeight</c> is a FLOOR</b>: a call site declaring a smaller <c>Height</c> would otherwise still measure
    /// 36.</para>
    ///
    /// <para><b>Style and palette are NOT independent parameters</b> on the engine's button factory — a supplied style
    /// WINS and the palette argument is dropped. So the palette must ride INSIDE the style: the default style folds it
    /// in, and the pill geometry is a <c>with</c> on top. Everything not listed stays on the stock ladder (1-px border,
    /// 14 px, centre alignment, the −3 focus margin, the 83 ms brush).</para>
    /// <para><c>// DELETE in Wave 5 (buttons)</c> — see the note on <see cref="PillHeight"/> above.</para></summary>
    public static BoxEl Pill(string label, Action onClick, ButtonAppearance appearance = ButtonAppearance.Accent,
                             Button.ButtonPalette? palette = null, string? glyph = null, float minHeight = PillHeight)
        => Button.Create(label, onClick, appearance, glyph: glyph,
            style: Button.DefaultStyle(appearance, palette: palette) with
            {
                CornerRadius = Radii.Full,
                MinHeight = minHeight,
                Padding = new Edges4(18f, 6f, 18f, 7f),
                Bold = true,
            }) with
        {
            HoverScale = Design.Motion.ScaleStandard.Hover,
            PressScale = Design.Motion.ScaleStandard.Press,
            Cursor = CursorId.Hand,
        };

    // ══ 3. THE ICON ARM AND THE STANDARD ICON BUTTON ═════════════════════════════════════════════════════════════════

    /// <summary>The ICON-ONLY arm of the pill — row 2 of the geometry table. Same height, same appearance ramp, same
    /// hover/press rung and the same hand cursor as its labeled siblings, but SQUARE, so the full radius resolves to a
    /// circle and the cluster reads as "three capsules, one of which happens to be round" rather than as two grammars
    /// parked next to each other.
    ///
    /// <para>It wears the BUTTON appearance ramp, not the icon button's own subtle one: beside a filled standard capsule
    /// a transparent-until-hover square disappears. The inner glyph's own animated scale is switched OFF — this skin's
    /// documented divergence is that the whole CAPSULE scales, and running both would compound to a 1.12 hover.</para>
    ///
    /// <para><paramref name="requestsContext"/> is the overflow "…" case: it re-enters the engine's context funnel to
    /// find the surface's ATTACHED menu instead of carrying a handler of its own. The two are mutually exclusive in the
    /// reconciler, so the handler is DROPPED when it is set.</para>
    /// <para><c>// DELETE in Wave 5 (buttons)</c> — the icon arm's replacement is the standard <see cref="IconAction"/>
    /// (row 3) or <see cref="AccentIconButton"/> above; nothing replaces the 36-capsule icon ARM itself, because the
    /// new grammar has no CTA cluster that needs one.</para></summary>
    public static BoxEl IconPill(string glyph, Action? onClick, ButtonAppearance appearance = ButtonAppearance.Standard,
                                 Button.ButtonPalette? palette = null, float size = PillHeight,
                                 bool requestsContext = false)
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
            // The button ramp's HAIRLINE, which the icon-button style has no knob for — without it the round arm is
            // the one control in the cluster with no edge.
            BorderBrush = s.BorderBrush,
            HoverBorderBrush = s.HoverBorderBrush,
            PressedBorderBrush = s.PressedBorderBrush,
            BorderWidth = s.BorderWidth,
            HoverScale = Design.Motion.ScaleStandard.Hover,
            PressScale = Design.Motion.ScaleStandard.Press,
            Cursor = CursorId.Hand,
        };
        return requestsContext ? box with { OnClick = null, ClickRequestsContext = true } : box;
    }

    /// <summary>Row 3, and the DEFAULT: the standard 32 × 32, 4-radius icon button. Toolbars, panels, rows, flyouts,
    /// dialogs. If a new icon affordance is not obviously one of the other two rows, it is this one.
    /// <para><paramref name="onMedia"/> is the glyph on a DARK field in either theme (the artist bleed's hero): on-media
    /// ink with the glass hover/press ramp, instead of the theme's near-black glyph that would vanish there.</para></summary>
    public static BoxEl IconAction(string glyph, Action? onClick, bool requestsContext = false,
                                   float size = IconButtonSize, bool onMedia = false)
    {
        var style = IconButton.DefaultStyle with { Size = size, CornerRadius = Radii.Control };
        if (onMedia)
            style = style with
            {
                Foreground = Design.OnMedia.Ink, HoverForeground = Design.OnMedia.Ink,
                PressedForeground = Design.OnMedia.InkSecondary, DisabledForeground = Design.OnMedia.InkTertiary,
                Fill = Design.OnMedia.GlassRest, HoverFill = Design.OnMedia.GlassHover, PressedFill = Design.OnMedia.GlassPressed,
            };
        var box = IconButton.Create(glyph, onClick ?? NoOp, style: style) with
        {
            HoverScale = Design.Motion.ScaleSubtle.Hover,
            PressScale = Design.Motion.ScaleSubtle.Press,
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
            HoverScale = Design.Motion.ScaleEmphatic.Hover,
            PressScale = Design.Motion.ScaleEmphatic.Press,
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
        Children = [new TextEl(glyph) { Size = size, FontFamily = Theme.IconFont, Color = color }],
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
            Fill = Design.OnMedia.ScrimRest,
            HoverFill = Design.OnMedia.ScrimHover,
            PressedFill = Design.OnMedia.ScrimPressed,
            BorderWidth = 1f, BorderColor = Design.OnMedia.Stroke,
            Role = AutomationRole.Button, Focusable = true,
            FocusVisualMargin = Design.FocusInsetBordered,
            Cursor = CursorId.Hand,
            HoverScale = Design.Motion.ScaleEmphatic.Hover,
            PressScale = Design.Motion.ScaleEmphatic.Press,
            BlocksDragArm = true,
            OnClick = onClick,
            Children = [FabGlyph(glyph, size * 0.42f, Design.OnMedia.Ink)],
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

    // ══ 5. THE ACCENT CTA'S COLOUR RAMP ══════════════════════════════════════════════════════════════════════════════

    /// <summary>The stock accent button ramp with <paramref name="fill"/> substituted for the accent fill. Shades mirror
    /// the token ladder exactly (the SAME colour at 0.90 / 0.80 alpha for the secondary/tertiary rungs); the DISABLED
    /// legs stay on the SYSTEM tokens, because a disabled control must not advertise the page's accent.
    ///
    /// <para><paramref name="border"/> overrides the elevation-border gradient (a photo-local white ramp passes its
    /// own). <c>BackgroundSizing.OuterBorderEdge</c> is the accent-button-style setter: the fill runs UNDER the 1-px
    /// border rather than inside it.</para>
    /// <para><c>// DELETE in Wave 5 (buttons)</c> — replaced by <see cref="Button.ButtonPalette.ForAccent"/> (E1),
    /// called directly from <see cref="PrimaryButton"/>/<see cref="AccentIconButton"/>.</para></summary>
    public static Button.ButtonPalette CtaPalette(ColorF fill, ColorF? ink = null, GradientSpec? border = null)
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

    /// <summary>The PRESSED label's alpha: the primary on-accent ink dimmed — 0x80 where that ink is DARK, 0xB3 where it
    /// is LIGHT.
    /// <para>Keyed off the ink's LUMINANCE, not off the theme and not off token equality. An artwork accent can invert
    /// the ink against the theme, and a caller may pass an explicit ink that is not the palette's own near-black
    /// constant, so neither a theme read nor an equality test would be correct here.</para></summary>
    public static float OnFillSecondaryAlpha(in ColorF ink)
        => ColorContrast.RelativeLuminance(ink) < 0.5f ? 0x80 / 255f : 0xB3 / 255f;

    // ══ THE CONTEXT PLAY BUTTON — the hero ▶ that says what a press will do ═════════════════════════════════════════════

    /// <summary>Does a press on this container's ▶ PAUSE right now? True when the container is the playing context (or
    /// the playing item, for a playable), playback is running and no fault stands (the same gate
    /// <see cref="ContextPlayRules"/> uses to decide Toggle vs Start). A SUBSCRIBING read, coarse-first like
    /// <see cref="Playback.InstallCardSeam"/>: an idle surface reads one bool and never joins the identity fan-out, and
    /// a surface that is not the playing one re-renders on no skip at all.</summary>
    public static bool ShowsPause(EntityId target)
    {
        if (target.IsEmpty || !Playback.HasCardContext.Value) return false;
        bool relates = CardRelation.IsPlayable(target.Kind)
            ? CardRelation.Relates(target, default, Playback.CurrentId.Value)
            : CardRelation.Relates(target, Playback.ContextUri.Value, default);
        return relates && Playback.Error.Value == Playback.Fault.None && Playback.IsPlaying.Value;
    }

    /// <summary><see cref="ShowsPause(EntityId)"/> for a uri (the parse interns — UI thread, i.e. a Render).</summary>
    public static bool ShowsPause(string uri)
        => !string.IsNullOrEmpty(uri) && ShowsPause(EntityId.Parse(uri));

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
            bool pause = ShowsPause(_id);
            return pause
                ? PlayButton(Accent(), OnClick, Loc.Get(Strings.Home.Pause), Icons.Pause)
                : PlayButton(Accent(), OnClick);
        }
    }
}
