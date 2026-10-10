// ── Entities/Profile.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// The profile page's LEAVES: the round-avatar hero (avatar · name · stats · actions) and its collapse into the band, the
// section headers / shelves / seeds / vacancies, and the card adapters (playlist, artist, person, ranked artist). The page
// host that composes them (demand, load faces, band, spy, sections) is Profile.Page.cs; the pure decisions are
// Profile.Rules.cs.
//
// Role: UI   Owner: U2   Template: Artist.UI.cs §4.1 (HeroBanner / HeroIdentity / HeroActions), Artist.Page.cs §2.6 (shelves)
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix U §4 + "Reconciliation, round 2"
//
// TYPE (Design.cs "THE TYPE RAMP" — no new alias, no raw TextEl size): the name is ArtistDisplay / ArtistTitle /
// ArtistCompactTitle by tier; stats and ranks are StatHero (the sanctioned 350 cut); stat captions are Caption at 600
// (Track.Drawer.Stat's idiom); section headers are Controls.AccentHeader; card text is the shared surface's. No eyebrow
// over the name. Sentence case everywhere (the words are the `person` loc group).
//
// COVERS (round 2): an avatar / a card cover is the row's already-renderable url (`User.Image`, `Controls.ArtUrl`); a
// cover-less playlist's `spotify:mosaic:` token composes through `Controls.MosaicTiles` + `Controls.Mosaic`.
//
// NAMES: no member here is called Title/Subtitle/Caption/Image/Actions — they would hide `Ui.*` and the Actions type.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Profile
{
    // ══ 1. THE HERO ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the hero states. <see cref="PlaceholderFor"/> is the shimmer's representative shape — its words are
    /// never shown (the deriver paints bars), its avatar is a plain bone circle.</summary>
    internal readonly record struct HeroText(string Name, string? AvatarUrl, uint PersonArgb, int Followers, int Following,
                                             int Playlists, bool Own, bool ShowFollows, bool Placeholder)
    {
        public static HeroText For(User u, bool own, string? avatarUrl)
            => new(u.Name, avatarUrl, ProfileTone.Argb(u.Color), u.Followers, u.Following, u.PublicPlaylists,
                   own, u.ShowFollows, false);

        public static HeroText PlaceholderFor(bool own)
            => new("Display name", null, 0u, 1_000, 1_000, 100, own, true, true);
    }

    /// <summary>The hero's verbs, built ONCE by the page (a stable instance). Null at the call site = the shimmer arm
    /// (shapes only).</summary>
    internal sealed record HeroActs(Action Share, Func<ContextMenuModel?> Menu, Action<ProfileStat> OpenStat);

    /// <summary>The round-avatar hero and its collapse into the 56-DIP band (Artist.HeroBanner's pinning, verbatim):
    /// horizontal tiers put avatar | copy bottom-aligned; stacked tiers put avatar over copy. Content sits on the hero's
    /// bottom edge inside a fixed tier height, so the sticky collapse is exact.</summary>
    internal static Element HeroBanner(in HeroText t, string uri, float width, in ProfileHeroMetrics m, bool compactCanHit,
                                       HeroActs? acts, Element? band, float floor = ArtistHeroLayout.CompactIdentityHeight)
    {
        float w = MathF.Max(1f, width);
        float collapse = ProfileHeroLayout.CollapseDistance(in m, floor);
        ScrollEffectSpec[] collapseEffects =
        [
            new(ScrollEffect.Parallax(0.0, collapse, 0f, -collapse)),
            new(ScrollEffect.Fade(ArtistHeroLayout.ExpandedFadeStart(collapse), collapse, 1f, 0f)),
        ];

        Element avatar = Avatar(in t, m.Avatar);
        Element copy = Identity(in t, uri, in m, acts);
        Element content = m.Stacked
            ? new BoxEl
            {
                Direction = 1, Gap = ProfileHeroLayout.StackedAvatarGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children = [avatar, copy],
            }
            : new BoxEl
            {
                Direction = 0, Gap = ProfileHeroLayout.AvatarGap, AlignItems = FlexAlign.End, MinWidth = 0f,
                Children = [avatar, copy],
            };
        Element expanded = new BoxEl
        {
            Width = w, Height = m.Height, Direction = 0, Justify = FlexJustify.Center,
            HitTestVisible = !compactCanHit, ScrollEffects = collapseEffects,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Justify = FlexJustify.End, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    MaxWidth = Design.Size.PageMaxW,   // aligns with the magazine's 1600 measure
                    Padding = new Edges4(m.Gutter, m.TopPad, m.Gutter, m.BottomPad),
                    Children = [content],
                },
            ],
        };
        // Pinned at the viewport top and COLLAPSING into the compact band (Artist.HeroBanner's contract): the Leading
        // collapse cuts the expanded presentation at that edge by itself — paint and input — so no ClipToBounds here.
        return new BoxEl
        {
            Direction = 1, Height = m.Height, ZStack = true,
            Children = band is null ? [expanded] : [expanded, band],
        }.Sticky(0f).Collapse(collapse, floor, CollapseAnchor.Leading);
    }

    /// <summary>The photo in a clipped circle (Artist.UI's pick-avatar precedent: <c>Controls.Artwork</c>, so the tile is
    /// the WATCHED placeholder, not PersonPicture's frozen fill); no photo → PersonPicture initials on the person's own
    /// colour.</summary>
    static Element Avatar(in HeroText t, float edge)
    {
        if (t.Placeholder)
            return new BoxEl { Width = edge, Height = edge, Shrink = 0f, Corners = Radii.Circle(edge), Fill = Tok.FillSubtleSecondary };
        if (t.AvatarUrl is { Length: > 0 } url)
            return new BoxEl
            {
                Width = edge, Height = edge, Shrink = 0f, ZStack = true, ClipToBounds = true,
                Corners = Radii.Circle(edge), BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
                Children = [Controls.Artwork(url, edge, edge, edge / 2f, decodePx: ProfileHeroLayout.AvatarDecodePx)],
            };
        ColorF? fill = t.PersonArgb != 0 ? Design.Palette.ToColor(t.PersonArgb) : null;
        return PersonPicture.Create("", edge, displayName: t.Name, fill: fill) with { Shrink = 0f, Shadow = Elevation.Card };
    }

    /// <summary>Name (the tier's Artist alias, line budget from the metrics) · stats · actions. No eyebrow.</summary>
    static BoxEl Identity(in HeroText t, string uri, in ProfileHeroMetrics m, HeroActs? acts)
    {
        TextEl name = (m.Tier switch
        {
            ArtistHeroTier.Wide => Design.Type.ArtistDisplay(t.Name),
            ArtistHeroTier.Medium => Design.Type.ArtistTitle(t.Name),
            _ => Design.Type.ArtistCompactTitle(t.Name),
        }) with
        {
            Color = Tok.TextPrimary, Wrap = m.NameLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = m.NameLines,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 1f,
            Grow = m.Stacked ? 0f : 1f, Basis = m.Stacked ? float.NaN : 0f,
            AlignSelf = m.Stacked ? FlexAlign.Stretch : FlexAlign.Auto,
            MaxWidth = m.CopyMaxWidth,
            Children =
            [
                // One line at every tier (the hero is sized for it — no empty band over a short name); a long name
                // ellipsises and shows in full in the trim tooltip.
                Controls.TrimmedTitle(name, t.Name),
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, ProfileHeroLayout.NameToStats(in m), 0f, 0f),
                    Children = [Stats(in t, in m, acts)],
                },
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Margin = new Edges4(0f, ProfileHeroLayout.StatsToActions(in m), 0f, 0f),
                    Children = [HeroActions(in t, uri, acts)],
                },
            ],
        };
    }

    /// <summary>The numerals — explicit rows (Narrow's 2-row budget splits 2 + 1), never a wrap, so the height the
    /// metrics budget is the height laid out.</summary>
    static Element Stats(in HeroText t, in ProfileHeroMetrics m, HeroActs? acts)
    {
        Span<ProfileStatCell> cells = stackalloc ProfileStatCell[ProfileStats.Max];
        int n = ProfileStats.Plan(t.Own, t.ShowFollows, t.Followers, t.Following, t.Playlists, cells);
        int perRow = ProfileStats.PerRow(n, m.StatRows);
        int rows = (n + perRow - 1) / perRow;
        var lines = new Element[rows];
        for (int r = 0; r < rows; r++)
        {
            int start = r * perRow, count = Math.Min(perRow, n - start);
            var kids = new Element[count];
            for (int i = 0; i < count; i++) kids[i] = Stat(cells[start + i], acts);
            lines[r] = new BoxEl
            {
                Direction = 0, Gap = ProfileHeroLayout.StatGap, AlignItems = FlexAlign.Start, MinWidth = 0f, Children = kids,
            };
        }
        return rows == 1 ? lines[0] : new BoxEl { Direction = 1, Gap = ProfileHeroLayout.StatRowGap, MinWidth = 0f, Children = lines };
    }

    /// <summary>A StatHero numeral over its sentence-case caption (Track.Drawer.Stat). A cell that links (the cell's
    /// own rule: never Public playlists, never a zero) is a subtle-plated button into the list route; the ±8/±4 margin
    /// keeps the TEXT on the copy's edge while the plate overhangs it.</summary>
    static Element Stat(in ProfileStatCell c, HeroActs? acts)
    {
        var cell = new BoxEl
        {
            Direction = 1, Shrink = 1f, MinWidth = 0f, Corners = Radii.ControlAll,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
            Margin = new Edges4(-Spacing.S, -Spacing.XS, -Spacing.S, -Spacing.XS),
            Children =
            [
                Design.Type.StatHero(ProfileStats.Numeral(c.Value, CultureInfo.CurrentCulture), null),
                Caption(StatLabel(in c)) with
                {
                    Weight = 600, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };
        if (!c.Links || acts is null) return cell;
        var open = acts.OpenStat;
        var kind = c.Kind;
        return (cell with { Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => open(kind) })
            .Interactive(Interaction.Subtle);
    }

    /// <summary>The caption under a numeral, plural-aware. The followers key goes through its KEY constant: the typed
    /// accessor the loc generator emits for a plural whose branches are single bare words
    /// (<c>one {Follower} other {Followers}</c>) reads each word as another argument, so only <c>count</c> is bound here.</summary>
    static string StatLabel(in ProfileStatCell c) => c.Kind switch
    {
        ProfileStat.Followers => Loc.Format(Strings.Person.Stat.FollowersKey, ("count", c.Value)),
        ProfileStat.Following => Loc.Get(Strings.Person.Stat.Following),
        _ => Strings.Person.Stat.Playlists(c.Value),
    };

    /// <summary>[FollowToggle (others only)] [Share] [⋯] — the normal grammar (Artist.HeroActions' family). Never
    /// skeleton content: <c>Skeletonized(false)</c> leaves a same-size blank in the shimmer.</summary>
    static Element HeroActions(in HeroText t, string uri, HeroActs? acts)
    {
        string name = t.Name;
        var kids = new List<Element>(4);
        // Your own profile: the account lives on spotify.com — a stock button whose glyph says it leaves the app
        // (replaces the account flyout's old "Account" row, #161).
        if (t.Own)
            kids.Add(Button.Create(Loc.Get(Strings.Person.OpenAccount), s_openAccount, ButtonAppearance.Standard,
                glyph: Icons.OpenInNewWindow));
        if (!t.Own)
            kids.Add(acts is null || uri.Length == 0
                ? Controls.FollowToggle.SkeletonShape()
                // Keyed on the uri: FollowToggle carries its uri as a mount-frozen field.
                : Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name })
                    with { Key = "profile-follow:" + uri, SkeletonProxy = s_followShape });
        kids.Add(Album.CommandCircle(Icons.Share, Loc.Get(Strings.Menu.Share), acts?.Share ?? s_noop));
        kids.Add(acts is null
            ? new BoxEl { Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Corners = Radii.ControlAll }
            : Detail.MoreButton(acts.Menu, Controls.IconButtonSize, 16f, round: false) with { Key = "profile-more:" + uri });
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = ProfileHeroLayout.ActionRow,
            Children = kids.ToArray(),
        }.Skeletonized(false);
    }

    static readonly Func<Element> s_followShape = static () => Controls.FollowToggle.SkeletonShape();
    static readonly Action s_noop = static () => { };

    /// <summary>The Spotify account page (subscription, privacy, password) — web-only; opened in the user's browser.</summary>
    public const string AccountUrl = "https://www.spotify.com/account";
    static readonly Action s_openAccount = static () => Actions.Services.OpenExternal?.Invoke(AccountUrl);

    /// <summary>The ⋯ menu: Share ▸ (Copy link / Copy Spotify URI / Open in Spotify Web) — the show page's ⋯ grammar
    /// (<c>Episode.ShareMenu</c>, whose Copy link reads the uri; the registered CopyLink verb reads a TRACK set). A
    /// profile has no Pin row: no sidebar pin resolves a user route yet, and the verb would only ever be absent. Null when
    /// nothing applies, so the ⋯ opens nothing rather than an empty flyout.</summary>
    internal static ContextMenuModel? MenuFor(User u, string name)
    {
        if (!u.IsValid || !u.Uri.IsValid) return null;
        var ctx = new ActionContext(new ActionTarget(TargetKind.None, [], u.Uri, name, PlaylistHost.None), Actions.Services);
        if (Episode.ShareMenu(u.Uri, in ctx) is not { } share) return null;
        return new ContextMenuModel([share], Actions.Menu.Header(u.Image, name, Loc.Get(Strings.Search.TypeUser), circular: true));
    }

    // ══ 2. THE SECTIONS ═══════════════════════════════════════════════════════════════════════════════════════════════

    internal static string SectionTitle(ProfileSection s) => Loc.Get(s switch
    {
        ProfileSection.TopArtists => Strings.Person.TopArtists,
        ProfileSection.Playlists => Strings.Person.PublicPlaylists,
        ProfileSection.RecentArtists => Strings.Person.RecentArtists,
        ProfileSection.Following => Strings.Person.Following,
        _ => Strings.Person.Followers,
    });

    /// <summary>The band pivot's word for a section (the shorter set; the list page's facet bar reuses the two people
    /// words).</summary>
    internal static string PivotLabel(ProfileSection s) => Loc.Get(s switch
    {
        ProfileSection.TopArtists => Strings.Person.Pivot.TopArtists,
        ProfileSection.Playlists => Strings.Person.Pivot.Playlists,
        ProfileSection.RecentArtists => Strings.Person.Pivot.Artists,
        ProfileSection.Following => Strings.Person.Pivot.Following,
        _ => Strings.Person.Pivot.Followers,
    });

    /// <summary>AccentHeader · [a quiet badge chip] · [See all] — the shelf's custom header (the shelf adds its chevrons).</summary>
    internal static Element SectionHeader(string title, ColorF accent, Action? seeAll, string? chip = null)
    {
        BoxEl head = Controls.AccentHeader(title, accent) with { Shrink = 1f };
        if (seeAll is null && chip is null) return head;
        var kids = new List<Element>(3) { head };
        if (chip is { Length: > 0 }) kids.Add(Controls.Chip(chip));
        if (seeAll is not null) kids.Add(HyperlinkButton.Create(Loc.Get(Strings.Home.SeeAll), seeAll) with { Shrink = 0f });
        return new BoxEl { Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    // The artist shelf's geometry (Artist.Page.ShelfOf = PagedShelf's defaults) at a ONE-line caption.
    const float ShelfMinCard = 150f, ShelfMaxCard = 200f, ShelfGap = 12f, ShelfHeaderGap = 12f, ShelfChevron = 32f;
    /// <summary>The rank numeral's column beside a top-artist card.</summary>
    internal const float RankColumn = 36f;

    static readonly SurfaceShape s_card = Shape.Shelf(captionLines: 1);
    static readonly Func<float, float> s_cardHeight = static w => SurfaceGeometry.ShelfHeight(w, 1f, 1, false);
    static readonly Func<float, float> s_rankedHeight = static w => SurfaceGeometry.ShelfHeight(w - RankColumn, 1f, 1, false);
    static readonly Func<ProfileCard, int, string> s_keyOf = static (c, _) => c.Key;
    static readonly Action<ProfileCard, int> s_open = static (c, _) => Open(c);
    static readonly Func<ProfileCard, int, float, Element> s_playlistCard = static (c, _, w) => PlaylistCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_artistCard = static (c, _, w) => ArtistCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_personCard = static (c, _, w) => PersonCard(c, w);
    static readonly Func<ProfileCard, int, float, Element> s_rankedCard = static (c, _, w) => RankedArtistCard(c, w);

    /// <summary>A present section with rows. Every shelf of surface cards passes <c>onInvoke</c> (the slot owns click +
    /// focus: one Tab stop per card). "See all" rides the two people sections only (the caller passes null elsewhere).</summary>
    internal static Element Shelf(ProfileSection s, ProfileCard[] items, ColorF accent, Action? seeAll)
    {
        string title = SectionTitle(s);
        return s switch
        {
            ProfileSection.TopArtists => ShelfOf(items, s_rankedCard,
                SectionHeader(title, accent, null, Loc.Get(Strings.Person.OnlyYou)), s_rankedHeight),
            ProfileSection.Playlists => ShelfOf(items, s_playlistCard, SectionHeader(title, accent, seeAll), s_cardHeight),
            ProfileSection.RecentArtists => ShelfOf(items, s_artistCard, SectionHeader(title, accent, null), s_cardHeight),
            _ => ShelfOf(items, s_personCard, SectionHeader(title, accent, seeAll), s_cardHeight),
        };
    }

    static Element ShelfOf(ProfileCard[] items, Func<ProfileCard, int, float, Element> cardAt, Element header,
                           Func<float, float> cardHeight) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Children = [PagedShelf.Create(items, cardAt, onInvoke: s_open, cardHeight: cardHeight, header: header,
            measured: false, keyOf: s_keyOf, lift: ShelfLift.None)],   // the shared card hovers fill-only: no lift halo
    };

    /// <summary>A section's shimmer SOURCE at the shelf's real fit: the real header, then the fitted count of SEED
    /// surfaces (a PagedShelf yields no rows against an unmeasured viewport — Browse's must-not-simplify 11).</summary>
    internal static Element SeedShelf(ProfileSection s, ColorF accent, float width)
    {
        bool ranked = s == ProfileSection.TopArtists;
        var (perPage, cardW) = FillRowVirtualLayout.Fit(width, ShelfMinCard, ShelfMaxCard, ShelfGap);
        var cells = new Element[perPage];
        for (int i = 0; i < perPage; i++)
        {
            Element seed = Controls.Surface(Controls.CardData.Seed, s_card, ranked ? cardW - RankColumn : cardW);
            cells[i] = ranked
                ? new BoxEl { Direction = 0, Width = cardW, Shrink = 0f, Children = [new BoxEl { Width = RankColumn, Shrink = 0f }, seed] }
                : seed;
        }
        return new BoxEl
        {
            Direction = 1, Gap = ShelfHeaderGap, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinHeight = ShelfChevron, Children = [SectionHeader(SectionTitle(s), accent, null)] },
                new BoxEl { Direction = 0, Gap = ShelfGap, MinWidth = 0f, ClipToBounds = true, Children = cells },
            ],
        };
    }

    /// <summary>Public playlists, answered and empty (the only section kept when empty — the page plans the rest away):
    /// your own copy offers the way out, another person's just says so.</summary>
    internal static Element EmptyShelf(ProfileSection s, bool own) => own
        ? Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
              title: Loc.Get(Strings.Person.Empty.PlaylistsOwn), subtitle: Loc.Get(Strings.Person.Empty.PlaylistsOwnHint))
        : Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
              title: Loc.Get(Strings.Person.Empty.Playlists), subtitle: "");

    /// <summary>A failed section: its header over a compact error vacancy WITH its own Retry.</summary>
    internal static Element SectionError(ProfileSection s, ColorF accent, Action retry)
        => Artist.SectionBlock(SectionTitle(s),
            Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: retry), accent);

    /// <summary>The 404 face (a private / closed account): the page keeps its frame, no Retry — nothing will answer.</summary>
    internal static Element Unavailable()
        => Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Person.Empty.Unavailable),
            subtitle: Loc.Get(Strings.Person.Empty.UnavailableHint));

    // ══ 3. THE CARDS ══════════════════════════════════════════════════════════════════════════════════════════════════

    static Element CardSubtitle(string text)
        => Design.Type.TrackMeta(text) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>A follower-count message (<paramref name="key"/> = a <c>…FollowersKey</c> const): <c>count</c> picks the
    /// plural arm, <c>n</c> is the grouped numeral — the formatter's own <c>#</c> prints invariant digits with no grouping
    /// ("85324 followers").</summary>
    internal static string FollowerCount(string key, int count)
        => Loc.Format(key, ("count", count), ("n", ProfileStats.Numeral(count, CultureInfo.CurrentCulture)));

    static void Open(in ProfileCard c)
    {
        switch (c.Kind)
        {
            case EntityKind.Playlist:
            {
                var pl = new Playlist(c.Slot);
                Shell.GoTo(Shell.For(pl.Uri, Entities.Strings.Resolve(pl.TitleId)));
                break;
            }
            case EntityKind.Artist:
                Track.GoToArtist(new Artist(c.Slot));
                break;
            case EntityKind.User:
            {
                var u = new User(c.Slot);
                Shell.GoTo(Shell.For(u.Uri, u.Name));
                break;
            }
        }
    }

    /// <summary>Square cover (a cover-less playlist composes its 2×2 mosaic), title, "N followers" when non-zero.</summary>
    static Element PlaylistCard(ProfileCard c, float w)
    {
        var pl = new Playlist(c.Slot);
        string title = Entities.Strings.Resolve(pl.TitleId);
        float inner = SurfaceParts.InnerOf(w);
        string? url = Controls.ArtUrl(pl.ImageId);
        // A server mosaic token paints its 2×2; a cover-less playlist (your own, which the library holds with no image)
        // composes its member-track mosaic exactly like the sidebar and the playlist page (Playlist.CoverArt).
        Element? cover = Controls.MosaicTiles(pl.ImageId).Length == 4 || url is null ? Playlist.CoverArt(pl, inner) : null;
        var hit = new EntityRef(EntityKind.Playlist, c.Slot);
        var card = c;
        var data = new Controls.CardData(pl.Uri.Text, title,
            c.Followers > 0 ? CardSubtitle(FollowerCount(Strings.Person.Card.FollowersKey, c.Followers)) : null,
            url, () => Open(card), () => Playback.PlayOrToggleContext(pl.Id), Drag: Search.DragOf(hit),
            CoverOverride: cover)
        {
            Menu = Search.MenuOf(hit),
        };
        return Controls.Surface(data, s_card, w);
    }

    static Element ArtistCard(ProfileCard c, float w)
    {
        var ar = new Artist(c.Slot);
        var hit = new EntityRef(EntityKind.Artist, c.Slot);
        var card = c;
        var data = new Controls.CardData(ar.Uri.Text, ar.Name, CardSubtitle(Loc.Get(Strings.Search.TypeArtist)),
            Controls.ArtUrl(ar.ImageId), () => Open(card), () => Playback.PlayOrToggleContext(ar.Id), Circular: true,
            Drag: Search.DragOf(hit))
        {
            Menu = Search.MenuOf(hit),
        };
        return Controls.Surface(data, s_card, w);
    }

    /// <summary>A follow-list entry: an artist is the artist card; a person is a circle (photo, else initials on their own
    /// colour), "Profile", no play, no drag (Search.Drags refuses users), no menu.</summary>
    static Element PersonCard(ProfileCard c, float w)
    {
        if (c.Kind == EntityKind.Artist) return ArtistCard(c, w);
        var u = new User(c.Slot);
        string name = u.Name;
        string? url = u.Image;
        float inner = SurfaceParts.InnerOf(w);
        uint argb = ProfileTone.Argb(u.Color);
        ColorF? fill = argb != 0 ? Design.Palette.ToColor(argb) : null;
        var card = c;
        var data = new Controls.CardData(u.Uri.Text, name, CardSubtitle(Loc.Get(Strings.Search.TypeUser)), url,
            () => Open(card), Circular: true,
            CoverOverride: url is null ? PersonPicture.Create("", inner, displayName: name, fill: fill) : null);
        return Controls.Surface(data, s_card, w);
    }

    /// <summary>Own top artists: the rank as a StatHero numeral in a 36-DIP column at the cover's top, the circular
    /// artist card beside it (its extent is ShelfHeight(w − 36), which <c>s_rankedHeight</c> hands the shelf).</summary>
    static Element RankedArtistCard(ProfileCard c, float w) => new BoxEl
    {
        Direction = 0, Width = w, Shrink = 0f, AlignItems = FlexAlign.Start,
        Children =
        [
            new BoxEl
            {
                Width = RankColumn, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.End, HitTestVisible = false,
                Padding = new Edges4(0f, SurfaceGeometry.ShelfGutterTop + SurfaceGeometry.ShelfPlatePad, Spacing.XS, 0f),
                Children = [Design.Type.StatHero(ProfileStats.Numeral(c.Rank, CultureInfo.CurrentCulture), null) with { Color = Tok.TextSecondary }],
            },
            ArtistCard(c, w - RankColumn),
        ],
    };
}
