using System;

namespace Wavee;

/// <summary>Pure QR-plate sizing arithmetic, split out of <see cref="QrGrid"/> — <c>QrGrid</c> is a
/// <c>Component</c> that draws with engine <c>BoxEl</c>/<c>ColorF</c> types, so it cannot be source-included into
/// Wavee.Tests (Wavee.Tests.csproj: engine-free pure files only); this file has no engine dependency, so it can be
/// (see <c>Compile Include="..\Wavee\Features\Auth\QrPlate.cs"</c>) and its numbers pinned directly by
/// <c>QrGridTests</c>.
///
/// <para>ISO/IEC 18004 mandates a 4-module quiet zone on every edge, so a symbol of <c>modules</c> needs
/// <c>modules + 8</c> total cells across. The requested <paramref name="size"/> (DIP) is only ever a HINT: the cell
/// must be a whole number of pixels or the grid blurs and stops scanning, so the plate is <c>cell * total</c>, never
/// exactly <paramref name="size"/>. The floor is 2, not 3 — the QR component (`QrGrid.cs`) used to floor at 3, which
/// made an 80-DIP request for a 29-module (v3) symbol round UP to 111 DIP (`Math.Max(3, 80/37) = 3` → `3*37 = 111`,
/// 39% over budget); a 2-DIP cell is still 3-4 physical px at 150-200% scaling and scans fine, and it lets a small
/// request actually shrink toward what was asked instead of overshooting by a third.</para></summary>
internal static class QrPlate
{
    public static int CellFor(float size, int modules) => Math.Max(2, (int)(size / (modules + 8)));

    public static int PlateFor(float size, int modules) => (modules + 8) * CellFor(size, modules);
}
