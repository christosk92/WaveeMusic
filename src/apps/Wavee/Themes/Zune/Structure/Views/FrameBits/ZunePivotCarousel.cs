// ── Themes/Zune/Structure/Views/FrameBits/ZunePivotCarousel.cs — the Zune pivot carousel: the panel's head, the band's Compact row 1 (owner, 2026-10-24) ──
//
// The owner, on the right panel: the pivots never wrap, the edge fade is gentle, and switching tabs moves like a Zune / Windows Phone pivot. The
// band's Compact row 1 (the lit destination leads, Z-Part-Band compact) is the same pivot and moves the same way (audit Z-OWNER, B10c): ONE
// mechanism over a key type (the panel's modes, the band's destination ids).
//
//   ORDER    the selected word leads on the strip's gutter; the others follow in the cyclic order (the panel: queue · lyrics · now playing ·
//            friends, then video while the video body is up; the band: home · browse · library · recents), so the words before it wrap round to
//            the end of the line. One line, every word at its size: the ones that do not fit run off under the strip's right edge fade.
//   SLIDE    a change slides the strip by S so the new lead reaches the gutter, on the Zune pivot slide (`ZuneMotion.PivotSlideMs`, SmoothOut).
//            Every word keeps its node: after the commit's layout each word's TranslateX is seeded from where it was drawn (a FLIP from the place
//            the previous layout gave it, so a crossing that changed the gap or the size between the two is no step). A word that wraps round
//            leaves on the side of travel (it slides on with the strip, fading, until it is past the strip's edge) and arrives from beyond the
//            other edge; the leg nobody can see is skipped, and where both legs show (a strip that shows the whole cycle) it leaves over the first
//            half and arrives over the second. It never moves where it can be seen.
//   TRAVEL   a click on a trailing word goes forward (Windows Phone: the tapped header slides to the front); Left / Right on a word step back /
//            forward one (the focus follows the selection to the front); a change from outside the strip (the bar's queue and lyrics doors, a
//            navigation) takes the shorter way round.
//   REDUCED  nothing travels: the words that moved fade in where they land (a value, never a skipped hook).
//
// The owner keeps one carousel across renders and drives it from a layout effect: `Order` (and `Show`) on every render, `Slide` after the commit's
// layout, `TakeRefocus` after that. A strip that is not a carousel this render (the band above Compact, where its words keep their layout slide)
// still calls `Slide` with `animate: false`, so the places the next change slides from are the ones on screen.

using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Scene;

namespace Wavee.Themes.Zune;

/// <summary>A row of pivot words and the carousel that moves them, kept by its owner across renders (<see cref="ZunePanel"/>'s head, the band's
/// Compact row 1).</summary>
internal sealed class ZunePivotCarousel<TKey> where TKey : notnull
{
    readonly IEqualityComparer<TKey> _cmp;
    readonly Dictionary<TKey, NodeHandle> _words;
    readonly Dictionary<TKey, Action<NodeHandle>> _realized;
    readonly Dictionary<TKey, Action> _clicks;
    readonly Dictionary<TKey, float> _widths;
    Dictionary<TKey, float> _laidX, _nowX;
    TKey[] _laid = [];
    float _laidCycle;
    bool _laidOpen, _refocus;
    int _intent;

    /// <summary>The words in the order the strip draws them (the owner sets it on every render).</summary>
    public TKey[] Order = [];

    /// <summary>The selection verb (null where the strip offers no choice: a linked pane, whose head is its one word).</summary>
    public Action<TKey>? Show;

    /// <summary>The strip that clips the words (its width is what shows).</summary>
    public NodeHandle Strip;

    /// <summary>The gap between two words (the cycle's step past the last one too) and the lead's inset on the strip (the strip shows from it).</summary>
    public float Gap, Gutter;

    /// <summary>Left / Right on a word (on the strip, bubbled from the focused word; or chained from a word's own key handler).</summary>
    public readonly Action<KeyEventArgs> OnKey;

    /// <summary>The strip was realized.</summary>
    public readonly Action<NodeHandle> OnStrip;

    public ZunePivotCarousel(IEqualityComparer<TKey>? comparer = null)
    {
        _cmp = comparer ?? EqualityComparer<TKey>.Default;
        _words = new(_cmp);
        _realized = new(_cmp);
        _clicks = new(_cmp);
        _widths = new(_cmp);
        _laidX = new(_cmp);
        _nowX = new(_cmp);
        OnKey = Key;
        OnStrip = h => Strip = h;
    }

    /// <summary>The realize callback of <paramref name="key"/>'s word (one per key, kept).</summary>
    public Action<NodeHandle> RealizedOf(TKey key)
    {
        if (!_realized.TryGetValue(key, out var realized)) _realized[key] = realized = h => _words[key] = h;
        return realized;
    }

