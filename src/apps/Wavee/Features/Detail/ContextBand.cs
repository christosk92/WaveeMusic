using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Signals;
using Wavee.Features.Detail;

namespace Wavee;

/// <summary>One entry in a context band's pivot: the section's stable key (the same key its
/// <see cref="SectionAnchors"/> registration uses) and its already-localized title.</summary>
readonly record struct ContextPivotItem(string Key, string Label);

/// <summary>The page-owned registry the context band's scroll spy reads WITHOUT being an ancestor of the scroller:
/// each pivot-visible section reports its wrapper node here (keyed by the section key) and the page reports the scroll
/// viewport. The band resolves "which section am I in" from these through <c>SceneStore.AbsoluteRect</c> — the same
/// reachable pattern <c>ShyMonthPill</c> and the sidebar selection pill use.
///
/// <para>A plain mutable class, not a signal graph: it is written from <c>OnRealized</c> (a layout-time callback) and
/// read from a scroll effect, both on the UI thread, and turning each of ~14 node handles into a signal would buy a
/// reactivity nobody subscribes to — the SCROLL signal is what drives the recompute.</para></summary>
sealed class SectionAnchors
{
    /// <summary>The scrolling viewport the sections live in. Captured with <c>ScrollEl.OnRealized</c>.</summary>
    public NodeHandle Viewport;

    readonly Dictionary<string, NodeHandle> _nodes = new(StringComparer.Ordinal);

    public void Set(string key, NodeHandle node) => _nodes[key] = node;
    public NodeHandle Get(string key) => _nodes.TryGetValue(key, out var n) ? n : default;

    /// <summary>Drop every registration — called when the page swaps identity (artist → artist in the reused slot),
    /// so a stale node from the previous artist can never answer a pivot click.
    ///
    /// <para><b>Call this from RENDER, never from an effect.</b> The registry is filled by <c>OnRealized</c>, which
    /// the reconciler fires while committing the tree this render returned; <c>UseEffect</c> bodies run after PRESENT,
    /// i.e. AFTER that. A reset scheduled as an effect therefore erases the registrations of the very frame that made
    /// them — and since a node realizes exactly once, nothing ever puts them back (D40: the spy stuck on item 0 and
    /// every pivot click dead, on the FIRST mount, not just on a re-route).</para></summary>
    public void Reset() { _nodes.Clear(); Viewport = default; }
}

