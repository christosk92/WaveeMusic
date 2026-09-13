using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Shared, page-owned registry the shy month pill reads WITHOUT being an ancestor of the scroller: each month
/// board reports its header node here (keyed by "yyyy-MM"), the page reports the scroll viewport node, and refreshes the
/// ordered month labels each render. The pill resolves the month at the viewport top from these via
/// <c>SceneStore.AbsoluteRect</c> — the same reachable pattern the sidebar selection pill uses.</summary>
sealed class ShyMonthTracker
{
    public NodeHandle Viewport;
    public NodeHandle PanelHeader;
    public string Label = "";
}

/// <summary>The floating "shy" month indicator (R3 artist-schedule rework): a small elevated pill, top-center over the
/// page, showing the month currently at the top of the viewport ("SEPTEMBER 2026"). It appears while the user scrolls
/// and fades ~1s after scrolling stops.
///
/// Engine constraints honored (all three killed a previous overlay feature): (1) the overlay root is
/// <c>HitTestPassThrough</c> and the pill surface is <c>HitTestVisible = false</c>, so it is exempt from
/// <c>InputDispatcher.ScrollableUnder</c>/hit-testing and never steals wheel/touchpad input — the scroller sibling
/// beneath wins the hit; (2) the pill surface has an EXPLICIT size (never NaN in the ZStack); (3) nothing binds a
/// child's size to its container's measured size. Scroll is read through the page-published offset signal + the shared
/// <see cref="ShyMonthTracker"/> geometry, so per-frame scroll updates re-render ONLY this small component, never the
/// page. Scroll-stop is detected on the engine's animation clock (a re-armed invisible duration track — never the wall
/// clock), and reveal/dismiss ride the declarative transition surface, which degrades to instant under reduced motion.</summary>
sealed class ShyMonthPill : Component
{
    readonly ShyMonthTracker _tracker;
    readonly Signal<float> _scroll;

    public ShyMonthPill(ShyMonthTracker tracker, Signal<float> scroll)
    {
        _tracker = tracker;
        _scroll = scroll;
    }

    static readonly LayoutTransition Presence = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(200f, Easing.SmoothOut),
        Enter: new EnterExit(Dy: -8f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -6f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(260f, Easing.SmoothOut));

    public override Element Render()
    {
        var active = UseSignal(false);
        var label = UseSignal<string?>(null);
        var arm = UseSignal(0);
        var lastOff = UseRef(float.NaN);

        // Per-scroll-change: refresh the month at the viewport top and (re)arm the linger — NO page re-render (only this
        // component subscribes to the hot offset signal). The first run establishes the baseline silently. The pill is
        // strictly a boards-region affordance: it exists ONLY while scroll activity is recent AND a month header has
        // actually crossed the viewport top — ResolveMonth returns null above the boards, so at rest over the hero or
        // scrolled back above the first board it deactivates immediately instead of lingering.
        UseSignalEffect(() =>
        {
            float off = _scroll.Value;   // subscribe → fires on each throttled scroll step
            Reactive.Untrack(() =>
            {
                float prev = lastOff.Value;
                lastOff.Value = off;
                if (float.IsNaN(prev)) return;
                if (MathF.Abs(off - prev) < 0.5f) return;

                string? m = ResolveMonth();
                if (m is null)
                {
                    if (active.Peek()) active.Value = false;   // above the boards region → the pill must not exist
                    return;
                }
                if (!string.Equals(m, label.Peek(), StringComparison.Ordinal)) label.Value = m;
                if (!active.Peek()) active.Value = true;
                arm.Value++;   // restart the 1s linger countdown
            });
        });

        int a = arm.Value;               // subscribe → remount the linger clock (re-arm) on each scroll step
        bool show = active.Value;        // subscribe → mount/unmount the clock with visibility
        string text = label.Value ?? ""; // subscribe → refresh the pill caption

        // The linger clock is mounted ONLY while shown, so the pill costs nothing per frame once it has faded out. Keyed
        // by the arm counter so each scroll step remounts a fresh 1s countdown (a running scroll never lets it fire).
        Element? clock = show
            ? Embed.Comp(() => new ShyPillLingerClock { OnElapsed = () => { if (active.Peek()) active.Value = false; } })
                with { Key = "shy-linger:" + a }
            : null;

        Element pill = Flow.Show(() => active.Value, Surface(text));

        return new BoxEl
        {
            // Twofold pass-through guarantee (verified against InputDispatcher): HitTestVisible=false makes HitTestAny
            // early-return for this whole overlay subtree BEFORE it recurses into children (so neither the Flow.Show
            // wrapper nor the pill surface can ever claim the wheel), and HitTestPassThrough yields the node so
            // ScrollableUnder finds the scroller SIBLING beneath. The pill never steals wheel/touchpad, shown or hidden.
            HitTestVisible = false, HitTestPassThrough = true, Grow = 1f, Direction = 1,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
            Padding = new Edges4(0f, Spacing.M, 0f, 0f),
            Children = clock is null ? [ pill ] : [ pill, clock ],
        };
    }

