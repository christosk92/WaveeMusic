// ── Themes/WinUi/Structure/Rules/Tiers.Collection.cs — WinUI's library ladder ───────────────────────────────────────────────────────────────
//
// The library page (Classic's `Pages.Library` picture, which WinUI inherits) folds into its one-column drill-in on a narrow page and splits the navigator
// and the pane from 600 of the page's own width (`LibraryLadders.Layout`, held 24 either way, the content ladder's Small edge as Classic's). Without a
// ladder the core answers "split" at every width, and a Compact window drew the 240 navigator beside a pane that ran off the window's edge.

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;

namespace Wavee.Themes.WinUi;

public static partial class WinUiTiers
{
    /// <summary>The split layout begins here on the page's own width; the hold is 24 both ways (entered below 576, left at 624).</summary>
    public const float LibrarySplitFrom = 600f, LibrarySplitHold = 24f;

    /// <summary>The library's layout ladder (index 0 the one-column drill-in, 1 the split), built here: this runs inside <c>Policy</c>'s initializer, so it reads
    /// no static of this class (constants only).</summary>
    static partial void LaddersCollection(ImmutableArray<NamedLadder>.Builder ladders)
    {
        ladders.Add(new NamedLadder(LibraryLadders.Layout,
            TierLadder.Of(new TierEdge(LibrarySplitFrom, NarrowHold: LibrarySplitHold, WidenHold: LibrarySplitHold))));
        // The library artist reader WinUI draws (Classic's, a stand-in) measures its arms on its own width. With no ladder declared the core answered the
        // wide arm at every width: in the split's ~400 DIP lane the band's one row squeezed the name to nothing and pushed shuffle and ↗ past the edge, and
        // every album head cut its title to a few letters (owner video 2026-10-25). Classic's arms: narrow under 640, medium under the wide band's lane.
        ladders.Add(new NamedLadder(ArtistLadders.Reader, TierLadder.Of(
            new TierEdge(ReaderNarrowBelow, NarrowHold: 0f, WidenHold: ReaderHold),
            new TierEdge(ReaderMediumBelow, NarrowHold: 0f, WidenHold: ReaderHold))));
    }

    /// <summary>The library artist reader's edges (Classic's, whose reader WinUI draws): the narrow arm under 640, the medium arm (no spine, the stacked band)
    /// under the wide band's lane; each edge holds 24 on widening.</summary>
    public const float ReaderNarrowBelow = 640f, ReaderMediumBelow = Artist.ReaderShape.WideBandLaneW, ReaderHold = 24f;
}
