// ── Themes/Common/CommonMiniPlayer.Parts.cs — the mini player's parts (#197) ───────────────────────────────────────────
//
// The pieces the four forms share: the title block (a Zune title bleeds past its column under a fade, never shrunk or cut), the faces
// and their boxes, the seek row with its times flush to the column and the edge line (drawn by the theme over the bar's own scrub host),
// the transport, the heart in the theme's way (plain, Zune's ring, Classic's plate), the badge, the window buttons, the lyric ticker,
// the lyrics window (the app's own lyrics view), the volume HUD. A control answers a right-click with nothing, so the window's own
// right-click (the options panel) never fires through it.

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
    // ══ TEXT ═════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The title, the artist (or "Playing on …" in the accent) and, with <paramref name="detail"/>, the detail line. The title line
    /// keeps <paramref name="reserve"/> DIP clear of the window's right edge (the window buttons); a bleeding title runs to there and fades
    /// out.</summary>
    static Element Meta(Ctx m, bool detail, float reserve = 0f, bool column = false)
    {
        var st = m.St;
        string title = m.Bar.TitleText();
        bool remote = m.Bar.DeviceIsRemote();
        // Without a detail line (the strip, the square, a short wide card) "Playing on …" takes the artist's line, in the accent: the title stays.
        bool onLine = remote && !detail && m.P.DetailText() is { Length: > 0 };
        TextEl a = onLine
            ? st.Artist(m.P.DetailText(), m.Accent, m.Media) with { Color = m.Media ? m.Ink : m.Accent }
            : st.Artist(m.P.ArtistText(), m.Accent, m.Media);
        var lines = new List<Element>(3) { Title(m, title, reserve), a };
        if (detail && m.P.DetailText() is { Length: > 0 } d)
            lines.Add(st.Detail(d) with { Color = remote ? (m.Media ? m.Ink : m.Accent) : m.Ink3 });
        return new BoxEl { Key = H.MetaKey, Grow = column ? 0f : 1f, Shrink = column ? 0f : 1f, MinWidth = 0f, Direction = 1, Gap = H.LineGap, Children = lines.ToArray() };
    }

    /// <summary>The title line: ellipsized in WinUI and Classic; in Zune large, light and lowercase, running past its column to the window's
    /// edge under a fade (owner rule: Zune display type is never shrunk or cut).</summary>
    static Element Title(Ctx m, string title, float reserve)
    {
        var st = m.St;
        var t = st.Title(title, m.Shape) with { Color = m.Ink };
        if (!st.TitleBleeds)
            return reserve <= st.Pad ? t : new BoxEl { Direction = 0, MinWidth = 0f, Margin = new Edges4(0f, 0f, reserve - st.Pad, 0f), Children = [t] };
        return new BoxEl
        {
            Direction = 0, MinWidth = 0f, ClipToBounds = true, Margin = new Edges4(0f, 0f, reserve - st.Pad, 0f),
            EdgeFade = new EdgeFadeSpec(EdgeMask.Right, st.TitleFade),
            Children = [t with { Shrink = 0f, MaxLines = 1, Trim = TextTrim.None }],
        };
    }

    /// <summary>The title block's height without its detail line (the title's and the artist's line boxes): what a filling form lifts.</summary>
    static float TitleBlockHeight(Ctx m)
    {
        var st = m.St;
        static float Line(TextEl t) => t.LineHeight > 0f ? t.LineHeight : t.Size * 1.35f;
        return Line(st.Title("", m.Shape)) + H.LineGap + Line(st.Artist("", m.Accent, m.Media));
    }

    // ══ FACES ════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The strip's thumb at <paramref name="side"/> (it grows with the strip's height), its badge, a click back to Wavee.</summary>
    static Element Thumb(Ctx m, float side)
    {
        return new BoxEl
        {
            Width = side, Height = side, Shrink = 0f, ZStack = true, Cursor = CursorId.Hand, OnClick = m.P.BackToWavee, Role = AutomationRole.Button,
            Children =
            [
                m.P.FaceView(new MiniFaceRequest(m.Face, side, side, m.V.ArtUrl, m.St.ThumbCorner, MiniFaceUse.Thumb)),
                Badge(m, inline: false),
            ],
        };
    }

    /// <summary>The art box (wide, tall): the face, a click back to Wavee, and with <paramref name="chrome"/> the window buttons on glass in its
    /// top-right corner (the tall column's boxed faces: the buttons read as the art's own, never straddling its edge).</summary>
    static Element FaceBox(Ctx m, float w, float h, bool chrome)
    {
        var children = new List<Element>(2) { FaceLayer(m, w, h, MiniFaceUse.Box, paint: VizPaint(m, H.VizBarsWide)) };
        if (chrome) children.Add(WindowButtons(m, glass: true));
        return new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(m.St.ArtCorner),
            Cursor = CursorId.Hand, OnClick = m.P.BackToWavee, Role = AutomationRole.Button,
            Children = children.ToArray(),
        };
    }

    /// <summary>The face, keyed by its index so a face change cross-fades the new one over the old.</summary>
    static BoxEl FaceLayer(Ctx m, float w, float h, MiniFaceUse use, Func<MiniVisualizerFrame, Element>? paint = null, float corner = -1f)
    {
        float c = corner >= 0f ? corner : use == MiniFaceUse.Fill ? 0f : m.St.ArtCorner;
        return new BoxEl
        {
            Key = H.FaceKey + ":" + m.Face, Width = w, Height = h, ClipToBounds = true, Enter = s_faceSwap, Exit = s_faceSwap, HitTestVisible = false,
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
            Children = [m.P.FaceView(new MiniFaceRequest(m.Face, w, h, m.V.ArtUrl, c, use, paint))],
        };
    }

    /// <summary>The theme's visualizer for this window (null for every other face, which keep the full player's picture). Its ground fills the
    /// box; its bars stand above <paramref name="foot"/>, where a filling layout writes its title and controls.</summary>
    static Func<MiniVisualizerFrame, Element>? VizPaint(Ctx m, int bars, float foot = 0f)
    {
        if (m.Kind != MiniFaceKind.Visualizer) return null;
        var st = m.St;
        var accent = m.Accent;
        return f => new BoxEl
        {
            Width = f.Width, Height = f.Height, ZStack = true, ClipToBounds = true, Fill = H.VizGround, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, AlignSelf = FlexAlign.Stretch, HitTestVisible = false,
                    Gradient = new GradientSpec(GradientShape.Linear, H.ShadeAngle, [new GradientStop(H.VizGlowFrom, accent with { A = 0f }), new GradientStop(1f, st.Tint2(accent))]),
                },
                new BoxEl
                {
                    Width = f.Width, Height = MathF.Max(0f, f.Height - foot), AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                    Children = [st.Visualizer(f with { Height = MathF.Max(0f, f.Height - foot), Bands = Pick(f.Bands, bars), Peaks = Pick(f.Peaks, bars) }, accent)],
                },
            ],
        };
    }

    /// <summary><paramref name="n"/> bands spread over the deck's (the lows on the left).</summary>
    static FloatSignal[] Pick(FloatSignal[] bands, int n)
    {
        var picked = new FloatSignal[n];
        for (int i = 0; i < n; i++) picked[i] = bands[Math.Min(bands.Length - 1, i * bands.Length / n)];
        return picked;
    }

    /// <summary>The object faces' ground: the accent's soft glow behind the record, the disc, the cassette, the reels.</summary>
    static Element Glow(Ctx m, float w, float h, Element face) => new BoxEl
    {
        Width = w, Height = h, AlignSelf = FlexAlign.Start, ZStack = true, HitTestVisible = false,
        Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, m.St.Tint2(m.Accent)), new GradientStop(1f, m.St.Tint2(m.Accent) with { A = 0f })]),
        Children = [face],
    };

    // ══ CLASSIC'S WAVE ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The tall column's tinted band behind its object faces, closing on one slow swell (never behind a cover).</summary>
    static Element WaveBand(Ctx m, float w, float h) => new BoxEl
    {
        Width = w, Height = h, AlignSelf = FlexAlign.Start, ZStack = true, HitTestVisible = false,
        Gradient = new GradientSpec(GradientShape.Linear, H.ShadeAngle, [new GradientStop(0f, m.St.Tint2(m.Accent)), new GradientStop(1f, m.St.Tint(m.Accent))]),
        Children = [new PathEl { Geometry = MiniWave.Foot(w, h), Fill = m.St.Solid(m.Accent), Width = w, Height = h }],
    };

    // ══ SEEK ═════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The seek row: the elapsed time on the column's left edge, the rail, the remaining time (a toggle to the duration) ending on the
    /// column's right edge. The digits are the style's detail type over the bar's own label text.</summary>
    static Element SeekRow(Ctx m)
    {
        var st = m.St;
        var req = (m.Accent, m.Media, m.Playing, m.Hot);
        return new BoxEl
        {
            Key = H.SeekKey, Height = H.SeekRowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = H.SeekGap,
            Children =
            [
                Time(m, remaining: false),
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [m.P.SeekRail(rail => st.SeekRail(new MiniSeekRequest(rail, req.Accent, req.Media, req.Playing, Edge: false, req.Hot)))],
                },
                Time(m, remaining: true),
            ],
        };
    }

    static Element Time(Ctx m, bool remaining)
    {
        var digits = m.St.Detail("") with { Text = Prop.Of(m.P.TimeText(remaining)), Color = m.Ink2, Shrink = 0f, Trim = TextTrim.None };
        return new BoxEl
        {
            Key = H.TimeKey + (remaining ? ":right" : ":left"), MinWidth = H.TimeMinW, Height = H.SeekRowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
            Justify = remaining ? FlexJustify.End : FlexJustify.Start,
            OnClick = remaining ? m.P.ToggleRemaining : null, Cursor = remaining ? CursorId.Hand : null,
            Role = remaining ? AutomationRole.Button : AutomationRole.Text, HitTestVisible = remaining,
            HitSlop = remaining ? new Edges4(0f, H.TimeSlop, 0f, H.TimeSlop) : default, OnContextRequested = remaining ? s_noContext : null,
            Children = [digits],
        };
    }

    /// <summary>The window's edge line (strip, square): a hairline at rest that thickens into a seek bar while the controls show.</summary>
    static Element Edge(Ctx m)
    {
        var st = m.St;
        var req = (m.Accent, m.Playing, m.Hot);
        return new BoxEl
        {
            Key = H.EdgeKey, AlignSelf = FlexAlign.End, Direction = 0, Enter = s_fade, Exit = s_fade,
            Children = [m.P.SeekRail(rail => st.SeekRail(new MiniSeekRequest(rail, req.Accent, OnDark: false, req.Playing, Edge: true, req.Hot)))],
        };
    }

    // ══ TRANSPORT ════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Transport(Ctx m, float playBox, float gap, float glyph = 0f) => new BoxEl
    {
        Key = H.TransportKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = gap,
        Children = [Prev(m, glyph), Play(m, playBox), Next(m, glyph)],
    };

    static Element Prev(Ctx m, float glyph = 0f)
    {
        var verbs = m.Bar.Verbs;
        return Btn(m, Icons.Previous, () => verbs.Previous(), m.V.PrevEnabled && m.V.CanTransport, Loc.Get(Strings.Player.Previous), glyph: glyph);
    }

    static Element Next(Ctx m, float glyph = 0f)
    {
        var verbs = m.Bar.Verbs;
        return Btn(m, Icons.Next, () => verbs.Next(), m.V.NextEnabled && m.V.CanTransport, Loc.Get(Strings.Player.Next), glyph: glyph);
    }

    static Element Play(Ctx m, float box)
    {
        var st = m.St;
        var verbs = m.Bar.Verbs;
        Action? click = m.V.Primary switch
        {
            PrimaryVerb.TogglePlay => () => verbs.TogglePlay("mini"),
            PrimaryVerb.Retry => () => verbs.Retry(),
            _ => null,
        };
        var a = m.Accent;
        return new BoxEl
        {
            Key = H.PlayKey, Width = box, Height = box, Shrink = 0f, Corners = CornerRadius4.All(st.PlayCorner), OnContextRequested = s_noContext,
            Fill = a, HoverFill = ColorF.Lerp(a, H.OnMediaInk, H.PlayHoverMix), PressedFill = ColorF.Lerp(a, H.OnAccentDark, H.PlayPressMix),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Cursor = CursorId.Hand, OnClick = click,
            Role = AutomationRole.Button, Focusable = click is not null,
            Children = [Glyph(m.V.PauseGlyph ? Icons.Pause : Icons.Play, st.PlayGlyph, m.OnAccent)],
        };
    }

    /// <summary>The save heart in the theme's way: plain, Zune's ring, Classic's tinted plate. Saved, it fills in the accent.</summary>
    static Element SaveButton(Ctx m)
    {
        var st = m.St;
        var verbs = m.Bar.Verbs;
        bool liked = m.V.Liked;
        bool canSave = m.P.CanSave();
        var icon = m.V.FaceIsEpisode ? ActionIcons.Resolve(ActionIcons.Save, liked) : ActionIcons.Resolve(ActionIcons.Heart, liked);
        ColorF ink = !canSave ? m.Ink with { A = m.Ink.A * H.DisabledAlpha } : liked || st.Heart == MiniHeart.Plate ? m.Accent : m.Ink;
        // Over imagery the accent reads lifted toward white (a light-mode accent is dark, and the scrim under it is too).
        if (m.Media) ink = liked ? ColorF.Lerp(m.Accent, H.OnMediaInk, H.OnDarkLift) : m.Ink;
        var plate = st.Heart switch
        {
            MiniHeart.Ring => ColorF.Transparent,
            MiniHeart.Plate => m.Media ? H.OnMediaInk with { A = H.OnMediaPlateAlpha } : st.Tint(m.Accent),
            _ => ColorF.Transparent,
        };
        float ring = st.Heart == MiniHeart.Ring ? H.BadgeRing : 0f;
        float inner = st.ButtonBox - 2f * ring;
        var face = new BoxEl
        {
            Width = inner, Height = inner, Shrink = 0f, Corners = CornerRadius4.All(st.ButtonCorner), Fill = plate,
            BorderWidth = ring, BorderColor = ring > 0f ? ink : ColorF.Transparent, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestVisible = false, Children = [Glyph(icon.Glyph ?? Icons.Heart, st.Heart == MiniHeart.Ring ? H.SavedGlyph : st.ButtonGlyph, ink)],
        };
        var b = new BoxEl
        {
            Key = H.SaveKey, Width = st.ButtonBox, Height = st.ButtonBox, Shrink = 0f, Corners = CornerRadius4.All(st.ButtonCorner),
            Fill = ColorF.Transparent, HoverFill = HoverPlate(m), PressedFill = HoverPlate(m),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Cursor = canSave ? CursorId.Hand : null,
            OnClick = canSave ? () => verbs.ToggleLike() : null, Role = AutomationRole.Button, Focusable = canSave, OnContextRequested = s_noContext,
            Children = [face],
        };
        return ToolTip.Wrap(b, Loc.Get(liked ? Strings.Menu.Saved : Strings.Menu.SaveToLiked));
    }

    /// <summary>The saved state at rest (strip, square): a filled heart in the accent (white over imagery), its slot kept when not saved.</summary>
    static Element Saved(Ctx m) => new BoxEl
    {
        Width = H.SavedGlyph, Height = H.SavedGlyph, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
        Opacity = m.V.Liked ? 1f : 0f,
        Children = [Glyph(Icons.HeartFill, H.SavedGlyph, m.Media ? H.OnMediaInk : m.Accent)],
    };

    static Element Device(Ctx m, float box = 0f)
    {
        var p = m.P;
        var overlay = m.Overlay;
        var a = m.A;
        bool remote = m.Bar.DeviceIsRemote();
        return Btn(m, Icons.Devices, () => OpenDevices(p, overlay, a), true, Loc.Get(Strings.Mini.PlayOn), lit: remote, box: box) with { Key = H.DeviceKey };
    }

    static Element OptionsButton(Ctx m, float box)
    {
        var p = m.P;
        var st = m.St;
        var overlay = m.Overlay;
        var a = m.A;
        bool chrome = box < st.ButtonBox;   // a window button (else the strip plate's own)
        return Btn(m, Icons.More, () => OpenOptions(p, st, overlay, a), true, Loc.Get(Strings.Mini.Options),
            box: box, glyph: chrome ? st.ChromeGlyph : 0f, corner: chrome ? st.ChromeCorner : -1f) with { Key = H.OptionsKey };
    }

    static Element BackButton(Ctx m, float box)
        => Btn(m, Icons.BackToWindow, m.P.BackToWavee, true, Loc.Get(Strings.Mini.Back), box: box, glyph: m.St.ChromeGlyph, corner: m.St.ChromeCorner) with { Key = H.BackKey };

    /// <summary>The window buttons, top right on hover (Wide, Square, Tall): options and back to Wavee, 32 DIP and 8 apart, 6 from the corner.
    /// Over imagery (<paramref name="glass"/>) each stands on its own dark glass plate (round in WinUI and Classic, square in Zune) in on-media
    /// ink; on the window's ground they are plain buttons.</summary>
    static Element WindowButtons(Ctx m, bool glass)
    {
        var st = m.St;
        var bm = glass ? m with { Media = true } : m;
        Element Plate(Element button) => !glass ? button : new BoxEl
        {
            Width = st.ChromeBox, Height = st.ChromeBox, Shrink = 0f, Corners = CornerRadius4.All(st.ChromeCorner), Fill = H.ChromeOnDark,
            Children = [button],
        };
        return new BoxEl
        {
            Key = H.ChromeKey, Height = st.ChromeBox, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, H.ChromeInset, H.ChromeInset, 0f),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = H.ChromeGap, Enter = s_fade, Exit = s_fade,
            Children = [Plate(OptionsButton(bm, st.ChromeBox)), Plate(BackButton(bm, st.ChromeBox))],
        };
    }

    /// <summary>The play state where no play button shows: three bars while the music moves, the pause glyph while it rests. Drawn still (a
    /// looping equaliser would ask for a frame every frame of a Cover face that otherwise draws once a pixel).</summary>
    static Element Badge(Ctx m, bool inline)
    {
        var st = m.St;
        var a = m.Accent;
        Element mark = m.Playing
            ? new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.End, Gap = H.EqBarGap, Height = H.EqBarMax, HitTestVisible = false,
                Children = [Eq(m, H.EqBar0), Eq(m, H.EqBar1), Eq(m, H.EqBar2)],
            }
            : Glyph(Icons.Pause, st.Badge * H.BadgeGlyph, m.OnAccent);
        float ring = inline ? 0f : H.BadgeRing;
        float side = st.Badge + 2f * ring;
        return new BoxEl
        {
            Width = side, Height = side, Shrink = 0f, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.End,
            Margin = inline ? default : new Edges4(0f, 0f, -H.BadgeOffset - ring, -H.BadgeOffset - ring),
            Corners = CornerRadius4.All(st.BadgeCorner > 0f ? side * 0.5f : 0f), Fill = a, BorderWidth = ring, BorderColor = st.Solid(a),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false, Children = [mark],
        };
    }

    static Element Eq(Ctx m, float share) => new BoxEl
    {
        Width = H.EqBarW, Height = H.EqBarMax * share, Shrink = 0f, Fill = m.OnAccent, Corners = CornerRadius4.All(H.EqBarW * 0.5f), HitTestVisible = false,
    };

    static Element Shade(MiniPlayerStyle st) => new BoxEl
    {
        Grow = 1f, AlignSelf = FlexAlign.Stretch, HitTestVisible = false,
        Gradient = new GradientSpec(GradientShape.Linear, H.ShadeAngle, [new GradientStop(H.ShadeFrom, st.Shade() with { A = 0f }), new GradientStop(1f, st.Shade())]),
    };

    /// <summary>The tall immersive scrim: dark under the top label, clear through the middle, dark under the panel.</summary>
    static Element Scrim() => new BoxEl
    {
        Grow = 1f, AlignSelf = FlexAlign.Stretch, HitTestVisible = false,
        Gradient = new GradientSpec(GradientShape.Linear, H.ShadeAngle,
        [
            new GradientStop(0f, H.HudPlate with { A = H.ScrimTopAlpha }), new GradientStop(H.ScrimTopEnd, H.HudPlate with { A = 0f }),
            new GradientStop(H.ScrimClearEnd, H.HudPlate with { A = 0f }), new GradientStop(H.ScrimFootFrom, H.HudPlate with { A = H.ScrimFootAlpha }),
        ]),
    };

    /// <summary>The volume row of a roomy tall column: mute, the slider, the percent.</summary>
    static Element VolumeRow(Ctx m)
    {
        var bar = m.Bar;
        var st = m.St;
        var accent = m.Accent;
        bool muted = bar.Muted.Value;
        var vol = bar.Volume;
        return new BoxEl
        {
            Key = H.VolumeKey, Height = H.VolumeRowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = H.TallPanelGap,
            Margin = new Edges4(-H.CtrlBleedBottom, -H.CtrlBleedBottom, -H.CtrlBleedBottom, -H.CtrlBleedBottom),
            Children =
            [
                Btn(m, muted ? Icons.Mute : Icons.Volume, bar.ToggleMute, true, Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute)),
                VolumeSlider(vol, bar.SetVolume, accent, m.Size.Width - 2f * st.Pad + 2f * H.CtrlBleedBottom - st.ButtonBox - H.VolumePctW - 2f * H.TallPanelGap),
                new BoxEl { Width = H.VolumePctW, Direction = 0, Justify = FlexJustify.End, HitTestVisible = false,
                    Children = [st.Detail("") with { Text = Prop.Of(() => Percent(vol.Value)), Color = m.Ink2 }] },
            ],
        };
    }

    static Element VolumeSlider(FloatSignal vol, Action<float> set, ColorF accent, float length) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center,
        Children =
        [
            Slider.Create(vol, set, new Slider.SliderOptions { IsThumbToolTipEnabled = false }, length: MathF.Max(0f, length),
                style: Slider.DefaultStyle with
                {
                    ValueFill = accent, ValueFillPointerOver = accent, ValueFillPressed = accent,
                    ThumbFill = accent, ThumbFillPointerOver = accent, ThumbFillPressed = accent,
                }),
        ],
    };

    static string Percent(float v) => ((int)MathF.Round(Math.Clamp(v, 0f, 1f) * 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    static Element Hud(PlayerBarProps bar)
    {
        var vol = bar.Volume;
        var muted = bar.Muted;
        return new BoxEl
        {
            Key = H.HudKey, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, Direction = 0, AlignItems = FlexAlign.Center, Gap = H.HudGap,
            Padding = new Edges4(H.HudPadX, H.HudPadY, H.HudPadX, H.HudPadY), Fill = H.HudPlate, Corners = CornerRadius4.All(H.HudCorner),
            Enter = s_fade, Exit = s_fade, HitTestVisible = false,
            Children =
            [
                new TextEl("") { Text = Prop.Of(() => muted.Value || vol.Value <= 0f ? Icons.Mute : Icons.Volume), Size = H.HudGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = H.OnMediaInk },
                Ui.BodyStrong("") with { Text = Prop.Of(() => Percent(muted.Value ? 0f : vol.Value)), Color = H.OnMediaInk },
            ],
        };
    }

    // ══ BUTTONS ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    static ColorF HoverPlate(Ctx m) => m.Media ? H.OnMediaInk with { A = H.OnMediaPlateAlpha } : Tok.FillSubtleSecondary;

    static Element Glyph(string glyph, float size, ColorF ink)
        => new TextEl(glyph) { Size = size, FontFamily = Tok.Definition.Fonts.Icon, Color = ink };

    /// <summary>A control's answer to a right-click: nothing (the window's own right-click opens the options panel).</summary>
    static readonly Action<ContextRequestEventArgs> s_noContext = static _ => { };

    static BoxEl Btn(Ctx m, string icon, Action? onClick, bool enabled, string tip, bool lit = false, float box = 0f, float glyph = 0f, float corner = -1f)
    {
        var st = m.St;
        float b = box > 0f ? box : st.ButtonBox;
        float g = glyph > 0f ? glyph : st.ButtonGlyph;
        ColorF ink = !enabled ? m.Ink with { A = m.Ink.A * H.DisabledAlpha } : lit ? m.Accent : m.Ink;
        return new BoxEl
        {
            Width = b, Height = b, Shrink = 0f, Corners = CornerRadius4.All(corner >= 0f ? corner : st.ButtonCorner), OnContextRequested = s_noContext,
            Fill = ColorF.Transparent, HoverFill = HoverPlate(m), PressedFill = HoverPlate(m),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Cursor = enabled ? CursorId.Hand : null,
            OnClick = enabled ? onClick : null, Role = AutomationRole.Button, Focusable = enabled,
            Children = [Glyph(icon, g, ink)],
        };
    }

    // ══ LYRICS ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The lyrics face's window: the app's own lyrics view (the side pane's rows, ink, word wipe and follow, in the theme's style) at
    /// the theme's mini type, in a box of <paramref name="height"/> that clips it. The box hands the view its edge fades and gutter
    /// (<see cref="LyricsBox"/>): the structure's view fades its reading layers with them (the lines dissolve at the top and at the AI
    /// disclosure's edge, never cut) and the disclosure under them takes its one-line form. The active line's centre sits at
    /// <paramref name="band"/>. Not a target: the wheel stays the window's volume.</summary>
    static Element LyricWindow(Ctx m, float height, float band, float gutter, float fadeFoot, bool onDark) => new BoxEl
    {
        Key = H.LyricsKey + ":window", Height = height, Shrink = 0f, Direction = 1, ClipToBounds = true, HitTestVisible = false,
        Children =
        [
            FluentGpu.Hooks.Ctx.Provide(LyricsBox.Context,
                new LyricsBox(new EdgeFadeSpec(EdgeMask.Vertical, 0f, height * H.LyricFadeTop, 0f, MathF.Max(H.LyricFadeMin, fadeFoot)), gutter),
                m.P.LyricsView(new MiniLyricsView(onDark, m.St.LyricScale(m.Shape), band, gutter,
                    onDark ? ColorF.Lerp(m.Accent, H.OnMediaInk, H.OnDarkLift) : default))),
        ],
    };

    sealed record TickerProps(MiniLyricsFrame Frame, Ctx M, Element Fallback);

    /// <summary>The strip's ticker: the sung line over the next, both rolling up when the line changes; the theme's start bar beside them.
    /// Without synced lines it is the title block.</summary>
    sealed class Ticker : Component
    {
        static readonly EnterExit Rise = new(Dy: H.TickerRise, Opacity: 0f, Active: true);

        public override Element Render()
        {
            var p = UseProps<TickerProps>();
            var frame = p.Frame;
            if (frame.State != MiniLyricsState.Lines || frame.Lines.Count == 0) return p.Fallback;
            var m = p.M;
            var st = m.St;
            var lines = frame.Lines;
            int a = Math.Clamp(frame.Active.Value, 0, lines.Count - 1);
            string next = a + 1 < lines.Count ? lines[a + 1] : "";
            var col = new BoxEl
            {
                Key = H.TickerKey, Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, HitTestVisible = false,
                Children =
                [
                    new BoxEl { Key = "now:" + a, Direction = 1, MinWidth = 0f, Enter = Rise, Exit = s_fade, HitTestVisible = false,
                        Children = [st.LyricLine(new MiniLyricLine(lines[a], true, MiniLyricUse.TickerNow, MiniShape.Strip, m.Accent, false))] },
                    new BoxEl { Key = "next:" + a, Direction = 1, MinWidth = 0f, Enter = Rise, Exit = s_fade, HitTestVisible = false,
                        Children = [st.LyricLine(new MiniLyricLine(next, false, MiniLyricUse.TickerNext, MiniShape.Strip, m.Accent, false))] },
                ],
            };
            if (st.TickerBar <= 0f) return col;
            return new BoxEl
            {
                Direction = 0, Gap = H.TickerBarGap, MinWidth = 0f, HitTestVisible = false,
                Children = [new BoxEl { Width = st.TickerBar, AlignSelf = FlexAlign.Stretch, Fill = m.Accent, HitTestVisible = false }, col],
            };
        }
    }
}
