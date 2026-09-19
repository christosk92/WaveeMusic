// ── Platform/Controls.Podcast.cs ───────────────────────────────────────────────────────────────────────────────────
// the podcast reader's small controls: the badge CHIP, the played LEDGER bar, the STAR row (the rate flyout's), the
// start-here / next-in-the-story DOOR (+ its `DoorData`), and the reader's FIND box
//
// Role: UI
// Owner: O
// Wave: P1 (the podcast rework — nothing mounts these yet; waves P3/P5 do)
// Budget: 420 lines
// Spec: podcast-show-rework-implementation.md §5.5 (the surface), §2 W1-W4 (placement), §7 (motion), §4 (keyboard);
//       the approved prototype docs/plans/wavee/podcast-show-episode-mica.html (.chip, .ledger, .flyout .stars, .door,
//       .find)
//
// ── THE TONE IS A LIVE READ ──────────────────────────────────────────────────────────────────────────────────────────
//
// Every control here takes the show's tone as a `Func<ColorF>` and binds it (`Prop.Of(tone)`), so a palette that lands
// after the page mounted re-fires the binds instead of re-rendering anything. The one exception is the lead door's
// GRADIENT: `BoxEl.Gradient` is a plain value, so `Door` reads the tone ONCE while building — called from a component's
// render, that read subscribes the caller, which re-renders on the (rare) tone change.
//
// ── WHAT IS NOT HERE ─────────────────────────────────────────────────────────────────────────────────────────────────
//
// No caps transform. The prototype sets the door label in capitals via CSS; this file does NOT upper-case a localized
// string (Design.Type.Eyebrow's rule — a caps transform mangles Turkish dotted i and German ß): the label keeps its own
// casing at 11/700 with +90 tracking. The ledger and the stars never format a number: their callers pass values.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>One door (W2's "start here" cards, W4's "next in the story"): a card that OPENS on click and carries its own
/// play disc. <paramref name="Label"/> is the small toned word ("begin here", "latest", "next"); <paramref name="Why"/>
/// and <paramref name="Meta"/> ("38 min", "Sep 15 · 28 min") are optional; <paramref name="Lead"/> paints the tone
/// gradient (the door the show wants you to take); <paramref name="Numeral"/> is the 64-px ghost numeral top-right (the
/// episode number, null for a trailer); a null <paramref name="Play"/> drops the disc. <paramref name="Tone"/> is the
/// show's tone (label ink, disc fill, lead gradient).</summary>
public sealed record DoorData(string Label, string Title, string? Why, string? Meta, bool Lead, string? Numeral,
                              Action? Play, Action Open, Func<ColorF> Tone);

public static partial class Controls
{
    static readonly Func<ColorF> s_podcastAccent = static () => Tok.AccentTextPrimary;

    /// <summary><paramref name="c"/> at <paramref name="alpha"/> × its own alpha (the prototype's
    /// <c>color-mix(tone N%, transparent)</c>).</summary>
    static ColorF ToneAt(in ColorF c, float alpha) => c with { A = c.A * alpha };

    // ══ 1. THE BADGE CHIP ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The badge chip's metrics: 16 high, radius 3, 11/600, 6-DIP waist, an 11-DIP glyph 4 before the word.</summary>
    public const float BadgeHeight = 16f, BadgeRadius = 3f, BadgeTextSize = 11f, BadgePadX = 6f, BadgeGlyph = 11f;
    /// <summary>A toned chip's plate: the tone at 22 % (the prototype's <c>.chip.tone</c>).</summary>
    public const float BadgeToneFill = 0.22f;

