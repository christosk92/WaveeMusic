using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Core.Sidebar;

namespace Wavee;

// F.1.4 — the sidebar's shimmer placeholders, for `Skel.Region(shimmerSource: …)`.
//
// WHY EXPLICIT SHAPES AT ALL. Skel.Region's preferred overload DERIVES the shimmer from the real content rendered against
// the loadable's seed — but a streaming LIST is exactly the documented exception: `Flow.For` over an empty seed yields
// zero rows, so there is nothing to derive from. That is why the landed sidebar hands Skel.Region an explicit
// shimmerSource, and why these shapes exist.
//
// The Cozy+subtitle Row() below is byte-identical to WaveeSidebar's landed PlaylistSkeletonRow (44 DIP, padding
// (9,0,8,0), gap 10, a 32×32 r6 tile, and 140×12 / 80×10 r4 bars) so relocating that call site changes no pixel. The
// bar colour is SkeletonStyle.Default's Tok.FillSubtleSecondary — the same fill the deriver would have produced.

static class SidebarSkeletons
{
    /// <summary>One pending list row, sized by the same density ladder the real row uses
    /// (<see cref="SidebarEntityRow.HeightFor"/>), so the shimmer→content swap never changes the section's height.</summary>
    /// <param name="index">Row position. Only read when <paramref name="jitter"/> is set.</param>
    /// <param name="jitter">Vary the title-bar width deterministically per <paramref name="index"/> so a tall stack does
    /// not read as a mechanical grid. OFF by default — the landed Classic playlist shimmer is uniform and stays so.</param>
    public static Element Row(int index = 0, SidebarDensity density = SidebarDensity.Cozy, bool subtitle = true,
                             bool jitter = false, float heightOverride = float.NaN, float artOverride = float.NaN)
    {
        float height = float.IsNaN(heightOverride) ? SidebarEntityRow.HeightFor(density, subtitle) : heightOverride;
        float art = float.IsNaN(artOverride) ? SidebarRowMetrics.ArtFor(density) : artOverride;
        float titleW = jitter ? 108f + (index % 4) * 16f : 140f;

        Element text = subtitle
            ? new BoxEl
            {
                Direction = 1, Grow = 1f, Gap = 4f,
                Children = [Bar(titleW, 12f), Bar(80f, 10f)],
            }
            : new BoxEl { Direction = 1, Grow = 1f, Children = [Bar(titleW, 12f)] };

        return new BoxEl
        {
            Direction = 0, Height = height, AlignItems = FlexAlign.Center, Gap = 10f,
            Padding = new Edges4(9f, 0f, 8f, 0f),
            Children =
            [
                new BoxEl { Width = art, Height = art, Corners = CornerRadius4.All(SidebarCover.Radius(art, false)), Fill = Tok.FillSubtleSecondary },
                text,
            ],
        };
    }

    /// <summary>A stack of <paramref name="count"/> pending rows at the <c>SkeletonStyle.Default</c> row gap — the whole
    /// <c>shimmerSource</c> for a list section in one call.</summary>
    public static Element Rows(int count, SidebarDensity density = SidebarDensity.Cozy, bool subtitle = true,
                              bool jitter = false)
    {
        var kids = new Element[count < 0 ? 0 : count];
        for (int i = 0; i < kids.Length; i++) kids[i] = Row(i, density, subtitle, jitter);
        return new BoxEl { Direction = 1, Gap = SkeletonStyle.Default.RowGap, Children = kids };
    }

    /// <summary>One pending rail tile. Delegates to the shared tile so the rail's pending and ready states are the same
    /// box.</summary>
    public static Element Rail(int index = 0)
    {
        _ = index;   // position-independent by design: a rail of identical 40-DIP squares is the correct pending look
        return SidebarRailItem.Skeleton();
    }

    /// <summary>A stack of <paramref name="count"/> pending rail tiles at the rail's 6-DIP gap.</summary>
    public static Element RailStack(int count)
    {
        var kids = new Element[count < 0 ? 0 : count];
        for (int i = 0; i < kids.Length; i++) kids[i] = Rail(i);
        return new BoxEl { Direction = 1, Gap = 6f, AlignItems = FlexAlign.Center, Children = kids };
    }

    /// <summary>One pending GRID cell: a square cover placeholder plus a single caption bar (grid cells show no
    /// subtitle).</summary>
    public static Element GridCell(int index = 0, float edge = SidebarCover.S48)
    {
        _ = index;
        return new BoxEl
        {
            Direction = 1, Gap = 6f,
            Children =
            [
                new BoxEl { Width = edge, Height = edge, Corners = CornerRadius4.All(SidebarCover.Radius(edge, false)), Fill = Tok.FillSubtleSecondary },
                Bar(edge - 8f, 10f),
            ],
        };
    }

    static Element Bar(float w, float h) => new BoxEl
    {
        Width = w, Height = h, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary,
    };
}
