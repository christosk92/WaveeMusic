// ── Themes/Classic/Structure/Views/Nav/ClassicNavRowParts.cs — the selection pill and the pending row (themes plan 6.1, T5f) ───
//
// Moved from `Shell/Sidebar.UI.Rows.cs` (`Sidebar.SelectionPill`, `Sidebar.Skeletons`) by T5f: both read the row ladder (the pill's size
// and radius, the pending row's icon column), which is the structure's. The pane's selection FLIGHT (the 600 ms route travel, its
// registrations by route) stays core (`INavRowHost.RegisterSelectionPill`); this is the picture it moves.

using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>
/// One NavigationViewItem SelectionIndicator (3×16, radius 1.5, accent) permanently owned by every selectable realized
/// row. The pane runs the paired 600 ms route flight on the TRANSFORM; a recycle is never navigation, so it snaps.
///
/// <para><b>Opacity is BOUND, never authored</b> (#22/#23 — two pills lit at once): one thunk for the node's whole life
/// reads the slot's LIVE <see cref="SidebarPillState"/>, so anything that wrote the node's opacity (a force-completed
/// flight, a registration pointing at a recycled slot) is re-derived on the row's own epoch and can never stick.</para>
/// </summary>
internal sealed class ClassicSelectionPill : Component
{
    readonly INavRowHost _owner;
    readonly Func<SidebarPillState> _state;
    readonly Prop<float> _opacity;
    readonly SidebarPillLane _lane;
    NodeHandle _self;
    string? _route;

    public ClassicSelectionPill(INavRowHost owner, Func<SidebarPillState> state, SidebarPillLane lane = SidebarPillLane.List)
    {
        _owner = owner;
        _state = state;
        _lane = lane;
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
            _owner.RegisterSelectionPill(route, _self, _lane);
            // A recycled node may inherit an interrupted transform from the route previously bound to the slot; snap
            // to the same visibility the bound channel reads, so the snap can never disagree with it.
            if (recycled) NavigationSelectionMotion.SnapVertical(anim, _self, selected);
        }, DepKey.From(routeHash));

        return new BoxEl
        {
            Width = SidebarGeometry.PillW,
            Height = SidebarGeometry.PillH,
            Margin = new Edges4(state.Indent, state.Top, 0f, 0f),
            Corners = CornerRadius4.All(SidebarGeometry.PillRadius),
            Fill = Tok.AccentDefault,
            Opacity = _opacity,
            // NavigationSelectionMotion expresses WinUI's CenterPoint swap as painted-top coordinates: a top-edge
            // origin is part of the primitive's geometry contract.
            TransformOriginY = 0f,
            HitTestVisible = false,
            OnRealized = h => _self = h,
        };
    }
}

/// <summary>The explicit shimmer shapes (a streaming list has no seed rows to derive a skeleton from). Sized by the
/// same ladders the real row uses, so the shimmer→content swap never changes a section's height. (The rail's pending
/// tiles are not here: they are the media surface's own seed face — <c>Rail.SeedStack</c>.)</summary>
internal static class ClassicSkeletons
{
    /// <summary>One pending row in the row's own ladder: the row's icon column (the art centred in its 32 slot at slot
    /// x 10), the label at slot x 50, bars 140×12 (+ 80×10 at gap 4 in a two-line row), r4. Uniform by design (every
    /// position draws the same row).</summary>
    public static Element Row(SidebarRowShape shape, float heightOverride = float.NaN,
                              float artOverride = float.NaN)
    {
        bool subtitle = shape == SidebarRowShape.EntityTwoLine;
        float height = float.IsNaN(heightOverride) ? SidebarGeometry.HeightOf(shape) : heightOverride;
        float art = float.IsNaN(artOverride) ? SidebarGeometry.ArtOf(shape) : artOverride;
        Element text = subtitle
            ? new BoxEl { Direction = 1, Grow = 1f, Gap = SidebarGeometry.SkeletonGap, Children = [Bar(SidebarGeometry.SkeletonTitleW, SidebarGeometry.SkeletonTitleH), Bar(SidebarGeometry.SkeletonSubtitleW, SidebarGeometry.SkeletonSubtitleH)] }
            : new BoxEl { Direction = 1, Grow = 1f, Children = [Bar(SidebarGeometry.SkeletonTitleW, SidebarGeometry.SkeletonTitleH)] };
        return new BoxEl
        {
            Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = SidebarGeometry.LabelGap,
            Margin = new Edges4(0f, SidebarGeometry.RowMarginY, 0f, SidebarGeometry.RowMarginY),
            Padding = new Edges4(0f, 0f, SidebarGeometry.TrailingPad, 0f),
            Children =
            [
                new BoxEl
                {
                    Width = SidebarGeometry.IconColumn, Height = height, Shrink = 0f,
                    Padding = new Edges4(SidebarGeometry.LeadInset, 0f, 0f, 0f),
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = art, Height = art, Corners = CornerRadius4.All(Sidebar.Cover.Radius(art, false)),
                            Fill = Tok.FillSubtleSecondary,
                        },
                    ],
                },
                text,
            ],
        };
    }

    static Element Bar(float w, float h) => new BoxEl
    {
        Width = w, Height = h, Corners = CornerRadius4.All(SidebarGeometry.SkeletonBarRadius), Fill = Tok.FillSubtleSecondary,
    };
}