    /// <summary>A small badge beside a title — "exclusive", "E", "video", "episode 9", "subscribers only". Neutral:
    /// <c>TextSecondary</c> on the subtle plate. <paramref name="tone"/>: the show's tone ink on the tone at 22 %
    /// (<paramref name="toneOf"/>, the system accent text when null). Not interactive — a filter chip is the OTHER
    /// <c>Chip</c> overload (<c>Controls.Art.cs</c>).</summary>
    public static Element Chip(string text, string? glyph = null, bool tone = false, Func<ColorF>? toneOf = null)
    {
        Func<ColorF> t = toneOf ?? s_podcastAccent;
        Prop<ColorF> ink = tone ? Prop.Of(t) : Tok.TextSecondary;
        Prop<ColorF> fill = tone ? Prop.Of(() => ToneAt(t(), BadgeToneFill)) : Tok.FillSubtleSecondary;
        var label = new TextEl(text)
        {
            Size = BadgeTextSize, LineHeight = BadgeHeight, Weight = 600, Color = ink, MaxLines = 1, Wrap = TextWrap.NoWrap,
        };
        Element[] kids = glyph is { Length: > 0 } g ? [Icon(g, BadgeGlyph) with { Color = ink }, label] : [label];
        return new BoxEl
        {
            Direction = 0, Height = BadgeHeight, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 0f,
            Padding = new Edges4(BadgePadX, 0f, BadgePadX, 0f), Corners = CornerRadius4.All(BadgeRadius), Fill = fill,
            Children = kids,
        };
    }

    // ══ 2. THE LEDGER BAR ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ledger bar: 4 high, 2 between parts, every part at least 2 wide.</summary>
    public const float LedgerHeight = 4f, LedgerGap = 2f, LedgerMinPart = 2f;
    /// <summary>The in-progress part's tone (the prototype's 45 % mix) and the floor a part's weight never drops under
    /// (so an empty part keeps its 2-DIP stub and the flex never divides by zero).</summary>
    public const float LedgerProgressTone = 0.45f, LedgerFloor = 0.001f;

    /// <summary>The played ledger (W1): three flex parts — played (the tone), in progress (the tone at 45 %), to go (the
    /// subtle ground). <paramref name="played"/> and <paramref name="progress"/> are FRACTIONS of the show (0..1, the
    /// rest is "to go"); both are live (<see cref="Prop{T}"/>), and a change re-renders only this bar and slides each
    /// part from its old rect over 250 ms (plan §7 — a mark-played animates, a rail drag does not).</summary>
    public static Element LedgerBar(Prop<float> played, Prop<float> progress, Func<ColorF> tone)
        => Embed.Comp(new LedgerProps(played, progress, tone), static () => new LedgerHost());

    /// <summary>The three part weights for <see cref="LedgerBar"/>: each fraction clamped into what is left of the whole
    /// (progress never overlaps played), the rest to go, every weight floored at <see cref="LedgerFloor"/>.</summary>
    public static (float Played, float Progress, float ToGo) LedgerPartsOf(float played, float progress)
    {
        float a = Clamp01(played);
        float b = MathF.Min(Clamp01(progress), 1f - a);
        float c = MathF.Max(0f, 1f - a - b);
        return (MathF.Max(LedgerFloor, a), MathF.Max(LedgerFloor, b), MathF.Max(LedgerFloor, c));

        static float Clamp01(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, 1f);
    }

    /// <summary>Where the flex puts the three parts in a bar <paramref name="width"/> wide — the FLIP's "from" and "to"
    /// (the layout itself is the engine's flex; this mirrors it closely enough to start a slide from). A part whose share
    /// falls under <see cref="LedgerMinPart"/> is pinned at it and the others share what is left, Yoga's freeze-and-
    /// redistribute.</summary>
    public static void LedgerGeometry(float width, float played, float progress, float toGo, Span<float> x, Span<float> w)
    {
        Span<float> g = stackalloc float[3];
        g[0] = played; g[1] = progress; g[2] = toGo;
        Span<bool> pinned = stackalloc bool[3];
        float pool = MathF.Max(0f, width - 2f * LedgerGap), sum = g[0] + g[1] + g[2];
        for (int pass = 0; pass < 3; pass++)
        {
            bool changed = false;
            for (int i = 0; i < 3; i++)
            {
                if (pinned[i]) continue;
                float share = sum > 0f ? pool * g[i] / sum : 0f;
                if (share >= LedgerMinPart) continue;
                pinned[i] = true; w[i] = LedgerMinPart; pool -= LedgerMinPart; sum -= g[i]; changed = true;
            }
            if (!changed) break;
        }
        for (int i = 0; i < 3; i++)
            if (!pinned[i]) w[i] = sum > 0f ? MathF.Max(0f, pool) * g[i] / sum : 0f;
        x[0] = 0f;
        x[1] = w[0] + LedgerGap;
        x[2] = x[1] + w[1] + LedgerGap;
    }

