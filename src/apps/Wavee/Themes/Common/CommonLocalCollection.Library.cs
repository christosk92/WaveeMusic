// ── Themes/Common/CommonLocalCollection.Library.cs — "On this PC" in the library's own clothes (WinUI and Classic, #240) ──────────
//
// The owner (2026-11-24): "this also just doesnt look like winui style", "these cards dont look like wavee/winui cards". So the WinUI/Classic page
// wears what Wavee's own library wears (W-Library, W-LibraryList) instead of a design of its own:
//
//   the cards and rows   THE house media surface (`Controls.Surface`): the shelf card every Home and library shelf shows (the plate, the hover,
//                        the play FAB), and the media row at the library row's 40 art over a 56 plate. A record without art gets the surface's own
//                        placeholder (`Controls.Artwork`), as the library shows it, never a tile of its own.
//   the toolbar          the library's: the sort as a DropDownButton over a radio MenuFlyout ("Recently added ⌄"), the Filter box, and the
//                        structure's view pair (`StyleKeys.LibraryControls`' View: WinUI's grid | list cells and its "⋯" of views and sizes). A
//                        structure that registers no library controls (Classic) gets the same two cells drawn here.
//   the views            the library's codes (0 compact list, 1 list, 2 compact grid, 3 grid; sizes small, medium, large), remembered by the core.
//
// Zune keeps its own cover wall (`AlbumWall`, `ArtistGrid`) and its own cards.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using L = Wavee.Themes.Common.CommonLocalCollectionRules;
using K = Wavee.Themes.Common.CommonLocalCollectionRules;   // inside a Component, where `L` is the component's own method
using P = Wavee.Strings.LocalFiles.Page;

namespace Wavee.Themes.Common;

internal static partial class CommonLocalCollectionParts
{
    // ── the house media surface ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A surface's uri for an album or an artist of the files' tags (no Spotify entity: nothing flies by it, nothing plays by it).</summary>
    static string AlbumUri(string key) => "wavee:local:album:" + key;
    static string ArtistUri(string key) => "wavee:local:artist:" + key;

    /// <summary>A surface's second line, as the house adapters write it (one secondary caption line).</summary>
    static TextEl Second(string text) => R.Type.TrackMeta(text) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>An album's cover for the surface (<c>CardData.CoverOverride</c>): the house artwork (its placeholder when the files carry no art),
    /// tagged with the album's flight key (<see cref="LocalOpened.AlbumTile"/>: it flies into the header of the album's folder and home again; a
    /// placeholder flies whole), and on a card (<paramref name="card"/>) the "New" tag and the quality pill. A row's cover is <paramref name="edge"/>
    /// square; a card's fills the box its structure gives it (WinUI's is flush with the cell, Classic's sits in the plate's padding), the house
    /// no-art note centred on it at the card's <paramref name="edge"/>.</summary>
    static Element AlbumCover(LocalAlbumCard a, float edge, bool card, bool pill)
    {
        string flight = LocalOpened.AlbumTile(a.Key);
        string? connect = a.Cover is null ? null : flight;
        if (!card)
            return new BoxEl
            {
                Width = edge, Height = edge, Shrink = 0f, MorphId = a.Cover is null ? Design.MorphKeys.Whole(flight) : null,
                Children = [Controls.Artwork(a.Cover, edge, edge, Radii.Control, connect: connect)],
            };
        var kids = CardArt(a.Cover, edge, Radii.Card, connect);
        if (a.IsNew) kids.Add(NewTag());
        if (pill && a.Quality.Length > 0) kids.Add(Pill(a.Quality));
        return new BoxEl { ZStack = true, MorphId = a.Cover is null ? Design.MorphKeys.Whole(flight) : null, Children = kids.ToArray() };
    }

