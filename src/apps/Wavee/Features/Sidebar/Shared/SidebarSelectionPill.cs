using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

readonly record struct SidebarPillRegistration(NodeHandle Node);

/// <summary>
/// One NavigationViewItem-style SelectionIndicator. Every selectable realized row permanently owns one 3×16 node. On a
/// route edge the previous and next rows run the paired Microsoft NavigationView timeline; an unrealized peer, first
/// paint, geometry refresh, or recycle snaps. That ownership removes the global-overlay feedback loop that made the cue
/// flicker in the section-header band.
///
/// <para><b>Opacity is BOUND, never authored (#22/#23 — two pills lit at once).</b> It used to be
/// <c>Opacity = selected ? 1f : 0f</c>, a mount-time literal off the slot's snapshot, while the pane's transaction wrote
/// the same node's opacity channel directly. Anything that lit a node the row's own state called dark — a
/// force-completed flight snapped <c>visible: true</c>, a route→node registration pointing at a slot since recycled onto
/// another row — stuck, because nothing re-derived it. The opacity is now one bound read of the slot's LIVE state
/// (<see cref="SidebarPillState"/>, the drop cue's discipline), so it is re-evaluated by the row's own epoch and can
/// never be stale; <see cref="NavigationSelectionMotion"/> owns the TRANSFORM (the route→route slide), and the pane may
/// only ever assert "visible" for the node that this bound read would light anyway.</para>
/// </summary>
sealed class SidebarSelectionPill : Component
{
    public const float PillH = 16f;
    public const float PillW = 3f;

    readonly SidebarPane _owner;
    readonly Func<SidebarPillState> _state;
    // ONE thunk for the node's whole life: the reconciler wires a bound channel at MOUNT, so re-allocating it per render
    // would be pure garbage — and the mount-time capture is exactly why the probe, not the render, must own the value.
    readonly Prop<float> _opacity;
    NodeHandle _self;
    string? _route;

    public SidebarSelectionPill(SidebarPane owner, Func<SidebarPillState> state)
    {
        _owner = owner;
        _state = state;
        _opacity = Prop.Of(() => _state().Opacity);
    }

    public override Element Render()
    {
        var state = _state();
        bool selected = state.Selected;
        bool recycled = _route is not null && !string.Equals(_route, state.Route, StringComparison.Ordinal);
        int routeHash = state.Route is null ? 0 : StringComparer.Ordinal.GetHashCode(state.Route);
        UseLayoutEffect(() =>
        {
            _route = state.Route;
            var anim = Context.Anim;
            if (anim is null || _self.IsNull || state.Route is not { Length: > 0 } route) return;
            _owner.RegisterSelectionPill(route, _self);
            // A recycled node may inherit an interrupted transform from the route previously bound to the slot. The pane
            // owns real navigation transactions; recycle is never navigation and therefore always snaps. The visibility
            // it snaps to is the same probe the bound channel reads, so the snap can never disagree with it.
            if (recycled) NavigationSelectionMotion.SnapVertical(anim, _self, selected);
        }, DepKey.From(routeHash));

        return new BoxEl
        {
            Width = PillW,
            Height = PillH,
            Margin = new Edges4(state.Indent, state.Top, 0f, 0f),
            Corners = CornerRadius4.All(PillW * 0.5f),
            Fill = Tok.AccentDefault,
            Opacity = _opacity,
            // NavigationSelectionMotion expresses WinUI's animated CenterPoint swap as painted-top coordinates.
            // A top-edge origin is therefore part of the primitive's geometry contract, not a visual preference.
            TransformOriginY = 0f,
            HitTestVisible = false,
            OnRealized = h => _self = h,
        };
    }
}