    sealed record LedgerProps(Prop<float> Played, Prop<float> Progress, Func<ColorF> Tone);

    /// <summary>The ledger's host. Its render READS the two fractions (subscribing to whatever they bind), so a value
    /// change re-renders this 3-box bar and nothing else; the layout effect keyed on the weights then FLIPs each part from
    /// the geometry the OLD weights gave at the bar's current width. Handlers are built once in the constructor.</summary>
    sealed class LedgerHost : Component
    {
        static readonly MotionTokenDef s_flex = MotionTokenDef.Eased(Design.Motion.Standard, Easing.FluentStandard, ReducedMotionPolicy.SnapEnd);

        readonly NodeHandle[] _parts = new NodeHandle[3];
        readonly Action<NodeHandle>[] _realized;
        readonly Action<RectF> _measured;
        readonly Action _slide;
        float _barW, _a, _b, _c;
        float _prevA = -1f, _prevB, _prevC;
        Func<ColorF>? _tone;
        Prop<ColorF> _playedInk, _progressInk;

        public LedgerHost()
        {
            _realized = [h => _parts[0] = h, h => _parts[1] = h, h => _parts[2] = h];
            _measured = r => _barW = r.W;
            _slide = Slide;
        }

        public override Element Render()
        {
            var p = UseProps<LedgerProps>();
            (_a, _b, _c) = LedgerPartsOf(p.Played.Current(), p.Progress.Current());
            if (!ReferenceEquals(_tone, p.Tone))
            {
                Func<ColorF> tone = p.Tone;
                _tone = tone;
                _playedInk = Prop.Of(tone);
                _progressInk = Prop.Of(() => ToneAt(tone(), LedgerProgressTone));
            }
            UseLayoutEffect(_slide, DepKey.From(_a, _b));
            return new BoxEl
            {
                Direction = 0, Height = LedgerHeight, Gap = LedgerGap, MinWidth = 0f, Shrink = 0f,
                OnBoundsChanged = _measured,
                Children = [Part(0, _a, _playedInk), Part(1, _b, _progressInk), Part(2, _c, Tok.FillSubtleTertiary)],
            };
        }

        BoxEl Part(int i, float weight, Prop<ColorF> fill) => new()
        {
            Grow = weight, Basis = 0f, MinWidth = LedgerMinPart, Corners = CornerRadius4.All(LedgerHeight / 2f),
            Fill = fill, TransformOriginX = 0f, OnRealized = _realized[i],
        };

        void Slide()
        {
            float a = _a, b = _b, c = _c, pa = _prevA, pb = _prevB, pc = _prevC;
            _prevA = a; _prevB = b; _prevC = c;
            if (pa < 0f || _barW <= 0f || Context.Anim is not { } anim) return;   // the mount run: nothing to slide from
            Span<float> ox = stackalloc float[3];
            Span<float> ow = stackalloc float[3];
            Span<float> nx = stackalloc float[3];
            Span<float> nw = stackalloc float[3];
            LedgerGeometry(_barW, pa, pb, pc, ox, ow);
            LedgerGeometry(_barW, a, b, c, nx, nw);
            for (int i = 0; i < 3; i++)
            {
                if (_parts[i].IsNull) continue;
                var s = Words.SlideFrom(ox[i], ow[i], nx[i], nw[i]);
                if (!s.Animate) continue;
                anim.SeedValue(_parts[i], AnimChannel.TranslateX, 0f, in s_flex, from: s.Dx);
                anim.SeedValue(_parts[i], AnimChannel.ScaleX, 1f, in s_flex, from: s.Scale);
            }
        }
    }

