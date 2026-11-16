// ── Home/RhythmCards.cs: the shared, theme-free pieces of the Home rhythm templates (solution A) ─────────────────────────
//
// LeadGrid, CompactGrid, ArtistCircles and CappedShelf are drawn by each theme in its own voice (`*HomeZones.Rhythm.cs`); what
// they share is only this: one card's surface data (the same open, play, drag and menu every Home card has) and the counting a
// capped grid does (it shows at most columns x rows cards; GridFill lays them out with no empty slot).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.HomeUi;

namespace Wavee;

public static class RhythmCards
{
    /// <summary>The media surface's data for a Home card in a rhythm template: its own open, play, drag and menu (the menu only where an
    /// overlay can host it), its second line as the theme built it.</summary>
    public static Controls.CardData Data(in HomeCard c, Element? second, IOverlayService? host, bool circular)
    {
        var cc = c;
        var menu = HomeCardNav.MenuOf(in c);
        bool hasMenu = menu is not null && host is not null && !Controls.IsNullOverlay(host);
        return new Controls.CardData(c.Uri, c.Title, second, c.ImageUrl, () => HomeCardNav.Open(in cc), () => HomeCardNav.Play(in cc),
            Circular: circular, Drag: HomeCardNav.DragOf(in cc), ShowMenu: hasMenu) { Menu = hasMenu ? menu : null };
    }

    /// <summary>How many cards a capped grid of <paramref name="rows"/> rows at <paramref name="cols"/> columns shows from
    /// <paramref name="available"/> (GridFill then spreads them evenly over those rows, the last row growing).</summary>
    public static int Shown(int cols, int rows, int available) => Math.Min(Math.Max(0, available), Math.Max(1, cols) * Math.Max(1, rows));

    /// <summary><paramref name="cols"/> equal star tracks.</summary>
    public static TrackSize[] Stars(int cols)
    {
        var tracks = new TrackSize[Math.Max(1, cols)];
        for (int i = 0; i < tracks.Length; i++) tracks[i] = TrackSize.Star();
        return tracks;
    }

    /// <summary>The cards a rhythm template draws: the usable ones, in order (a blank seed card is left out; a feed section has none).</summary>
    public static List<HomeCard> Usable(IReadOnlyList<HomeCard> items)
    {
        var list = new List<HomeCard>(items.Count);
        foreach (var c in items) if (!c.IsBlank) list.Add(c);
        return list;
    }
}
