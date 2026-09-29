// ── Wavee.Tests/CoverShapeTests.cs — a non-square cover is never circular ──────────────────────────────────────────

using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>A card asks for a circle (an artist, a radio station) and gets one only on a SQUARE cover. A wide tile that
/// inherited the flag (the Radio 2-span lead, a 16:9 cover) rounded with half its width and rendered as a pill; the
/// shelf and grid hosts now read this one rule for the radius, the clip and the centred labels.</summary>
public sealed class CoverShapeTests
{
    [Fact]
    public void A_square_cover_asked_to_be_circular_is_circular()
        => Assert.True(Controls.CoverShape.IsCircular(circular: true, aspect: 1f));

    [Fact]
    public void A_sixteen_by_nine_cover_is_never_circular()
        => Assert.False(Controls.CoverShape.IsCircular(circular: true, aspect: 16f / 9f));

    [Fact]
    public void A_540_by_296_cover_is_never_circular()
        => Assert.False(Controls.CoverShape.IsCircular(circular: true, aspect: 540f / 296f));

    [Fact]
    public void A_cover_not_asked_to_be_circular_is_not()
        => Assert.False(Controls.CoverShape.IsCircular(circular: false, aspect: 1f));
}