    /// <summary>The click of a trailing word: it comes to the front, forward.</summary>
    public Action ClickOf(TKey key)
    {
        if (!_clicks.TryGetValue(key, out var click)) _clicks[key] = click = () => Go(key, 1);
        return click;
    }

    /// <summary>Is <paramref name="node"/> one of the strip's words?</summary>
    public bool IsWord(NodeHandle node)
    {
        foreach (var (_, word) in _words) if (word == node) return true;
        return false;
    }

    /// <summary>The word of <paramref name="key"/>, or null while it is not realized.</summary>
    public NodeHandle WordOf(TKey key) => _words.TryGetValue(key, out var node) ? node : NodeHandle.Null;

    void Go(TKey key, int travel)
    {
        if (Show is not { } show) return;
        _intent = travel;
        show(key);
    }

    void Key(KeyEventArgs e)
    {
        if (e.Handled || e.Mods != KeyModifiers.None || Show is null || Order.Length < 2) return;
        if (e.KeyCode is not (Keys.Right or Keys.Left)) return;
        e.Handled = true;
        _refocus = true;
        if (e.KeyCode == Keys.Right) Go(Order[1], 1);
        else Go(Order[^1], -1);
    }

    /// <summary>Was the last change made from the keyboard (the focus follows the selection)? Clears the flag.</summary>
    public bool TakeRefocus()
    {
        bool refocus = _refocus;
        _refocus = false;
        return refocus;
    }

    float Width(TKey key) => _widths.TryGetValue(key, out float w) ? w : 0f;

    /// <summary>Seeds the words' slide from the order last laid out to <see cref="Order"/>, after the commit's layout (a layout effect), and records
    /// where every word now stands. Returns the direction of travel: 1 when the strip moved left (the new lead came in from the right), -1 the other
    /// way, 0 when nothing travelled (the first layout, <paramref name="animate"/> false now or at the last layout, the same order).</summary>
    public int Slide(SceneStore scene, AnimEngine anim, bool animate, bool reduced)
    {
        var order = Order;
        var prev = _laid;
        bool wasOpen = _laidOpen;
        float prevCycle = _laidCycle;
        _laid = order;
        _laidOpen = animate;
        int intent = _intent;
        _intent = 0;

        // Where every word stands now (from the gutter); the map of the last layout is what the slide starts from.
        var now = _nowX;
        now.Clear();
        float cycle = 0f;
        foreach (var key in order)
        {
            if (!_words.TryGetValue(key, out var word) || !scene.IsLive(word)) continue;
            var b = scene.Bounds(word);
            _widths[key] = b.W;
            now[key] = b.X - Gutter;
            cycle = MathF.Max(cycle, b.X - Gutter + b.W + Gap);
        }
        var before = _laidX;
        _laidX = now;
        _nowX = before;
        _laidCycle = cycle;
        if (!animate || !wasOpen || prev.Length == 0 || order.Length == 0 || Same(prev, order)) return 0;

        float gutter = Gutter, ms = ZuneMotion.PivotSlideMs;
        // The strip's travel: forward it moves left by the new lead's old place, back it moves right by what followed the new lead.
        float shift = 0f;
        if (!_cmp.Equals(prev[0], order[0]) && before.TryGetValue(order[0], out float leadX))
        {
            float forward = leadX, back = leadX - prevCycle;
            shift = intent > 0 ? forward : intent < 0 ? back : forward <= -back ? forward : back;
        }
        float shows = scene.IsLive(Strip) ? scene.Bounds(Strip).W - gutter : float.PositiveInfinity;

        foreach (var key in order)
        {
            if (!_words.TryGetValue(key, out var node) || !scene.IsLive(node) || !now.TryGetValue(key, out float newX)) continue;
            float w = Width(key);
            ref var paint = ref scene.Paint(node);
            float drawnDx = paint.LocalTransform.Dx, alpha = paint.Opacity;
            if (!before.TryGetValue(key, out float oldX))
            {
                // A word the strip did not show (a pane docked away, the video body arriving): it fades in where it lands.
                anim.Animate(node, AnimChannel.Opacity, 0f, 1f, ms, Easing.SmoothOut);
                continue;
            }
            float from = oldX + drawnDx - newX;   // where it is drawn now, from its new place
            if (MathF.Abs(from) < ZuneFrameRules.PanelPivotStill) { Settle(anim, node, drawnDx, alpha, ms); continue; }
            if (reduced)
            {
                // Nothing travels: it lands, and fades in there.
                anim.Animate(node, AnimChannel.TranslateX, 0f, 0f, 1f, Easing.Linear);
                anim.Animate(node, AnimChannel.Opacity, 0f, 1f, ms, Easing.SmoothOut);
                continue;
            }
            bool wraps = shift != 0f && MathF.Abs(oldX - shift - newX) > ZuneFrameRules.PanelPivotStill;
            if (!wraps)
            {
                anim.Animate(node, AnimChannel.TranslateX, from, 0f, ms, Easing.SmoothOut);
                if (alpha < 1f) anim.Animate(node, AnimChannel.Opacity, alpha, 1f, ms, Easing.SmoothOut);
                continue;
            }
            float drawnX = oldX + drawnDx;
            bool leaves = drawnX < shows && drawnX + w > -gutter;   // the leg that slides on from where it is drawn shows
            bool arrives = newX < shows;                            // the leg that slides into its new place shows
            Wrap(anim, node, new WrapLegs(from, drawnX, newX, w, shift, shows, gutter, leaves, arrives), ms);
        }
        return shift > 0f ? 1 : shift < 0f ? -1 : 0;
    }

