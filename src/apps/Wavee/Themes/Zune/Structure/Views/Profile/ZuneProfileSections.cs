// ── Themes/Zune/Structure/Views/Profile/ZuneProfileSections.cs — Zune's profile page sections (themes plan 6.3; P5 Z7) ──────────────────────
//
// Board Z-Profile: every section opens on the discovery heading row (26/32 light, "only visible to you" 14 after the top artists, the "see all" word at
// the end of the two people sections) over its body:
//
//   top artists   ranked rows in the tier's columns: the 40 light numeral, the round art (56), the name 15 over its "artist" word 12
//   playlists     a shelf of 154 covers 16 apart (the shared surface in Zune's style), a playlist with no public playlists says so under the heading
//   artists       a shelf of round 154 covers
//   people        a shelf of round covers (an artist is the artist card, a person the circle with the initials on their own colour)
//
// The section plan, each section's body verdict, the card model and the caps (`ProfileSections`) stay core; what a card opens or plays is `ProfileVerbs`.
// NAMES: no member here is called Title/Subtitle/Caption/Image/Actions: they would hide `Ui.*` and the Actions type.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using Z = Wavee.Themes.Zune.ZuneProfileRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's profile page sections.</summary>
internal static class ZuneProfileSections
{
    // ══ the section words ══

    internal static string SectionTitle(ProfileSection s) => Loc.Get(s switch
    {
        ProfileSection.TopArtists => Strings.Person.TopArtists,
        ProfileSection.Playlists => Strings.Person.PublicPlaylists,
        ProfileSection.RecentArtists => Strings.Person.RecentArtists,
        ProfileSection.Following => Strings.Person.Following,
        _ => Strings.Person.Followers,
    });

    /// <summary>A section's heading row: the heading, its subtitle ("only visible to you" on the top artists), the "see all" word at the end.</summary>
    static Element Head(ProfileSection s, Action? seeAll)
    {
        string? note = s == ProfileSection.TopArtists ? Loc.Get(Strings.Person.OnlyYou) : null;
        Element? tools = seeAll is null
            ? null
            : K.Link("zune-profile-seeall:" + ProfileSections.Key(s), Loc.Get(Strings.Home.SeeAll), seeAll);
        return K.Heading(SectionTitle(s), note, tools);
    }

    // ══ the shelves ══

    static SurfaceShape CardShape => global::Wavee.Shape.Shelf(captionLines: 1);
    static readonly Func<float, float> s_cardHeight = static w => SurfaceGeometry.ShelfHeight(w, 1f, 1, false);
    static readonly Func<ProfileCard, int, string> s_keyOf = static (c, _) => c.Key;
    static readonly Action<ProfileCard, int> s_open = static (c, _) => ProfileVerbs.Open(c);
    static readonly Func<ProfileCard, int, float, Element> s_playlistCard = static (c, _, w) => PlaylistCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_artistCard = static (c, _, w) => ArtistCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_personCard = static (c, _, w) => PersonCard(c, w);

    /// <summary>A present section with rows. "See all" rides the two people sections only (the caller passes null elsewhere).</summary>
    internal static Element Shelf(ProfileSection s, ProfileCard[] items, Action? seeAll, ZuneProfileLayout lay) => s switch
    {
        ProfileSection.TopArtists => K.Section(Head(s, null), TopArtists(items, lay)),
        ProfileSection.Playlists => ShelfOf(items, s_playlistCard, Head(s, seeAll)),
        ProfileSection.RecentArtists => ShelfOf(items, s_artistCard, Head(s, null)),
        _ => ShelfOf(items, s_personCard, Head(s, seeAll)),
    };

