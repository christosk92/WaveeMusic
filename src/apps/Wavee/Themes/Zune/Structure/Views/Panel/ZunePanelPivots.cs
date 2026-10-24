// ── Themes/Zune/Structure/Views/Panel/ZunePanelPivots.cs — the panel's pivot carousel and its pane slide (owner, 2026-10-24) ────────────
//
// The owner, on the right panel: the pivots never wrap, the edge fade is gentle, and switching tabs moves like a Zune / Windows Phone pivot.
//
//   ORDER    the selected word leads on the panel's gutter; the others follow in the panel's cyclic order (queue · lyrics · now playing ·
//            friends, then video while the video body is up), so the words before it wrap round to the end of the line. One line, every
//            word at its size: the ones that do not fit run off under the strip's right edge fade (`ZunePanel.Head`).
//   SLIDE    a change slides the strip by S so the new lead reaches the gutter, on the Zune pivot slide (`ZuneMotion.PivotSlideMs`,
//            SmoothOut). Every word keeps its node: after the commit's layout each word's TranslateX is seeded from where it was drawn
//            (a FLIP). A word that wraps round leaves on the side of travel (it slides on with the strip, fading, until it is past the
//            strip's edge) and arrives from beyond the other edge; the leg nobody can see is skipped, and where both legs show (a panel
//            that shows the whole cycle) it leaves over the first half and arrives over the second. It never moves where it can be seen.
//   TRAVEL   a click on a trailing word goes forward (Windows Phone: the tapped header slides to the front); Left / Right on the head step
//            back / forward one; a change from outside the head (the bar's queue and lyrics doors) takes the shorter way round.
//   PANES    the pane on show slides in from the side of travel, fading in, while the one it replaced stays mounted, slides the other way
//            and fades out (`SlidePanes`); the frame drops it when the slide has run.
//   REDUCED  nothing travels: the words that moved fade in where they land, the panes cross-fade (a value, never a skipped hook).

using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Scene;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The head's words and the carousel that moves them, kept by the panel's frame across renders.</summary>
internal sealed class ZunePanelPivots
{
    readonly Dictionary<PanelMode, NodeHandle> _words = new();
    readonly Dictionary<PanelMode, Action<NodeHandle>> _realized = new();
    readonly Dictionary<PanelMode, Action> _clicks = new();
    readonly Dictionary<PanelMode, float> _widths = new();
    readonly Dictionary<PanelMode, float> _oldX = new();
    PanelMode[] _laid = [];
    bool _laidOpen, _refocus;
    int _intent;

    /// <summary>The words in the order the head draws them (the frame sets it on every render).</summary>
    public PanelMode[] Order = [];

    /// <summary>The panel's pivot verb (null on a linked pane, whose head is its one word).</summary>
    public Action<PanelMode>? Show;

    /// <summary>The strip that clips the words (its width is what shows).</summary>
    public NodeHandle Strip;

    /// <summary>Left / Right on the head (bubbled from the focused word).</summary>
    public readonly Action<KeyEventArgs> OnKey;

    /// <summary>The strip was realized.</summary>
    public readonly Action<NodeHandle> OnStrip;

    public ZunePanelPivots()
    {
        OnKey = Key;
        OnStrip = h => Strip = h;
    }

    /// <summary>The realize callback of <paramref name="mode"/>'s word (one per mode, kept).</summary>
    public Action<NodeHandle> RealizedOf(PanelMode mode)
    {
        if (!_realized.TryGetValue(mode, out var realized)) _realized[mode] = realized = h => _words[mode] = h;
        return realized;
    }

    /// <summary>The click of a trailing word: it comes to the front, forward.</summary>
    public Action ClickOf(PanelMode mode)
    {
        if (!_clicks.TryGetValue(mode, out var click)) _clicks[mode] = click = () => Go(mode, 1);
        return click;
    }

    /// <summary>Is <paramref name="node"/> one of the head's words?</summary>
    public bool IsWord(NodeHandle node)
    {
        foreach (var (_, word) in _words) if (word == node) return true;
        return false;
    }

    /// <summary>The word of <paramref name="mode"/>, or null while it is not realized.</summary>
    public NodeHandle WordOf(PanelMode mode) => _words.TryGetValue(mode, out var node) ? node : NodeHandle.Null;