/// <summary>
/// Wavee's ONE sticky page header: the <b>text-chrome context bar</b>. A full-width 56-DIP band that slides in once
/// the page's hero has scrolled past, carrying the page title on the left, (where the page has sections) its own
/// sections as a text PIVOT in the middle, and text actions on the right.
///
/// <para><b>What it replaced, and why.</b> The app had two unrelated sticky headers. The artist page had an
/// art-tinted band with an avatar, the name and Play/Following capsules — a second, smaller copy of the hero it was
/// replacing, which is the one thing a collapsed header must not be. The track-detail pages had three FLOATING
/// objects instead of a header at all: a shadowed bordered capsule holding a cover and a title, a pair of bare glyph
/// buttons, and an accent circle play FAB, all hovering over live scrolling rows with nothing behind them. Both are
/// gone. This band is ONE hairline, NO shadow, no thumbnail, no plates and — see the offset-model note below — no
/// SURFACE either: the chrome is typography, which is the Zune pivot idiom the app's type ramp was already aimed at.</para>
///
/// <para><b>The parts are separate on purpose.</b> The artist band pins as one node inside the hero's own collapse
/// ZStack; the detail band pins as the identity row while the tracklist's column header pins directly under it as a
/// second node. Both must read as ONE band, so this class publishes the hairline, the row scaffolding and the clip
/// contract as pieces either page can assemble, rather than one Build() that would have to grow a mode flag for
/// the difference.</para>
///
/// <para>Arithmetic (what fits, what the pivot drops, which section is active) lives in the engine-free
/// <see cref="ContextBandLayout"/>, where the tests drive it.</para>
/// </summary>
static class ContextBand
{
    // ── THE OFFSET MODEL: THE BAND PAINTS NO FILL ────────────────────────────────────────────────────────────────
    //
    // The band has NO material. Not a translucent one, not an opaque one — none. Scrolled content is CLIPPED at the
    // band's lower edge instead of sliding under it, so the band's 56 (or 93) DIP are an unpainted OMISSION and what
    // shows there is whatever the page's real ground already is: the shell's live Mica smoke on the artist page, the
    // art-derived PageTone plane plus its blurred backdrop on the track-detail pages.
    //
    // WHY THE FILL HAD TO GO. Every fill this band ever wore was an APPROXIMATION of a surface it could not observe.
    // The last one was the honest version of that mistake — ColorContrast.Flatten(FileArea, MicaRef.*Default), the
    // content layer composited onto a no-wallpaper reference tone — and it still could not follow live Mica, which
    // takes its colour from the user's desktop. On a dark wallpaper the reference tone read as a SOLID BLACK SLAB
    // parked across the page (user report, screenshots). Parameterising the flatten over the detail pages' art tone
    // fixed one of the two consumers and left the artist page exactly as wrong; the offset model fixes both, for
    // free, and can never drift — there is no constant left to drift.
    //
    // THE CLIP IS THE CONTRACT. A band with no fill is only a band if nothing renders into its region:
    //   · artist page      — ONE ClipTopAtViewport bind at ContextBandLayout.Height on the magazine body + the blend
    //                        wash (ArtistPage.Body).
    //   · album / show     — the trailing scroller's ClipTopAtViewport at DetailVerticalLayout.StickyClipInset
    //                        (56 identity row + 36 column row + 1 hairline), already in place (DetailTracks.TrailingBody).
    //   · playlist / liked — the virtual list's ItemClipTopInset at the same inset, already in place
    //                        (DetailTracks.ApplyVerticalItemBand). Its ItemClipTopFadeBand feathers the cut.
    // Both engine mechanisms self-gate: the clip engages only once content actually reaches the line and releases
    // when it leaves, so there is no state to keep in sync with the band's reveal ramp.
    //
    // WHAT STILL PAINTS: the hairline under the band, the pivot's active underline, and the text ink. That is all.

    /// <summary>The band's single lower edge — a low-alpha SEPARATOR token, never the accent. The accent in this band
    /// is spent entirely on the two things that earn it: the primary action and the active-section underline.</summary>
    public static ColorF HairlineColor => Tok.StrokeDividerDefault;

    /// <summary>The 1-DIP lower edge, as a row child. Deliberately a real laid-out child rather than a border on the
    /// band: the detail page's band is TWO pinned nodes (identity row + column header) and only the LAST of them may
    /// carry the line, or the page shows a seam through the middle of one surface.</summary>
    public static Element Hairline() => new BoxEl
    {
        Height = ContextBandLayout.HairlineHeight, AlignSelf = FlexAlign.Stretch,
        Fill = HairlineColor, HitTestVisible = false,
    };

