// ── Themes/Classic/Structure/Views/Player/ClassicMiniPlayer.cs — the mini player's look in Classic (#197) ───────────────────
//
// The layout is the Common template's (Themes/Common/CommonMiniPlayer.cs); this is Classic's MiniPlayerStyle: a warm wash tinted from the
// accent (the album's with "Colour from album art", on by default as in the app), soft 14-DIP corners, the artist in the accent, the heart
// on an album-tinted plate, the wave edge on the square's cover and behind the tall column (the Common template draws it from WaveEdge),
// and Classic's own motion: the seek fill ripples like water while the music moves and lies flat while it rests; the visualizer is three
// slow sine waves; the active lyric line rests on a tinted plate.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Animation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Pal;
using FluentGpu.Render;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using H = Wavee.Themes.Common.CommonMiniPlayerRules;
using M = Wavee.Themes.Classic.ClassicMiniRules;

namespace Wavee.Themes.Classic;

internal static class ClassicMiniPlayer
{
    public static void Register(ViewRegistryBuilder views) => CommonMiniPlayer.Register(views, "classic.player.mini", Style);

    static TextEl Line(TextEl e) => e with { MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis };

    /// <summary>The wash: the window's ground drawn toward the accent.</summary>
    static ColorF Wash(ColorF accent) => ColorF.Lerp(Tok.WindowBackground, accent with { A = 1f }, R.IsLight ? M.WashLight : M.WashDark);

    static readonly MiniRailLook s_rail = new()
    {
        Track = M.Track, Fill = M.Track, Corner = M.TrackCorner, TrackAlpha = M.TrackAlpha, Thumb = M.Thumb, ThumbCorner = M.Thumb * 0.5f, ThumbDot = 0f,
        ThumbFill = static (accent, _) => accent, ThumbOnHot = true,
    };

    public static readonly MiniPlayerStyle Style = new()
    {
        MaterialLight = WindowMaterial.None,
        MaterialDark = WindowMaterial.None,
        ArtColourSlot = M.ArtColourSlot,
        ArtColourDefault = true,
        Ground = Wash,
        Solid = Wash,
        Glow = static _ => ColorF.Transparent,
        Tint = static a => a with { A = M.TintAlpha },
        Tint2 = static a => a with { A = M.Tint2Alpha },
        Radius = M.Radius, ArtCorner = M.ArtCorner, ThumbCorner = M.ThumbCorner,
        ButtonBox = M.ButtonBox, ButtonGlyph = M.ButtonGlyph, PlayBox = M.PlayBox, PlayBigBox = M.PlayBigBox, PlayGlyph = M.PlayGlyph,
        ChromeBox = M.ChromeBox, ChromeGlyph = M.ChromeGlyph, ButtonCorner = M.ButtonCorner, PlayCorner = M.PlayCorner, ChromeCorner = M.ChromeCorner,
        BadgeCorner = M.BadgeCorner, Heart = MiniHeart.Plate,
        Pad = M.Pad, Gap = M.Gap, StripPadStart = M.StripPadStart, StripThumb = M.StripThumb, StripGap = M.StripGap, Badge = M.Badge,
        Title = static (text, shape) => Line(R.Type.TrackTitle(text)) with { Size = M.TitleSize(shape), LineHeight = M.TitleSize(shape) * M.TitleLeading },
        TitleBleeds = false,
        TitleFade = 0f,
        Artist = static (text, accent, onDark) => Line(R.Type.TrackMeta(text)) with
        {
            Size = M.MetaSize, LineHeight = M.MetaLeading, Color = onDark ? H.OnMediaInk with { A = M.OnDarkArtistAlpha } : accent,
        },
        Detail = static text => Line(R.Type.TrackMeta(text)) with { Size = M.MetaSize, LineHeight = M.MetaLeading, Color = Tok.TextTertiary },
        CoverBleed = null,
        WaveEdge = true,
        Label = static text => Line(R.Type.TrackMeta(text)) with { Size = M.LabelSize, Color = Tok.TextPrimary },
        Small = static text => Line(R.Type.TrackMeta(text)) with { Size = M.SmallSize, LineHeight = M.SmallLeading, Color = Tok.TextSecondary },
        Heading = static text => Line(R.Type.TrackTitle(text)) with { Size = M.HeadingSize, Color = Tok.TextPrimary, FontFamily = R.Type.DisplayFace },
        SeekRail = static r => r.Rail.Line ? CommonMiniRail.Draw(r, s_rail) : r.Edge ? Edge(r) : Embed.Comp(new WaveRailProps(r), static () => new WaveRail()),
        Visualizer = static (f, accent) => Embed.Comp(new SinesProps(f, accent), static () => new Sines()),
        TickerBar = M.TickerBar,
        LyricPad = M.LyricPad,
        LyricLine = LyricLine,
        Veil = static () => Tok.FillSmoke with { A = M.VeilAlpha },
        Shade = static () => Tok.FillSmoke with { A = M.ShadeAlpha },
        HoverInMs = M.HoverInMs,
        HoverOutDelayMs = M.HoverOutDelayMs,
    };