    /// <summary>A shelf of cards. The card band is a LIVE prop of the shelf, and it is the same at every tier here, so a crossing keeps the shelf and its page.</summary>
    static Element ShelfOf(ProfileCard[] items, Func<ProfileCard, int, float, Element> cardAt, Element header) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Children =
        [
            PagedShelf.Create(items, cardAt, onInvoke: s_open, cardHeight: s_cardHeight, header: header, keyOf: s_keyOf,
                pager: ShelfPager.Chevrons, minCardW: H.ShelfCardW, maxCardW: H.ShelfCardW, fixedCardW: H.ShelfCardW, gap: H.ShelfGap,
                headerGap: H.HeadGap, snap: ShelfSnap.Page, edgeFade: HomeModuleLayout.ShelfEdgeFade,
                prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight, lift: ShelfLift.None) with { Key = "zune-profile-shelf" },
        ],
    };

    // ══ top artists: the ranked rows ══

    /// <summary>The ranked artists in the tier's columns: ONE grid whose column cap and floor are live props (the board's five columns of 64 rows).</summary>
    static Element TopArtists(ProfileCard[] items, ZuneProfileLayout lay)
    {
        var cells = new Element[items.Length];
        for (int i = 0; i < cells.Length; i++) cells[i] = TopRow(items[i], lay);
        return new GridEl
        {
            Key = "zune-profile-top", MinColWidth = lay.TopMinCol, MaxColumns = lay.TopCols, ColGap = lay.TopColGap, RowGap = lay.TopRowGap,
            Children = cells,
        };
    }

    /// <summary>The rank as the light numeral, the round art, the artist's name over its "artist" word. The whole row opens the artist.</summary>
    static Element TopRow(ProfileCard c, ZuneProfileLayout lay)
    {
        var ar = new Artist(c.Slot);
        string? url = Controls.ArtUrl(ar.ImageId);
        var card = c;
        float art = lay.TopArt;
        return new BoxEl
        {
            Key = Z.TopPrefix + c.Key, Direction = 0, Gap = Z.TopGap, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = lay.TopRowH,
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = () => ProfileVerbs.Open(card),
            Children =
            [
                new BoxEl
                {
                    Width = lay.RankW, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Start, HitTestVisible = false,
                    Children = [R.Type.StatHero(ProfileStats.Numeral(c.Rank, System.Globalization.CultureInfo.CurrentCulture), null) with { Color = Tok.TextTertiary }],
                },
                new BoxEl
                {
                    Width = art, Height = art, Shrink = 0f, ClipToBounds = true, Corners = CornerRadius4.All(art / 2f), HitTestVisible = false,
                    Layout = ZuneMotion.TitleScale,
                    Children = [Controls.Artwork(url, art, art, art / 2f)],
                },
                new BoxEl
                {
                    Direction = 1, Gap = Z.TopLineGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, HitTestVisible = false,
                    Children =
                    [
                        R.Type.TrackTitle(ar.Name) with
                        {
                            Color = Tok.TextPrimary, Case = TextCase.AsWritten, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        K.Sub(Loc.Get(Strings.Search.TypeArtist)),
                    ],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    // ══ the seeds ══

    /// <summary>A section's shimmer SOURCE at the shelf's real fit: the real heading, then the fitted count of SEED surfaces (a PagedShelf yields no rows
    /// against an unmeasured viewport).</summary>
    internal static Element SeedShelf(ProfileSection s, float width)
    {
        int count = Math.Clamp((int)MathF.Ceiling(width / (H.ShelfCardW + H.ShelfGap)), 1, Z.SeedCards);
        var cells = new Element[count];
        SurfaceShape shape = CardShape;
        for (int i = 0; i < cells.Length; i++)
        {
            Element seed = SurfaceParts.Seed(in shape, H.ShelfCardW);
            cells[i] = seed.Skel(seed);
        }
        return K.Section(Head(s, null),
            new BoxEl { Direction = 0, Gap = H.ShelfGap, MinWidth = 0f, ClipToBounds = true, Children = cells });
    }

    // ══ the states ══

    /// <summary>Public playlists, answered and empty (the only section kept when empty: the page plans the rest away): your own copy offers the way out,
    /// another person's just says so.</summary>
    internal static Element EmptyShelf(ProfileSection s, bool own) => K.Section(Head(s, null), own
        ? Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
              title: Loc.Get(Strings.Person.Empty.PlaylistsOwn), subtitle: Loc.Get(Strings.Person.Empty.PlaylistsOwnHint))
        : Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
              title: Loc.Get(Strings.Person.Empty.Playlists), subtitle: ""));

    /// <summary>A failed section: its heading over a compact error vacancy WITH its own Retry.</summary>
    internal static Element SectionError(ProfileSection s, Action retry)
        => K.Section(Head(s, null), Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry));

    /// <summary>The 404 face (a private or closed account): the page keeps its frame, no Retry: nothing will answer.</summary>
    internal static Element Unavailable()
        => Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Person.Empty.Unavailable),
            subtitle: Loc.Get(Strings.Person.Empty.UnavailableHint)) with { Key = Z.UnavailableKey };

    // ══ the cards ══

    static Element CardSubtitle(string text)
        => R.Type.TrackMeta(text) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>Square cover (a cover-less playlist composes its 2x2 mosaic), title, "N followers" when non-zero.</summary>
    static Element PlaylistCard(ProfileCard c, float w)
    {
        var pl = new Playlist(c.Slot);
        string title = ProfileVerbs.TitleOf(pl);
        float inner = SurfaceParts.InnerOf(w);
        string? url = Controls.ArtUrl(pl.ImageId);
        // A server mosaic token paints its 2x2; a cover-less playlist (your own, which the library holds with no image) composes its member-track mosaic.
        Element? cover = Controls.MosaicTiles(pl.ImageId).Length == 4 || url is null ? Playlist.CoverArt(pl, inner) : null;
        var hit = new EntityRef(EntityKind.Playlist, c.Slot);
        var card = c;
        var data = new Controls.CardData(pl.Uri.Text, title,
            c.Followers > 0 ? CardSubtitle(Profile.FollowerCount(Strings.Person.Card.FollowersKey, c.Followers)) : null,
            url, () => ProfileVerbs.Open(card), DetailVerbs.PlayContext(pl.Uri), Drag: Search.DragOf(hit),
            CoverOverride: cover)
        {
            Menu = Search.MenuOf(hit),
        };
        return Controls.Surface(data, CardShape, w);
    }

    static Element ArtistCard(ProfileCard c, float w)
    {
        var ar = new Artist(c.Slot);
        var hit = new EntityRef(EntityKind.Artist, c.Slot);
        var card = c;
        var data = new Controls.CardData(ar.Uri.Text, ar.Name, CardSubtitle(Loc.Get(Strings.Search.TypeArtist)),
            Controls.ArtUrl(ar.ImageId), () => ProfileVerbs.Open(card), DetailVerbs.PlayContext(ar.Uri), Circular: true,
            Drag: Search.DragOf(hit))
        {
            Menu = Search.MenuOf(hit),
        };
        return Controls.Surface(data, CardShape, w);
    }

    /// <summary>A follow-list entry: an artist is the artist card; a person is a circle (photo, else initials on their own colour), "Profile", no play, no
    /// drag, no menu.</summary>
    static Element PersonCard(ProfileCard c, float w)
    {
        if (c.Kind == EntityKind.Artist) return ArtistCard(c, w);
        var u = new User(c.Slot);
        string name = u.Name;
        string? url = u.Image;
        float inner = SurfaceParts.InnerOf(w);
        uint argb = ProfileTone.Argb(u.Color);
        ColorF? fill = argb != 0 ? ColorScience.ToColor(argb) : null;
        var card = c;
        var data = new Controls.CardData(u.Uri.Text, name, CardSubtitle(Loc.Get(Strings.Search.TypeUser)), url,
            () => ProfileVerbs.Open(card), null, Circular: true,
            CoverOverride: url is null ? PersonPicture.Create("", inner, displayName: name, fill: fill) : null);
        return Controls.Surface(data, CardShape, w);
    }
}
