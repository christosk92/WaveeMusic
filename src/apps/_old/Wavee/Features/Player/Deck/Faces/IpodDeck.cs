using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The iPod Classic face: a body, a backlit LCD and a click wheel that SEEKS.
///
/// <para><b>What moves.</b> Nothing but progress — the model is <c>ProgressModel</c>. The bar's fill is one bound
/// <c>ScaleX</c>, the diamond thumb one bound translation, and the two clocks are 1 Hz bound <c>Text</c> channels
/// with a FIXED width so a "9:59" -&gt; "10:00" step cannot reflow the screen. The LCD's artwork and its three text
/// lines live in <see cref="IpodScreen"/>, which re-renders only when the TRACK changes.</para>
///
/// <para><b>The wheel is a seek gesture, not a slider.</b> <see cref="IpodWheel"/> maps pointer angle to a delta:
/// one full turn of the wheel is a quarter of the track (the mockup's <c>0.25</c>), unwrapped across the ±PI seam,
/// and driven through the host's shared <see cref="DeckGesture"/> — so the click wheel and the record's headshell
/// are literally the same scrub, and the deck models see only <c>ScrubTargetMs</c>/<c>SeekTargetMs</c>.</para>
///
/// <para>Every dimension is a fraction of <c>side</c>; the CSS in <c>npv-player-styles-mica.html</c> (<c>.ipod*</c>)
/// is the geometry's source of truth.</para>
/// </summary>
static class IpodDeck
{
    // 1 Hz labels: one cached string per whole second, so a mounted deck stops allocating after the first minute.
    static readonly FormatCache<int> ElapsedCache = FormatCache.Create<int>();
    static readonly FormatCache<int> RemainingCache = FormatCache.Create<int>();

    public static Element Build(in NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
                                DeckSignals sig, PlaybackBridge bridge, DeckHost host)
    {
        string body = NpvPlayerPrefs.ChoiceSlug(settings, preset, "body");
        bool green = NpvPlayerPrefs.ChoiceSlug(settings, preset, "lcd") == "green";
        bool dark = body != "silver";

        ColorF lcdFill = green ? Hex(0xD6E6C4) : Hex(0xDFE6EA);
        ColorF ink = green ? Hex(0x1C2A14) : Hex(0x1C2230);
        ColorF inkSoft = ink with { A = 0.78f };

        float bodyX = 0.21f * side, bodyY = 0.02f * side, bodyW = 0.58f * side, bodyH = 0.96f * side;
        float lcdW = 0.84f * bodyW, lcdH = 0.43f * bodyH;
        float lcdX = bodyX + 0.08f * bodyW, lcdY = bodyY + 0.05f * bodyH;
        float wheelD = 0.74f * bodyW;
        float wheelX = bodyX + (bodyW - wheelD) * 0.5f;
        float wheelY = bodyY + bodyH * 0.955f - wheelD;

        // The playhead as the SCREEN sees it: a live drag owns the position (the wheel must move the bar before the
        // audio pipeline has acknowledged anything), otherwise the ticker's interpolated fraction.
        Func<float> frac = () =>
        {
            if (bridge.ScrubTargetMs.Value is { } scrub)
            {
                long dur = bridge.DurationMs.Value;
                return dur > 0L ? Clamp01(scrub / (float)dur) : 0f;
            }
            return Clamp01(sig.Frac.Value);
        };

        var kids = new List<CanvasChild>(4)
        {
            new(bodyX, bodyY, new BoxEl
            {
                Width = bodyW, Height = bodyH, Shrink = 0f,
                Corners = CornerRadius4.All(side * 0.055f),
                Gradient = dark
                    ? GradientSpec.Vertical(Hex(0x2A2C31), Hex(0x0F1013))
                    : GradientSpec.Vertical(Hex(0xF7F7F9), Hex(0xCFD2D8)),
                Shadow = new ShadowSpec(34f, 16f, 0f, Hex(0x000000, 0.5f)),
            }),
            new(lcdX, lcdY, Lcd(lcdW, lcdH, lcdFill, ink, inkSoft, sig, bridge, frac)),
            new(wheelX, wheelY, Embed.Comp(() => new IpodWheel
            {
                Bridge = bridge,
                Gesture = host.Gesture,
                Frac = frac,
                Diameter = wheelD,
                Body = body,
            }) with { Key = "ipod-wheel:" + body }),
        };

        return Canvas.Create(side, side, kids) with
        {
            Gradient = GradientSpec.Vertical(Hex(0x3A3F4A), Hex(0x22252C)),
        };
    }

