// ── Themes/Common/CommonMiniPlayer.cs — the mini player's picture (#197) ────────────────────────────────────────────────
//
// One layout for the three themes, drawn with each theme's MiniPlayerStyle (StyleKeys.MiniPlayer: its numbers, its type, its grounds and
// its own seek line, visualizer and lyric line), so WinUI, Zune and Classic each look like themselves while the four forms stay one layout
// (the approved prototype, scratchpad miniplayer/index.html, 2026-11-09). The window keeps any size the user gives it, so every form is
// fluid inside its range (MiniPlayer.Rules.Live decides the form from the live size):
//
//   Strip   thumb + badge (the thumb grows with the height), title and artist (or the lyric ticker), the saved heart, the edge line; on
//           hover a right-anchored plate fades in over the row (save · prev · play · next · device · options, as many as the width holds)
//           and the edge line thickens.
//   Wide    the face box at the height, title / artist / detail beside it (clear of the window buttons, at rest too), seek with times,
//           save · prev · play · next · device. The Lyrics face fills the card instead (the filling form at the card's size).
//   Square  the filling form: the face fills; title, artist, heart and badge at the foot over the shade, the edge line. On hover the face
//           stays: a deeper scrim fades in under the foot, the title rises above the controls (or, where the window is too short, gives
//           way to them in place), and save · prev · play · next · device over the seek row come in at the foot. Object faces and
//           Classic's cover stand on a band of the window's own ground.
//   Tall    the face above (Canvas / Visualizer / Lyrics: behind) title, seek, transport and device; volume when there is room. The rows
//           are the same for every face, so a face change never moves them.
//
// Wide, Square and Tall show two window buttons top right on hover (options, back to Wavee): on dark glass over imagery, plain on the
// window's ground. Face switching lives in the options panel only; right-click anywhere that is not a control toggles that panel too.
// The accent is the theme's (WinUI's, Zune's pink), or the album's with "Colour from album art" (Classic's default). State never hides
// (filled heart, play badge, "Playing on"; on hover the transport row shows both). Every transport and save target is the style's
// ButtonBox (40 DIP or more); the window buttons are ChromeBox (32). Hover is a signal with a delayed fall (in at once, out after
// HoverOutDelayMs); the hover layers mount and unmount with a fade.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using H = Wavee.Themes.Common.CommonMiniPlayerRules;

namespace Wavee.Themes.Common;

internal static partial class CommonMiniPlayer
{
    public static void Register(ViewRegistryBuilder views, string templateId, MiniPlayerStyle style)
    {
        views.Add(ViewKeys.MiniPlayer, Template, templateId);
        views.AddStyle(StyleKeys.MiniPlayer, style);
    }

