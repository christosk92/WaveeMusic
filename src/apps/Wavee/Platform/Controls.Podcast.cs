// ── Platform/Controls.Podcast.cs ───────────────────────────────────────────────────────────────────────────────────
// the podcast pages' small controls: the badge CHIP, the STAR row, the RATE flyout, the WORD line, and the FIND box (the
// people list's)
//
// Role: UI
// Owner: O
// Wave: P1 (the podcast rework — nothing mounts these yet; waves P3/P5 do)
// Budget: 420 lines
// Spec: podcast-show-rework-implementation.md §5.5 (the surface), §2 W1-W4 (placement), §7 (motion), §4 (keyboard);
//       the approved prototype docs/plans/wavee/podcast-show-episode-mica.html (.chip, .ledger, .flyout .stars, .find)
//
// ── THE TONE IS A LIVE READ ──────────────────────────────────────────────────────────────────────────────────────────
//
// Every control here takes the show's tone as a `Func<ColorF>` and binds it (`Prop.Of(tone)`), so a palette that lands
// after the page mounted re-fires the binds instead of re-rendering anything.
//
// ── WHAT IS NOT HERE ─────────────────────────────────────────────────────────────────────────────────────────────────
//
// No caps transform: this file does NOT upper-case a localized string (R.Type.Eyebrow's rule — a caps transform mangles
// Turkish dotted i and German ß). The stars never format a number: their callers pass values.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

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

    /// <summary>THE static badge beside a title — "exclusive", "E", "video", "episode 9", "subscribers only", and Search's
    /// "matched title" / "lyrics match" on the top result (D7: one badge, one chip; no private boxes). Neutral:
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

    // ══ 2. THE STAR ROW ══════════════════════════════════════════════════════════════════════════════════════════════

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
                      HoverScale = R.Motion.ScaleSubtle.Hover, PressScale = R.Motion.ScaleSubtle.Press,
                  }, Strings.Podcast.Rate.Stars(n))
                : cell with { IsEnabled = false };
        }
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XXS, AlignItems = FlexAlign.Center, Shrink = 0f, Role = AutomationRole.Rating,
            Children = stars,
        };
    }

    // ══ 2b. THE RATE FLYOUT ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The rate picker's metrics: the flyout presenter's padding, its measure (the note wraps inside it), the gap between its lines and
    /// the busy ring beside the stars.</summary>
    public const float RatePickerPadX = 16f, RatePickerPadTop = 15f, RatePickerPadBottom = 17f, RatePickerMaxWidth = 300f, RatePickerGap = 8f,
                       RatePickerRing = 16f;

    /// <summary>A theme's own Rate button (<paramref name="trigger"/>: its look, label and height) that opens THE rate picker of the podcast pages:
    /// "Rate {name}", the five stars holding the listener's own rating (a pick rates N through <paramref name="rate"/> while
    /// <see cref="Theming.Contracts.PodcastRateVm.Enabled"/>), a ring while the write is in flight, and the note (why the stars are off, or the
    /// failed write). The picker reads <paramref name="state"/> live, so it follows a write while it stays open. Wrapped in a box keyed
    /// <paramref name="key"/> (the trigger's own key would be dropped as a component's root).</summary>
    public static Element RateFlyout(string key, BoxEl trigger, IReadSignal<Theming.Contracts.PodcastRateVm> state, Action<int>? rate, float star,
                                     Func<ColorF>? tone = null)
        => new BoxEl
        {
            Key = key, Shrink = 0f, MinWidth = 0f,
            Children = [Embed.Comp(new RateFlyoutProps(trigger, state, rate, star, tone), static () => new RateFlyoutHost())],
        };

    sealed record RateFlyoutProps(BoxEl Trigger, IReadSignal<Theming.Contracts.PodcastRateVm> State, Action<int>? Rate, float Star, Func<ColorF>? Tone);

    /// <summary>The trigger with the picker attached. The anchor lives on the component (not in a per-render closure, as <c>Flyout.Attach</c>
    /// keeps it), so a re-render of the page around it (props re-pushed) never loses the realized node.</summary>
    sealed class RateFlyoutHost : Component
    {
        RateFlyoutProps? _p;
        IOverlayService? _overlay;
        NodeHandle _anchor;
        OverlayHandle? _open;
        readonly Action _toggle;
        readonly Action<NodeHandle> _realized;
        readonly Func<NodeHandle> _anchorOf;
        readonly Func<Element> _body;

        public RateFlyoutHost()
        {
            _toggle = Toggle;
            _realized = h => _anchor = h;
            _anchorOf = () => _anchor;
            _body = () => _p is { } p ? Embed.Comp(p, static () => new RatePicker()) : new BoxEl();
        }

        public override Element Render()
        {
            var p = _p = UseProps<RateFlyoutProps>();
            _overlay = UseContext(Overlay.Service);
            var own = p.Trigger.OnRealized;
            return p.Trigger with { OnClick = _toggle, OnRealized = own is null ? _realized : h => { own(h); _realized(h); } };
        }

        void Toggle()
        {
            if (_open is { IsOpen: true } shown) { shown.Close(); return; }
            if (IsNullOverlay(_overlay) || _anchor.IsNull) return;
            _open = _overlay.Open(_anchorOf, _body, FlyoutPlacement.BottomEdgeAlignedLeft, new PopupOptions(Chrome: PopupChrome.Popup));
        }
    }

    sealed class RatePicker : Component
    {
        public override Element Render()
        {
            var p = UseProps<RateFlyoutProps>();
            var s = p.State.Value;
            Element stars = StarRow(s.Mine, s.Enabled ? p.Rate : null, p.Star, p.Tone);
            var lines = new List<Element>(3)
            {
                BodyStrong(s.Title) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                new BoxEl
                {
                    Direction = 0, Gap = RatePickerGap, AlignItems = FlexAlign.Center,
                    Children = s.Busy ? [stars, ProgressRing.Indeterminate(RatePickerRing)] : [stars],
                },
            };
            if (s.Note.Length > 0)
                lines.Add(Caption(s.Note) with
                {
                    Wrap = TextWrap.Wrap, MinWidth = 0f, Color = s.Failed ? Tok.SystemFillCritical : Tok.TextSecondary,
                });
            return new BoxEl
            {
                Key = "rate-picker", Direction = 1, Gap = RatePickerGap, MinWidth = 0f, MaxWidth = RatePickerMaxWidth,
                Padding = new Edges4(RatePickerPadX, RatePickerPadTop, RatePickerPadX, RatePickerPadBottom),
                Children = lines.ToArray(),
            };
        }
    }

    // ══ 2c. THE WORD LINE ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A line of words (a header's topic or genre links, each <paramref name="units"/> item one word with its separator) followed by
    /// <paramref name="trailing"/> (its tags), that gives way a WHOLE word at a time: the words wrap inside their box and every line after the
    /// first is clipped away, so a word is never cut (never half a target) and a separator never ends the line; the tags always show, right after
    /// the last word that does. The line asks its row for its natural width (the words' and the tags' measured widths) and is the part of the row
    /// that shrinks; inside the width it is given, the words' box is exactly as wide as the words that fit. (A wrapping box takes every width it
    /// is offered, so the line measures its parts instead of asking the box.) <paramref name="line"/> is one line's height and
    /// <paramref name="slop"/> how far each word's hit rect reaches above and below it (kept inside the clip); <paramref name="padEnd"/> trailing
    /// room inside the words' box (a fade it must keep clear). The first frame lays the words out before their widths are known.</summary>
    public static Element WordLine(string key, Element[] units, Element[] trailing, float line, float slop, float gap, float padEnd = 0f)
        => Embed.Comp(new WordLineProps(units, trailing, line, slop, gap, padEnd), static () => new WordLineHost()) with { Key = key };

    sealed record WordLineProps(Element[] Units, Element[] Trailing, float Line, float Slop, float Gap, float PadEnd);

    sealed class WordLineHost : Component
    {
        const float Unknown = 1e6f;
        /// <summary>Bumped when a measured width changes (the words', the tags'); the width the row gave the line.</summary>
        readonly Signal<int> _measured = new(0);
        readonly Signal<float> _given = new(float.NaN);
        float[] _unitW = [];
        float _tagsW = float.NaN;
        Action<RectF>[] _unitMeasure = [];
        readonly Action<RectF> _arranged, _tagsMeasure;

        public WordLineHost()
        {
            _arranged = r => { if (float.IsNaN(_given.Peek()) || MathF.Abs(r.W - _given.Peek()) > 0.5f) _given.Value = r.W; };
            _tagsMeasure = r => { if (float.IsNaN(_tagsW) || MathF.Abs(r.W - _tagsW) > 0.5f) { _tagsW = r.W; _measured.Value++; } };
        }

        Action<RectF> UnitMeasure(int i) => r =>
        {
            if (i >= _unitW.Length || (!float.IsNaN(_unitW[i]) && MathF.Abs(r.W - _unitW[i]) <= 0.5f)) return;
            _unitW[i] = r.W;
            _measured.Value++;
        };

        public override Element Render()
        {
            var p = UseProps<WordLineProps>();
            int n = p.Units.Length;
            if (_unitW.Length != n)
            {
                _unitW = new float[n];
                Array.Fill(_unitW, float.NaN);
                _unitMeasure = new Action<RectF>[n];
                for (int i = 0; i < n; i++) _unitMeasure[i] = UnitMeasure(i);
            }
            _ = _measured.Value;
            float given = _given.Value;
            bool tags = p.Trailing.Length > 0;
            bool known = (!tags || !float.IsNaN(_tagsW)) && Array.TrueForAll(_unitW, static w => !float.IsNaN(w));
            float tagsRoom = tags && known ? _tagsW + p.Gap : 0f;
            float words = 0f;
            for (int i = 0; known && i < n; i++) words += (i > 0 ? p.Gap : 0f) + _unitW[i];
            float full = known ? words + p.PadEnd + tagsRoom : Unknown;
            // The words that fit in what the row gave: the longest run of whole words.
            float wordsW = float.NaN;
            if (known && !float.IsNaN(given))
            {
                float room = given - tagsRoom - p.PadEnd, x = 0f, fit = 0f;
                for (int i = 0; i < n; i++)
                {
                    x += (i > 0 ? p.Gap : 0f) + _unitW[i];
                    if (x > room + 0.01f) break;
                    fit = x;
                }
                wordsW = fit + p.PadEnd;
            }
            // The row never asks for more than the sizer (a row as wide as an old, wider grant would keep the line from shrinking back).
            float rowW = MathF.Min(given, full);
            var kids = new Element[n + 1];
            // A wrapping line places its first item even where it does not fit: an empty first item (its gap taken back) lets the first word leave
            // the line too.
            kids[0] = new BoxEl { Width = 0f, Height = p.Line, Shrink = 0f, Margin = new Edges4(0f, 0f, -p.Gap, p.Slop) };
            for (int i = 0; i < n; i++)
                kids[i + 1] = new BoxEl { Direction = 0, Shrink = 0f, OnBoundsChanged = _unitMeasure[i], Children = [p.Units[i]] };
            float h = p.Line + p.Slop + p.Slop;
            var wordsBox = new BoxEl
            {
                Width = wordsW, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Height = h, Padding = new Edges4(0f, p.Slop, p.PadEnd, 0f),
                Direction = 0, Wrap = true, Gap = p.Gap, AlignItems = FlexAlign.Start, Children = kids,
            };
            Element row = tags
                ? new BoxEl
                {
                    Width = rowW, Direction = 0, Gap = p.Gap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children = [wordsBox, new BoxEl { Direction = 0, Gap = p.Gap, AlignItems = FlexAlign.Center, Shrink = 0f, OnBoundsChanged = _tagsMeasure, Children = p.Trailing }],
                }
                : new BoxEl { Width = rowW, Direction = 0, MinWidth = 0f, Children = [wordsBox] };
            return new BoxEl
            {
                // Two layers: the sizer asks the row for the natural width; the words and the tags lay out in the width the row gave.
                ZStack = true, Shrink = 1f, MinWidth = 0f, Height = h, Margin = new Edges4(0f, -p.Slop, 0f, -p.Slop), OnBoundsChanged = _arranged,
                Children = [new BoxEl { Width = full, Height = h, HitTestVisible = false }, row],
            };
        }
    }

    // ══ 3. THE FIND BOX ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The find box's width (the prototype's 120-DIP input + its chrome).</summary>
    public const float FindBoxWidth = 180f;

    /// <summary>A list's own find (the people list's): a stock single-line TextBox writing <paramref name="query"/> on every
    /// edit — the list filters on it and never persists it (plan §4). Ctrl+F stays the omnibar.</summary>
    public static Element FindBox(Signal<string> query, string placeholder)
        => TextBox.Create(query, null, new TextBox.TextBoxOptions { Placeholder = placeholder, Width = FindBoxWidth });
}
