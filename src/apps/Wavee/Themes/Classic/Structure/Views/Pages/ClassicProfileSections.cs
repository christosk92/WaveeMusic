// ── Themes/Classic/Structure/Views/Pages/ClassicProfileSections.cs — Classic's profile page sections (themes plan 6.1, T10c2) ───
//
// From `Entities\Profile.UI.cs` §2-§3: the section headers and shelves, the seeds the derived shimmer reads, the empty / failed / "isn't available" vacancies and the
// card adapters (playlist, artist, person, ranked artist). The section plan, each section's body verdict, the card model and the caps (`ProfileSections`) stay core;
// what a card opens or plays is `ProfileVerbs` / `DetailVerbs`, and its menu and drag are the shared hit adapters.
//
// COVERS (round 2): an avatar / a card cover is the row's already-renderable url (`User.Image`, `Controls.ArtUrl`); a cover-less playlist's `spotify:mosaic:` token
// composes through `Controls.MosaicTiles` + the `Playlist.CoverArt` view.
//
// NAMES: no member here is called Title/Subtitle/Caption/Image/Actions — they would hide `Ui.*` and the Actions type.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's profile page sections.</summary>
internal static class ClassicProfileSections
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

    /// <summary>The band pivot's word for a section (the shorter set; the list page's facet bar reuses the two people words).</summary>
    internal static string PivotLabel(ProfileSection s) => Loc.Get(s switch
    {
        ProfileSection.TopArtists => Strings.Person.Pivot.TopArtists,
        ProfileSection.Playlists => Strings.Person.Pivot.Playlists,
        ProfileSection.RecentArtists => Strings.Person.Pivot.Artists,
        ProfileSection.Following => Strings.Person.Pivot.Following,
        _ => Strings.Person.Pivot.Followers,
    });

    /// <summary>AccentHeader · [a quiet badge chip] · [See all] — the shelf's custom header (the shelf adds its chevrons).</summary>
    static Element SectionHeader(string title, ColorF accent, Action? seeAll, string? chip = null)
    {
        BoxEl head = Controls.AccentHeader(title, accent) with { Shrink = 1f };
        if (seeAll is null && chip is null) return head;
        var kids = new List<Element>(3) { head };
        if (chip is { Length: > 0 }) kids.Add(Controls.Chip(chip));
        if (seeAll is not null) kids.Add(HyperlinkButton.Create(Loc.Get(Strings.Home.SeeAll), seeAll) with { Shrink = 0f });
        return new BoxEl { Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    // ══ the shelves ══

    static SurfaceShape CardShape => Shape.Shelf(captionLines: 1);
    static readonly Func<float, float> s_cardHeight = static w => SurfaceGeometry.ShelfHeight(w, 1f, 1, false);
    static readonly Func<float, float> s_rankedHeight = static w => SurfaceGeometry.ShelfHeight(w - ClassicProfileRules.RankColumn, 1f, 1, false);
    static readonly Func<ProfileCard, int, string> s_keyOf = static (c, _) => c.Key;
    static readonly Action<ProfileCard, int> s_open = static (c, _) => ProfileVerbs.Open(c);
    static readonly Func<ProfileCard, int, float, Element> s_playlistCard = static (c, _, w) => PlaylistCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_artistCard = static (c, _, w) => ArtistCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_personCard = static (c, _, w) => PersonCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_rankedCard = static (c, _, w) => RankedArtistCard(c, w);

    /// <summary>A present section with rows. Every shelf of surface cards passes <c>onInvoke</c> (the slot owns click + focus: one Tab stop per card). "See all" rides
    /// the two people sections only (the caller passes null elsewhere).</summary>
    internal static Element Shelf(ProfileSection s, ProfileCard[] items, ColorF accent, Action? seeAll, ClassicShelfBand band)
    {
        string title = SectionTitle(s);
        return s switch
        {
            ProfileSection.TopArtists => ShelfOf(items, s_rankedCard,
                SectionHeader(title, accent, null, Loc.Get(Strings.Person.OnlyYou)), s_rankedHeight, band),
            ProfileSection.Playlists => ShelfOf(items, s_playlistCard, SectionHeader(title, accent, seeAll), s_cardHeight, band),
            ProfileSection.RecentArtists => ShelfOf(items, s_artistCard, SectionHeader(title, accent, null), s_cardHeight, band),
            _ => ShelfOf(items, s_personCard, SectionHeader(title, accent, seeAll), s_cardHeight, band),
        };
    }

    /// <summary>A shelf at the tier's fit (TC6: a 164 strip whose third card peeks at Compact, ten columns at Huge), keyed by the fit: a PagedShelf reads it
    /// at mount, so a crossing that changes it remounts the shelf.</summary>
    static Element ShelfOf(ProfileCard[] items, Func<ProfileCard, int, float, Element> cardAt, Element header,
                           Func<float, float> cardHeight, ClassicShelfBand band) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Children = [PagedShelf.Create(items, cardAt, onInvoke: s_open, cardHeight: cardHeight, header: header,
            measured: false, keyOf: s_keyOf, lift: ShelfLift.None,   // the shared card hovers fill-only: no lift halo
            pager: band.Pager ? ShelfPager.Chevrons : ShelfPager.None, minCardW: band.MinCardW, maxCardW: band.MaxCardW, gap: band.Gap,
            fixedCardW: band.FixedCardW, maxColumns: band.MaxColumns) with { Key = ClassicArtistSections.ShelfKey(band) }],
    };

    /// <summary>The seed row's fit: the strip's fixed card (as many as start inside the width, the next one peeking), else the shelf's own fit capped at the
    /// tier's columns.</summary>
    static (int PerPage, float CardW) SeedFit(float width, in ClassicShelfBand band)
    {
        if (band.FixedCardW > 0f) return (Math.Max(1, (int)MathF.Ceiling(width / band.FixedCardW)), band.FixedCardW);
        var (perPage, cardW) = FillRowVirtualLayout.Fit(width, band.MinCardW, band.MaxCardW, band.Gap);
        if (band.MaxColumns <= 0 || perPage <= band.MaxColumns) return (perPage, cardW);
        int cols = band.MaxColumns;
        return (cols, MathF.Max(band.MinCardW, (width - (cols - 1) * band.Gap) / cols));
    }

    /// <summary>A section's shimmer SOURCE at the shelf's real fit: the real header, then the fitted count of SEED surfaces (a PagedShelf yields no rows against an
    /// unmeasured viewport — Browse's must-not-simplify 11).</summary>
    internal static Element SeedShelf(ProfileSection s, ColorF accent, float width, ClassicShelfBand band)
    {
        bool ranked = s == ProfileSection.TopArtists;
        var (perPage, cardW) = SeedFit(width, band);
        var cells = new Element[perPage];
        for (int i = 0; i < perPage; i++)
        {
            Element seed = Controls.Surface(Controls.CardData.Seed, CardShape, ranked ? cardW - ClassicProfileRules.RankColumn : cardW);
            cells[i] = ranked
                ? new BoxEl
                {
                    Direction = 0, Width = cardW, Shrink = 0f,
                    Children = [new BoxEl { Width = ClassicProfileRules.RankColumn, Shrink = 0f }, seed],
                }
                : seed;
        }
        return new BoxEl
        {
            Direction = 1, Gap = ClassicProfileRules.ShelfHeaderGap, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinHeight = ClassicProfileRules.ShelfChevron,
                    Children = [SectionHeader(SectionTitle(s), accent, null)],
                },
                new BoxEl { Direction = 0, Gap = band.Gap, MinWidth = 0f, ClipToBounds = true, Children = cells },
            ],
        };
    }

    // ══ the states ══

    /// <summary>Public playlists, answered and empty (the only section kept when empty — the page plans the rest away): your own copy offers the way out, another
    /// person's just says so.</summary>
    internal static Element EmptyShelf(bool own) => own
        ? Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
              title: Loc.Get(Strings.Person.Empty.PlaylistsOwn), subtitle: Loc.Get(Strings.Person.Empty.PlaylistsOwnHint))
        : Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
              title: Loc.Get(Strings.Person.Empty.Playlists), subtitle: "");

    /// <summary>A failed section: its header over a compact error vacancy WITH its own Retry.</summary>
    internal static Element SectionError(ProfileSection s, ColorF accent, Action retry)
        => ClassicArtistSections.SectionBlock(SectionTitle(s),
            Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry), accent);

    /// <summary>The 404 face (a private / closed account): the page keeps its frame, no Retry — nothing will answer.</summary>
    internal static Element Unavailable()
        => Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Person.Empty.Unavailable),
            subtitle: Loc.Get(Strings.Person.Empty.UnavailableHint));

    // ══ the cards ══

    static Element CardSubtitle(string text)
        => R.Type.TrackMeta(text) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>Square cover (a cover-less playlist composes its 2×2 mosaic), title, "N followers" when non-zero.</summary>
    static Element PlaylistCard(ProfileCard c, float w)
    {
        var pl = new Playlist(c.Slot);
        string title = ProfileVerbs.TitleOf(pl);
        float inner = SurfaceParts.InnerOf(w);
        string? url = Controls.ArtUrl(pl.ImageId);
        // A server mosaic token paints its 2×2; a cover-less playlist (your own, which the library holds with no image)
        // composes its member-track mosaic exactly like the sidebar and the playlist page (Playlist.CoverArt).
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

    /// <summary>A follow-list entry: an artist is the artist card; a person is a circle (photo, else initials on their own colour), "Profile", no play, no drag
    /// (Search.Drags refuses users), no menu.</summary>
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

    /// <summary>Own top artists: the rank as a StatHero numeral in a 36-DIP column at the cover's top, the circular artist card beside it (its extent is
    /// ShelfHeight(w − 36), which <c>s_rankedHeight</c> hands the shelf).</summary>
    static Element RankedArtistCard(ProfileCard c, float w) => new BoxEl
    {
        Direction = 0, Width = w, Shrink = 0f, AlignItems = FlexAlign.Start,
        Children =
        [
            new BoxEl
            {
                Width = ClassicProfileRules.RankColumn, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.End, HitTestVisible = false,
                Padding = new Edges4(0f, SurfaceGeometry.ShelfGutterTop + SurfaceGeometry.ShelfPlatePad, Spacing.XS, 0f),
                Children = [R.Type.StatHero(ProfileStats.Numeral(c.Rank, CultureInfo.CurrentCulture), null) with { Color = Tok.TextSecondary }],
            },
            ArtistCard(c, w - ClassicProfileRules.RankColumn),
        ],
    };
}
