using FluentGpu.Foundation;
using FluentGpu.Dsl;

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
}