    Element Surface(string text) => new BoxEl
    {
        Animate = Presence,
        TransformOriginX = 0.5f, TransformOriginY = 0f,
        // Non-interactive: the whole subtree is invisible to hit-testing, so scrolling passes straight through it.
        HitTestVisible = false,
        AlignSelf = FlexAlign.Center, Height = 32f, Shrink = 0f,   // explicit size (never NaN in a ZStack)
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
        Corners = CornerRadius4.All(Radii.Full),
        Acrylic = Tok.AcrylicFlyout, Fill = Tok.FillLayerDefault, Shadow = Elevation.Card,
        BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
        Children =
        [
            WaveeType.Eyebrow(text) with { Color = WaveeAccent.Decor, MaxLines = 1 },
        ],
    };

    // The month whose header sits at (or just above) the viewport top — the one with the greatest content-Y still not
    // below the top edge. NULL until the first board header crosses the top (the hero/spotlight zone), which is what
    // keeps the pill out of existence anywhere above the boards. Reads committed layout via AbsoluteRect (a one-frame
    // lag is invisible for a month cue); no ancestor relationship to the scroller is required.
    string? ResolveMonth()
    {
        var scene = Context.Scene;
        if (scene is null) return null;
        var vp = _tracker.Viewport;
        if (vp.IsNull || !scene.IsLive(vp)) return null;

        var header = _tracker.PanelHeader;
        if (header.IsNull || !scene.IsLive(header) || string.IsNullOrWhiteSpace(_tracker.Label)) return null;
        return scene.AbsoluteRect(header).Y <= scene.AbsoluteRect(vp).Y + 4f ? _tracker.Label : null;
    }
}

/// <summary>An invisible, re-armable ~1s countdown on the engine's animation clock (the <see cref="Marquee"/>/ToolTip
/// precedent): on mount it seeds a wall-accurate duration track on its own hidden node; when the track settles it fires
/// <see cref="OnElapsed"/> once. Remounting it (a new Key) restarts the countdown — so a running scroll keeps it from
/// ever firing, and 1s after the last scroll step it dismisses the pill. Costs one per-frame wake only while mounted.</summary>
sealed class ShyPillLingerClock : Component
{
    public required Action OnElapsed;
    const float LingerMs = 1000f;

    public override Element Render()
    {
        var tick = UseContext(FrameClock.Tick);   // re-render every frame while mounted
        var self = UseRef<NodeHandle>(default);
        var seeded = UseRef(false);
        var fired = UseRef(false);

        UseEffect(() =>
        {
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || self.Value.IsNull || !scene.IsLive(self.Value)) return;

            if (!seeded.Value)
            {
                seeded.Value = true;
                anim.Animate(self.Value, AnimChannel.Opacity, 1f, 1f, LingerMs, Easing.Linear);
                return;
            }
            if (!fired.Value && !anim.HasTracks(self.Value))
            {
                fired.Value = true;
                OnElapsed();
            }
        }, tick);

        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f, OnRealized = x => self.Value = x };
    }
}