    // ── the screen ──────────────────────────────────────────────────────────────────────────────────────────────

    static Element Lcd(float w, float h, ColorF fill, ColorF ink, ColorF inkSoft,
                       DeckSignals sig, PlaybackBridge bridge, Func<float> frac)
    {
        float barH = 0.14f * h;
        float chrome = MathF.Max(6f, w * 0.053f);      // 2.6cqw of the deck ~= 5.3% of the LCD
        float clock = MathF.Max(6f, w * 0.049f);
        float clockH = MathF.Ceiling(clock * 1.45f);
        float clockW = w * 0.30f;

        float progX = 0.04f * w, progW = 0.92f * w, progH = 0.08f * h;
        float progY = h * 0.91f - progH;
        float thumbW = MathF.Max(4f, progW * 0.06f), thumbH = progH * 1.3f;
        float battW = 0.09f * w, battH = 0.44f * barH;

        var kids = new List<CanvasChild>(10)
        {
            // Title bar: the chrome strip with the transport glyph, the caption and the battery.
            new(0f, 0f, new BoxEl
            {
                Width = w, Height = barH, Shrink = 0f,
                Gradient = GradientSpec.Vertical(Hex(0xF4F6F8), Hex(0xC7CDD6)),
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children =
                [
                    // The iPod's OWN chrome string, not Wavee's: it is part of the device's look, so it stays in the
                    // device's language exactly as the mockup draws it.
                    new TextEl("Now Playing")
                    {
                        Size = chrome, Weight = 700, Color = Hex(0x1C2230),
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.Clip,
                    },
                ],
            }),
            new(0f, barH - 1f, new BoxEl { Width = w, Height = 1f, Shrink = 0f, Fill = Hex(0x8B93A3) }),
            // The transport glyph is the LCD's ONLY state read: a bound Text channel, so play/pause never re-renders.
            new(0.03f * w, barH * 0.22f, new TextEl(Prop.Of(() => bridge.IsPlaying.Value ? Icons.Play : Icons.Pause))
            {
                Size = MathF.Max(6f, chrome * 0.92f), FontFamily = Theme.IconFont, Color = Hex(0x1C2230),
                // Start, not the container's default Stretch: a Canvas child with no explicit height would
                // otherwise be stretched to the whole LCD.
                Width = chrome * 1.4f, AlignSelf = FlexAlign.Start,
            }),
            new(w - 0.03f * w - battW, barH * 0.28f, new BoxEl
            {
                Width = battW, Height = battH, Shrink = 0f, BorderWidth = 1f, BorderColor = Hex(0x333333),
                Corners = CornerRadius4.All(1f),
                Children =
                [
                    new BoxEl { Width = battW * 0.7f, Height = MathF.Max(1f, battH - 2f), Margin = new Edges4(1f, 1f, 0f, 1f), Shrink = 0f, Fill = Hex(0x3AA757) },
                ],
            }),
            // Artwork + the three metadata lines — the only part of the screen that re-renders (on a track change).
            new(0f, 0f, Embed.Comp(() => new IpodScreen
            {
                Bridge = bridge, Sig = sig, Width = w, Height = h, Ink = ink, InkSoft = inkSoft,
            }) with { Key = "ipod-screen" }),
            // Progress: a bordered trough, one bound fill and one bound diamond.
            new(progX, progY, new BoxEl
            {
                Width = progW, Height = progH, Shrink = 0f, ClipToBounds = true,
                BorderWidth = 1f, BorderColor = Hex(0x2A2F38), Corners = CornerRadius4.All(2f), Fill = Hex(0xF6F9FB),
                Children =
                [
                    new BoxEl
                    {
                        Width = progW, Height = progH, Shrink = 0f,
                        Gradient = GradientSpec.Vertical(Hex(0x7DB2EA), Hex(0x2A6BC4)),
                        TransformOriginX = 0f, TransformOriginY = 0.5f,
                        Transform = Prop.Of(() => Affine2D.Scale(frac(), 1f)),
                    },
                ],
            }),
            // The thumb rides OUTSIDE the trough's clip; the rotation lives on an inner box because a node may own
            // exactly one transform (the bound translation is this one's).
            new(progX - thumbW * 0.5f, progY + (progH - thumbH) * 0.5f, new BoxEl
            {
                Width = thumbW, Height = thumbH, Shrink = 0f,
                Transform = Prop.Of(() => Affine2D.Translation(frac() * progW, 0f)),
                Children =
                [
                    new BoxEl
                    {
                        Width = thumbW, Height = thumbH, Shrink = 0f, Rotation = 45f,
                        Fill = Hex(0xE9EEF4), BorderWidth = 1f, BorderColor = Hex(0x2A2F38),
                    },
                ],
            }),
            new(0.04f * w, h * 0.99f - clockH, Clock(clockW, clockH, clock, ink, bridge, false)),
            new(w - 0.04f * w - clockW, h * 0.99f - clockH, Clock(clockW, clockH, clock, ink, bridge, true)),
        };

        return Canvas.Create(w, h, kids) with
        {
            Fill = fill, BorderWidth = 2f, BorderColor = Hex(0x2A2F38), Corners = CornerRadius4.All(3f),
        };
    }