    void Go(PanelMode mode, int travel)
    {
        if (Show is not { } show) return;
        _intent = travel;
        show(mode);
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

    float Width(PanelMode mode) => _widths.TryGetValue(mode, out float w) ? w : 0f;

    /// <summary>Seeds the words' slide from the order last laid out to <see cref="Order"/>, after the commit's layout (a layout effect). Returns
    /// the direction of travel: 1 when the strip moved left (the new lead came in from the right), -1 the other way, 0 when nothing travelled
    /// (the first layout, a closed panel, the same order).</summary>
    public int Slide(SceneStore scene, AnimEngine anim, bool open, bool reduced)
    {
        var order = Order;
        var prev = _laid;
        bool wasOpen = _laidOpen;
        _laid = order;
        _laidOpen = open;
        int intent = _intent;
        _intent = 0;
        foreach (var mode in order)
            if (_words.TryGetValue(mode, out var word) && scene.IsLive(word)) _widths[mode] = scene.Bounds(word).W;
        if (!open || !wasOpen || prev.Length == 0 || order.Length == 0 || Same(prev, order)) return 0;

        float gap = ZuneFrameRules.PanelPivotGap, gutter = ZuneFrameRules.PanelGutter, ms = ZuneMotion.PivotSlideMs;
        _oldX.Clear();
        float at = 0f;
        foreach (var mode in prev) { _oldX[mode] = at; at += Width(mode) + gap; }
        float prevCycle = at;

        // The strip's travel: forward it moves left by the new lead's old place, back it moves right by what followed the new lead.
        float shift = 0f;
        if (prev[0] != order[0] && _oldX.TryGetValue(order[0], out float leadX))
        {
            float forward = leadX, back = leadX - prevCycle;
            shift = intent > 0 ? forward : intent < 0 ? back : forward <= -back ? forward : back;
        }
        float shows = scene.IsLive(Strip) ? scene.Bounds(Strip).W - gutter : float.PositiveInfinity;

        float x = 0f;
        foreach (var mode in order)
        {
            float w = Width(mode), newX = x;
            x += w + gap;
            if (!_words.TryGetValue(mode, out var node) || !scene.IsLive(node)) continue;
            ref var paint = ref scene.Paint(node);
            float drawnDx = paint.LocalTransform.Dx, alpha = paint.Opacity;
            if (!_oldX.TryGetValue(mode, out float oldX))
            {
                // A word the head did not show (a pane docked away, the video body arriving): it fades in where it lands.
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
        // Both legs show (a panel that shows the whole cycle): out over the first part, in from the other side over the rest.
        float split = ZuneFrameRules.PanelPivotWrapSplit;
        anim.Keyframes(node, AnimChannel.TranslateX,
            [new(0f, legs.From), new(split, legs.LeftAt, Easing.SmoothOut), new(split, legs.ComesFrom), new(1f, 0f, Easing.SmoothOut)], ms);
        anim.Keyframes(node, AnimChannel.Opacity, [new(0f, 1f), new(split, 0f, Easing.SmoothOut), new(1f, 1f, Easing.SmoothOut)], ms);
    }

    /// <summary>The panes' slide: <paramref name="enter"/> comes in from the side of <paramref name="travel"/>, fading in, while <paramref name="leave"/>
    /// goes the other way, fading out, from where it is drawn. Reduced motion: a plain cross-fade.</summary>
    public static void SlidePanes(SceneStore scene, AnimEngine anim, NodeHandle enter, NodeHandle leave, int travel, bool reduced)
    {
        float ms = ZuneMotion.PivotSlideMs, shift = reduced ? 0f : ZuneFrameRules.PanelPaneShift * (travel < 0 ? -1f : 1f);
        if (!enter.IsNull && scene.IsLive(enter))
        {
            ref var paint = ref scene.Paint(enter);
            float dx = paint.LocalTransform.Dx, alpha = paint.Opacity;
            // A pane coming back mid-slide continues from where it is drawn.
            bool fresh = alpha >= 1f && dx == 0f;
            anim.Animate(enter, AnimChannel.TranslateX, fresh ? shift : dx, 0f, ms, Easing.SmoothOut);
            anim.Animate(enter, AnimChannel.Opacity, fresh ? 0f : alpha, 1f, ms, Easing.SmoothOut);
        }
        if (!leave.IsNull && scene.IsLive(leave))
        {
            ref var paint = ref scene.Paint(leave);
            float dx = paint.LocalTransform.Dx, alpha = paint.Opacity;
            anim.Animate(leave, AnimChannel.TranslateX, dx, dx - shift, ms, Easing.SmoothOut);
            anim.Animate(leave, AnimChannel.Opacity, alpha, 0f, ZuneFrameRules.PanelPaneFadeMs, Easing.SmoothOut);
        }
    }

    static bool Same(PanelMode[] a, PanelMode[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