    // ══ THE WAVE SEEK RAIL ═══════════════════════════════════════════════════════════════════════════════════════════════
    //
    // The played part is the wave ALONE; the unplayed part is the plain track, starting a few DIP after the playhead; the playhead is a short
    // rounded pill in the accent where they meet (shown at rest, a little taller while the controls show). The wave is calm (3 DIP peak to
    // peak, an 18-DIP wavelength, the track's own 3-DIP weight) and its swing tapers to nothing over the last half wavelength, so it runs
    // into the head flat. Pausing eases the swing to a straight line over 250 ms; playing eases it back. It is a series (a sampled stroke
    // with an analytic edge), resampled from a reused buffer: the drift steps about 15 times a second while playing, the ease steps at
    // frame pace only for its 250 ms, and a resting rail asks for nothing.

    sealed record WaveRailProps(MiniSeekRequest R);

    static readonly EnterExit s_edgeFade = new(Opacity: 0f, Active: true);

    /// <summary>The window's edge line (strip, square): flat at rest as before; while the controls show it cross-fades into the wave rail (the
    /// hover plate fades in over the same 120 ms, so the two read as one reveal).</summary>
    static Element Edge(MiniSeekRequest r) => new BoxEl
    {
        Grow = 1f, Height = H.EdgeHit, ZStack = true,
        Children = r.Hot
            ? [new BoxEl { Key = "edge:wave", Grow = 1f, Direction = 1, Justify = FlexJustify.End, Enter = s_edgeFade, Exit = s_edgeFade,
                Children = [Embed.Comp(new WaveRailProps(r), static () => new WaveRail())] }]
            : [new BoxEl { Key = "edge:flat", Grow = 1f, ZStack = true, Enter = s_edgeFade, Exit = s_edgeFade, Children = [CommonMiniRail.Draw(r, s_rail)] }],
    };

    sealed record HeadProps(ColorF Accent, bool Hot, float Height);

    sealed class WaveRail : Component
    {
        readonly FloatSignal _width = new(0f);
        readonly Signal<int> _frame = new(0);
        readonly Signal<bool> _easing = new(false);
        readonly float[] _buf = new float[SeriesSpec.MaxSamples];
        uint _version;
        float _amp = -1f, _full, _phase, _easeFrom, _easeTo, _easeT;
        bool _playing;
        ISeekRailHost? _host;
        Action<RectF>? _bounds;
        Action? _drift, _ease;
        Prop<SeriesSamples> _samples;
        Prop<float> _waveW, _trackW, _capShown;
        Prop<Affine2D> _capStart, _capEnd;
        bool _wired;

