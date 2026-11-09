// ── Themes/Common/CommonMiniPlayer.cs — the mini player's picture (#197) ────────────────────────────────────────────────
//
// One picture for the three themes, drawn with each theme's MiniPlayerStyle (StyleKeys.MiniPlayer: its numbers, its type, its
// grounds) and its own player parts (the seek rail and time labels are the theme's `Player.SeekRail` / `Player.TimeLabel` through the
// bar's builders), so WinUI, Zune and Classic each look like themselves while the four shapes stay one layout (the approved prototype):
//
//   Strip  6:1   thumb + badge, title and artist (or the lyric pair), the saved heart, a hairline; on hover the transport slides in.
//   Wide   12:5  the face box, title / artist / detail, seek with times, save · prev · play · next · device; on hover face flip + chrome.
//   Square 1:1   the face fills; title, artist, heart and badge at the foot, a hairline; on hover the whole transport over a veil.
//   Tall   9:16  the face above (or, for Canvas / Visualizer / Lyrics, behind) title, seek, transport and device.
//
// State never hides (filled heart, play badge, "Playing on"); controls hide only where the window is smaller than them (Strip,
// Square). Every transport and save target is the style's ButtonBox (40 DIP or more); the hover window buttons are ChromeBox (32).
// Hover is a signal with a delayed fall (in at once, out after HoverOutDelayMs); the hover layers mount and unmount with a fade.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Pal;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using H = Wavee.Themes.Common.CommonMiniPlayerRules;

namespace Wavee.Themes.Common;

internal static class CommonMiniPlayer
{
    public static void Register(ViewRegistryBuilder views, string templateId, MiniPlayerStyle style)
    {
        views.Add(ViewKeys.MiniPlayer, Template, templateId);
        views.AddStyle(StyleKeys.MiniPlayer, style);
    }

    static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

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
        var deviceAnchor = rc.UseRef<NodeHandle>(default);
        var deviceMenu = rc.UseRef<OverlayHandle?>(null);
        var optionsAnchor = rc.UseRef<NodeHandle>(default);
        var optionsMenu = rc.UseRef<OverlayHandle?>(null);
        var flipFrom = rc.UseRef(face);
        bool light = R.IsLight;
        rc.UseLayoutEffect(() => p.UseMaterial(light ? st.MaterialLight : st.MaterialDark, !light),
            DepKey.From(light ? 1 : 0, (int)st.MaterialLight, (int)st.MaterialDark, 0));
        bool hovering = hot.Value;
        int flipDir = face == flipFrom.Value ? 0 : (face > flipFrom.Value ? 1 : -1);
        rc.UseLayoutEffect(() => flipFrom.Value = face, face);

