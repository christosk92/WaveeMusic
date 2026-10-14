// ── Themes/Classic/Structure/Rules/Tiers.Browse.cs — the column ladders of Classic's Browse and Search genre grids (themes plan 3.6; T10a2) ──
//
// The three column functions that `Entities\Browse.Rules.cs`'s `BrowseLayout` held (0.2.9 `BrowseTiles.cs:286-358`), moved VERBATIM and out of
// core (plan 3.6: each move PR moves its own view's ladders into `Rules\Tiers.<Family>.cs`). Only Classic's views read them: the directory's
// Genres, Mood and More bands, a category page's tile blocks and the Search page's genre links. Each is a FIXED band or a floor-fit of the grid's own
// measured width — never a floor-divide that flaps across one pixel — so the views call the functions and compare nothing themselves (WVTH013).
//
// Public, because `BrowseLayoutTests` pins the bands (720 / 380 and the two floor-fits).

namespace Wavee.Themes.Classic;

/// <summary>Classic's Browse grid ladders.</summary>
public static class ClassicBrowseTiers
{
    /// <summary>The Genres grid's column bands — fixed bands, never a floor-divide that flaps across one pixel.</summary>
    public static int LinkColumns(float width) => width > 720f ? 3 : width > 380f ? 2 : 1;

    /// <summary>The Mood grid's column floor.</summary>
    public const float BarColMin = 132f;

    /// <summary>The Mood grid — plain floor-fit, NO cap.</summary>
    public static int BarColumns(float width) => Math.Max(1, (int)(width / BarColMin));

    /// <summary>The More grid's column floor.</summary>
    public const float MoreColMin = 168f;

    const int MoreColMax = 4;

    /// <summary>The More grid — floor-fit, capped at four.</summary>
    public static int MoreColumns(float width) => Math.Clamp((int)(width / MoreColMin), 1, MoreColMax);
}
