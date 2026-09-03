using Xunit;

namespace Wavee.Tests;

// QrPlate is the engine-free half of QrGrid (Features/Auth/QrPlate.cs / QrGrid.cs) — the plate/cell arithmetic
// QrGrid.Render draws with, pulled out so it can be pinned directly instead of only via a capture. Fixes the "80-DIP
// QR paints 111" bug: the old code floored the cell at 3, which for a 29-module (v3) symbol at size=80 rounded UP to
// 111 (Math.Max(3, 80/37)=3 → 3*37=111), 39% over the requested box. (#C)
public class QrGridTests
{
    // v3 (29 modules, total 37) at the requested 80 DIP → cell floors to 2 (80/37 = 2.16) → 37*2 = 74.
    [Fact]
    public void PlateFor_V3At80Dip_Is74()
        => Assert.Equal(74, QrPlate.PlateFor(80f, 29));

    // v4 (33 modules, total 41) at 80 DIP → cell floors to 2 (80/41 = 1.95, still clamped to the 2-DIP floor) → 82.
    // This is SetupLayout.QrPlateBudget's number (SetupLayoutTests.QrPlateBudget_IsTheV4SymbolPaintedAt80Dip).
    [Fact]
    public void PlateFor_V4At80Dip_Is82()
        => Assert.Equal(82, QrPlate.PlateFor(80f, 33));

    // v3 at 120 DIP → cell = 3 (120/37 = 3.24) → 111 — the OLD 80-DIP result, now only reached when 120 is actually
    // asked for.
    [Fact]
    public void PlateFor_V3At120Dip_Is111()
        => Assert.Equal(111, QrPlate.PlateFor(120f, 29));

    // A cell can never fall below 2 DIP (sub-2 modules stop scanning reliably), however small the request — the
    // floor that replaces the old floor-of-3.
    [Theory]
    [InlineData(0f, 21)]
    [InlineData(1f, 25)]
    [InlineData(10f, 33)]
    public void CellFor_NeverBelowTheTwoDipFloor(float size, int modules)
        => Assert.True(QrPlate.CellFor(size, modules) >= 2);

    // Once the request is generous enough that the floor no longer binds (size / total >= 2), the painted plate can
    // never exceed what was asked — the floor only ever pushes UP for a too-small request, never overshoots a
    // reasonable one.
    [Theory]
    [InlineData(74f, 29)]    // size/total == 2 exactly
    [InlineData(80f, 29)]
    [InlineData(82f, 33)]    // size/total == 2 exactly for a v4 symbol (80/41 would bind the floor — see the budget test)
    [InlineData(120f, 29)]
    [InlineData(200f, 37)]
    public void PlateFor_NeverExceedsSize_WhenTheFloorDoesNotBind(float size, int modules)
        => Assert.True(QrPlate.PlateFor(size, modules) <= size,
            $"plate {QrPlate.PlateFor(size, modules)} > size {size} for modules={modules}");
}