    /// <summary>One 1 Hz clock. FIXED <c>Width</c>: a bound <c>Text</c> is a scoped relayout, and a slot that can
    /// resize would push the screen around every time the minutes rolled over.</summary>
    static Element Clock(float w, float h, float size, ColorF ink, PlaybackBridge b, bool remaining) => new BoxEl
    {
        Width = w, Height = h, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
        Justify = remaining ? FlexJustify.End : FlexJustify.Start,
        Children =
        [
            new TextEl(Prop.Of(() => remaining ? RemainingText(b) : ElapsedText(b)))
            {
                Size = size, Color = ink, FontFamily = "Consolas", Wrap = TextWrap.NoWrap, MaxLines = 1,
            },
        ],
    };

    internal static long PlayheadMs(PlaybackBridge b)
        => b.ScrubTargetMs.Value ?? b.SeekTargetMs.Value ?? b.PositionMs.Value;

    internal static string ElapsedText(PlaybackBridge b)
        => ElapsedCache.Get((int)Math.Max(0L, PlayheadMs(b) / 1000L), static s => Mmss(s));

    internal static string RemainingText(PlaybackBridge b)
    {
        long left = Math.Max(0L, b.DurationMs.Value - PlayheadMs(b));
        return RemainingCache.Get((int)(left / 1000L), static s => "-" + Mmss(s));
    }

    static string Mmss(int totalSeconds)
        => (totalSeconds / 60).ToString(CultureInfo.InvariantCulture) + ":"
         + (totalSeconds % 60).ToString("D2", CultureInfo.InvariantCulture);

    internal static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    internal static ColorF Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);
}

/// <summary>
/// The LCD's artwork and its three metadata lines. Its OWN component because these are the only parts of the iPod
/// that change with the TRACK rather than with the playhead: it subscribes <c>CurrentTrack</c> and
/// <c>DeckSignals.CoverGen</c> and re-renders three leaves, while the progress bar, the thumb and the clocks — built
/// once by the face — keep their bind thunks untouched.
/// </summary>
sealed class IpodScreen : Component
{
    public required PlaybackBridge Bridge;
    public required DeckSignals Sig;
    public required float Width;
    public required float Height;
    public required ColorF Ink;
    public required ColorF InkSoft;

