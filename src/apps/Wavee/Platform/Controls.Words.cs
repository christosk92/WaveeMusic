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
// its sort and (Big) its episode tabs, so the rail lives here and Classic's `ClassicLibraryRails.WordRail/ScopeRail/ReaderSortRail` are thin
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
// ── FIT: THE WORDS THAT FIT, THE REST BEHIND "more" (the library's sort rail) ─────────────────────────────────────────
//
// A `fill` rail scrolls and feathers its overflow, which on a navigator too narrow for its words read as a clipped fourth word ("rece…") at EVERY size.
// A `fit` rail never clips a word: it shows the LEADING words that fit its slot whole and folds the rest into a trailing "more ▾" word that opens a radio
// menu of them. When the selected word is one of the folded ones, that word takes the trailing slot instead (its label, the heavy run, the underline), so
// the selection is always on screen. The words' natural widths are measured while they are shown and kept while they are folded (they never shrink: a
// word is `Shrink 0`), so the fit cannot oscillate; before a word was ever laid out its width is estimated from its label. Pinned by `FitCount`.
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
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Roles;
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
        /// <summary>An inactive word's ink, and any word's ink under the pointer (W8: 50 % / 85 % / 100 %). On the light ground half the ink read
        /// 3.3 : 1, so the light rest is <see cref="RestInkLight"/> (Zune measured it first: about 5 : 1 on the light page; audit B8).</summary>
        public const float RestInk = 0.5f, HoverInk = 0.85f, RestInkLight = 0.68f;

        /// <summary>An inactive word's ink on the active kind's ground.</summary>
        public static float RestInkNow => R.IsLight ? RestInkLight : RestInk;
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

        /// <summary>A <c>fill</c> rail's edge-fade band (DIP), on both the viewport's <c>AutoEdgeFadeBand</c> and
        /// <see cref="RevealOffset"/>'s margin — one number, so the word a reveal parks past the band is exactly the word
        /// the fade would otherwise dim.</summary>
        public const float FadeBand = 24f;

        /// <summary>How a reveal moves the rail: <c>Snap</c> for the mount frame, an unchanged selection (a geometry-only
        /// re-run) or reduced motion; <c>Glide</c> for a real selection change.</summary>
        public enum Reveal : byte { Snap, Glide }

        /// <summary>The reveal STYLE for a selection landing on <paramref name="selected"/> that was previously
        /// <paramref name="shown"/> (<see cref="int.MinValue"/> = the first frame, nothing shown yet).</summary>
        public static Reveal RevealFor(int shown, int selected, bool reducedMotion)
            => shown == int.MinValue || shown == selected || reducedMotion ? Reveal.Snap : Reveal.Glide;

        /// <summary>A coarse, DIP-grid key over the two extents a <c>fill</c> rail's reveal must react to — a resize or a
        /// late word answering. Deliberately NEVER folds in the OFFSET: the user's own scroll must not re-trigger a
        /// reveal of the already-selected word.</summary>
        public static long GeometryKey(float viewportW, float contentW)
            => ((long)MathF.Round(viewportW / 2f) << 32) | (uint)MathF.Round(contentW / 2f);

        /// <summary>The offset that brings the word at <c>[x, x+w]</c> fully inside the viewport and clear of the
        /// <paramref name="band"/>-wide edge fade on whichever side it is nearer — degrading to plain centring when the
        /// word is too wide for both bands to fit, and pinning the LEADING edge when it is wider than the viewport
        /// itself. Returns <see cref="float.NaN"/> when nothing needs to move (already clear, not laid out, or the
        /// viewport has no geometry yet).</summary>
        public static float RevealOffset(float x, float w, float viewport, float content, float offset, float band)
        {
            if (!(viewport > 1f) || !(w > MinSlideWidth) || !float.IsFinite(x)) return float.NaN;
            float m = MathF.Min(band, MathF.Max(0f, (viewport - w) * 0.5f));
            float lo = x + w + m - viewport;
            float hi = x - m;
            float t = lo > hi ? hi : offset < lo ? lo : offset > hi ? hi : offset;
            t = Math.Clamp(t, 0f, MathF.Max(0f, content - viewport));
            return MathF.Abs(t - offset) < 0.5f ? float.NaN : t;
        }

        /// <summary>A <c>fit</c> rail's estimate of a word's width (DIP per character of its label) before it was ever laid out, and its trailing word's
        /// chevron (10) and the gap before it.</summary>
        public const float FitCharW = 7f, FitChevron = 10f, FitChevronGap = 4f;

        /// <summary>How many LEADING words of <paramref name="widths"/> (each word's natural width) a <c>fit</c> rail shows in <paramref name="available"/> DIP with
        /// <paramref name="gap"/> between them: all of them when they fit whole; otherwise as many as leave room for the trailing word
        /// (<paramref name="trailing"/> wide, a gap before it) — possibly none. Nothing laid out yet (<paramref name="available"/> not positive): all.</summary>
        public static int FitCount(ReadOnlySpan<float> widths, float gap, float available, float trailing)
        {
            if (!(available > 0f) || widths.IsEmpty) return widths.Length;
            float total = 0f;
            for (int i = 0; i < widths.Length; i++) total += widths[i] + (i > 0 ? gap : 0f);
            if (total <= available + 0.5f) return widths.Length;
            float used = 0f;
            int k = 0;
            for (; k < widths.Length; k++)
            {
                float next = used + (k > 0 ? gap : 0f) + widths[k];
                if (next + gap + trailing > available + 0.5f) break;
                used = next;
            }
            return k;
        }

        /// <summary>A rail's measure, reported whenever it moves by more than half a DIP: <paramref name="total"/> is the
        /// row's own arranged width (the words, their counts and the gaps between them — what a toolbar must reserve for a
        /// rail it never lets shrink) and <paramref name="words"/> each word's arranged width, in word order (a hidden
        /// word reads 0). The array is the rail's own: read it inside the callback, never keep it.</summary>
        public delegate void Measured(float total, float[] words);

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
        /// The words are FROZEN at mount (a different set is a remount); their labels, counts and presence are live.</para>
        /// <para><paramref name="fill"/> is a horizontal-scroll viewport instead of the natural-width row: a narrow pane
        /// clips nothing and feathers instead, and the selected word is always brought fully into view (on selection, on
        /// resize, on the first frame it answers, and on keyboard focus). <c>false</c> (the default) is exactly today's
        /// element.</para>
        /// <para><paramref name="onMeasured"/> exposes the rail's natural width and each word's width (<see cref="Measured"/>)
        /// to a host that decides, from numbers, how much room the rail needs — the show reader's toolbar. Mount-time, like
        /// every other callback here.</para>
        /// <para><paramref name="fit"/> (instead of <paramref name="fill"/>): the leading words that fit whole, the rest folded behind a trailing "more ▾" word
        /// with a radio menu (see the file header), labelled <paramref name="more"/>.</para>
        /// <para><paramref name="restInk"/> is an inactive word's opacity, read live (null: <see cref="RestInkNow"/>, the kind's): a theme whose ground needs a
        /// heavier rest for the words' contrast states its own.</para>
        /// <para><paramref name="countRest"/> is a count badge's ink while its word rests, read live (null: the primary text ink: the badge rests with its
        /// word at the rest opacity, where the secondary ink read 2.8-3.5 : 1 at 11 DIP, audit B8; its size tells it apart). A selected word's badge is the
        /// secondary ink at full opacity.</para></summary>
        public static Element Rail(IReadOnlyList<Word> words, Signal<int> selected, Func<ColorF>? tone = null,
                                   bool big = false, AutomationRole role = AutomationRole.Button,
                                   Action<int>? onReselect = null, Action<int>? onSelect = null, bool fill = false,
                                   Measured? onMeasured = null, bool fit = false, Prop<string>? more = null, Func<float>? restInk = null,
                                   Func<ColorF>? countRest = null)
            => Embed.Comp(() => new RailHost(words, selected, tone ?? s_accent, big, role, onReselect, onSelect, fill && !fit, onMeasured,
                                             fit, more ?? Loc.Bind(Strings.Library.Rail.More), restInk ?? s_restInk, countRest ?? s_countInk));

        static readonly Func<ColorF> s_countInk = static () => Tok.TextPrimary;

        static readonly Func<float> s_restInk = static () => RestInkNow;

        sealed class RailHost : Component
        {
            readonly IReadOnlyList<Word> _words;
            readonly Signal<int> _selected;
            readonly Func<ColorF> _tone;
            readonly bool _big;
            readonly bool _fill;
            readonly AutomationRole _role;
            readonly Action<int>? _onReselect, _onSelect;
            // Built ONCE: every state rides a bind, so the rail never re-renders on a selection change.
            readonly int[] _codes;
            // Each word's arranged row-local x / width (its bounds handler) and its underline node (its realize handler)
            // — plain fields, never signals: the slide reads them at the edge and nothing re-renders for them.
            readonly float[] _x, _w;
            readonly NodeHandle[] _bars;
            // `onMeasured` only: the row's arranged width, and what the callback was last told (so a re-arrange that moved
            // nothing reports nothing).
            readonly Measured? _onMeasured;
            readonly float[]? _sentWords;
            float _rowW, _sentTotal = -1f;
            // `fill` only: the viewport's scroll handle, and a coarse geometry bump (GeometryKey over its viewport/extent
            // signals) so `AfterLayout` re-runs the reveal on a resize or a late-answering word — never on the user's own
            // scroll (GeometryKey folds in no offset).
            readonly ScrollHandle? _scroll;
            readonly Signal<int> _geom = new(0);
            long _geomKey = long.MinValue;
            Element? _row, _mount;
            Action? _afterLayout;
            int _shown = int.MinValue;   // the code whose underline was last on screen — the slide's "from"

            // `fit` only: each word's natural width (kept while it is folded), the slot's width, how many leading words show, the trailing word's anchor and menu.
            readonly bool _fit;
            readonly Prop<string> _more;
            readonly float[]? _nat;
            readonly Signal<int>? _fitShown;
            float _avail;
            NodeHandle _moreAnchor;
            OverlayHandle? _menu;
            IOverlayService? _overlay;
            readonly Func<float> _rest;
            readonly Func<ColorF> _countRest;

            public RailHost(IReadOnlyList<Word> words, Signal<int> selected, Func<ColorF> tone, bool big,
                            AutomationRole role, Action<int>? onReselect, Action<int>? onSelect, bool fill, Measured? onMeasured,
                            bool fit = false, Prop<string>? more = null, Func<float>? restInk = null, Func<ColorF>? countRest = null)
            {
                _rest = restInk ?? s_restInk;
                _countRest = countRest ?? s_countInk;
                _fit = fit;
                _more = more ?? (Prop<string>)"";
                if (fit) { _nat = new float[words.Count]; _fitShown = new Signal<int>(words.Count); }
                _words = words; _selected = selected; _tone = tone; _big = big; _role = role;
                _onReselect = onReselect; _onSelect = onSelect;
                _fill = fill;
                _onMeasured = onMeasured;
                _codes = new int[words.Count];
                _x = new float[words.Count];
                _w = new float[words.Count];
                _bars = new NodeHandle[words.Count];
                if (onMeasured is not null) _sentWords = new float[words.Count];
                if (_fill) _scroll = new ScrollHandle();
            }

            /// <summary>Tell the host what the rail measures — once per real change (the row's width or any word's moved by
            /// more than half a DIP). The row and every word each report their own bounds, in whatever order the arrange
            /// visits them, so this runs from all of them and only the last one to move anything is heard.</summary>
            void ReportMeasure()
            {
                if (_onMeasured is not { } cb || _sentWords is not { } sent || !(_rowW > 0f)) return;
                bool moved = MathF.Abs(_rowW - _sentTotal) > 0.5f;
                for (int i = 0; !moved && i < _w.Length; i++) moved = MathF.Abs(_w[i] - sent[i]) > 0.5f;
                if (!moved) return;
                _sentTotal = _rowW;
                Array.Copy(_w, sent, _w.Length);
                cb(_rowW, _w);
            }

            public override Element Render()
            {
                if (_fit) _overlay = UseContext(Overlay.Service);
                _row ??= Build();
                _mount ??= _fill ? Scroller(_row.Value) : _fit ? FitSlot(_row.Value) : _row;
                if (_scroll is { } scroll)
                    UseSignalEffect(() =>
                    {
                        long key = GeometryKey((float)scroll.ViewportSignal.Value, (float)scroll.ExtentSignal.Value);
                        if (key == _geomKey) return;
                        _geomKey = key;
                        _geom.Value = _geom.Peek() + 1;
                    });
                // Auto-tracked LAYOUT effect: it reads `selected` (and, `fill`, the geometry bump), so a selection change
                // or a resize re-runs it after that frame's layout and BEFORE its paint — the new bar is seeded at the
                // old bar's rect on the very frame it appears, and a scrolled-out selection is already back in view.
                UseLayoutEffect(_afterLayout ??= AfterLayout);
                return _mount.Value;
            }

            Element Build()
            {
                var kids = new Element[_words.Count + (_fit ? 1 : 0)];
                for (int i = 0; i < _words.Count; i++) kids[i] = BuildWord(i);
                if (_fit) kids[^1] = MoreWord();
                return new BoxEl
                {
                    Direction = 0, Height = _big ? BigHeight : Height, Gap = _big ? BigGap : Gap,
                    AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids,
                    OnBoundsChanged = _onMeasured is null ? null : r => { _rowW = r.W; ReportMeasure(); },
                };
            }

            /// <summary>The row's horizontal-scroll viewport (`fill`): parent-sized, NEVER <c>ContentSized</c> — this
            /// rail rides binds and never re-renders, so a hugged width would go stale the moment a late count or the
            /// desc chevron changed the row's natural extent underneath it.</summary>
            Element Scroller(Element row) => new ScrollEl
            {
                Horizontal = true, Content = row,
                Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Height = _big ? BigHeight : Height,
                SuppressScrollBar = true, AutoEdgeFade = true, AutoEdgeFadeBand = FadeBand,
                Handle = _scroll,
            };

            Element BuildWord(int i)
            {
                Word w = _words[i];
                int code = CodeOf(w, i);
                _codes[i] = code;
                Signal<int> selected = _selected;
                Func<ColorF> tone = _tone;
                Func<float> rest = _rest;
                bool big = _big;
                Func<bool> isOn = () => selected.Value == code;
                Prop<bool> visible = w.Visible ?? true;
                if (_fitShown is { } shown)
                {
                    var own = w.Visible;
                    int index = i;
                    visible = Prop.Of(() => index < shown.Value && (own is null || own.Value.Current()));
                }
                Element ink = new BoxEl
                {
                    ZStack = true, Shrink = 0f,
                    Children =
                    [
                        Ink(w.Label, big ? BigLightWeight : LightWeight, Prop.Of(() => isOn() ? ColorF.Transparent : Tok.TextPrimary), big),
                        Ink(w.Label, big ? BigHeavyWeight : HeavyWeight, Prop.Of(() => isOn() ? Tok.TextPrimary : ColorF.Transparent), big),
                    ],
                };
                Element head = w.Count is null && w.Chevron is null ? ink : Head(ink, w.Count, w.Chevron, isOn, _countRest);
                return new BoxEl
                {
                    Direction = 1, Gap = UnderlineGap, Shrink = 0f, Padding = new Edges4(0f, TopPad, 0f, 0f),
                    Role = _role, Focusable = true, Cursor = CursorId.Hand, OnClick = () => Tap(code),
                    Opacity = Prop.Of(() => isOn() ? 1f : rest()), HoverOpacity = HoverInk, Transition = s_inkFade,
                    Visible = visible,
                    OnBoundsChanged = r =>
                    {
                        _x[i] = r.X; _w[i] = r.W;
                        if (_onMeasured is not null) ReportMeasure();
                        if (_nat is { } nat && r.W > MinSlideWidth && MathF.Abs(nat[i] - r.W) > 0.5f) { nat[i] = r.W; Refit(); }
                    },
                    // Keyboard tabbing onto a word scrolled out of a `fill` rail's viewport brings it back — a no-op
                    // (RevealWord's own `!_fill` guard) on every other rail.
                    OnFocusChanged = on => { if (on) RevealWord(i, Design.Reduced ? Reveal.Snap : Reveal.Glide); },
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

            /// <summary>The word plus its count badge and/or its chevron, on one centred row. The badge is the secondary ink while its word is
            /// selected and <paramref name="countRest"/> while it rests (it rests with the word at the rest opacity).</summary>
            static Element Head(Element ink, Prop<string>? count, Func<bool>? chevron, Func<bool> isOn, Func<ColorF> countRest)
            {
                var kids = new List<Element>(3) { ink };
                if (count is { } c)
                    kids.Add(new TextEl(c)
                    {
                        Size = CountSize, LineHeight = CountLine, Color = Prop.Of(() => isOn() ? Tok.TextSecondary : countRest()), MaxLines = 1,
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
                // The style's casing for a word it navigates by (Zune lowers it: "all · unplayed · in progress"; Classic and WinUI keep it as written).
                Case = R.Case.For(LabelKind.Navigation),
            };

            // ── fit ──

            /// <summary>The fit rail's slot: the space the toolbar gives it (it grows and shrinks, never past its words), clipping nothing but holding the row.</summary>
            Element FitSlot(Element row) => new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Height = _big ? BigHeight : Height, ClipToBounds = true,
                OnBoundsChanged = r => { if (MathF.Abs(r.W - _avail) > 0.5f) { _avail = r.W; Refit(); } },
                Children = [row],
            };

            /// <summary>Re-decide how many leading words show (<see cref="FitCount"/>), from their kept natural widths (or the label estimate) and the slot.</summary>
            void Refit()
            {
                if (_nat is not { } nat || _fitShown is not { } shown) return;
                Span<float> widths = stackalloc float[nat.Length];
                for (int i = 0; i < nat.Length; i++)
                {
                    var w = _words[i];
                    bool present = w.Visible is not { } v || v.Current();
                    widths[i] = !present ? 0f : nat[i] > 0f ? nat[i] : w.Label.Current().Length * FitCharW;
                }
                float gap = _big ? BigGap : Gap;
                float moreW = _more.Current().Length * FitCharW + FitChevronGap + FitChevron;
                int k = FitCount(widths, gap, _avail, moreW);
                // The selected word folded: it takes the trailing slot, so the slot must hold IT.
                int active = IndexOf(_selected.Peek());
                if (k < widths.Length && active >= k)
                    k = Math.Min(k, FitCount(widths, gap, _avail, widths[active] + FitChevronGap + FitChevron));
                shown.SetIfChanged(k);
            }

            /// <summary>The trailing word: "more ▾", or the selected word when it is one of the folded ones (its label at the heavy weight, its underline). It opens
            /// a radio menu of the folded words; picking one is a tap on it (the active one re-selects: the library flips its direction).</summary>
            Element MoreWord()
            {
                var shown = _fitShown!;
                var selected = _selected;
                Func<ColorF> tone = _tone;
                Func<float> rest = _rest;
                bool big = _big;
                Func<int> foldedActive = () =>
                {
                    int a = IndexOf(selected.Value);
                    return a >= shown.Value ? a : -1;
                };
                var label = Prop.Of(() => foldedActive() is int a and >= 0 ? _words[a].Label.Current() : _more.Current());
                return new BoxEl
                {
                    Direction = 1, Gap = UnderlineGap, Shrink = 0f, Padding = new Edges4(0f, TopPad, 0f, 0f),
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = OpenMore, OnRealized = h => _moreAnchor = h,
                    Opacity = Prop.Of(() => foldedActive() >= 0 ? 1f : rest()), HoverOpacity = HoverInk, Transition = s_inkFade,
                    Visible = Prop.Of(() => shown.Value < _words.Count),
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, Gap = FitChevronGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                            Children =
                            [
                                new BoxEl
                                {
                                    ZStack = true, Shrink = 0f,
                                    Children =
                                    [
                                        Ink(label, big ? BigLightWeight : LightWeight, Prop.Of(() => foldedActive() >= 0 ? ColorF.Transparent : Tok.TextPrimary), big),
                                        Ink(label, big ? BigHeavyWeight : HeavyWeight, Prop.Of(() => foldedActive() >= 0 ? Tok.TextPrimary : ColorF.Transparent), big),
                                    ],
                                },
                                new BoxEl { Width = FitChevron, Height = FitChevron, Shrink = 0f, Children = [Icon(Icons.ChevronDown, FitChevron, Tok.TextSecondary)] },
                            ],
                        },
                        new BoxEl
                        {
                            Height = UnderlineThickness, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(1f),
                            Fill = Prop.Of(() => foldedActive() >= 0 ? tone() : ColorF.Transparent),
                        },
                    ],
                };
            }

            /// <summary>The folded words' radio menu under the trailing word (a second click closes it).</summary>
            void OpenMore()
            {
                var overlay = _overlay;
                if (overlay is null || Controls.IsNullOverlay(overlay) || _fitShown is not { } shown) return;
                if (_menu is { IsOpen: true } open) { open.Close(); return; }
                _menu = overlay.Open(
                    () => _moreAnchor,
                    () =>
                    {
                        var items = new List<MenuFlyoutItem>(_words.Count);
                        int selectedCode = _selected.Peek();
                        for (int i = shown.Peek(); i < _words.Count; i++)
                        {
                            var w = _words[i];
                            if (w.Visible is { } v && !v.Current()) continue;
                            int code = _codes[i];
                            items.Add(MenuFlyoutItem.RadioItem(w.Label.Current(), code == selectedCode, () => Tap(code)));
                        }
                        return MenuFlyout.Create(items, () => _menu?.Close(), size: OverlayFit.MenuSize);
                    },
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
                _menu.ClosedAction = () => _menu = null;
            }

            void Tap(int code)
            {
                if (IsReselect(_selected.Peek(), code)) { _onReselect?.Invoke(code); return; }
                _selected.Value = code;
                _onSelect?.Invoke(code);
            }

            void AfterLayout()
            {
                int code = _selected.Value;             // subscription 1
                if (_fill) _ = _geom.Value;              // subscription 2 — a resize or a late word re-runs this too
                if (_fit) Refit();                       // a selection that folded must take the trailing slot
                int from = _shown;
                _shown = code;

                RevealWord(IndexOf(code), RevealFor(from, code, Design.Reduced));

                if (from == code || from == int.MinValue || Context.Anim is not { } anim) return;
                int a = IndexOf(from), b = IndexOf(code);
                if (a < 0 || b < 0 || _bars[b].IsNull) return;
                var s = SlideFrom(_x[a], _w[a], _x[b], _w[b]);
                if (!s.Animate) return;
                anim.SeedValue(_bars[b], AnimChannel.TranslateX, 0f, in s_slide, from: s.Dx);
                anim.SeedValue(_bars[b], AnimChannel.ScaleX, 1f, in s_slide, from: s.Scale);
            }

            /// <summary>Bring word <paramref name="index"/> fully into a `fill` viewport, clear of its edge fade — a
            /// no-op on every other rail (no viewport) and while the viewport has not published its geometry yet.</summary>
            void RevealWord(int index, Reveal how)
            {
                if (_scroll is not { IsBound: true } scroll || (uint)index >= (uint)_x.Length) return;
                float t = RevealOffset(_x[index], _w[index], (float)scroll.Viewport, (float)scroll.Extent, (float)scroll.Offset.Peek(), FadeBand);
                if (!float.IsNaN(t)) scroll.ScrollTo(t, how == Reveal.Glide ? ScrollMove.Glide : ScrollMove.Immediate);
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
