// ── Themes/WinUi/Structure/Views/Nav/WinUiNavRowParts.cs — the selection pill, the pending row, the leading art and the head text (P6 W2b) ───
//
// Derived from Classic's `ClassicNavRowParts`. The pill and the pending row read the row ladder (`WinUiNavGeometry`); the pane's selection
// FLIGHT (the 600 ms route travel, its registrations by route) stays core (`INavRowHost.RegisterSelectionPill`); this is the picture it moves.
// `WinUiNavArt` is the leading visual (a 16 glyph or a 16 r2 thumbnail, the board's two forms) and `WinUiNavText` the section titles.

using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>
/// One NavigationViewItem SelectionIndicator (3×16, radius 1.5, accent) permanently owned by every selectable realized
/// row. The pane runs the paired 600 ms route flight on the TRANSFORM; a recycle is never navigation, so it snaps.
///
/// <para><b>Opacity is BOUND, never authored</b> (#22/#23 — two pills lit at once): one thunk for the node's whole life
/// reads the slot's LIVE <see cref="SidebarPillState"/>, so anything that wrote the node's opacity (a force-completed
/// flight, a registration pointing at a recycled slot) is re-derived on the row's own epoch and can never stick.</para>
/// </summary>
internal sealed class WinUiSelectionPill : Component
{
    readonly INavRowHost _owner;
    readonly Func<SidebarPillState> _state;
    readonly Prop<float> _opacity;
    readonly SidebarPillLane _lane;
    NodeHandle _self;
    string? _route;

    public WinUiSelectionPill(INavRowHost owner, Func<SidebarPillState> state, SidebarPillLane lane = SidebarPillLane.List)
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
            Width = WinUiNavGeometry.PillW,
            Height = WinUiNavGeometry.PillH,
            Margin = new Edges4(state.Indent, state.Top, 0f, 0f),
            Corners = CornerRadius4.All(WinUiNavGeometry.PillRadius),
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

/// <summary>The explicit shimmer shapes (a streaming list has no seed rows to derive a skeleton from). Sized by the same ladder the real row uses, so the
/// shimmer-to-content swap never changes a section's height: the 16 thumbnail at slot x 12, the label at slot x 44, one 140 x 12 bar.</summary>
internal static class WinUiSkeletons
{
    public static Element Row(SidebarRowShape shape) => new BoxEl
    {
        Direction = 0, Height = WinUiNavGeometry.HeightOf(shape), AlignItems = FlexAlign.Center, Gap = WinUiNavGeometry.LabelGap,
        Margin = new Edges4(0f, WinUiNavGeometry.RowMarginY, 0f, WinUiNavGeometry.RowMarginY),
        Padding = new Edges4(WinUiNavGeometry.LeadInset, 0f, WinUiNavGeometry.TrailingPad, 0f),
        Children =
        [
            new BoxEl
            {
                Width = WinUiNavGeometry.ArtSlot, Height = WinUiNavGeometry.ArtSlot, Shrink = 0f,
                Corners = CornerRadius4.All(WinUiNavGeometry.ThumbRadius), Fill = Tok.FillSubtleSecondary,
            },
            new BoxEl
            {
                Width = WinUiNavGeometry.SkeletonTitleW, Height = WinUiNavGeometry.SkeletonTitleH,
                Corners = CornerRadius4.All(WinUiNavGeometry.SkeletonBarRadius), Fill = Tok.FillSubtleSecondary,
            },
        ],
    };
}

/// <summary>The leading visual of an entry row: a 16 glyph for a route, Liked Songs and a folder, a 16 r2 thumbnail (a 2x2 mosaic for a cover-less
/// playlist, a circle for an artist) for everything else.</summary>
internal static class WinUiNavArt
{
    /// <summary>Does the entry wear a glyph (a route pin, Liked Songs, a folder) rather than a thumbnail?</summary>
    public static bool IsGlyph(in SidebarLibraryEntry e) => e.Kind is SidebarEntryKind.Folder or SidebarEntryKind.AppRoute || IsLiked(in e);

    /// <summary>The two spellings of the liked collection: the "liked" route pin id, or the collection uri.</summary>
    static bool IsLiked(in SidebarLibraryEntry e)
        => string.Equals(e.Id, SidebarCatalogue.LikedRoute, StringComparison.Ordinal)
           || (e.Uri is { Length: > 0 } uri && EntityUri.IsLikedCollection(uri));

    /// <summary>The glyph of a glyph entry: the folder mark, or the destination's own (Liked Songs: the heart).</summary>
    public static string GlyphOf(INavRowHost host, in SidebarLibraryEntry e)
        => e.Kind == SidebarEntryKind.Folder ? Icons.Folder : host.DestOf(IsLiked(in e) ? SidebarCatalogue.LikedRoute : e.Id).Glyph;

    /// <summary>The thumbnail: the image, else a 2x2 mosaic of the first four tiles (or the first one), else the neutral tile.</summary>
    public static Element Thumb(in SidebarLibraryEntry e)
    {
        float size = WinUiNavGeometry.ArtSlot;
        float radius = e.Circular || e.Kind == SidebarEntryKind.Artist ? size * 0.5f : WinUiNavGeometry.ThumbRadius;
        string? url = Controls.ArtUrl(e.Cover);
        if (url is null && e.MosaicTiles is { Count: > 0 } tiles)
        {
            if (tiles.Count >= 4
                && Controls.ArtUrl(tiles[0]) is { } t0 && Controls.ArtUrl(tiles[1]) is { } t1
                && Controls.ArtUrl(tiles[2]) is { } t2 && Controls.ArtUrl(tiles[3]) is { } t3)
                return Controls.Mosaic(new[] { t0, t1, t2, t3 }, size, size, radius);
            url = Controls.ArtUrl(tiles[0]);
        }
        return Controls.Artwork(url, size, size, radius, decodePx: WinUiNavGeometry.ThumbDecodePx);
    }
}

/// <summary>The section titles: the core's, except that WinUI calls the Collections section "Library" (the board).</summary>
internal static class WinUiNavText
{
    public static string TitleOf(SidebarSection section)
        => section.Kind == SidebarSectionKind.Collections ? Loc.Get(Strings.Theme.Winui.Nav.Library) : Sidebar.PaneText.TitleOf(section);
}