        var m = new Ctx(p, bar, v, st, shape, face, kind, size, hovering, flipDir, overlay, deviceAnchor, deviceMenu, optionsAnchor, optionsMenu);
        Element body = shape switch
        {
            MiniShape.Strip => Strip(in m),
            MiniShape.Square => Square(in m),
            MiniShape.Tall => Tall(in m),
            _ => Wide(in m),
        };
        return new BoxEl
        {
            Key = H.RootKey, Grow = 1f, AlignSelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true, Fill = st.Ground(),
            OnPointerMoveWithin = _ => { hotFall.Cancel(); if (!hot.Peek()) hot.Value = true; },
            OnPointerExit = () => hotFall.RestartIn(st.HoverOutDelayMs),
            OnPointerWheel = e => p.WheelVolume(e.Steps),
            Children = [body],
        };
    }

    /// <summary>Everything one render reads, passed by reference to the shape builders.</summary>
    readonly record struct Ctx(MiniPlayerProps P, PlayerBarProps Bar, PlayerBarView V, MiniPlayerStyle St, MiniShape Shape, int Face,
        MiniFaceKind Kind, Size2 Size, bool Hot, int FlipDir, IOverlayService Overlay, Ref<NodeHandle> DeviceAnchor,
        Ref<OverlayHandle?> DeviceMenu, Ref<NodeHandle> OptionsAnchor, Ref<OverlayHandle?> OptionsMenu, bool Media = false)
    {
        /// <summary>The ink a control draws in: on-media white over a filling face, else the theme's.</summary>
        public ColorF Ink => Media ? H.OnMediaInk : Tok.T.TextPrimary;
    }

    // ══ SHAPES ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Strip(in Ctx m)
    {
        var st = m.St;
        float thumb = st.StripThumb;
        bool lyrics = m.Kind == MiniFaceKind.Lyrics;
        Element meta = lyrics
            ? new BoxEl { Key = H.MetaKey, Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.Center, ClipToBounds = true,
                Children = [m.P.FaceView(new MiniFaceRequest(m.Face, 0f, 0f, null, 0f, MiniFaceUse.Line))] }
            : Meta(in m, detail: false);
        var row = new BoxEl
        {
            Grow = 1f, Direction = 0, AlignItems = FlexAlign.Center, Gap = st.StripGap, Padding = new Edges4(st.StripPadStart, 0f, st.Pad, 0f),
            Children = [Thumb(in m, thumb), meta, m.V.Liked ? Saved(st) : Spacer()],
        };
        var children = new List<Element> { row, Hairline(in m) };
        if (m.Hot)
            children.Add(new BoxEl
            {
                Key = H.HoverKey, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.End, Direction = 0, AlignItems = FlexAlign.Center,
                Padding = new Edges4(H.StripFade, 0f, st.Pad, 0f), Enter = s_fade, Exit = s_fade,
                Fill = st.Ground(),
                Children = [SaveButton(in m), Prev(in m), Play(in m, st.ButtonBox), Next(in m), Device(in m), Options(in m)],
            });
        return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
    }

    static Element Wide(in Ctx m)
    {
        var st = m.St;
        float box = MathF.Max(0f, m.Size.Height - 2f * st.Pad);
        var faceBox = FaceBox(in m, box, box, withFlip: true);
        var col = new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.SpaceBetween,
            Children =
            [
                m.Kind == MiniFaceKind.Lyrics
                    ? new BoxEl { Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
                        Children = [m.P.FaceView(new MiniFaceRequest(m.Face, 0f, 0f, null, 0f, MiniFaceUse.Line))] }
                    : Meta(in m, detail: true),
                SeekRow(in m),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
                    Children = [SaveButton(in m), Transport(in m, st.ButtonBox), Device(in m)],
                },
            ],
        };
        var children = new List<Element>
        {
            new BoxEl { Grow = 1f, Direction = 0, Gap = st.Gap, Padding = Edges4.All(st.Pad), Children = [faceBox, col] },
        };
        if (m.Hot) children.Add(Chrome(in m));
        return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
    }

    static Element Square(in Ctx m)
    {
        var st = m.St;
        var children = new List<Element>
        {
            FaceLayer(in m, m.Size.Width, m.Size.Height, MiniFaceUse.Fill),
            Shade(st),
        };
        var hm = m with { Media = true };
        if (!m.Hot)
        {
            children.Add(new BoxEl
            {
                Key = H.RestKey, AlignSelf = FlexAlign.End, Direction = 0, AlignItems = FlexAlign.End, Gap = st.Gap,
                Padding = Edges4.All(st.Pad), Enter = s_fade, Exit = s_fade, HitTestVisible = false,
                Children = [Meta(in m, detail: false, onMedia: true), m.V.Liked ? Saved(st, onMedia: true) : Spacer(), Badge(in m)],
            });
            children.Add(Hairline(in m));
        }
        else
            children.Add(new BoxEl
            {
                Key = H.HoverKey, Grow = 1f, Direction = 1, Justify = FlexJustify.SpaceBetween, Padding = Edges4.All(st.Pad), Fill = st.Veil(),
                Enter = s_fade, Exit = s_fade,
                Children =
                [
                    new BoxEl { Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center,
                        Children = [FlipPair(in hm), ChromeButtons(in hm)] },
                    new BoxEl { Direction = 0, Justify = FlexJustify.Center, Children = [Transport(in hm, st.PlayBox)] },
                    new BoxEl { Direction = 1, Children = [SeekRow(in hm),
                        new BoxEl { Direction = 0, Justify = FlexJustify.SpaceBetween, Children = [SaveButton(in hm), Device(in hm)] }] },
                ],
            });
        return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
    }

    static Element Tall(in Ctx ctx)
    {
        var st = ctx.St;
        bool immersive = ctx.Kind is MiniFaceKind.Canvas or MiniFaceKind.Visualizer or MiniFaceKind.Lyrics;
        var m = immersive ? ctx with { Media = true } : ctx;
        var panel = new BoxEl
        {
            Direction = 1, Gap = st.Gap,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = st.Gap, Children = [Meta(in m, detail: false, onMedia: immersive), SaveButton(in m)] },
                SeekRow(in m),
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
                    Children = [Spacer(st.ButtonBox), Transport(in m, st.PlayBox), Device(in m)] },
            ],
        };
        if (immersive)
        {
            var children = new List<Element>
            {
                FaceLayer(in m, m.Size.Width, m.Size.Height, MiniFaceUse.Fill),
                Shade(st),
                new BoxEl { AlignSelf = FlexAlign.End, Padding = new Edges4(st.Pad, 0f, st.Pad, st.Pad), Children = [panel] },
            };
            if (m.Hot) children.Add(Chrome(in m));
            return new BoxEl { Grow = 1f, ZStack = true, Children = children.ToArray() };
        }
        float art = MathF.Max(0f, MathF.Min(m.Size.Width - 2f * st.Pad, m.Size.Height * H.TallArtShare));
        var column = new BoxEl
        {
            Grow = 1f, Direction = 1, Gap = st.Gap, Padding = Edges4.All(st.Pad),
            Children = [new BoxEl { Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [FaceBox(in m, art, art, withFlip: false)] }, panel],
        };
        var all = new List<Element> { column };
        if (m.Hot) all.Add(Chrome(in m));
        return new BoxEl { Grow = 1f, ZStack = true, Children = all.ToArray() };
    }

    // ══ PARTS ════════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Meta(in Ctx m, bool detail, bool onMedia = false)
    {
        var st = m.St;
        string title = m.Bar.TitleText();
        string artist = m.P.ArtistText();
        TextEl t = st.Title(title, m.Shape), a = st.Artist(artist);
        if (onMedia) { t = t with { Color = H.OnMediaInk }; a = a with { Color = H.OnMediaInk with { A = H.OnMediaSecondaryAlpha } }; }
        var lines = new List<Element> { t, a };
        if (detail && m.P.DetailText() is { Length: > 0 } d) lines.Add(st.Detail(d));
        return new BoxEl { Key = H.MetaKey, Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, Gap = H.LineGap, Children = lines.ToArray() };
    }

    static Element Thumb(in Ctx m, float side) => new BoxEl
    {
        Width = side, Height = side, Shrink = 0f, ZStack = true, Cursor = CursorId.Hand, OnClick = m.P.BackToWavee,
        Role = AutomationRole.Button,
        Children = [m.P.FaceView(new MiniFaceRequest(m.Face, side, side, m.V.ArtUrl, m.St.ArtCorner, MiniFaceUse.Thumb)), Badge(in m)],
    };

    static Element FaceBox(in Ctx m, float w, float h, bool withFlip)
    {
        var children = new List<Element> { FaceLayer(in m, w, h, MiniFaceUse.Box) };
        if (withFlip && m.Hot) children.Add(new BoxEl { AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Center, Enter = s_fade, Exit = s_fade,
            Fill = m.St.ChromePlate(), Corners = CornerRadius4.All(m.St.ChromeBox * 0.5f), Children = [FlipPair(in m)] });
        return new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(m.St.ArtCorner),
            Cursor = CursorId.Hand, OnClick = m.P.BackToWavee, Role = AutomationRole.Button,
            Children = children.ToArray(),
        };
    }

    /// <summary>The face, keyed by its index so a flip slides the new one in (from the side the flip went).</summary>
    static Element FaceLayer(in Ctx m, float w, float h, MiniFaceUse use)
    {
        float corner = use == MiniFaceUse.Fill ? 0f : m.St.ArtCorner;
        var enter = m.FlipDir == 0 ? (EnterExit?)null : new EnterExit(Dx: m.FlipDir * w * H.FlipSlide, Opacity: 0f, Active: true);
        return new BoxEl
        {
            Key = H.FaceKey + ":" + m.Face, Width = w, Height = h, ClipToBounds = true, Enter = enter, Exit = s_fade, HitTestVisible = false,
            Children = [m.P.FaceView(new MiniFaceRequest(m.Face, w, h, m.V.ArtUrl, corner, use))],
        };
    }

    static Element SeekRow(in Ctx m) => new BoxEl
    {
        Key = H.SeekKey, Height = m.St.ButtonBox, Direction = 0, AlignItems = FlexAlign.Center, Gap = H.SeekGap,
        Children =
        [
            new BoxEl { MinWidth = H.TimeMinW, Children = [m.P.TimeLabel(false, m.Media ? H.OnMediaInk : null)] },
            new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [m.Bar.SeekBar()] },
            new BoxEl { MinWidth = H.TimeMinW, AlignItems = FlexAlign.End, Children = [m.P.TimeLabel(true, m.Media ? H.OnMediaInk : null)] },
        ],
    };

    static Element Hairline(in Ctx m) => m.Bar.ProgressLine is { } line
        ? new BoxEl { AlignSelf = FlexAlign.End, HitTestVisible = false, Children = [line()] }
        : Spacer();

    static Element Transport(in Ctx m, float playBox) => new BoxEl
    {
        Key = H.TransportKey, Direction = 0, AlignItems = FlexAlign.Center, Children = [Prev(in m), Play(in m, playBox), Next(in m)],
    };

    static Element Prev(in Ctx m)
    {
        var verbs = m.Bar.Verbs;
        return Btn(m.St, Icons.Previous, () => verbs.Previous(), m.V.PrevEnabled && m.V.CanTransport, "", ink: m.Ink);
    }

    static Element Next(in Ctx m)
    {
        var verbs = m.Bar.Verbs;
        return Btn(m.St, Icons.Next, () => verbs.Next(), m.V.NextEnabled && m.V.CanTransport, "", ink: m.Ink);
    }

    static Element Play(in Ctx m, float box)
    {
        var st = m.St;
        var verbs = m.Bar.Verbs;
        var primary = m.V.Primary;
        Action? click = primary switch
        {
            PrimaryVerb.TogglePlay => () => verbs.TogglePlay("mini"),
            PrimaryVerb.Retry => () => verbs.Retry(),
            _ => null,
        };
        return new BoxEl
        {
            Key = H.PlayKey, Width = box, Height = box, Shrink = 0f, Corners = CornerRadius4.All(st.PlayCorner),
            Fill = Tok.T.AccentDefault, HoverFill = Tok.T.AccentSecondary, PressedFill = Tok.T.AccentTertiary,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Cursor = CursorId.Hand, OnClick = click,
            Role = AutomationRole.Button, Focusable = click is not null,
            Children = [Glyph(m.V.PauseGlyph ? Icons.Pause : Icons.Play, st.PlayGlyph, Tok.T.TextOnAccentPrimary)],
        };
    }

    static Element SaveButton(in Ctx m)
    {
        var verbs = m.Bar.Verbs;
        var icon = m.V.FaceIsEpisode ? ActionIcons.Resolve(ActionIcons.Save, m.V.Liked) : ActionIcons.Resolve(ActionIcons.Heart, m.V.Liked);
        return Btn(m.St, icon.Glyph ?? Icons.Heart, () => verbs.ToggleLike(), m.V.LikeEnabled, "", lit: m.V.Liked && !m.Media, ink: m.Ink) with { Key = H.SaveKey };
    }

    static Element Saved(MiniPlayerStyle st, bool onMedia = false) => new BoxEl
    {
        Width = st.ButtonGlyph, Height = st.ButtonGlyph, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [Glyph(Icons.HeartFill, st.ChromeGlyph, onMedia ? H.OnMediaInk : Tok.T.AccentTextPrimary)],
    };

    static Element Device(in Ctx m)
    {
        var bar = m.Bar;
        var overlay = m.Overlay;
        var anchor = m.DeviceAnchor;
        var menu = m.DeviceMenu;
        bool remote = bar.DeviceIsRemote();
        return Btn(m.St, Icons.Devices, () => bar.ToggleDevicePicker(overlay, anchor, menu), true, "", lit: remote && !m.Media,
            onRealized: h => anchor.Value = h, ink: m.Ink) with { Key = H.DeviceKey };
    }

    static Element Options(in Ctx m)
    {
        var p = m.P;
        var overlay = m.Overlay;
        var anchor = m.OptionsAnchor;
        var menu = m.OptionsMenu;
        var st = m.St;
        return ChromeBtn(st, Icons.More, () => OpenOptions(p, st, overlay, anchor, menu), Loc.Get(Strings.Mini.Options), m.Ink, h => anchor.Value = h)
            with { Key = H.OptionsKey };
    }

    static Element Chrome(in Ctx m) => new BoxEl
    {
        Key = H.ChromeKey, Height = m.St.ChromeBox, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = Edges4.All(m.St.Gap * 0.5f),
        Fill = m.St.ChromePlate(), Corners = CornerRadius4.All(m.St.ButtonCorner), Enter = s_fade, Exit = s_fade,
        Children = [ChromeButtons(in m)],
    };

    static Element ChromeButtons(in Ctx m) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center,
        Children = [Options(in m), ChromeBtn(m.St, Icons.BackToWindow, m.P.BackToWavee, Loc.Get(Strings.Mini.Back), m.Ink) with { Key = H.BackKey }],
    };

    static Element FlipPair(in Ctx m)
    {
        var p = m.P;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            Children =
            [
                ChromeBtn(m.St, Icons.ChevronLeft, () => p.StepFace(-1), Loc.Get(Strings.Mini.PrevFace), m.Ink),
                ChromeBtn(m.St, Icons.ChevronRight, () => p.StepFace(1), Loc.Get(Strings.Mini.NextFace), m.Ink),
            ],
        };
    }

    static Element Badge(in Ctx m)
    {
        var st = m.St;
        return new BoxEl
        {
            Width = st.Badge, Height = st.Badge, Shrink = 0f, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End,
            Corners = CornerRadius4.All(st.PlayCorner), Fill = Tok.T.AccentDefault, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = [Glyph(m.V.PauseGlyph ? Icons.Pause : Icons.Play, st.Badge * H.BadgeGlyph, Tok.T.TextOnAccentPrimary)],
        };
    }

    static Element Shade(MiniPlayerStyle st) => new BoxEl
    {
        Grow = 1f, HitTestVisible = false,
        Gradient = new GradientSpec(GradientShape.Linear, H.ShadeAngle, [new GradientStop(H.ShadeFrom, st.Shade() with { A = 0f }), new GradientStop(1f, st.Shade())]),
    };

    static Element Spacer(float w = 0f) => new BoxEl { Width = w, Height = 0f, Shrink = 0f, HitTestVisible = false };

    static Element Glyph(string glyph, float size, ColorF ink)
        => new TextEl(glyph) { Size = size, FontFamily = Tok.Definition.Fonts.Icon, Color = ink };

    static Element Btn(MiniPlayerStyle st, string glyph, Action? onClick, bool enabled, string tip, bool lit = false, Action<NodeHandle>? onRealized = null,
        ColorF? ink = null)
    {
        ColorF baseInk = ink ?? Tok.T.TextPrimary;
        ColorF glyphInk = !enabled ? baseInk with { A = baseInk.A * H.DisabledAlpha } : lit ? Tok.T.AccentTextPrimary : baseInk;
        var b = new BoxEl
        {
            Width = st.ButtonBox, Height = st.ButtonBox, Shrink = 0f, Corners = CornerRadius4.All(st.ButtonCorner),
            Fill = ColorF.Transparent, HoverFill = Tok.T.FillSubtleSecondary, PressedFill = Tok.T.FillSubtleTertiary,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Cursor = enabled ? CursorId.Hand : null,
            OnClick = enabled ? onClick : null, Role = AutomationRole.Button, Focusable = enabled, OnRealized = onRealized,
            Children = [Glyph(glyph, st.ButtonGlyph, glyphInk)],
        };
        return tip.Length > 0 ? ToolTip.Wrap(b, tip) : b;
    }

    static Element ChromeBtn(MiniPlayerStyle st, string glyph, Action onClick, string tip, ColorF ink, Action<NodeHandle>? onRealized = null)
    {
        var b = new BoxEl
        {
            Width = st.ChromeBox, Height = st.ChromeBox, Shrink = 0f, Corners = CornerRadius4.All(st.ButtonCorner),
            Fill = ColorF.Transparent, HoverFill = Tok.T.FillSubtleSecondary, PressedFill = Tok.T.FillSubtleTertiary,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Cursor = CursorId.Hand, OnClick = onClick,
            Role = AutomationRole.Button, Focusable = true, OnRealized = onRealized,
            Children = [Glyph(glyph, st.ChromeGlyph, ink)],
        };
        return ToolTip.Wrap(b, tip);
    }

    // ══ OPTIONS ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    static readonly MiniShape[] s_shapes = [MiniShape.Strip, MiniShape.Wide, MiniShape.Square, MiniShape.Tall];

    static string ShapeName(MiniShape s) => s switch
    {
        MiniShape.Strip => Loc.Get(Strings.Mini.Strip),
        MiniShape.Square => Loc.Get(Strings.Mini.Square),
        MiniShape.Tall => Loc.Get(Strings.Mini.Tall),
        _ => Loc.Get(Strings.Mini.Wide),
    };

    static void OpenOptions(MiniPlayerProps p, MiniPlayerStyle st, IOverlayService overlay, Ref<NodeHandle> anchor, Ref<OverlayHandle?> menu)
    {
        if (menu.Value is { IsOpen: true } open) { open.Close(); return; }
        menu.Value = overlay.Open(() => anchor.Value, () => OptionsBody(p, st, menu), FlyoutPlacement.BottomEdgeAlignedRight,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
        menu.Value.ClosedAction = () => menu.Value = null;
    }

    static Element OptionsBody(MiniPlayerProps p, MiniPlayerStyle st, Ref<OverlayHandle?> menu)
    {
        var rows = new List<Element> { Head(Loc.Get(Strings.Mini.Shape)) };
        foreach (var s in s_shapes)
        {
            var shape = s;
            rows.Add(Row(ShapeName(shape), p.Shape.Peek() == shape, () => { p.SetShape(shape); menu.Value?.Close(); }));
        }
        rows.Add(Head(Loc.Get(Strings.Mini.Face)));
        for (int i = 0; i < p.FaceCount; i++)
        {
            int f = i;
            rows.Add(Row(p.FaceName(f), Math.Max(0, p.Face.Peek()) == f, () => p.SetFace(f)));
        }
        rows.Add(Row(Loc.Get(Strings.Mini.OnTop), p.OnTop.Peek(), () => { p.ToggleOnTop(); menu.Value?.Close(); }));
        return new BoxEl { Width = H.OptionsW, Direction = 1, Padding = Edges4.All(H.OptionsPad), Children = rows.ToArray() };

        Element Head(string text) => new BoxEl { Padding = Edges4.All(H.OptionsPad), Children = [st.Detail(text)] };

        Element Row(string text, bool on, Action act) => new BoxEl
        {
            Height = H.OptionsRow, Direction = 0, AlignItems = FlexAlign.Center, Gap = H.SeekGap, Padding = new Edges4(H.OptionsPad, 0f, H.OptionsPad, 0f),
            Corners = CornerRadius4.All(st.ButtonCorner), Fill = ColorF.Transparent, HoverFill = Tok.T.FillSubtleSecondary,
            Cursor = CursorId.Hand, OnClick = act, Role = AutomationRole.MenuItem, Focusable = true,
            Children = [new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [st.Artist(text) with { Color = on ? Tok.T.AccentTextPrimary : Tok.T.TextPrimary }] },
                on ? Glyph(Icons.Accept, st.ChromeGlyph, Tok.T.AccentTextPrimary) : Spacer()],
        };
    }
}