    public override Element Render()
    {
        var track = Bridge.CurrentTrack.Value;
        _ = Sig.CoverGen.Value;             // the artwork swaps on the album edge, not on every track change

        float w = Width, h = Height;
        float art = 0.34f * w;
        float title = MathF.Max(7f, w * 0.059f);
        float line = MathF.Max(6f, w * 0.053f);
        float textX = 0.42f * w, textW = 0.54f * w, textY = 0.22f * h;

        string? url = track is null ? null : DeckArt.CoverUrl(track);
        var lines = new List<Element>(3)
        {
            new TextEl(track?.Title ?? "")
            {
                Size = title, Weight = 700, Color = Ink, Width = textW,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            new TextEl(ArtistLine(track))
            {
                Size = line, Color = InkSoft, Width = textW,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            new TextEl(track?.Album.Name ?? "")
            {
                Size = line, Color = InkSoft, Width = textW,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };

        return Canvas.Create(w, h,
        [
            new CanvasChild(0.04f * w, 0.20f * h,
                DeckArt.Cover(url, art, CornerRadius4.All(2f), NowPlayingPanel.HeroWashColor(url), track?.Image?.BlurHash)),
            new CanvasChild(textX, textY, new BoxEl
            {
                Width = textW, Direction = 1, Gap = MathF.Max(1f, h * 0.012f), Shrink = 0f,
                Children = lines.ToArray(),
            }),
        ]) with { HitTestVisible = false };
    }

    static string ArtistLine(Track? track)
    {
        if (track is null || track.Artists.Count == 0) return "";
        if (track.Artists.Count == 1) return track.Artists[0].Name;
        return DetailFormat.ArtistNames(track.Artists);
    }
}

/// <summary>
/// The click wheel. Angular scrub: the pointer's polar angle around the wheel's centre is differenced per move,
/// unwrapped across the ±PI seam, and one full turn moves the playhead a QUARTER of the track — the same ratio the
/// mockup uses, which is what makes the wheel feel geared rather than absolute.
///
/// <para>The drag state is instance fields rather than hooks because the wheel is mounted once per body finish (the
/// <c>Key</c> the face gives it) and nothing outside re-pushes into it. The gesture itself is the host's shared
/// <see cref="DeckGesture"/>: the wheel never touches the bridge's seek API directly.</para>
///
/// <para>The centre button and the two skip captions are their OWN clickable boxes, so a press on them becomes the
/// hit node and never arms the wheel's scrub (the dispatcher fires <c>OnPointerDown</c> on the hit node only).</para>
/// </summary>
sealed class IpodWheel : Component
{
    public required PlaybackBridge Bridge;
    public required DeckGesture Gesture;
    public required Func<float> Frac;
    public required float Diameter;
    public required string Body;

    float _lastAngle;
    float _dragFrac;
    bool _active;
    bool _moved;

    public override Element Render()
    {
        float d = Diameter;
        bool dark = Body != "silver";
        bool u2 = Body == "u2";
        float centre = 0.36f * d;
        float cap = MathF.Max(6f, d * 0.058f);
        float capH = MathF.Ceiling(cap * 1.5f);
        float glyph = MathF.Max(7f, d * 0.075f);
        ColorF capInk = dark ? IpodDeck.Hex(0xC9CED8, 0.75f) : IpodDeck.Hex(0x8D939D);

        var wheelStops = u2
            ? new[] { new GradientStop(0f, IpodDeck.Hex(0xD8262B)), new GradientStop(1f, IpodDeck.Hex(0x8F1418)) }
            : dark
                ? new[] { new GradientStop(0f, IpodDeck.Hex(0x2F3239)), new GradientStop(1f, IpodDeck.Hex(0x1A1C21)) }
                : new[] { new GradientStop(0f, IpodDeck.Hex(0xF3F3F5)), new GradientStop(1f, IpodDeck.Hex(0xDCDEE3)) };
        var centreStops = dark
            ? new[] { new GradientStop(0f, IpodDeck.Hex(0x3A3D45)), new GradientStop(1f, IpodDeck.Hex(0x1F2126)) }
            : new[] { new GradientStop(0f, IpodDeck.Hex(0xFFFFFF)), new GradientStop(1f, IpodDeck.Hex(0xE6E8EC)) };

        var kids = new List<CanvasChild>(5)
        {
            new((d - cap * 3.6f) * 0.5f, 0.09f * d, Caption("MENU", cap * 3.6f, capH, cap, capInk, null)),
            new(0.08f * d, (d - capH) * 0.5f, Glyph(Icons.Previous, capH * 1.4f, capH, glyph, capInk, Previous)),
            new(d - 0.08f * d - capH * 1.4f, (d - capH) * 0.5f, Glyph(Icons.Next, capH * 1.4f, capH, glyph, capInk, Next)),
            new((d - capH * 1.4f) * 0.5f, d - 0.08f * d - capH, Glyph(Icons.Play, capH * 1.4f, capH, glyph, capInk, TogglePlay)),
            new((d - centre) * 0.5f, (d - centre) * 0.5f, new BoxEl
            {
                Width = centre, Height = centre, Shrink = 0f, Corners = Radii.Circle(centre),
                Gradient = new GradientSpec(GradientShape.Radial, 0f, centreStops),
                BorderWidth = 1f, BorderColor = IpodDeck.Hex(0x000000, 0.2f),
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = TogglePlay,
            }),
        };

        return Canvas.Create(d, d, kids) with
        {
            Corners = Radii.Circle(d),
            Gradient = new GradientSpec(GradientShape.Radial, 0f, wheelStops),
            BorderWidth = 1f, BorderColor = IpodDeck.Hex(0x000000, 0.15f),
            Cursor = CursorId.Hand,
            Role = AutomationRole.Slider,
            OnPointerDown = Down,
            OnDrag = Move,
            OnClick = Release,          // the drag-END edge (the SeekBar contract), not a tap
            OnDragCanceled = Abort,
        };
    }

    static Element Caption(string text, float w, float h, float size, ColorF ink, Action? onClick) => new BoxEl
    {
        Width = w, Height = h, Shrink = 0f, Direction = 0,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Cursor = onClick is null ? (CursorId?)null : CursorId.Hand,
        Role = onClick is null ? AutomationRole.None : AutomationRole.Button,
        OnClick = onClick,
        Children = [new TextEl(text) { Size = size, Weight = 700, Color = ink, CharSpacing = 80f, Wrap = TextWrap.NoWrap }],
    };

    static Element Glyph(string glyph, float w, float h, float size, ColorF ink, Action onClick) => new BoxEl
    {
        Width = w, Height = h, Shrink = 0f, Direction = 0,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = onClick,
        Children = [new TextEl(glyph) { Size = size, FontFamily = Theme.IconFont, Color = ink }],
    };

    void TogglePlay() => PlayerBarContent.TogglePlayPause(Bridge);
    void Previous() => _ = Bridge.Player.PreviousAsync();
    void Next() => _ = Bridge.Player.NextAsync();

    void Down(Point2 local)
    {
        long dur = Bridge.DurationMs.Peek();
        if (dur <= 0L) return;
        _lastAngle = AngleAt(local);
        _dragFrac = IpodDeck.Clamp01(Frac());
        _moved = false;
        _active = true;
        Gesture.Begin((long)(_dragFrac * dur));
    }

    void Move(Point2 local)
    {
        if (!_active) return;
        long dur = Bridge.DurationMs.Peek();
        if (dur <= 0L) return;
        float a = AngleAt(local);
        float delta = a - _lastAngle;
        // Unwrap: atan2 flips sign across the -PI/+PI seam, and without this a wheel crossing 9 o'clock would jump
        // a quarter of the track backwards.
        if (delta > MathF.PI) delta -= MathF.Tau;
        else if (delta < -MathF.PI) delta += MathF.Tau;
        _lastAngle = a;
        if (MathF.Abs(delta) < 1e-4f) return;
        _moved = true;
        _dragFrac = IpodDeck.Clamp01(_dragFrac + delta / MathF.Tau * 0.25f);
        Gesture.Move((long)(_dragFrac * dur));
    }

    void Release()
    {
        if (!_active) return;
        _active = false;
        long dur = Bridge.DurationMs.Peek();
        // A TAP on the wheel's rim is not a seek: committing the position the finger landed on would fire a pointless
        // seek to where the playhead already is (and, mid-buffer, an audible one).
        if (_moved && dur > 0L) Gesture.Commit((long)(_dragFrac * dur));
        else Gesture.Cancel();
    }

    void Abort()
    {
        if (!_active) return;
        _active = false;
        Gesture.Cancel();
    }

    float AngleAt(Point2 local)
    {
        float r = Diameter * 0.5f;
        return MathF.Atan2(local.Y - r, local.X - r);
    }
}