    /// <summary>A card cover's layers: the house artwork filling the cover box, and over a cover the files gave none the house no-art note, centred
    /// at the card's <paramref name="edge"/> (<see cref="Controls.NoArtMark(float, float)"/>).</summary>
    static List<Element> CardArt(string? cover, float edge, float corners, string? connect = null)
    {
        var kids = new List<Element>(4) { Controls.Artwork(cover, float.NaN, float.NaN, corners, decodePx: Controls.ShelfDecodePx, connect: connect) };
        if (cover is null && Controls.NoArtMark(edge, edge) is { } mark)
            kids.Add(new BoxEl { AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, HitTestVisible = false, Children = [mark] });
        return kids;
    }

    static Controls.CardData AlbumData(LocalAlbumCard a, LocalCollectionVerbs v, Element? second, Element cover)
    {
        string key = a.Key;
        return new Controls.CardData(AlbumUri(key), a.Title, second, a.Cover, () => v.OpenAlbum(key), () => v.PlayAlbum(key), ShowMenu: false)
        {
            CoverOverride = cover,
        };
    }

    static string AlbumLine(LocalAlbumCard a, bool year)
        => year && a.Year > 0 ? a.Artist + " · " + a.Year.ToString(CultureInfo.InvariantCulture) : a.Artist;

    /// <summary>Home's shelf card for an album: the house shelf card (Home's own) at <paramref name="w"/>, its cover flush with the shelf's line.</summary>
    static Element ShelfAlbum(LocalAlbumCard a, float w, LocalCollectionVerbs v, bool pill)
        => Controls.Surface(AlbumData(a, v, Second(a.Artist), AlbumCover(a, w, card: true, pill)), Shape.Shelf(1) with { Flush = true }, w) with { Key = "lc-album:" + a.Key };

