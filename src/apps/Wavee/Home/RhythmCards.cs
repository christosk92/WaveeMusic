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

    // ── the DJ card (AI DJ, plan-dj §1.1, §2.8) ─────────────────────────────────────────────────────────────────────────────
    //
    // The DJ's Home card states its own look (`home.card.*`): a background colour, three "jellyfish" colours (the official card animates a
    // jellyfish in them over the background) and the colour of its words. A theme's lead draws the DJ as that colour field with the card's words
    // over it, in its own type and arrangement; these are only the colours.

    /// <summary>Does the card draw as the DJ (the DJ card, with the colours its answer stated)?</summary>
    public static bool DrawsDj(in HomeCard c) => c.IsDj && !c.DjColors.IsEmpty;

    /// <summary>The DJ field: the jellyfish's glow over the DJ's background colour (the official card's jellyfish, still): its top colour at the
    /// <paramref name="shape"/>'s origin, its middle colour fainter, the background at the far end. Each colour is laid over the background by its
    /// own alpha and at most <see cref="DjGlow"/> of the way (the stops are opaque: what shows through is the DJ's blue, never the page's).</summary>
    public static GradientSpec DjWash(DjCardColors k, GradientShape shape, float angleDeg)
    {
        ColorF back = DjBack(k);
        return new GradientSpec(shape, angleDeg,
            [new GradientStop(0f, Over(back, k.Top, DjGlow)), new GradientStop(0.5f, Over(back, k.Mid, DjGlow * 0.5f)), new GradientStop(1f, Over(back, k.Bottom, DjGlow * 0.25f))]);
    }

    /// <summary>How far the jellyfish colours reach over the background at their strongest.</summary>
    const float DjGlow = 0.6f;

    /// <summary>The DJ field's base colour (its background; the middle jellyfish colour when it stated none).</summary>
    public static ColorF DjBack(DjCardColors k) => ColorScience.ToColor(k.Background != 0 ? k.Background | 0xFF000000u : k.Mid | 0xFF000000u);

    /// <summary>The colour of the DJ card's words: its stated text colour, else the base's own readable ink.</summary>
    public static ColorF DjInk(DjCardColors k)
    {
        if (k.Text != 0) return ColorScience.ToColor(k.Text);
        ColorF b = DjBack(k);
        return 0.2126f * b.R + 0.7152f * b.G + 0.0722f * b.B < 0.5f ? ColorF.FromRgba(255, 255, 255) : ColorF.FromRgba(0, 0, 0);
    }

    static ColorF Over(ColorF back, uint argb, float reach) => argb == 0 ? back : ColorF.Lerp(back, ColorScience.ToColor(argb | 0xFF000000u), (argb >> 24) / 255f * reach);
}