        public override Element Render()
        {
            var r = UseProps<WaveRailProps>().R;
            var rail = r.Rail;
            var host = rail.Host;
            _host = host;
            bool enabled = rail.Enabled;
            bool drawn = enabled || rail.Pending;
            bool playing = r.Playing && drawn;
            float full = r.Edge ? M.EdgeWaveAmp : M.WaveAmp;   // the strip's edge swings less: it runs under the thumb's badge
            _full = full;
            if (_amp < 0f) { _amp = playing ? full : 0f; _playing = playing; }   // the first frame shows the state as it is
            if (!_wired) Wire(host);

            // Play / pause: ease the swing toward its new size (at once under reduced motion).
            UseLayoutEffect(() =>
            {
                if (playing == _playing) return;
                _playing = playing;
                _easeFrom = _amp;
                _easeTo = playing ? _full : 0f;
                _easeT = 0f;
                if (Motion.ReducedMotion) { _amp = _easeTo; _frame.Value = _frame.Peek() + 1; return; }
                _easing.Value = true;
            }, DepKey.From(playing));
            bool easing = _easing.Value;
            bool moves = UseIsActive().Value && !Motion.ReducedMotion;
            UseInterval(_ease!, M.EaseTickMs, enabled: easing);
            UseInterval(_drift!, M.WaveTickMs, enabled: moves && playing && !easing);

            ColorF track = r.OnDark ? H.OnMediaInk with { A = H.OnMediaTrackAlpha } : Tok.TextPrimary with { A = M.TrackAlpha };
            ColorF accent = drawn ? r.Accent : r.Accent with { A = r.Accent.A * H.DisabledAlpha };
            var kids = new List<Element>(4)
            {
                new BoxEl
                {
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.End, Height = M.Track, Width = _trackW, Corners = CornerRadius4.All(M.TrackCorner),
                    Fill = track, HitTestVisible = false,
                },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start, Height = M.WaveH, Width = _waveW, Direction = 0, HitTestVisible = false,
                    Children =
                    [
                        new SeriesEl
                        {
                            Samples = _samples, Shape = SeriesShape.Stroke, Thickness = M.Track, Baseline = 1f, Amplitude = 1f, Color = accent,
                            AntiAlias = true, Grow = 1f, Height = M.WaveH,
                        },
                    ],
                },
                // Round caps: a series ends square, so a dot of the stroke's width sits on each end (riding the wave's height at the start).
                Cap(accent, _capStart), Cap(accent, _capEnd),
            };
            if (drawn)
                kids.Add(new BoxEl
                {
                    Width = M.HeadW, Height = (r.Edge ? M.EdgeHeadH : M.HeadH) * M.HeadHotScale, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Transform = host.ThumbTransform(M.HeadW), OnRealized = host.ThumbRealized,
                    Children = [Embed.Comp(new HeadProps(accent, r.Hot && !r.Edge, r.Edge ? M.EdgeHeadH : M.HeadH), static () => new Head())],   // the edge's head keeps its size
                });
            kids.Add(host.KeyLadderTick());
            return new BoxEl
            {
                Key = (r.Edge ? H.EdgeKey : H.SeekKey) + ":rail", Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = r.Edge ? M.EdgeH : H.SeekRowH,
                ZStack = true, Role = AutomationRole.Slider, IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null, OnBoundsChanged = _bounds,
                OnPointerDown = enabled ? host.PointerDown : null, OnDrag = enabled ? host.DragMove : null,
                OnClick = enabled ? host.Commit : null, OnDragCanceled = enabled ? host.Cancel : null,
                Children = kids.ToArray(),
            };
        }

        /// <summary>The binds and ticks, built once: the head's x (the bar's own thumb placement), the wave's and the track's widths, the samples.</summary>
        void Wire(ISeekRailHost host)
        {
            _wired = true;
            var width = _width;
            var frame = _frame;
            var fill = host.FillTransform;
            _bounds = b => { _host?.BoundsChanged(b); width.Value = b.W; };
            _waveW = Prop.Of(() => WaveWidth(width.Value, fill.Current().M11));
            _trackW = Prop.Of(() => MathF.Max(0f, width.Value - HeadX(width.Value, fill.Current().M11) - 0.5f * M.HeadW - M.TrackGap));
            _samples = Prop.Of(() =>
            {
                _ = frame.Value;
                float w = WaveWidth(width.Value, fill.Current().M11);
                return Sample(w);
            });
            _capShown = Prop.Of(() => WaveWidth(width.Value, fill.Current().M11) >= 1f ? 1f : 0f);
            _capStart = Prop.Of(() =>
            {
                _ = frame.Value;
                float w = WaveWidth(width.Value, fill.Current().M11);
                float t = Math.Clamp(w / (0.5f * M.WavePeriod), 0f, 1f);
                float d = _amp * t * t * (3f - 2f * t) * MathF.Sin(2f * MathF.PI / M.WavePeriod * _phase);
                return Affine2D.Translation(-0.5f * M.Track, -d);
            });
            _capEnd = Prop.Of(() => Affine2D.Translation(WaveWidth(width.Value, fill.Current().M11) - 0.5f * M.Track, 0f));
            _drift = () =>
            {
                float next = _phase + M.WaveStep;
                _phase = next >= M.WavePeriod ? next - M.WavePeriod : next;
                _frame.Value = _frame.Peek() + 1;
            };
            _ease = () =>
            {
                _easeT = MathF.Min(1f, _easeT + M.EaseTickMs / M.EaseMs);
                float e = 1f - (1f - _easeT) * (1f - _easeT) * (1f - _easeT);
                _amp = _easeFrom + (_easeTo - _easeFrom) * e;
                if (_playing) { float next = _phase + M.WaveStep * M.EaseTickMs / M.WaveTickMs; _phase = next >= M.WavePeriod ? next - M.WavePeriod : next; }
                _frame.Value = _frame.Peek() + 1;
                if (_easeT >= 1f) _easing.Value = false;
            };
        }