    // ══ 3. THE STAR ROW ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Stars in a row, and the padding round each star's hit/hover plate.</summary>
    public const int StarCount = 5;
    /// <inheritdoc cref="StarCount"/>
    public const float StarPad = 2f;

    /// <summary>Five stars, the first <paramref name="value"/> filled, <paramref name="size"/> each. With a
    /// <paramref name="rate"/> every star is a button (a tab stop, named "N stars") that rates N, inked in
    /// <paramref name="tone"/> (the accent text when null). A null <paramref name="rate"/> is DISPLAY-ONLY and looks
    /// disabled — the rate flyout before a write path exists (plan wave P10), where the stars sit beside
    /// <c>podcast.rate.unavailable</c>.</summary>
    public static Element StarRow(int value, Action<int>? rate, float size, Func<ColorF>? tone = null)
    {
        Prop<ColorF> ink = rate is null ? Tok.TextDisabled : Prop.Of(tone ?? s_podcastAccent);
        float edge = size + 2f * StarPad;
        var stars = new Element[StarCount];
        for (int i = 0; i < StarCount; i++)
        {
            int n = i + 1;
            var cell = new BoxEl
            {
                Width = edge, Height = edge, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Children = [Icon(n <= value ? Icons.FavoriteStarFill : Icons.FavoriteStar, size) with { Color = ink }],
            };
            stars[i] = rate is { } r
                ? Named(cell with
                  {
                      Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => r(n),
                      HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                      HoverScale = Design.Motion.ScaleSubtle.Hover, PressScale = Design.Motion.ScaleSubtle.Press,
                  }, Strings.Podcast.Rate.Stars(n))
                : cell with { IsEnabled = false };
        }
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XXS, AlignItems = FlexAlign.Center, Shrink = 0f, Role = AutomationRole.Rating,
            Children = stars,
        };
    }

    // ══ 4. THE DOOR ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The door's metrics: min 132 high (a caller's grid keeps it ≥ 230 wide), 14 padding, the 64-px ghost
    /// numeral at 9 % ink 10 from the right and 6 above the top, the 32 disc with a 14 glyph, the lead gradient's 26 %.</summary>
    public const float DoorMinHeight = 132f, DoorMinWidth = 230f, DoorPad = 14f, DoorNumeralSize = 64f,
                       DoorNumeralInk = 0.09f, DoorDisc = 32f, DoorDiscGlyph = 14f, DoorLeadTone = 0.26f;

    const string DoorFace = "Segoe UI Variable Display";
    static readonly MotionTarget s_doorLift = new() { OffsetY = -1f };

    /// <summary>A door (W2 / W4): an r8 card — the tone gradient when it LEADS — lifting 1 DIP on hover (150 ms, off
    /// under reduced motion) with a subtle plate. The label 11/700 in the tone, the title 15/600 (≤ 2 lines), the why 12
    /// (≤ 2 lines), then the foot: a 32 tone disc that PLAYS and the meta line. The whole card OPENS; the disc is its own
    /// tab stop (plan §4). The numeral is a ghost behind the copy, clipped by the card's corners.</summary>
    public static Element Door(DoorData d)
    {
        var copy = new List<Element>(5)
        {
            new TextEl(d.Label)
            {
                Size = 11f, LineHeight = 14f, Weight = 700, CharSpacing = 90f, Color = Prop.Of(d.Tone),
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
            new TextEl(d.Title)
            {
                Size = 15f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary,
                MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        if (d.Why is { Length: > 0 } why)
            copy.Add(new TextEl(why)
            {
                Size = 12f, LineHeight = 17f, Color = Tok.TextSecondary,
                MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
        copy.Add(new BoxEl { Grow = 1f });   // the foot sits on the card's floor (the prototype's margin-top: auto)
        copy.Add(DoorFoot(d));

        var layers = new List<Element>(3)
        {
            // The hover plate: a sibling UNDER the copy, faded in by the card's hover (a gradient card has no fill to
            // ramp, so a HoverFill would do nothing on the lead door).
            new BoxEl
            {
                HitTestVisible = false, Opacity = 0f, HoverOpacity = 1f, HoverDurationMs = MotionTok.ControlFast.DurationMs,
                Corners = Radii.CardAll, Fill = Tok.FillSubtleSecondary,
            },
        };
        if (d.Numeral is { Length: > 0 } numeral)
            layers.Add(new BoxEl
            {
                // Its own clipping layer, so the card root stays unclipped (its focus rect draws outside it).
                ZStack = true, HitTestVisible = false, ClipToBounds = true, Corners = Radii.CardAll,
                Children =
                [
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End, Margin = new Edges4(0f, 0f, 10f, 0f),
                        OffsetY = -6f, Opacity = DoorNumeralInk,
                        Children =
                        [
                            new TextEl(numeral)
                            {
                                FontFamily = DoorFace, Size = DoorNumeralSize, LineHeight = DoorNumeralSize, Weight = 300,
                                Color = Tok.TextPrimary, MaxLines = 1,
                            },
                        ],
                    },
                ],
            });
        layers.Add(new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Padding = Edges4.All(DoorPad), MinWidth = 0f, Children = copy.ToArray(),
        });

        return new BoxEl
        {
            ZStack = true, MinHeight = DoorMinHeight, MinWidth = 0f,
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Gradient = d.Lead ? LeadGradient(d.Tone()) : (GradientSpec?)null,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = d.Open,
            WhileHover = s_doorLift, Transition = MotionTok.ControlFast,
            Children = layers.ToArray(),
        };
    }

    /// <summary>The lead door's wash: the tone at 26 % in the top-left corner fading into the card fill by 70 % of the
    /// diagonal (the prototype's <c>linear-gradient(135deg, …)</c> — the engine's 45° runs top-left to bottom-right).</summary>
    static GradientSpec LeadGradient(in ColorF tone) => new(GradientShape.Linear, 45f,
    [
        new GradientStop(0f, ToneAt(tone, DoorLeadTone)),
        new GradientStop(0.7f, Tok.FillCardDefault),
    ]);

    static Element DoorFoot(DoorData d)
    {
        var kids = new List<Element>(2);
        if (d.Play is { } play) kids.Add(DoorPlay(play, d.Tone));
        if (d.Meta is { Length: > 0 } meta)
            kids.Add(new TextEl(meta)
            {
                Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f, Shrink = 1f,
            });
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray(),
        };
    }

    /// <summary>The door's 32 disc: the tone, a contrast-picked play glyph, the emphatic scale tier, its own tab stop and
    /// its name ("Play"). It blocks a drag arm so a door that ever becomes a drag source still plays.</summary>
    static Element DoorPlay(Action play, Func<ColorF> tone) => Named(new BoxEl
    {
        Width = DoorDisc, Height = DoorDisc, Shrink = 0f, Corners = Radii.Circle(DoorDisc),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Fill = Prop.Of(tone),
        Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered, Cursor = CursorId.Hand,
        HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
        BlocksDragArm = true, OnClick = play,
        Children =
        [
            new BoxEl
            {
                Width = DoorDiscGlyph, Height = DoorDiscGlyph, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Play, DoorDiscGlyph) with { Color = Prop.Of(() => ColorContrast.PickContrast(tone())) }],
            },
        ],
    }, Loc.Get(Strings.Detail.Play));

    // ══ 5. THE FIND BOX ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The find box's width in the reader's sticky rail (the prototype's 120-DIP input + its chrome).</summary>
    public const float FindBoxWidth = 180f;

    /// <summary>The reader's in-show find (W1): a stock single-line TextBox writing <paramref name="query"/> on every
    /// edit — the reader filters on it and never persists it (plan §4). It is reached by Tab from the rail; Ctrl+F stays
    /// the omnibar.</summary>
    public static Element FindBox(Signal<string> query, string placeholder)
        => TextBox.Create(query, null, new TextBox.TextBoxOptions { Placeholder = placeholder, Width = FindBoxWidth });
}
