using FluentGpu.Foundation;
using FluentGpu.Dsl;
using Wavee.Features.Detail;

namespace Wavee.Features.Browse;

/// <summary>The overlay masthead's layout reserve — <c>FrameTop</c> + SurfaceDisplay (<see cref="Ui.TitleLarge"/>)
/// line height. A CONSTANT, not a live measure: parked family pages must not re-pad when the overlay fades out on
/// browse → playlist.</summary>
static class BrowseMastheadMetrics
{
    public const float TitleLine = 52f;
    public const float Reserve = Spacing.XXXL + TitleLine;

    /// <summary>Top inset a masthead-family page body uses: overlay reserve plus the gap that used to sit under
    /// the in-flow band.</summary>
    public const float BodyTop = Reserve + Spacing.L;

    public static Edges4 FamilyBodyPad(float bottom)
        => new(Spacing.PageWide, BodyTop, Spacing.PageWide, bottom);

    /// <summary>The offset model's other half (see <c>ContextBand</c>): the overlay masthead PAINTS NOTHING — a fill
    /// would read as a black slab on live Mica — so a family page whose body scrolls under it must cut its content
    /// at the band's lower edge, which is exactly the reserve. The shell's real ground then shows where the crumb
    /// sits instead of the page's H1 sliding through it. Pages whose body pads the reserve INSIDE a ScrollView
    /// (Concerts, the Browse directory) are the ones that need this; a non-scrolling outer shape never bleeds.</summary>
    public const float ClipInset = Reserve;

    /// <summary>The feather at that cut — the same band every detail surface uses, so content dissolves into the
    /// masthead identically everywhere instead of being guillotined by it.</summary>
    public const float ClipFadeBand = DetailVerticalLayout.StickyFadeBand;

    /// <summary>Family body padding for a page that clips under the band: the reserve is a SPACER above the clipped
    /// node (so its ClipBelow engages exactly when content reaches the band, not at rest), leaving only the gutters
    /// and the bottom on the node itself.</summary>
    public static Edges4 FamilyUnderBandPad(float bottom)
        => new(Spacing.PageWide, 0f, Spacing.PageWide, bottom);
}