        Element Cap(ColorF accent, Prop<Affine2D> at) => new BoxEl
        {
            Width = M.Track, Height = M.Track, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start, Corners = CornerRadius4.All(0.5f * M.Track),
            Fill = accent, Opacity = _capShown, Transform = at, HitTestVisible = false,
        };

        /// <summary>The head's centre: the played fraction of the rail, kept a half head inside either end (as the bar's thumb placement).</summary>
        static float HeadX(float w, float fraction) => Math.Clamp(w * fraction, 0.5f * M.HeadW, MathF.Max(0.5f * M.HeadW, w - 0.5f * M.HeadW));

        static float WaveWidth(float w, float fraction) => MathF.Max(0f, HeadX(w, fraction) - 0.5f * M.HeadW - M.WaveGap);

        /// <summary>The wave across <paramref name="w"/> DIP into the reused buffer: one sample a DIP, the swing tapering (smoothstep) to nothing
        /// over the last half wavelength. A sample is a 0..1 height in the box (0.5 the middle line).</summary>
        SeriesSamples Sample(float w)
        {
            int n = w < 1f ? 0 : Math.Min(_buf.Length, (int)MathF.Ceiling(w / M.SampleStep) + 1);
            float step = n > 1 ? w / (n - 1) : 0f, half = 0.5f * M.WavePeriod, k = 2f * MathF.PI / M.WavePeriod, amp = _amp, phase = _phase;
            for (int i = 0; i < n; i++)
            {
                float x = i * step;
                float t = Math.Clamp((w - x) / half, 0f, 1f);
                float taper = t * t * (3f - 2f * t);
                float d = amp * taper * MathF.Sin(k * (x + phase));
                _buf[i] = Math.Clamp(0.5f + d / M.WaveH, 0f, 1f);
            }
            return new SeriesSamples(_buf, n, ++_version);
        }
    }

    /// <summary>The playhead: a short rounded pill in the accent, a little taller while the controls show (eased, never snapped).</summary>
    sealed class Head : Component
    {
        bool _seen;

        public override Element Render()
        {
            var p = UseProps<HeadProps>();
            float to = p.Hot ? M.HeadHotScale : 1f;
            float from = _seen ? (p.Hot ? 1f : M.HeadHotScale) : to;
            _seen = true;
            UseTransition(AnimChannel.ScaleY, from, to, M.HeadMs, Easing.FluentDecelerate, DepKey.From(p.Hot));
            return new BoxEl
            {
                Width = M.HeadW, Height = p.Height, Corners = CornerRadius4.All(0.5f * M.HeadW), Fill = p.Accent, HitTestVisible = false,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
            };
        }
    }

    /// <summary>A sine-like line (a cubic rise and fall per period) across <paramref name="w"/>, centred in <paramref name="h"/>.</summary>
    static PathData Sine(float w, float h, float period, float amp)
    {
        float mid = h * 0.5f;
        var b = new PathBuilder();
        b.MoveTo(0f, mid);
        for (float x = 0f; x < w - 0.5f; x += period)
        {
            b.CubicTo(x + 0.25f * period, mid - amp, x + 0.25f * period, mid - amp, x + 0.5f * period, mid);
            b.CubicTo(x + 0.75f * period, mid + amp, x + 0.75f * period, mid + amp, x + period, mid);
        }
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    // ══ THE VISUALIZER: THREE SLOW SINES ═════════════════════════════════════════════════════════════════════════════════

    sealed record SinesProps(MiniVisualizerFrame Frame, ColorF Accent);

    /// <summary>Classic's analyser: three waves drifting at their own pace (they move only while the music does), each swelling with its third of
    /// the bands (a bound scale, no re-render).</summary>
    sealed class Sines : Component
    {
        public override Element Render()
        {
            var p = UseProps<SinesProps>();
            var f = p.Frame;
            bool run = f.Running.Value;
            return new BoxEl
            {
                Width = f.Width, Height = f.Height, ZStack = true, ClipToBounds = true, HitTestVisible = false,
                Children =
                [
                    Wave(f, run, 2, M.Viz2Top, M.Viz2H, M.Viz2Ms, p.Accent with { A = M.Viz2Alpha }),
                    Wave(f, run, 1, M.Viz1Top, M.Viz1H, M.Viz1Ms, H.OnMediaInk with { A = M.Viz1Alpha }),
                    Wave(f, run, 0, M.Viz0Top, M.Viz0H, M.Viz0Ms, p.Accent),
                ],
            };
        }

        static Element Wave(MiniVisualizerFrame f, bool run, int third, float top, float height, float ms, ColorF ink)
        {
            float h = f.Height * height, w = f.Width + M.VizPeriod;
            var bands = f.Bands;
            int from = third * bands.Length / 3, to = (third + 1) * bands.Length / 3;
            Element line = new PathEl
            {
                Geometry = Sine(w, h, M.VizPeriod, h * M.VizAmp), Width = w, Height = h, StrokeColor = ink,
                Stroke = new StrokeStyle(M.VizStroke, LineCap.Round, LineJoin.Round),
            };
            return new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = f.Width, Height = h, Margin = new Edges4(0f, f.Height * top, 0f, 0f),
                ClipToBounds = true, HitTestVisible = false, TransformOriginY = 0.5f,
                Transform = Prop.Of(() =>
                {
                    float sum = 0f;
                    for (int i = from; i < to; i++) sum += bands[i].Value;
                    float level = to > from ? sum / (to - from) : 0f;
                    return Affine2D.Scale(1f, M.VizMin + (1f - M.VizMin) * Math.Clamp(level, 0f, 1f));
                }),
                Children = [run ? Deck.Drift(line, M.VizPeriod, ms, pingPong: false) : line],
            };
        }
    }

    // ══ LYRICS ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A lyric line in the display face: the ticker's sung line over its next; a column line on a soft plate, the active one tinted
    /// with the album's colour (on imagery, a white veil).</summary>
    static Element LyricLine(MiniLyricLine l)
    {
        if (l.Use == MiniLyricUse.TickerNow)
            return Line(R.Type.TrackMeta(l.Text)) with { Size = M.TickerNow, LineHeight = M.TickerNowLeading, Color = Tok.TextPrimary, FontFamily = R.Type.DisplayFace };
        if (l.Use == MiniLyricUse.TickerNext)
            return Line(R.Type.TrackMeta(l.Text)) with { Size = M.TickerNext, LineHeight = M.TickerNextLeading, Color = Tok.TextTertiary };
        float size = M.LyricSize(l.Shape);
        ColorF ink = l.Active ? (l.OnDark ? H.OnMediaInk : Tok.TextPrimary) : (l.OnDark ? H.OnMediaInk with { A = M.InactiveOnDarkAlpha } : Tok.TextTertiary);
        ColorF plate = !l.Active ? ColorF.Transparent : l.OnDark ? H.OnMediaInk with { A = M.ActiveOnDarkPlate } : l.Accent with { A = M.Tint2Alpha };
        float space = size * M.LyricSpace;
        return new BoxEl
        {
            Direction = 1, Padding = new Edges4(M.LyricPad, space, M.LyricPad, space), Margin = new Edges4(0f, 0f, 0f, M.LyricGap),
            Corners = CornerRadius4.All(M.LyricCorner), Fill = plate, BrushTransitionMs = M.HoverInMs, HitTestVisible = false,
            Children =
            [
                R.Type.TrackMeta(l.Text) with
                {
                    Size = size, LineHeight = size * M.LyricLeading, Color = ink, FontFamily = R.Type.DisplayFace, Wrap = TextWrap.Wrap, MaxLines = 0,
                    Trim = TextTrim.None, Shrink = 1f, MinWidth = 0f,
                },
            ],
        };
    }
}
