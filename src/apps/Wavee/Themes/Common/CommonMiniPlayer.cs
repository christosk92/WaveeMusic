// ── Themes/Common/CommonMiniPlayer.cs — the mini player's picture (#197) ────────────────────────────────────────────────
//
// One layout for the three themes, drawn with each theme's MiniPlayerStyle (StyleKeys.MiniPlayer: its numbers, its type, its grounds and
// its own seek line, visualizer and lyric line), so WinUI, Zune and Classic each look like themselves while the four shapes stay one layout
// (the approved prototype, scratchpad miniplayer/index.html, 2026-11-09):
//
//   Strip  6:1   thumb + badge, title and artist (or the lyric ticker), the saved heart, the edge line; on hover a right-anchored plate
//                fades in over the row (save · prev · play · next · device · options) and the edge line thickens into a seek bar.
//   Wide   12:5  the face box, title / artist / detail (or the lyric window), seek with times, save · prev · play · next · device; on hover
//                the ‹ › flip on the art and the options / back pill.
//   Square 1:1   the face fills; title, artist, heart and badge at the foot, the edge line; on hover the whole transport over a veil, in
//                on-dark ink in every mode. Object faces and Classic's wave-edged cover sit above a band of the window's own ground.
//   Tall   9:16  the face above (Canvas / Visualizer / Lyrics: behind) title, seek, transport and device; volume when there is room.
//
// The accent is the theme's (WinUI's, Zune's pink), or the album's with "Colour from album art" (Classic's default). State never hides
// (filled heart, play badge, "Playing on"); controls hide only where the window is smaller than them (Strip, Square). Every transport and
// save target is the style's ButtonBox (40 DIP or more); the hover window buttons are ChromeBox (32). Hover is a signal with a delayed
// fall (in at once, out after HoverOutDelayMs); the hover layers mount and unmount with a fade.

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

    /// <summary>The flyouts' anchors and handles (one set per window).</summary>
    sealed class Anchors
    {
        public readonly Ref<NodeHandle> Device = new(default), Options = new(default);
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
        var flipFrom = rc.UseRef(face);
        bool light = R.IsLight;
        rc.UseLayoutEffect(() => p.UseMaterial(light ? st.MaterialLight : st.MaterialDark, !light),
            DepKey.From(light ? 1 : 0, (int)st.MaterialLight, (int)st.MaterialDark, 0));
        bool hovering = hot.Value || anchors.OptionsMenu.Value is { IsOpen: true } || anchors.DeviceMenu.Value is { IsOpen: true };
        int flipDir = face == flipFrom.Value ? 0 : (face > flipFrom.Value ? 1 : -1);
        rc.UseLayoutEffect(() => flipFrom.Value = face, face);

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

        var m = new Ctx(p, bar, v, st, shape, face, kind, size, hovering, flipDir, overlay, anchors, accent, OnAccent(accent), roomy);
        Element body = shape switch
        {
            MiniShape.Strip => Strip(m),
            MiniShape.Square => Square(m),
            MiniShape.Tall => Tall(m),
            _ => Wide(m),
        };
        var children = new List<Element>(2) { body };
        if (hud.Value) children.Add(Hud(bar));
        return new BoxEl
        {
            Key = H.RootKey, Grow = 1f, AlignSelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true, Fill = st.Ground(accent),
            OnPointerMoveWithin = _ => { hotFall.Cancel(); if (!hot.Peek()) hot.Value = true; },
            OnPointerExit = () => hotFall.RestartIn(st.HoverOutDelayMs),
            OnPointerWheel = e => p.WheelVolume(e.Steps),
            Children = children.ToArray(),
        };
    }

    /// <summary>Everything one render reads, passed to the shape builders.</summary>
    sealed record Ctx(MiniPlayerProps P, PlayerBarProps Bar, PlayerBarView V, MiniPlayerStyle St, MiniShape Shape, int Face,
        MiniFaceKind Kind, Size2 Size, bool Hot, int FlipDir, IOverlayService Overlay, Anchors A, ColorF Accent, ColorF OnAccent, bool Roomy,
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

    // ══ SHAPES ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Strip(Ctx m)
    {
        var st = m.St;
        bool lyrics = m.Kind == MiniFaceKind.Lyrics;
        Element meta = lyrics ? m.P.Lyrics(frame => Embed.Comp(new TickerProps(frame, m, Meta(m, detail: false)), static () => new Ticker()))
                              : Meta(m, detail: false);
        var row = new BoxEl
        {
            Grow = 1f, Direction = 0, AlignItems = FlexAlign.Center, Gap = st.StripGap, Padding = new Edges4(st.StripPadStart, 0f, st.Pad, 0f),
            Children = [Thumb(m), new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, Children = [meta] }, Saved(m)],
        };
        var children = new List<Element>(4);
        if (m.Kind == MiniFaceKind.Visualizer)
            children.Add(new BoxEl
            {
                Key = H.FaceKey + ":strip", Grow = 1f, AlignSelf = FlexAlign.Stretch, Opacity = H.StripVizAlpha, HitTestVisible = false,
                Children = [m.P.FaceView(new MiniFaceRequest(m.Face, m.Size.Width, m.Size.Height, m.V.ArtUrl, 0f, MiniFaceUse.Fill, VizPaint(m, H.VizBarsStrip)))],
            });
        children.Add(row);
        children.Add(Edge(m));
        if (m.Hot)
        {
            var solid = st.Solid(m.Accent);
            float w = H.StripFadeLead + 6f * st.ButtonBox + H.StripOverlayEnd;
            children.Add(new BoxEl
            {
                Key = H.HoverKey, Width = w, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.End, Direction = 0, AlignItems = FlexAlign.Center,
                Margin = new Edges4(0f, 0f, 0f, H.StripOverlayFoot), Padding = new Edges4(H.StripFadeLead, 0f, H.StripOverlayEnd, 0f),
                Enter = s_fade, Exit = s_fade,
                Gradient = new GradientSpec(GradientShape.Linear, 0f, [new GradientStop(0f, solid with { A = 0f }), new GradientStop(H.StripFade / w, solid), new GradientStop(1f, solid)]),
                Children = [SaveButton(m), Prev(m), Play(m, st.ButtonBox), Next(m), Device(m), OptionsButton(m, st.ButtonBox)],
            });
        }
        return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
    }

    static Element Wide(Ctx m)
    {
        var st = m.St;
        float box = MathF.Max(0f, m.Size.Height - 2f * st.Pad);
        bool lyrics = m.Kind == MiniFaceKind.Lyrics;
        float chromeW = 2f * st.ChromeBox + 2f * H.ChromePad + H.ChromeGap + H.ChromeInset;
        Element top = lyrics
            ? new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, Gap = H.LineGap,
                Children =
                [
                    LyricMeta(m, reserve: m.Hot ? chromeW : 0f),
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, Margin = new Edges4(-LyricBleed(st), 0f, 0f, 0f),
                        Children = [LyricWindow(m, MathF.Max(0f, box - H.SeekRowH - st.ButtonBox), H.LyricAnchorWide, onDark: false)],
                    },
                ],
            }
            : Meta(m, detail: true, reserve: m.Hot ? chromeW : 0f, column: true);
        var col = new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.SpaceBetween, Gap = H.WideColGap,
            Children =
            [
                top,
                SeekRow(m),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
                    Margin = new Edges4(-H.CtrlBleedX, 0f, -H.CtrlBleedX, -H.CtrlBleedBottom),
                    Children = [SaveButton(m), Transport(m, st.PlayBox, 0f), Device(m)],
                },
            ],
        };
        var children = new List<Element>(2)
        {
            new BoxEl { Grow = 1f, Direction = 0, Gap = st.Gap, Padding = Edges4.All(st.Pad), Children = [FaceBox(m, box, box, flip: true), col] },
        };
        if (m.Hot) children.Add(Chrome(m, withPick: false));
        return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
    }

    static Element Square(Ctx m)
    {
        var st = m.St;
        bool lyrics = m.Kind == MiniFaceKind.Lyrics;
        bool obj = m.Kind == MiniFaceKind.Object;
        bool waveCover = st.WaveEdge && m.Kind == MiniFaceKind.Cover;
        bool dark = !obj && !waveCover;   // the face fills the window: the text and controls read on-dark
        float w = m.Size.Width, h = m.Size.Height;
        var children = new List<Element>(8);
        if (obj)
            children.Add(Glow(m, w, h - H.SquareObjectFoot, FaceLayer(m, w, h - H.SquareObjectFoot, MiniFaceUse.Box, corner: 0f)));
        else if (waveCover)
            children.Add(new BoxEl
            {
                Width = w, Height = h - H.SquareWaveFoot, AlignSelf = FlexAlign.Start, ZStack = true, HitTestVisible = false,
                Children = [FaceLayer(m, w, h - H.SquareWaveFoot, MiniFaceUse.Fill), WaveFoot(m, w, h - H.SquareWaveFoot)],
            });
        else
            children.Add(FaceLayer(m, w, h, MiniFaceUse.Fill, paint: VizPaint(m, H.VizBarsSquare)));
        if (st.CoverBleed is { } bleed && m.Kind == MiniFaceKind.Cover)
            children.Add(new BoxEl
            {
                Key = H.BleedKey, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(H.BleedX, H.BleedY, 0f, 0f),
                Opacity = H.BleedAlpha, HitTestVisible = false, Children = [bleed(m.P.ArtistText(), w)],
            });
        if (lyrics)
        {
            float inset = MarkedInset(st);
            children.Add(new BoxEl
            {
                Key = H.LyricsKey, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 1, HitTestVisible = false,
                Margin = new Edges4(inset, H.SquareLyricTop, H.SquareRestInset, H.SquareLyricFoot),
                Children = [LyricWindow(m with { Media = true }, h - H.SquareLyricTop - H.SquareLyricFoot, H.LyricAnchorSquare, onDark: true)],
            });
        }
        if (dark) children.Add(Shade(m.St));
        var hm = m with { Media = true };
        if (!m.Hot)
        {
            var rm = dark ? hm : m;
            children.Add(new BoxEl
            {
                Key = H.RestKey, AlignSelf = FlexAlign.End, Direction = 0, AlignItems = FlexAlign.End, Gap = st.Gap,
                Padding = new Edges4(H.SquareRestInset, 0f, H.SquareRestInset, H.SquareRestFoot), Enter = s_fade, Exit = s_fade, HitTestVisible = false,
                Children = [Meta(rm, detail: false), Saved(rm), Badge(rm, inline: true)],
            });
            children.Add(Edge(m));
        }
        else
            children.Add(new BoxEl
            {
                Key = H.HoverKey, Grow = 1f, Direction = 1, Justify = FlexJustify.SpaceBetween, Fill = st.Veil(),
                Padding = new Edges4(H.SquareHoverPadX, H.SquareHoverTop, H.SquareHoverPadX, H.SquareHoverFoot), Enter = s_fade, Exit = s_fade,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center,
                        Children = [FlipPair(hm, withName: true), new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = H.SquareTopGap,
                            Children = [Device(hm, H.SquareTopButton), OptionsButton(hm, H.SquareTopButton), BackButton(hm, H.SquareTopButton)] }],
                    },
                    new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [Transport(hm, st.PlayBigBox, H.SquareMidGap, H.SquareMidGlyph)] },
                    new BoxEl
                    {
                        Direction = 1,
                        Children = [SeekRow(hm), new BoxEl { Direction = 0, Margin = new Edges4(-H.CtrlBleedBottom, 0f, -H.CtrlBleedBottom, -H.CtrlBleedBottom), Children = [SaveButton(hm)] }],
                    },
                ],
            });
        return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
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
            var children = new List<Element>(6)
            {
                FaceLayer(m, w, h, MiniFaceUse.Fill, paint: VizPaint(m, H.VizBarsTall)),
                Scrim(),
            };
            if (!m.Hot)
                children.Add(new BoxEl
                {
                    Key = H.LabelKey, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(H.TallLabelX, H.TallLabelY, 0f, 0f),
                    Enter = s_fade, Exit = s_fade, HitTestVisible = false, Children = [st.Label(m.P.FaceName(m.Face)) with { Color = m.Ink2 }],
                });
            if (lyrics)
                children.Add(new BoxEl
                {
                    Key = H.LyricsKey, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 1, HitTestVisible = false,
                    Margin = new Edges4(MarkedInset(st), H.TallLyricTop, st.Pad, H.TallLyricFoot),
                    Children = [LyricWindow(m, h - H.TallLyricTop - H.TallLyricFoot, H.LyricAnchorTall, onDark: true)],
                });
            children.Add(new BoxEl
            {
                AlignSelf = FlexAlign.End, Direction = 1, Gap = H.TallPanelGap, Padding = new Edges4(st.Pad, 0f, st.Pad, H.TallPadFoot),
                Children = [Meta(m, detail: false, column: true), SeekRow(m), TallTransport(m)],
            });
            if (m.Hot) children.Add(Chrome(m, withPick: true));
            return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
        }
        var c = ctx;
        float art = MathF.Max(0f, MathF.Min(w - 2f * st.Pad, h - (c.Roomy ? H.TallFixedRoomy : H.TallFixed)));
        var rows = new List<Element>(5)
        {
            new BoxEl { Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [FaceBox(c, art, art, flip: false)] },
            Meta(c, detail: true, column: true),
            SeekRow(c),
            TallTransport(c),
        };
        if (c.Roomy) rows.Add(VolumeRow(c));
        var all = new List<Element>(3);
        if (st.WaveEdge) all.Add(WaveBand(c, w, h * H.WaveBandShare));
        all.Add(new BoxEl { Grow = 1f, Direction = 1, Gap = H.TallGap, Padding = new Edges4(st.Pad, H.TallPadTop, st.Pad, H.TallPadFoot), Children = rows.ToArray() });
        if (c.Hot) all.Add(Chrome(c, withPick: true));
        return new BoxEl { Grow = 1f, ZStack = true, Children = all.ToArray() };
    }

    static Element TallTransport(Ctx m) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
        Margin = new Edges4(-H.CtrlBleedBottom, 0f, -H.CtrlBleedBottom, -H.CtrlBleedBottom),
        Children = [SaveButton(m), Transport(m, m.St.PlayBigBox, H.TallTransportGap), Device(m)],
    };
}