    static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);
    /// <summary>A form change cross-fades the old layout out and the new one in (the prototype's swap); a face change cross-fades the face.
    /// Declared swap families: the parts under them are rebuilt on purpose.</summary>
    static readonly EnterExit s_shapeSwap = SwapFamilies.Declare("mini.shape", s_fade), s_faceSwap = SwapFamilies.Declare("mini.face", s_fade);
    /// <summary>The filling form's title rises over the hover controls through a spacer whose height reflows (never a snap).</summary>
    static readonly LayoutTransition s_rise = LayoutTransition.BoundsT(SizeMode.Reflow);

    /// <summary>The menus' anchor (the window itself: they open above it, or below it, never over its controls) and handles (one set
    /// per window).</summary>
    sealed class Anchors
    {
        public readonly Ref<NodeHandle> Window = new(default);
        public readonly Ref<OverlayHandle?> DeviceMenu = new(null), OptionsMenu = new(null);
    }

    public static Element Template(MiniPlayerProps p, in ViewContext ctx)
    {
        var rc = ctx.Hooks;
        var bar = p.Bar;
        var v = bar.Host.Use(rc);
        var st = ctx.StyleOf(StyleKeys.MiniPlayer);
        var shape = p.Shape.Value;
        int face = Math.Max(0, p.Face.Value);
        var kind = p.FaceKind(face);
        var size = rc.UseContextSignal(Viewport.Size).Value;
        var overlay = rc.UseContext(Overlay.Service);
        var hot = rc.UseSignal(false);
        var hotFall = rc.UseTimeout(() => { if (hot.Peek()) hot.Value = false; }, st.HoverOutDelayMs, DepKey.Empty);
        var anchors = rc.UseMemo(static () => new Anchors(), DepKey.Empty);
        bool light = R.IsLight;
        rc.UseLayoutEffect(() => p.UseMaterial(light ? st.MaterialLight : st.MaterialDark, !light),
            DepKey.From(light ? 1 : 0, (int)st.MaterialLight, (int)st.MaterialDark, 0));
        // The menus' handles are plain refs: the overlay service's pin epoch (both menus are anchored to this window, so they pin it) is the
        // signal that re-reads them on every open and close. Without it a menu closed with the pointer outside left the controls up until
        // some other change re-rendered the window.
        _ = overlay.PinEpoch.Value;
        bool hovering = hot.Value || anchors.OptionsMenu.Value is { IsOpen: true } || anchors.DeviceMenu.Value is { IsOpen: true };

        // THE WINDOW LEAVE (YouTube's rule, owner 2026-11-11): the pointer leaving the window (any edge, onto another monitor, under a window
        // shown over this one, off a menu of ours closed outside) or the window losing activation drops the hover AT ONCE; the hover layers' own
        // fade is the hide. The out-delay is for a hover taken inside the window only. A menu of ours that is open keeps the controls (hovering).
        // While they show, the platform also confirms a resting pointer is still here (InputHooks.WatchPointerPresence).
        var hooks = rc.UseContext(InputHooks.Current);
        var hotCause = rc.UseRef("mount");
        rc.UseEffect(() =>
        {
            if (hooks is null) return (Action?)null;
            Action leave = () => { hotFall.Cancel(); hotCause.Value = "leave-window"; if (hot.Peek()) hot.Value = false; };
            hooks.PointerLeftWindowObserved += leave;
            hooks.WindowBlurObserved += leave;
            return () => { hooks.PointerLeftWindowObserved -= leave; hooks.WindowBlurObserved -= leave; };
        }, DepKey.Empty);
        rc.UseEffect(() =>
        {
            Log.Info("mini", "[mini.chrome] " + (hovering ? "show" : "hide") + " cause=" + (hovering && !hot.Peek() ? "menu" : hotCause.Value));
            if (hooks is null || !hovering) return (Action?)null;
            hooks.WatchPointerPresence(true);
            return () => hooks.WatchPointerPresence(false);
        }, hovering ? 1 : 0);

        // THE VOLUME HUD: a wheel step shows it, HudMs after the last one it goes.
        var hud = rc.UseSignal(false);
        var hudSeen = rc.UseRef(-1);
        var hudOff = rc.UseTimeout(() => { if (hud.Peek()) hud.Value = false; }, H.HudMs, DepKey.Empty);
        rc.UseSignalEffect(() =>
        {
            int n = p.VolumeHud.Value;
            if (hudSeen.Value < 0) { hudSeen.Value = n; return; }   // the mount seeds: a wheel before this window opened shows nothing
            if (n == hudSeen.Value) return;
            hudSeen.Value = n;
            hud.Value = true;
            hudOff.RestartIn(H.HudMs);
        });

        // THE ACCENT: the theme's, or the playing cover's with "Colour from album art".
        bool fromArt = p.ArtColour(st.ArtColourSlot, st.ArtColourDefault);
        ColorF accent = fromArt && p.AlbumAccent(v.ArtUrl) is { } album ? album : Tok.AccentDefault;
        bool roomy = p.Roomy.Value;

        var m = new Ctx(p, bar, v, st, shape, face, kind, size, hovering, overlay, anchors, accent, OnAccent(accent), roomy);
        // The wide card's Lyrics face fills the window (a one-line window beside the art read as a crumb): the filling form at the card's size.
        bool wideFill = shape == MiniShape.Wide && kind == MiniFaceKind.Lyrics;
        Element body = shape switch
        {
            MiniShape.Strip => Strip(m),
            MiniShape.Square => Square(m),
            MiniShape.Tall => Tall(m),
            _ => wideFill ? Square(m with { Shape = MiniShape.Square }) : Wide(m),
        };
        var glow = st.Glow(accent);
        var children = new List<Element>(3);
        if (glow.A > 0f)
            children.Add(new BoxEl
            {
                Grow = 1f, AlignSelf = FlexAlign.Stretch, HitTestVisible = false,
                Gradient = new GradientSpec(GradientShape.Linear, H.GlowAngle, [new GradientStop(0f, glow), new GradientStop(H.GlowEnd, glow with { A = 0f })]),
            });
        children.Add(new BoxEl
        {
            Key = H.ShapeKey + ":" + shape + (wideFill ? ":fill" : ""), Grow = 1f, AlignSelf = FlexAlign.Stretch, Direction = 1, Enter = s_shapeSwap, Exit = s_shapeSwap, Children = [body],
        });
        if (hud.Value) children.Add(Hud(bar));
        return new BoxEl
        {
            Key = H.RootKey, Grow = 1f, AlignSelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true, Fill = st.Ground(accent),
            OnRealized = h => anchors.Window.Value = h,
            OnPointerMoveWithin = _ => { hotFall.Cancel(); if (!hot.Peek()) { hotCause.Value = "enter"; hot.Value = true; } },
            // a drag that started here (the volume, the seek) keeps the controls until its release: the window's own leave decides then
            OnPointerExit = () => { if (hooks?.IsPointerHeld?.Invoke() == true) return; hotCause.Value = "exit"; hotFall.RestartIn(st.HoverOutDelayMs); },
            OnPointerWheel = e => p.WheelVolume(e.Steps),
            // right-click on the window (a control answers its own request with nothing) toggles the options panel, as ⋯ does
            OnContextRequested = _ => OpenOptions(p, st, overlay, anchors),
            Children = children.ToArray(),
        };
    }

    /// <summary>Everything one render reads, passed to the form builders.</summary>
    sealed record Ctx(MiniPlayerProps P, PlayerBarProps Bar, PlayerBarView V, MiniPlayerStyle St, MiniShape Shape, int Face,
        MiniFaceKind Kind, Size2 Size, bool Hot, IOverlayService Overlay, Anchors A, ColorF Accent, ColorF OnAccent, bool Roomy,
        bool Media = false)
    {
        /// <summary>The ink a control draws in: on-media white over imagery (every mode), else the theme's.</summary>
        public ColorF Ink => Media ? H.OnMediaInk : Tok.TextPrimary;
        public ColorF Ink2 => Media ? H.OnMediaInk with { A = H.OnMediaSecondaryAlpha } : Tok.TextSecondary;
        public ColorF Ink3 => Media ? H.OnMediaInk with { A = H.OnMediaTertiaryAlpha } : Tok.TextTertiary;
        public bool Playing => V.PauseGlyph;
    }

    /// <summary>The accent's own ink (the play glyph, the badge): dark on a light accent, white otherwise.</summary>
    static ColorF OnAccent(ColorF a)
    {
        static float Lin(float c) => c <= 0.03928f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        float l = 0.2126f * Lin(a.R) + 0.7152f * Lin(a.G) + 0.0722f * Lin(a.B);
        return l > H.OnAccentLuma ? H.OnAccentDark : H.OnMediaInk;
    }

    // ══ FORMS ════════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Strip(Ctx m)
    {
        var st = m.St;
        bool lyrics = m.Kind == MiniFaceKind.Lyrics;
        float thumb = Math.Clamp(m.Size.Height - 2f * H.StripThumbInset, st.StripThumb, H.StripThumbMax);
        bool viz = m.Kind == MiniFaceKind.Visualizer;
        Element meta = lyrics ? m.P.Lyrics(frame => Embed.Comp(new TickerProps(frame, m, Meta(m, detail: false)), static () => new Ticker()))
                              : Meta(m, detail: false, hug: viz);
        var cells = new List<Element>(4)
        {
            Thumb(m, thumb),
            // beside a visualizer the text hugs its words (shrinking to its ellipsis when the strip is narrow), so the analyser keeps to the rest
            new BoxEl { Grow = viz ? 0f : 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, Children = [meta] },
        };
        if (viz)
            // The analyser runs only where the text is not: a cell that takes what the text leaves (none on a narrow strip), the window-wide
            // picture anchored to its right and clipped, fading in from the text's side. The bands stay the face's bound transforms.
            cells.Add(new BoxEl
            {
                Key = H.FaceKey + ":strip", Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true,
                Opacity = H.StripVizAlpha, HitTestVisible = false, EdgeFade = new EdgeFadeSpec(EdgeMask.Left, H.StripVizFade),
                Children = [new BoxEl
                {
                    Width = m.Size.Width, Height = m.Size.Height, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.End, HitTestVisible = false,
                    Children = [m.P.FaceView(new MiniFaceRequest(m.Face, m.Size.Width, m.Size.Height, m.V.ArtUrl, 0f, MiniFaceUse.Fill, VizPaint(m, H.VizBarsStrip)))],
                }],
            });
        cells.Add(Saved(m));
        var row = new BoxEl
        {
            Grow = 1f, Direction = 0, AlignItems = FlexAlign.Center, Gap = st.StripGap, Padding = new Edges4(st.StripPadStart, 0f, st.Pad, 0f),
            Children = cells.ToArray(),
        };
        var children = new List<Element>(3) { row };
        if (m.Hot)
        {
            // As many of the six as the width holds (device goes first, then save); the plate stands centred on the row at any height.
            int n = H.StripButtons(m.Size.Width, st.ButtonBox);
            var buttons = new List<Element>(6);
            if (n >= 5) buttons.Add(SaveButton(m));
            buttons.Add(Prev(m));
            buttons.Add(Play(m, st.ButtonBox));
            buttons.Add(Next(m));
            if (n >= 6) buttons.Add(Device(m));
            buttons.Add(OptionsButton(m, st.ButtonBox));
            float w = H.StripFadeLead + buttons.Count * st.ButtonBox + H.StripOverlayEnd;
            var solid = st.Solid(m.Accent);
            children.Add(new BoxEl
            {
                Key = H.HoverKey, Width = w, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.End, Direction = 0, AlignItems = FlexAlign.Center,
                Padding = new Edges4(H.StripFadeLead, 0f, H.StripOverlayEnd, 0f),
                Enter = s_fade, Exit = s_fade,
                Gradient = new GradientSpec(GradientShape.Linear, 0f, [new GradientStop(0f, solid with { A = 0f }), new GradientStop(H.StripFade / w, solid), new GradientStop(1f, solid)]),
                Children = buttons.ToArray(),
            });
        }
        children.Add(Edge(m));   // over the plate: the plate covers the row to the window's foot (no text shows under it)
        return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
    }

    static Element Wide(Ctx m)
    {
        var st = m.St;
        float box = MathF.Max(0f, m.Size.Height - 2f * st.Pad);
        // The title keeps clear of the window buttons at rest too, so hovering never re-trims it.
        Element top = Meta(m, detail: box >= st.WideDetailFrom, reserve: H.ChromeReserve(st.ChromeBox), column: true);
        var col = new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.SpaceBetween, Gap = H.WideColGap,
            Children =
            [
                top,
                SeekRow(m, lift: H.TimeLiftWide),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
                    Margin = new Edges4(-H.HeartBleed(st.Heart, H.CtrlBleedX), 0f, -H.CtrlBleedX, -H.CtrlBleedBottom),
                    Children = [SaveButton(m), Transport(m, st.PlayBox, 0f), Device(m)],
                },
            ],
        };
        var children = new List<Element>(2)
        {
            new BoxEl { Grow = 1f, Direction = 0, Gap = st.Gap, Padding = Edges4.All(st.Pad), Children = [FaceBox(m, box, box, chrome: false), col] },
        };
        if (m.Hot) children.Add(WindowButtons(m, glass: false));
        return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
    }

    /// <summary>The filling form: the square, and the wide card's Lyrics face. The face stays as it is on hover: the controls come in at the
    /// foot over a deeper scrim, and the title rises above them where the window is tall enough, else gives way to them in place.</summary>
    static Element Square(Ctx m)
    {
        var st = m.St;
        bool lyrics = m.Kind == MiniFaceKind.Lyrics;
        bool obj = m.Kind == MiniFaceKind.Object;
        bool groundCover = st.GroundBand && m.Kind == MiniFaceKind.Cover;
        bool dark = !obj && !groundCover;   // the foot is imagery: the text and controls read on-dark
        float w = m.Size.Width, h = m.Size.Height;
        // The record, the disc, the cassette and the reels are the full player's own tiles: inset like the tall column's art (never against
        // the window's top edge, never under the title).
        float side = obj ? MathF.Max(0f, MathF.Min(w - 2f * st.Pad, h - H.SquareObjectFoot - st.Pad - H.SquareObjectGap)) : 0f;
        // The window buttons stand on glass over imagery: everywhere but an object face's ground, unless its tile reaches under them. The
        // short top scrim is for a face that fills the top with imagery only (an object face stands on the window's ground: no scrim there).
        bool glass = !obj || (w + side) * 0.5f > w - (H.ChromeInset + 2f * st.ChromeBox + H.ChromeGap);

        // The foot: the title block at rest; on hover the controls (transport row over the seek row) under it. The title rises by the
        // controls' height where the risen title still clears the window buttons, else it cross-fades with the controls in place. Over
        // lyrics it always gives way in place: a risen title would sit on the lines, and only the foot under the controls may darken.
        var fm = dark ? m with { Media = true } : m;
        float controlsH = st.PlayBigBox + H.SquareHoverRowGap + H.SeekRowH;
        float controlsTop = h - H.SquareHoverFoot - controlsH;
        float rise = controlsH + H.SquareHoverFoot + H.SquareTitleAir - H.SquareRestFoot;
        float titleH = MathF.Max(TitleBlockHeight(fm), st.Badge);
        float risenTop = h - H.SquareRestFoot - rise - titleH;
        bool rises = !lyrics && risenTop >= H.ChromeInset + st.ChromeBox + H.SquareTitleAir;
        // The analyser stands above the foot: the resting title at rest; while the controls show, between the window buttons and the whole
        // hover block (the risen title, or the controls where it gives way in place). Its box eases between the two (VizPaint), so no bar ever
        // runs through the text or under the buttons.
        float vizFoot = m.Hot ? h - (rises ? risenTop : controlsTop) + H.SquareTitleAir : H.SquareLyricFoot;
        float vizHead = m.Hot ? H.ChromeInset + st.ChromeBox + H.SquareTitleAir : 0f;
        var children = new List<Element>(12);
        if (obj)
        {
            children.Add(Glow(m, w, h - H.SquareObjectFoot,
                FaceLayer(m, side, side, MiniFaceUse.Box, corner: 0f) with { JustifySelf = FlexAlign.Center, Margin = new Edges4(0f, st.Pad, 0f, 0f) }));
        }
        else if (groundCover)
            children.Add(FaceLayer(m, w, h - H.SquareCoverFoot, MiniFaceUse.Fill));
        else
            children.Add(FaceLayer(m, w, h, MiniFaceUse.Fill, paint: VizPaint(m, H.VizBarsSquare, vizFoot, vizHead)));
        if (st.CoverBleed is { } bleed && m.Kind == MiniFaceKind.Cover)
            children.Add(new BoxEl
            {
                Key = H.BleedKey, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(H.BleedX, H.BleedY, 0f, 0f),
                Opacity = H.BleedAlpha, HitTestVisible = false, Children = [bleed(m.P.ArtistText(), w)],
            });
        if (lyrics)
        {
            float lh = h - H.SquareLyricTop - H.SquareLyricFoot;
            children.Add(new BoxEl
            {
                Key = H.LyricsKey, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 1, HitTestVisible = false,
                Margin = new Edges4(0f, H.SquareLyricTop, 0f, H.SquareLyricFoot),
                Children = [LyricWindow(m with { Media = true }, lh, H.LyricAnchorSquare, H.SquareRestInset, lh * H.LyricFadeFoot, onDark: true)],
            });
        }
        if (dark) children.Add(Shade(st));

        if (m.Hot)
        {
            float scrimTop = MathF.Max(0f, (rises ? risenTop : controlsTop) - H.HoverScrimLead);
            var tone = dark ? st.Shade() : st.Solid(m.Accent);
            var deep = dark ? tone with { A = H.HoverScrimAlpha } : tone;
            children.Add(new BoxEl
            {
                Key = H.ScrimKey + ":foot", Height = h - scrimTop, AlignSelf = FlexAlign.End, Enter = s_fade, Exit = s_fade, HitTestVisible = false,
                Gradient = new GradientSpec(GradientShape.Linear, H.ShadeAngle,
                    [new GradientStop(0f, tone with { A = 0f }), new GradientStop(H.HoverScrimLead / MathF.Max(1f, h - scrimTop), tone), new GradientStop(1f, deep)]),
            });
            if (!obj)
                children.Add(new BoxEl
                {
                    Key = H.ScrimKey + ":top", Height = H.TopScrimH, AlignSelf = FlexAlign.Start, Enter = s_fade, Exit = s_fade, HitTestVisible = false,
                    Gradient = new GradientSpec(GradientShape.Linear, H.ShadeAngle,
                        [new GradientStop(0f, H.HudPlate with { A = H.TopScrimAlpha }), new GradientStop(1f, H.HudPlate with { A = 0f })]),
                });
        }
        Element[] title = rises
            ? [TitleRow(fm, m.Hot), new BoxEl { Height = m.Hot ? rise : 0f, Shrink = 0f, HitTestVisible = false, Animate = s_rise }]
            : m.Hot ? [] : [TitleRow(fm, hot: false) with { Key = H.TitleKey + ":row", Enter = s_fade, Exit = s_fade }];
        children.Add(new BoxEl
        {
            Key = H.TitleKey, AlignSelf = FlexAlign.End, Direction = 1, HitTestVisible = false,
            Padding = new Edges4(H.SquareRestInset, 0f, H.SquareRestInset, H.SquareRestFoot), Children = title,
        });
        if (m.Hot)
            children.Add(new BoxEl
            {
                Key = H.HoverKey, AlignSelf = FlexAlign.End, Direction = 1, Gap = H.SquareHoverRowGap, Enter = s_fade, Exit = s_fade,
                Padding = new Edges4(H.SquareRestInset, 0f, H.SquareRestInset, H.SquareHoverFoot),
                Children = [TransportRow(fm, foot: 0f), SeekRow(fm)],
            });
        else
            children.Add(Edge(m));
        if (m.Hot) children.Add(WindowButtons(m, glass));
        return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
    }

    /// <summary>The filling form's title row: the title block, then the saved heart and the play badge at rest. On hover the two leave (the
    /// transport row shows both states) from a slot that keeps its width, so the title is never re-trimmed.</summary>
    static BoxEl TitleRow(Ctx m, bool hot)
    {
        var st = m.St;
        var marks = new BoxEl
        {
            Width = H.SavedGlyph + st.Gap + st.Badge, Height = st.Badge, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
            Children = hot
                ? []
                : [new BoxEl { Key = H.RestKey, Direction = 0, AlignItems = FlexAlign.End, Gap = st.Gap, Enter = s_fade, Exit = s_fade, HitTestVisible = false,
                    Children = [Saved(m), Badge(m, inline: true)] }],
        };
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.End, Gap = st.Gap, HitTestVisible = false, Children = [Meta(m, detail: false), marks] };
    }

    static Element Tall(Ctx ctx)
    {
        var st = ctx.St;
        bool immersive = ctx.Kind is MiniFaceKind.Canvas or MiniFaceKind.Visualizer or MiniFaceKind.Lyrics;
        float w = ctx.Size.Width, h = ctx.Size.Height;
        if (immersive)
        {
            var m = ctx with { Media = true };
            bool lyrics = m.Kind == MiniFaceKind.Lyrics;
            // The panel holds the column's own rows (title block with its detail line, seek, transport, volume when roomy) at the column's
            // own gaps: a change between a boxed face and a filling one moves nothing.
            float panel = (m.Roomy ? H.TallFixedRoomy : H.TallFixed) - H.TallPadTop;
            var children = new List<Element>(6)
            {
                FaceLayer(m, w, h, MiniFaceUse.Fill, paint: VizPaint(m, H.VizBarsTall, panel)),
                Scrim(),
            };
            if (lyrics)
                children.Add(new BoxEl
                {
                    Key = H.LyricsKey, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 1, HitTestVisible = false,
                    Margin = new Edges4(0f, H.TallLyricTop, 0f, panel),
                    Children = [LyricWindow(m, MathF.Max(0f, h - H.TallLyricTop - panel), H.LyricAnchorTall, st.Pad,
                        MathF.Max(0f, h - H.TallLyricTop - panel) * H.LyricFadeFoot, onDark: true)],
                });
            var panelRows = new List<Element>(4) { Meta(m, detail: true, column: true), SeekRow(m), TransportRow(m, foot: H.CtrlBleedBottom) };
            if (m.Roomy) panelRows.Add(VolumeRow(m));
            children.Add(new BoxEl
            {
                AlignSelf = FlexAlign.End, Direction = 1, Gap = H.TallGap, Padding = new Edges4(st.Pad, 0f, st.Pad, H.TallPadFoot),
                Children = panelRows.ToArray(),
            });
            if (m.Hot) children.Add(WindowButtons(m, glass: true));
            return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
        }
        var c = ctx;
        float art = MathF.Max(0f, MathF.Min(w - 2f * st.Pad, h - (c.Roomy ? H.TallFixedRoomy : H.TallFixed)));
        var rows = new List<Element>(5)
        {
            // the window buttons sit inside the art's top-right corner on glass (never straddling its edge)
            new BoxEl { Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [FaceBox(c, art, art, chrome: c.Hot)] },
            Meta(c, detail: true, column: true),
            SeekRow(c),
            TransportRow(c, foot: H.CtrlBleedBottom),
        };
        if (c.Roomy) rows.Add(VolumeRow(c));
        var all = new List<Element>(2);
        if (st.GroundBand && c.Kind == MiniFaceKind.Object) all.Add(WaveBand(c, w, h * H.WaveBandShare));
        all.Add(new BoxEl { Grow = 1f, Direction = 1, Gap = H.TallGap, Padding = new Edges4(st.Pad, H.TallPadTop, st.Pad, H.TallPadFoot), Children = rows.ToArray() });
        return new BoxEl { Grow = 1f, ZStack = true, Children = all.ToArray() };
    }

    /// <summary>The full transport as ONE row of five boxes spread across the column (save · previous · play · next · device), bleeding by the
    /// controls' bleed into the padding so the glyphs line up with the text: at the least width (232) all five stay inside the window.</summary>
    static Element TransportRow(Ctx m, float foot) => new BoxEl
    {
        Key = H.TransportKey, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
        Margin = new Edges4(-H.HeartBleed(m.St.Heart, H.CtrlBleedBottom), 0f, -H.CtrlBleedBottom, -foot),
        Children = [SaveButton(m), Prev(m), Play(m, m.St.PlayBigBox), Next(m), Device(m)],
    };
}
