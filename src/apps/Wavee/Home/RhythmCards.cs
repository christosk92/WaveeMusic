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
        // A blank seed card is the surface's seed face: the shape's own lines at their line boxes, so the loaded card lands at its height.
        if (c.IsBlank) return Controls.CardData.Seed with { Circular = circular };
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

    /// <summary>A card's node key in a rhythm template: its uri, or a blank seed card's own code (blanks share the empty uri). A rhythm template draws
    /// its zone's every card, a blank one too: a section whose answer is on its way is its template over blank cards, derived flat.</summary>
    public static string KeyOf(in HomeCard c) => c.IsBlank ? "blank" + c.DedupeKey.ToString(System.Globalization.CultureInfo.InvariantCulture) : c.Uri;
}