    /// <summary>The band's title: BodyStrong (14 / 20 / 600) in primary ink, one line, ellipsized. It never wraps and
    /// never drops — see <see cref="ContextBandLayout"/> for the priority order.</summary>
    public static TextEl Title(string text) => Ui.BodyStrong(text) with
    {
        Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1,
        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The optional second line under the title (owner · count on a detail page). Caption / tertiary — it is
    /// context for the title, not a competing label.</summary>
    public static TextEl Byline(string text) => Ui.Caption(text) with
    {
        Color = Tok.TextTertiary, MinWidth = 0f, MaxLines = 1,
        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The band's lower edge as an OVERLAY layer, for a band whose height is fixed by a collapse ladder (the
    /// artist hero's <c>PresentedH</c> bind targets exactly <see cref="ContextBandLayout.Height"/>, so the line has to
    /// sit inside that 56 rather than add a 57th DIP and re-open the collapse arithmetic).</summary>
    public static Element HairlineOverlay(float width) => new BoxEl
    {
        Width = width, Height = ContextBandLayout.Height,
        Direction = 1, Justify = FlexJustify.End, HitTestVisible = false,
        Children = [Hairline()],
    };

    /// <summary>The identity row: <see cref="ContextBandLayout.Height"/> tall, gutter-padded, UNPAINTED, and carrying
    /// no edge of its own — the caller decides where the band's ONE hairline goes (overlaid on the artist page, in
    /// flow under the tracklist's column header on the detail pages).
    ///
    /// <para>No <c>Fill</c> at all (see the offset-model note above). <c>HitTestVisible</c> is still true — the
    /// engine's hit test is geometric, not paint-derived, so an unpainted box with handlers is a live target exactly
    /// like the title bar's unpainted caption islands. The row must stay hittable so its actions work AND so the band
    /// keeps swallowing wheel/click that would otherwise reach the rows scrolling behind it.</para></summary>
    public static Element Row(float width, float gutter, Element[] children) => new BoxEl
    {
        Direction = 0, Width = width, Height = ContextBandLayout.Height,
        Padding = new Edges4(gutter, 0f, gutter, 0f),
        Gap = ContextBandLayout.ClusterGap,
        AlignItems = FlexAlign.Center,
        HitTestVisible = true,
        Children = children,
    };

    /// <summary>The clip inset each page owes the band, and the feather at that cut — both pure geometry, so both live
    /// in <see cref="ContextBandLayout"/> where the tests drive them. Re-exposed here only so a band call site does not
    /// have to know which of the two files owns which half of the contract.</summary>
    public const float ClipInset = ContextBandLayout.ClipInset;

    /// <inheritdoc cref="ClipInset"/>
    public const float ClipFadeBand = ContextBandLayout.ClipFadeBand;

    /// <summary>Wrap a page section so the band's scroll spy can find it. Layout-neutral by construction — a bare
    /// column wrapper around the section, adding no size, no padding and no flex of its own.</summary>
    public static Element Anchor(SectionAnchors anchors, string key, Element section) => new BoxEl
    {
        Key = "anchor:" + key, Direction = 1, MinWidth = 0f,
        OnRealized = h => anchors.Set(key, h),
        Children = [section],
    };
}

/// <summary>
/// The band's middle cluster: the page's own sections as text links, with a 2-DIP accent underline marking the one
/// the visitor is currently inside.
///
/// <para><b>Why a component.</b> The active index changes on scroll, and scroll is the hottest signal on the page.
/// Isolating the spy here means a scroll step re-renders THIS cluster and nothing else — the artist page's magazine
/// body, its virtualized discography grids and the band's own title/actions are all untouched. The scroll signal is
/// subscribed by an EFFECT (not by Render), so most scroll steps do not even re-render this: the effect writes the
/// active-index signal through <c>SetIfChanged</c>, so a render happens only when the answer actually changes — at
/// section boundaries, a handful of times per page.</para>
///
/// <para><b>Props are re-pushed, never frozen.</b> The item set genuinely changes after mount (an artist's extras,
/// top albums and biography all arrive after the first Ready render, so sections appear mid-stream), which is exactly
/// the case <c>Embed.Comp(props, factory)</c> + <c>UseProps</c> exists for — a constructor argument would freeze the
/// pivot at whatever the page knew on the first frame. Only the two things that are genuinely mount-stable (the
/// anchor registry and the scroll signal) ride the constructor.</para>
/// </summary>
sealed class ContextPivot : Component
{
    /// <summary>The band never carries more than this many pivot items regardless of what fits — a pivot is a
    /// glance-and-aim affordance, and past a dozen words it is a menu. Also the stack budget the spy scans in.</summary>
    public const int MaxItems = 16;

    /// <summary>Re-pushed per render. Every item stays mounted in the horizontally scrolling pivot;
    /// <paramref name="BandBottom"/> is the lower edge of the sticky chrome (and the gutter a click scrolls to).</summary>
    public sealed record Props(ContextPivotItem[] Items, float BandBottom, ColorF Accent);

    readonly SectionAnchors _anchors;
    readonly IReadSignal<float> _scroll;
    readonly IReadSignal<float> _viewportHeight;
    readonly IReadSignal<bool> _atScrollEnd;

    // The live props, read by the spy effect (which cannot subscribe to props). A plain render-local field write —
    // the same idiom DetailTracks uses to publish `_recsLive` to its bound slots.
    ContextPivotItem[] _items = [];
    float _bandBottom;
    NodeHandle[] _tabNodes = [];
    Action<NodeHandle>[] _tabRealized = [];
    Action[] _tabClicks = [];
    NodeHandle _tabViewport;
    bool _scrollSelectionSeeded;

    public ContextPivot(SectionAnchors anchors, IReadSignal<float> scroll, IReadSignal<float> viewportHeight,
                        IReadSignal<bool> atScrollEnd)
    {
        _anchors = anchors;
        _scroll = scroll;
        _viewportHeight = viewportHeight;
        _atScrollEnd = atScrollEnd;
    }

    public override Element Render()
    {
        var p = UseProps<Props>();
        _items = p.Items;
        _bandBottom = p.BandBottom;

        var active = UseSignal(0);

        // Scroll → re-resolve. Untracked inside, so writing the active index can never make this effect its own
        // dependency; SetIfChanged is what keeps a 4000-px scroll to a handful of renders.
        UseSignalEffect(() =>
        {
            _ = _scroll.Value;                       // subscribe (throttled by the page's own geometry projector)
            _ = _viewportHeight.Value;               // resize changes the upper-quarter activation line
            _ = _atScrollEnd.Value;                  // the final short section wins at the real scroll limit
            Reactive.Untrack(() => Resolve(active));
        });

        // The item SET changing (a section arrived) also changes the answer, and props are not a signal the effect
        // above can see — so resolve once more after any render that changed the set. DepKey over the count + the
        // first/last keys is enough: sections are appended and removed as whole units, never re-ordered in place.
        UseLayoutEffect(() => Resolve(active), DepKey.From(SetKey(p.Items)));

        int shown = Math.Min(p.Items.Length, MaxItems);
        EnsureTabSlots(shown);

        int activeIndex = active.Value;   // subscribe → re-render only on a boundary crossing
        int current = shown > 0 ? Math.Clamp(activeIndex, 0, shown - 1) : -1;
        UseLayoutEffect(() => RevealActive(current), DepKey.From(HashCode.Combine(current, shown, SetKey(p.Items))));

        if (shown == 0) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        var kids = new Element[shown];
        for (int i = 0; i < shown; i++)
            kids[i] = Link(p.Items[i].Label, i == current, p.Accent, _tabClicks[i], _tabRealized[i]);

        var row = new BoxEl
        {
            Direction = 0, Gap = ContextBandLayout.PivotGap, Shrink = 0f,
            AlignItems = FlexAlign.Center,
            Children = kids,
        };

        return new ScrollEl
        {
            Horizontal = true,
            ContentSized = true,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            Height = ContextBandLayout.Height,
            SuppressScrollBar = true,
            AutoEdgeFade = true,
            EdgeCues = ScrollEdgeCues.None,
            Content = row,
            OnRealized = CaptureTabViewport,
        };
    }

    /// <summary>One pivot link. The hover handler is on the LINK's own box (never on the row) so
    /// <c>TextEl.HoverColor</c> scopes to the word the pointer is actually over.
    /// <para>The underline is ALWAYS mounted and switches COLOUR — transparent when inactive — rather than being
    /// conditionally mounted: a mount/unmount would re-run the reconciler for a 2-DIP rect on every boundary, and a
    /// brush cross-fade is what makes the mark move as one gesture. No FLIP flight between items (a 2-DIP rule flying
    /// across a 56-DIP bar is a distraction, not wayfinding).</para></summary>
    static Element Link(string label, bool isActive, ColorF accent, Action go, Action<NodeHandle> realized) => new BoxEl
    {
        Direction = 1, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Height = ContextBandLayout.Height - 2f * Spacing.M,
        Padding = new Edges4(ContextBandLayout.PivotPadX, 0f, ContextBandLayout.PivotPadX, 0f),
        Corners = Radii.ControlAll,
        Role = AutomationRole.Tab, Focusable = true, Cursor = CursorId.Hand, OnClick = go, OnRealized = realized,
        Children =
        [
            new TextEl(label)
            {
                Size = WaveeCta.TextActionSize, LineHeight = WaveeCta.TextActionLineHeight,
                Weight = WaveeCta.TextActionWeight,
                Color = isActive ? Tok.TextPrimary : Tok.TextSecondary,
                HoverColor = Tok.TextPrimary,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            },
            new BoxEl
            {
                Height = ContextBandLayout.UnderlineHeight, AlignSelf = FlexAlign.Stretch,
                Margin = new Edges4(0f, ContextBandLayout.UnderlineGap, 0f, 0f),
                Fill = isActive ? accent : ColorF.Transparent,
                BrushTransitionMs = WaveeMotion.Fast,
                HitTestVisible = false,
            },
        ],
    };

    /// <summary>Cache each tab's node capture and click delegate for the current item count.</summary>
    void EnsureTabSlots(int count)
    {
        if (_tabNodes.Length == count) return;
        int oldCount = _tabNodes.Length;
        Array.Resize(ref _tabNodes, count);
        Array.Resize(ref _tabRealized, count);
        Array.Resize(ref _tabClicks, count);
        for (int i = 0; i < count; i++)
        {
            if (i < oldCount) continue;
            int index = i;
            _tabRealized[i] = h => _tabNodes[index] = h;
            _tabClicks[i] = () => GoTo(index);
        }
        _scrollSelectionSeeded = false;
    }

    void CaptureTabViewport(NodeHandle node) => _tabViewport = node;

    void RevealActive(int index)
    {
        if ((uint)index >= (uint)_tabNodes.Length || _tabViewport.IsNull) return;
        var node = _tabNodes[index];
        var scene = Context.Scene;
        if (node.IsNull || scene is null || !scene.IsLive(node) || !scene.IsLive(_tabViewport)) return;
        ScrollIntoView.BringInto(Context, _tabViewport, node, Spacing.S,
            animate: _scrollSelectionSeeded && !Motion.ReducedMotion);
        _scrollSelectionSeeded = true;
    }

    /// <summary>Click → scroll that section's top to just under the band. Through the engine's ONE programmatic
    /// bring-into-view seam against the EXPLICIT viewport the page registered, so a nested scroller deeper in the
    /// page can never be the thing that moves. Animated unless reduced motion is on, in which case it snaps.</summary>
    void GoTo(int index)
    {
        if ((uint)index >= (uint)_items.Length) return;
        var node = _anchors.Get(_items[index].Key);
        // LIVENESS, not just non-null: a registration outlives its node whenever the page reuses its slot, and
        // BringInto against a reclaimed handle would read another node's rect. Same guard the spy's Resolve applies.
        var scene = Context.Scene;
        if (node.IsNull || _anchors.Viewport.IsNull || scene is null
            || !scene.IsLive(node) || !scene.IsLive(_anchors.Viewport)) return;
        ScrollIntoView.BringInto(Context, _anchors.Viewport, node,
            margin: _bandBottom, alignmentRatio: 0f, animate: !Motion.ReducedMotion);
    }

    /// <summary>Read committed layout and publish the active index. One AbsoluteRect per pivot item, only on a scroll
    /// step the page's own projector already let through (its 24-DIP write floor), and only up to the first
    /// unrealized section — a page whose lower half has not laid out costs a couple of reads, not a walk.</summary>
    void Resolve(Signal<int> active)
    {
        var scene = Context.Scene;
        if (scene is null) return;
        var vp = _anchors.Viewport;
        if (vp.IsNull || !scene.IsLive(vp)) return;

        int n = Math.Min(_items.Length, MaxItems);
        if (n == 0) return;
        RectF vpRect = scene.AbsoluteRect(vp);
        float vpTop = vpRect.Y;
        Span<float> tops = stackalloc float[MaxItems];
        for (int i = 0; i < n; i++)
        {
            var node = _anchors.Get(_items[i].Key);
            tops[i] = node.IsNull || !scene.IsLive(node) ? float.NaN : scene.AbsoluteRect(node).Y - vpTop;
        }

        float viewportHeight = _viewportHeight.Peek();
        if (viewportHeight <= 0f) viewportHeight = vpRect.H;
        int at = ContextBandLayout.ActiveSection(tops[..n], _bandBottom, viewportHeight, _atScrollEnd.Peek());
        if (at >= 0) active.SetIfChanged(at);
    }

    static int SetKey(ContextPivotItem[] items)
    {
        var hash = new HashCode();
        hash.Add(items.Length);
        for (int i = 0; i < items.Length; i++) hash.Add(items[i].Key, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