    /// <summary>A word whose place did not change: any slide or fade it was in finishes from where it is drawn.</summary>
    static void Settle(AnimEngine anim, NodeHandle node, float drawnDx, float alpha, float ms)
    {
        if (drawnDx != 0f) anim.Animate(node, AnimChannel.TranslateX, drawnDx, 0f, ms, Easing.SmoothOut);
        if (alpha < 1f) anim.Animate(node, AnimChannel.Opacity, alpha, 1f, ms, Easing.SmoothOut);
    }

    /// <summary>Where a word that wraps round the cycle is: <see cref="From"/> its drawn offset from its new place, <see cref="DrawnX"/> and <see cref="NewX"/>
    /// on the strip (from the gutter), its width, the strip's travel, how much of the strip shows (from the gutter) and the gutter, and which of its
    /// two legs show.</summary>
    readonly record struct WrapLegs(float From, float DrawnX, float NewX, float W, float Shift, float Shows, float Gutter, bool Leaves, bool Arrives)
    {
        /// <summary>The leaving leg's end offset: on with the strip by <see cref="Shift"/>, and at least far enough to be wholly past the strip's edge (left
        /// going forward, right going back), so the word is out of sight before it takes its new place.</summary>
        public float LeftAt => Shift > 0f
            ? From - MathF.Max(Shift, DrawnX + W + Gutter + ZuneFrameRules.PanelPivotStill)
            : From + MathF.Max(-Shift, Shows - DrawnX + ZuneFrameRules.PanelPivotStill);

        /// <summary>The arriving leg's start offset: where the strip's travel puts it, and at least wholly past the strip's other edge, so it comes into
        /// sight sliding.</summary>
        public float ComesFrom => Shift > 0f
            ? MathF.Max(Shift, Shows - NewX + ZuneFrameRules.PanelPivotStill)
            : MathF.Min(Shift, -Gutter - W - NewX - ZuneFrameRules.PanelPivotStill);
    }

    /// <summary>A word that wraps round the cycle: it leaves with the strip, fading, until it is out of sight, and arrives from the other side, fading in.
    /// Only the legs that show are played; between the two it is out of sight, so it never moves where it can be seen.</summary>
    static void Wrap(AnimEngine anim, NodeHandle node, in WrapLegs legs, float ms)
    {
        if (!legs.Leaves)
        {
            anim.Animate(node, AnimChannel.TranslateX, legs.ComesFrom, 0f, ms, Easing.SmoothOut);
            anim.Animate(node, AnimChannel.Opacity, 0f, 1f, ms, Easing.SmoothOut);
            return;
        }
        if (!legs.Arrives)
        {
            // It slides off with the strip, fading, and takes its new place (out of sight) as the slide ends.
            anim.Keyframes(node, AnimChannel.TranslateX, [new(0f, legs.From), new(1f, legs.LeftAt, Easing.SmoothOut), new(1f, 0f)], ms);
            anim.Keyframes(node, AnimChannel.Opacity, [new(0f, 1f), new(1f, 0f, Easing.SmoothOut), new(1f, 1f)], ms);
            return;
        }
        // Both legs show (a strip that shows the whole cycle): out over the first part, in from the other side over the rest.
        float split = ZuneFrameRules.PanelPivotWrapSplit;
        anim.Keyframes(node, AnimChannel.TranslateX,
            [new(0f, legs.From), new(split, legs.LeftAt, Easing.SmoothOut), new(split, legs.ComesFrom), new(1f, 0f, Easing.SmoothOut)], ms);
        anim.Keyframes(node, AnimChannel.Opacity, [new(0f, 1f), new(split, 0f, Easing.SmoothOut), new(1f, 1f, Easing.SmoothOut)], ms);
    }

    bool Same(TKey[] a, TKey[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!_cmp.Equals(a[i], b[i])) return false;
        return true;
    }
}