    /// <summary>Home's shelf card for a song with no album tag: the house shelf card; it plays the song (and shows it playing: its uri is the file's).</summary>
    static Element ShelfSong(LocalSongCard s, float w, LocalCollectionVerbs v, bool pill)
    {
        long id = s.Id;
        var kids = CardArt(s.Cover, w, Radii.Card);
        if (s.IsNew) kids.Add(NewTag());
        if (pill && s.Quality.Length > 0) kids.Add(Pill(s.Quality));
        var data = new Controls.CardData(LocalLibrary.UriOf(id), s.Title, Second(s.Artist), s.Cover, () => v.PlayFile(id), () => v.PlayFile(id), ShowMenu: false)
        {
            CoverOverride = new BoxEl { ZStack = true, Children = kids.ToArray() },
        };
        return Controls.Surface(data, Shape.Shelf(1) with { Flush = true }, w) with { Key = "lc-song:" + id.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>An artist's surface data: the round house artwork at <paramref name="edge"/> (the no-art note when the files gave no picture), the
    /// name over the counts.</summary>
    static Controls.CardData ArtistData(LocalArtistCard a, LocalCollectionVerbs v, bool second, float edge, bool card)
    {
        string key = a.Key;
        return new Controls.CardData(ArtistUri(key), a.Name, second ? Second(CountsText(a.Albums, a.Tracks)) : null, a.Cover,
            () => v.ShuffleArtist(key), () => v.ShuffleArtist(key), Circular: true, ShowMenu: false)
        {
            CoverOverride = card ? new BoxEl { ZStack = true, Children = CardArt(a.Cover, edge, Radii.Full).ToArray() } : Controls.Artwork(a.Cover, edge, edge, edge / 2f),
        };
    }

    // ── the library's toolbar ───────────────────────────────────────────────────────────────────────────────────────

    static readonly string[] s_noSuggest = [];

    /// <summary>The sort as the library's DropDownButton (W-Library: "Recently added ⌄" over a radio MenuFlyout headed "Sort by"), keyed by the sort in
    /// force so a pick re-labels it.</summary>
    static Element SortButton(string key, string state, string label, IReadOnlyList<MenuFlyoutItem> items, float height)
    {
        var parts = new TemplateParts { [DropDownButton.PartRoot] = b => b with { Height = height } };
        Element button = Embed.Comp(() => new DropDownButton { Label = label, Items = items, Parts = parts }) with { Key = key + ":" + state };
        return new BoxEl { Key = key, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [Controls.Named(button, Loc.Get(P.SortBy))] };
    }

    /// <summary>The toolbar over Albums and Artists: the sort, the Filter box and the view pair on one line (the sort and the pair over the filter at
    /// Compact), on the page gutter.</summary>
    static Element Toolbar(string id, Element sort, LocalCollectionPageProps p, CommonLocalCollectionLayout lay, float g, LibraryControlsStyle? controls)
    {
        float h = lay.ControlH;
        Element filter = AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Library.Filter), text: p.Filter(), queryIcon: Icons.Search, grow: 1f,
            maxFillWidth: L.FilterW, minHeight: h, cornerRadius: Radii.Control);
        Element view = controls is not null ? controls.View(new LibraryViewArgs(p.ListView(), p.ListSize(), h)) : Embed.Comp(new ViewPairProps(p.ListView(), h), static () => new ViewPair());
        Element body = lay.ActionsBeside
            ? new BoxEl
            {
                Direction = 0, Gap = L.ToolbarGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = [sort, Spacer(), new BoxEl { Direction = 0, Width = L.FilterW, Shrink = 1f, MinWidth = 0f, Children = [filter] }, view],
            }
            : new BoxEl
            {
                Direction = 1, Gap = L.ToolbarGap, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = L.ToolbarGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [sort, Spacer(), view] },
                    filter,
                ],
            };
        return Gut("lc-toolbar:" + id, g, body, top: L.ToolbarTop, bottom: L.ToolbarBottom);
    }

    sealed record ViewPairProps(Signal<int> View, float Height);

    /// <summary>The grid | list pair where the structure registers no library controls (Classic: the WinUI layout, W-Library's two cells on one
    /// control plate, the one in force on the subtle fill). It keeps the compactness the persisted code carries.</summary>
    sealed class ViewPair : Component
    {
        public override Element Render()
        {
            var a = UseProps<ViewPairProps>();
            var view = a.View;
            bool grid = view.Value >= 2;
            float cellH = a.Height - 2f * K.ViewStroke;
            return new BoxEl
            {
                Key = "lc-view", Direction = 0, Shrink = 0f, Height = a.Height, Corners = Radii.ControlAll, Padding = Edges4.All(K.ViewStroke),
                BorderWidth = K.ViewStroke, BorderColor = Tok.StrokeControlDefault,
                Children =
                [
                    Cell("lc-view:grid", Icons.ViewGrid, grid, () => view.Value = view.Peek() is 0 or 2 ? 2 : 3, Loc.Get(Strings.Library.View.Grid), cellH),
                    Cell("lc-view:list", Icons.ViewList, !grid, () => view.Value = view.Peek() is 0 or 2 ? 0 : 1, Loc.Get(Strings.Library.View.List), cellH),
                ],
            };
        }

        static Element Cell(string key, string glyph, bool on, Action tap, string name, float height) => Controls.Named(new BoxEl
        {
            Key = key, Width = K.ViewCellW, Height = height, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            Fill = on ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent, HoverFill = on ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, OnClick = tap,
            Children = [Icon(glyph, K.ViewGlyph, on ? Tok.TextPrimary : Tok.TextSecondary)],
        }, name);
    }

    // ── Albums and Artists ──────────────────────────────────────────────────────────────────────────────────────────

    static readonly LocalAlbumSort[] s_albumSorts = [LocalAlbumSort.Added, LocalAlbumSort.Artist, LocalAlbumSort.Title, LocalAlbumSort.Year];
    static readonly LocalArtistSort[] s_artistSorts = [LocalArtistSort.Name, LocalArtistSort.Songs];

    static string ArtistSortWord(LocalArtistSort s) => s == LocalArtistSort.Songs ? Loc.Get(P.SortSongs) : Loc.Get(P.SortName);

    /// <summary>Albums as the library lists them (WinUI/Classic): the toolbar, then the albums as house cards or rows, filtered as typed.</summary>
    public static Element LibraryAlbums(LocalCollectionView v, LocalCollectionPageProps p, CommonLocalCollectionLayout lay, float g, LibraryControlsStyle? controls)
    {
        var sort = p.Sort.Peek();
        var set = p.SetSort;
        var items = new List<MenuFlyoutItem>(s_albumSorts.Length + 1) { MenuFlyoutItem.Header(Loc.Get(P.SortBy)) };
        foreach (var s in s_albumSorts)
        {
            var to = s;
            items.Add(MenuFlyoutItem.RadioItem(SortWord(s), s == sort, () => set(to)));
        }
        Element bar = Toolbar("albums", SortButton("lc-sort", sort.ToString(), SortWord(sort), items, lay.ControlH), p, lay, g, controls);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = [bar, Embed.Comp(new ListProps(L.WallKey, v, false, sort, default, p, lay, g), static () => new ListHost()) with { Key = L.WallKey }],
        };
    }

    /// <summary>Artists as the library lists them (WinUI/Classic): the toolbar (by name, or most songs first), then round house cards or rows.</summary>
    public static Element LibraryArtists(LocalCollectionView v, LocalCollectionPageProps p, CommonLocalCollectionLayout lay, float g, LibraryControlsStyle? controls)
    {
        var sort = p.ArtistSort.Peek();
        var set = p.SetArtistSort;
        var items = new List<MenuFlyoutItem>(s_artistSorts.Length + 1) { MenuFlyoutItem.Header(Loc.Get(P.SortBy)) };
        foreach (var s in s_artistSorts)
        {
            var to = s;
            items.Add(MenuFlyoutItem.RadioItem(ArtistSortWord(s), s == sort, () => set(to)));
        }
        Element bar = Toolbar("artists", SortButton("lc-artist-sort", sort.ToString(), ArtistSortWord(sort), items, lay.ControlH), p, lay, g, controls);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
            Children = [bar, Embed.Comp(new ListProps(L.ArtistsKey, v, true, default, sort, p, lay, g), static () => new ListHost()) with { Key = L.ArtistsKey }],
        };
    }

    sealed record ListProps(string Key, LocalCollectionView View, bool Artists, LocalAlbumSort Sort, LocalArtistSort ArtistSort, LocalCollectionPageProps Page,
        CommonLocalCollectionLayout Lay, float Gutter);

    /// <summary>The list under the toolbar: reads the view, the size and the filter (a change re-renders this list alone), and draws the matching items
    /// as ONE virtual grid of house cards (keyed per item, so a column change glides each card) or ONE virtual list of house rows.</summary>
    sealed class ListHost : Component
    {
        // The artists by most songs, for the view they were sorted from.
        LocalCollectionView? _bySongsFor;
        IReadOnlyList<LocalArtistCard>? _bySongs;

        public override Element Render()
        {
            var lp = UseProps<ListProps>();
            var p = lp.Page;
            int code = Math.Clamp(p.ListView().Value, 0, 3);
            int size = Math.Clamp(p.ListSize().Value, 0, K.GridScale.Length - 1);
            string filter = p.Filter().Value.Trim();
            float w = UseMeasuredWidth(1f).Value;
            bool grid = code >= 2, compact = code is 0 or 2;
            float g = lp.Gutter;
            var lay = lp.Lay;
            var verbs = p.Verbs;

            int count;
            Func<int, string> keyOf;
            Func<int, float, Element> card;
            Func<int, Element> row;
            if (lp.Artists)
            {
                var all = ArtistsBy(lp.View, lp.ArtistSort);
                if (all.Count == 0) return Fill(Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page, Loc.Get(P.NoAlbums), ""));
                var shown = filter.Length == 0 ? all : Where(all, a => Has(a.Name, filter));
                count = shown.Count;
                keyOf = i => "lc-artist:" + shown[i].Key;
                card = (i, cell) => Controls.Surface(ArtistData(shown[i], verbs, !compact, cell, card: true), Shape.Shelf(compact ? 0 : 1) with { Flush = true }, cell);
                row = i => Controls.Surface(ArtistData(shown[i], verbs, !compact, compact ? K.CompactRowArt : K.RowArt, card: false), RowShape(compact));
            }
            else
            {
                var all = lp.View.AlbumsBy(lp.Sort);
                if (all.Count == 0) return Fill(Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page, Loc.Get(P.NoAlbums), ""));
                var shown = filter.Length == 0 ? all : Where(all, a => Has(a.Title, filter) || Has(a.Artist, filter));
                count = shown.Count;
                keyOf = i => "lc-album:" + shown[i].Key;
                card = (i, cell) => Controls.Surface(AlbumData(shown[i], verbs, compact ? null : Second(AlbumLine(shown[i], year: true)), AlbumCover(shown[i], cell, card: true, pill: true)),
                    Shape.Shelf(compact ? 0 : 1) with { Flush = true }, cell);
                row = i => Controls.Surface(AlbumData(shown[i], verbs, compact ? null : Second(AlbumLine(shown[i], year: true)),
                    AlbumCover(shown[i], compact ? K.CompactRowArt : K.RowArt, card: false, pill: false)), RowShape(compact));
            }
            if (count == 0) return Fill(Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Page, Loc.Get(Strings.Library.NoMatch), ""));
            if (w <= 0f) return Fill(new BoxEl());

            string scroll = p.RouteKey + ":" + lp.Key;
            if (grid)
            {
                float gap = lay.WallGap;
                var (cols, cell) = Columns(w, g, (lp.Artists ? lay.ArtistMin : lay.WallMin) * K.GridScale[size], gap);
                float itemH = SurfaceGeometry.ShelfHeight(cell, 1f, compact ? 0 : 1, false, flush: true);
                Element list = Virtual.Grid(count, cols, itemH, gap, i => card(i, cell) with { Layout = K.CardReflow }, keyOf) with { ScrollKey = scroll + ":grid" };
                return Fill(Gut(lp.Key + ":grid", g, list, grow: true));
            }
            // A row's plate hangs its padding left of the gutter, so its art stands on the page's line (the library's rows do the same).
            float rowH = compact ? K.CompactRowH : K.RowH;
            Element rows = Virtual.List(count, rowH, row, keyOf) with { ScrollKey = scroll + ":list" };
            return Fill(Gut(lp.Key + ":list", MathF.Max(0f, g - SurfaceGeometry.RowPad), rows, grow: true));
        }

        IReadOnlyList<LocalArtistCard> ArtistsBy(LocalCollectionView v, LocalArtistSort sort)
        {
            if (sort == LocalArtistSort.Name) return v.Artists;
            if (!ReferenceEquals(_bySongsFor, v) || _bySongs is null)
            {
                var list = new List<LocalArtistCard>(v.Artists);
                list.Sort(static (a, b) => b.Tracks != a.Tracks ? b.Tracks.CompareTo(a.Tracks) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                _bySongs = list;
                _bySongsFor = v;
            }
            return _bySongs;
        }

        static Element Fill(Element child) => new BoxEl { Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, Children = [child] };
    }

    /// <summary>The house media row at the library row's art (40 over a 56 plate; the compact row 32 over 48, the title alone).</summary>
    static SurfaceShape RowShape(bool compact) => Shape.Row(compact ? L.CompactRowArt : L.RowArt) with { MinHeight = compact ? L.CompactRowH : L.RowH };

    static bool Has(string text, string filter) => text.Contains(filter, StringComparison.CurrentCultureIgnoreCase);

    static List<T> Where<T>(IReadOnlyList<T> all, Func<T, bool> keep)
    {
        var list = new List<T>();
        foreach (var x in all) if (keep(x)) list.Add(x);
        return list;
    }
}
