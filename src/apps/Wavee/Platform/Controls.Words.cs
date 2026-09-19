// ── Platform/Controls.Words.cs ─────────────────────────────────────────────────────────────────────────────────────
// THE WORD RAIL (Zune's text pivot) — the library's sort / scope / reader-sort rails and the podcast reader's filter ·
// sort · episode-tab rails — its Big (Display-face) variant, the count badges, and the wrapping word LINKS (a show's
// topic row)
//
// Role: UI
// Owner: O
// Wave: P1 (the podcast rework)
// Budget: 420 lines
// Spec: podcast-show-rework-implementation.md §5.5 (the surface), §7 (motion), §4 (keyboard / roles);
//       library-rework-implementation.md W8 (the rail this was promoted from — `User.UI.cs`'s RailBar / RailWord, deleted)
//
// ── ONE RAIL, EVERY SURFACE ──────────────────────────────────────────────────────────────────────────────────────────
//
// The library rework drew the rail privately inside `User`; the podcast reader needs the same words for its filter,
// its sort and (Big) its episode tabs, so the rail lives here and `User.WordRail/ScopeRail/ReaderSortRail` are thin
// adapters over it. The library's metrics are the rail's metrics (13.5/18, gap 14, height 32, 400 → 600, ink 50 % /
// 85 % hover / 100 %, a 2-DIP underline 3 DIP under the run): moving the rail must not move a library pixel.
//
// ── TWO STACKED RUNS, NEVER A BOUND WEIGHT ───────────────────────────────────────────────────────────────────────────
//
// `TextEl.Weight` is a plain `ushort` (engine `Dsl/Element.cs`), so the active weight is TWO STACKED RUNS whose inks
// cross-fade over 83 ms. The heavy run measures the ZStack (a ZStack takes its widest child), so activating a word
// never shifts the rail by a fraction of a DIP.
//
// ── THE UNDERLINE SLIDES (plan §7: 167 ms) ───────────────────────────────────────────────────────────────────────────
//
// Every word keeps its OWN 2-DIP underline box (so the bar's geometry, its hover dim and its layout truth are exactly
// the library's), and a selection change FLIPs the newly active word's bar from where the old one was: TranslateX from
// the x delta and ScaleX from the width ratio, both decaying to identity. Layout is the resting state — a window resize
// that cancels the track leaves the bar exactly under its word. The bars snap their fill (no brush fade): one bar is
// seen to move, not one fading out while another fades in. Reduced motion snaps at the seed (the token's policy).
//
// ── PROPS FREEZE AT MOUNT, AND THAT IS SAFE HERE ─────────────────────────────────────────────────────────────────────
//
// A rail receives its SELECTED signal instance, per-word `Prop`s and delegates that resolve current state — the only
// shape that is safe to freeze. A different word SET is a remount (a `Key` at the call site); a word that appears later
// (an episode tab whose data answered) rides `Word.Visible`, a bound presence, and needs no remount.
//
// ── ZERO ALLOCATION AFTER MOUNT ──────────────────────────────────────────────────────────────────────────────────────
//
// Every word, bind thunk, bounds handler and realize handler is built ONCE. A selection change re-fires binds and one
// layout effect; the rail never re-renders.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Controls
{
    /// <summary>The word rail and its word links. See the file header for the three rules it keeps.</summary>
    public static class Words
    {
        // ══ METRICS ══════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The standard rail — the library rework's W8 values, moved here verbatim.</summary>
        public const float Size = 13.5f, Line = 18f, Gap = 14f, Height = 32f;
        /// <summary>The Big rail (the episode page's tabs): the Display face, 20/26, gap 22, height 40.</summary>
        public const float BigSize = 20f, BigLine = 26f, BigGap = 22f, BigHeight = 40f;
        /// <summary>The two stacked runs' weights: 400 → 600 standard, 300 → 400 Big (plan §5.5).</summary>
        public const ushort LightWeight = 400, HeavyWeight = 600, BigLightWeight = 300, BigHeavyWeight = 400;
        /// <summary>An inactive word's ink, and any word's ink under the pointer (W8: 50 % / 85 % / 100 %).</summary>
        public const float RestInk = 0.5f, HoverInk = 0.85f;
        /// <summary>The underline: 2 DIP, 3 DIP under the run.</summary>
        public const float UnderlineThickness = 2f, UnderlineGap = 3f;
        /// <summary>A word's count badge ("unplayed 41"): 11 on a 14 line, 4 DIP after the word.</summary>
        public const float CountSize = 11f, CountLine = 14f;
        /// <summary>The underline's slide between two words (plan §7).</summary>
        public const float SlideMs = 167f;
        /// <summary>The word links (a show's topics): 13/18 words, 12 across and 4 down between them.</summary>
        public const float LinkSize = 13f, LinkLine = 18f, LinkGapX = 12f, LinkGapY = 4f;

        const float Tracking = -5f, BigTracking = -6f;
        const float TopPad = 2f;
        /// <summary>Below this a width is "not laid out yet" (a collapsed word, a first frame) and nothing slides.</summary>
        const float MinSlideWidth = 0.5f;
        const string DisplayFace = "Segoe UI Variable Display";

        /// <summary>The ink/opacity fade every word shares: the 83-ms WinUI BrushTransition (plan §7).</summary>
        static readonly MotionTokenDef s_inkFade = MotionTok.ControlFaster;
        static readonly MotionTokenDef s_slide = MotionTokenDef.Eased(SlideMs, Easing.FluentStandard, ReducedMotionPolicy.SnapEnd);
        static readonly Func<ColorF> s_accent = static () => Tok.AccentDefault;

        // ══ THE WORD ═════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>One rail word.
        /// <para><paramref name="Count"/> is a small badge after the word ("unplayed <b>41</b>"); an empty string hides it
        /// (a bound presence — a count that answers late re-fires one bind). <paramref name="Visible"/> is the word's
        /// bound presence (null = always): a hidden word keeps its CODE, so the selected value never shifts when a tab
        /// appears. <paramref name="Code"/> is the value the rail writes into its selected signal for this word — the
        /// word's POSITION when negative (the default), a persisted code otherwise (the library navigator's sort codes are
        /// not positions). <paramref name="Chevron"/> is the navigator's direction cue: a 10-px chevron after the word
        /// while it answers true.</para></summary>
        public readonly record struct Word(Prop<string> Label, Prop<string>? Count = null, Prop<bool>? Visible = null,
                                           int Code = -1, Func<bool>? Chevron = null);

        // ══ THE PURE DECISIONS (pinned by WordsRailTests) ════════════════════════════════════════════════════════════

        /// <summary>The value a word writes: its <see cref="Word.Code"/>, or its position when it carries none.</summary>
        public static int CodeOf(in Word word, int index) => word.Code >= 0 ? word.Code : index;

        /// <summary>Tapping the ACTIVE word is a re-select (the navigator flips its direction on it); tapping any other
        /// word selects it.</summary>
        public static bool IsReselect(int selected, int tapped) => selected == tapped;

        /// <summary>A FLIP start: the translate and the left-pivot scale a bar laid out at its NEW rect starts from so it
        /// is seen leaving the OLD one, both decaying to identity. <c>Animate</c> false = snap (nothing to slide).</summary>
        public readonly record struct Slide(bool Animate, float Dx, float Scale);

        /// <summary>The <see cref="Slide"/> from the laid-out rect (<paramref name="fromX"/>, <paramref name="fromW"/>) to
        /// (<paramref name="toX"/>, <paramref name="toW"/>). Nothing slides from or to a rect that has not been laid out
        /// (a collapsed word, the first frame) or between identical rects. The ledger bar's segments reuse it.</summary>
        public static Slide SlideFrom(float fromX, float fromW, float toX, float toW)
        {
            if (!(fromW > MinSlideWidth) || !(toW > MinSlideWidth) || !float.IsFinite(fromX) || !float.IsFinite(toX)) return default;
            if (fromX == toX && fromW == toW) return default;
            return new Slide(true, fromX - toX, fromW / toW);
        }

        // ══ THE RAIL ═════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>A rail bound to one int signal: the active word 100 % ink at the heavy weight over a 2-DIP
        /// <paramref name="tone"/> underline (the system accent when null), the rest 50 % (85 % on hover), and a count
        /// badge after any word that has one.
        /// <para>Tapping another word writes its code into <paramref name="selected"/> and then calls
        /// <paramref name="onSelect"/>; tapping the active word writes nothing and calls <paramref name="onReselect"/>.
        /// Every word is its own tab stop with the engine's focus rect, Enter/Space activate it, and each word carries
        /// <paramref name="role"/> — <c>Button</c> for a filter/sort rail, <c>Tab</c> for the episode tabs. (The engine
        /// has no toolbar/tab-list role for the row itself; like the stock SelectorBar, only the words expose peers.)</para>
        /// <para><paramref name="big"/> is the episode page's Display-face rail (20/26, 300 → 400, gap 22, height 40).
        /// The words are FROZEN at mount (a different set is a remount); their labels, counts and presence are live.</para></summary>
        public static Element Rail(IReadOnlyList<Word> words, Signal<int> selected, Func<ColorF>? tone = null,
                                   bool big = false, AutomationRole role = AutomationRole.Button,
                                   Action<int>? onReselect = null, Action<int>? onSelect = null)
            => Embed.Comp(() => new RailHost(words, selected, tone ?? s_accent, big, role, onReselect, onSelect));

        sealed class RailHost : Component
        {
            readonly IReadOnlyList<Word> _words;
            readonly Signal<int> _selected;
            readonly Func<ColorF> _tone;
            readonly bool _big;
            readonly AutomationRole _role;
            readonly Action<int>? _onReselect, _onSelect;
            // Built ONCE: every state rides a bind, so the rail never re-renders on a selection change.
            readonly int[] _codes;
            // Each word's arranged row-local x / width (its bounds handler) and its underline node (its realize handler)
            // — plain fields, never signals: the slide reads them at the edge and nothing re-renders for them.
            readonly float[] _x, _w;
            readonly NodeHandle[] _bars;
            Element? _row;
            Action? _slideBar;
            int _shown = int.MinValue;   // the code whose underline was last on screen — the slide's "from"

            public RailHost(IReadOnlyList<Word> words, Signal<int> selected, Func<ColorF> tone, bool big,
                            AutomationRole role, Action<int>? onReselect, Action<int>? onSelect)
            {
                _words = words; _selected = selected; _tone = tone; _big = big; _role = role;
                _onReselect = onReselect; _onSelect = onSelect;
                _codes = new int[words.Count];
                _x = new float[words.Count];
                _w = new float[words.Count];
                _bars = new NodeHandle[words.Count];
            }

            public override Element Render()
            {
                _row ??= Build();
                // Auto-tracked LAYOUT effect: it reads `selected`, so a selection change re-runs it after that frame's
                // layout and BEFORE its paint — the new bar is seeded at the old bar's rect on the very frame it appears.
                UseLayoutEffect(_slideBar ??= SlideBar);
                return _row;
            }

            Element Build()
            {
                var kids = new Element[_words.Count];
                for (int i = 0; i < kids.Length; i++) kids[i] = BuildWord(i);
                return new BoxEl
                {
                    Direction = 0, Height = _big ? BigHeight : Height, Gap = _big ? BigGap : Gap,
                    AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids,
                };
            }

            Element BuildWord(int i)
            {
                Word w = _words[i];
                int code = CodeOf(w, i);
                _codes[i] = code;
                Signal<int> selected = _selected;
                Func<ColorF> tone = _tone;
                bool big = _big;
                Func<bool> isOn = () => selected.Value == code;
                Element ink = new BoxEl
                {
                    ZStack = true, Shrink = 0f,
                    Children =
                    [
                        Ink(w.Label, big ? BigLightWeight : LightWeight, Prop.Of(() => isOn() ? ColorF.Transparent : Tok.TextPrimary), big),
                        Ink(w.Label, big ? BigHeavyWeight : HeavyWeight, Prop.Of(() => isOn() ? Tok.TextPrimary : ColorF.Transparent), big),
                    ],
                };
                Element head = w.Count is null && w.Chevron is null ? ink : Head(ink, w.Count, w.Chevron);
                return new BoxEl
                {
                    Direction = 1, Gap = UnderlineGap, Shrink = 0f, Padding = new Edges4(0f, TopPad, 0f, 0f),
                    Role = _role, Focusable = true, Cursor = CursorId.Hand, OnClick = () => Tap(code),
                    Opacity = Prop.Of(() => isOn() ? 1f : RestInk), HoverOpacity = HoverInk, Transition = s_inkFade,
                    Visible = w.Visible ?? true,
                    OnBoundsChanged = r => { _x[i] = r.X; _w[i] = r.W; },
                    Children =
                    [
                        head,
                        new BoxEl
                        {
                            Height = UnderlineThickness, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(1f),
                            Fill = Prop.Of(() => isOn() ? tone() : ColorF.Transparent),
                            TransformOriginX = 0f,          // the slide's scale pivots on the bar's LEFT edge
                            OnRealized = h => _bars[i] = h,
                        },
                    ],
                };
            }

            /// <summary>The word plus its count badge and/or its chevron, on one centred row.</summary>
            static Element Head(Element ink, Prop<string>? count, Func<bool>? chevron)
            {
                var kids = new List<Element>(3) { ink };
                if (count is { } c)
                    kids.Add(new TextEl(c)
                    {
                        Size = CountSize, LineHeight = CountLine, Color = Tok.TextSecondary, MaxLines = 1,
                        Visible = Prop.Of(() => c.Current() is { Length: > 0 }),
                    });
                if (chevron is not null)
                    kids.Add(new BoxEl
                    {
                        Width = 10f, Height = 10f, Shrink = 0f, Visible = Prop.Of(chevron),
                        Children = [Icon(Icons.ChevronDown, 10f, Tok.TextSecondary)],
                    });
                return new BoxEl
                {
                    Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids.ToArray(),
                };
            }

            static TextEl Ink(Prop<string> label, ushort weight, Prop<ColorF> ink, bool big) => new(label)
            {
                Size = big ? BigSize : Size, LineHeight = big ? BigLine : Line, Weight = weight,
                CharSpacing = big ? BigTracking : Tracking, FontFamily = big ? DisplayFace : null, MaxLines = 1,
                Color = ink, BrushTransitionMs = Design.Motion.Faster,
                AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start,
            };

            void Tap(int code)
            {
                if (IsReselect(_selected.Peek(), code)) { _onReselect?.Invoke(code); return; }
                _selected.Value = code;
                _onSelect?.Invoke(code);
            }

            void SlideBar()
            {
                int code = _selected.Value;   // the subscription
                int from = _shown;
                _shown = code;
                if (from == code || from == int.MinValue || Context.Anim is not { } anim) return;
                int a = IndexOf(from), b = IndexOf(code);
                if (a < 0 || b < 0 || _bars[b].IsNull) return;
                var s = SlideFrom(_x[a], _w[a], _x[b], _w[b]);
                if (!s.Animate) return;
                anim.SeedValue(_bars[b], AnimChannel.TranslateX, 0f, in s_slide, from: s.Dx);
                anim.SeedValue(_bars[b], AnimChannel.ScaleX, 1f, in s_slide, from: s.Scale);
            }

            int IndexOf(int code)
            {
                for (int i = 0; i < _codes.Length; i++) if (_codes[i] == code) return i;
                return -1;
            }
        }

        // ══ THE WORD LINKS ═══════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Lowercase wrapping word links (a show's topic row): <c>TextSecondary</c> at rest, the
        /// <paramref name="tone"/> plus an underline under the pointer, each word a Hyperlink tab stop that runs its
        /// action. The hover is two stacked runs cross-faded by the engine's reveal (each run's box declares
        /// <c>HoverOpacity</c> and follows the word's hover), because a text's underline is a plain flag and its
        /// hover colour a static value — the tone is a live read.
        /// <para>The lowering is the current culture's (invariant under the app's <c>InvariantGlobalization</c>) and is
        /// applied to catalogue text (Spotify's topic titles), never to a loc string.</para></summary>
        public static Element Links(IReadOnlyList<(string Text, Action Go)> words, float width, Func<ColorF> tone)
        {
            Prop<ColorF> hot = Prop.Of(tone);
            var kids = new Element[words.Count];
            for (int i = 0; i < kids.Length; i++)
            {
                var (text, go) = words[i];
                string word = text.ToLower(CultureInfo.CurrentCulture);
                kids[i] = new BoxEl
                {
                    ZStack = true, Shrink = 0f, Corners = CornerRadius4.All(3f),
                    // The wrap's Gap is BOTH axes (the engine's flex wrap), so the 4 is the row gap and the margin tops the
                    // column gap up to 12.
                    Margin = new Edges4(0f, 0f, LinkGapX - LinkGapY, 0f),
                    Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = go,
                    Children =
                    [
                        LinkLayer(LinkText(word, Tok.TextSecondary, underline: false), rest: 1f, hover: 0f),
                        LinkLayer(LinkText(word, hot, underline: true), rest: 0f, hover: 1f),
                    ],
                };
            }
            return new BoxEl
            {
                Direction = 0, Wrap = true, Width = width, Gap = LinkGapY, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children = kids,
            };
        }

        static BoxEl LinkLayer(TextEl text, float rest, float hover) => new()
        {
            HitTestVisible = false, Opacity = rest, HoverOpacity = hover, HoverDurationMs = Design.Motion.Faster,
            Children = [text],
        };

        static TextEl LinkText(string word, Prop<ColorF> ink, bool underline) => new(word)
        {
            Size = LinkSize, LineHeight = LinkLine, Color = ink, Underline = underline, MaxLines = 1,
        };
    }
}
