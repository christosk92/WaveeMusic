// ── Entities/Search.Hits.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the search hit's CORE vocabulary: what a hit says (title, cover, kind word, subtitle), where it goes (open, play), its context menu, its drag
// source, its trailing control, the surface data of a row and a shelf card, and the pure decisions of a row (`SearchHitRules`)
//
// Role: CORE (data and verbs)
// Owner: P (stream P3); T10a2 split the pictures out into Classic's `Views\Pages\ClassicSearch*.cs` and left what the omnibar flyout, the profile
// pages and the user page still call here.
//
// ── ONE HIT ROW ──────────────────────────────────────────────────────────────────────────────────────────────────────────
//
// Every hit — All-tab list, Best matches cell, Songs cell, Artists / Podcasts / Episodes / Profiles lists — is a row of the app's ONE media
// surface (`Controls.Surface`) over `HitData`, the hit's data. The surface owns the hover and press plate, the hand cursor, the focus ring, the
// play FAB and the hot-revealed "…", so a podcast row looks identical wherever it came from (ch 13 §0.8) and nothing paints a row by hand. The
// title, art and subtitle read the TARGET row's columns through `HomeCard` (the same live handle Home's cards use); a TRACK hit takes the track
// adapter (`Track.RowData`: the cover fallback, the hidden-artwork look, the play leg) and keeps its search subtitle; the chrome flags (lyrics
// match, music video) read `Search.FlagsOf`. A TRACK hit gets the full track menu and a depositable payload straight off its own row — the 0.2.9
// `TrackOf` scan disappears because the edge target IS the Track handle (ch 13 §7). Artists are no longer a thinner row (W10): they are this row,
// circular, with the menu, drag and follow control 0.2.9 forgot (§9 gap 8). Which kind follows, saves, drags or wears the eyebrow is
// `SearchHitRules` — pure, pinned by SearchHitDataTests. The shelf card (`CardDataOf`) is the same grammar at the shelf's shape.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace Wavee;

public readonly partial struct Search
{
    /// <summary>What every hit factory needs beyond the hit, resolved ONCE per page render.</summary>
    public readonly record struct HitContext(string Query, Shell.NavOrigin? Origin, IOverlayService? Overlay, bool HideTrackArt);

    /// <summary>One Best-matches / rail cell: the hit plus its row version and chrome, so the shelf's value snapshot
    /// changes when the row hydrates.</summary>
    public readonly record struct HitItem(EntityRef Ref, uint Version, SearchHitFlags Flags);

    /// <summary>One row of the no-top-hits fallback list: the hit and whether it is the large lead row.</summary>
    public readonly record struct FallbackHit(HitItem Hit, bool Large);

    /// <summary>The All row's own list count for a facet: its hits of that kind, the genre tiles for Genres.</summary>
    internal static int LocalCountOf(Search all, SearchFacet f)
    {
        if (f == SearchFacet.Genres) return all.GenreSlots.Length;
        var kind = KindOf(f);
        return kind == EntityKind.Unknown ? 0 : all.CountOf(kind);
    }

    // ══ 3. WHAT A HIT SAYS ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The hit's title: the target row's own name column (a profile's display name).</summary>
    internal static string TitleOf(EntityRef hit)
        => hit.Kind == EntityKind.User ? Entities.Strings.Resolve(new User(hit.Slot).NameId) : new HomeCard(hit).Title;

    /// <summary>The hit's art url, or null.</summary>
    internal static string? CoverOf(EntityRef hit)
        => hit.Kind == EntityKind.User ? Controls.ArtUrl(new User(hit.Slot).ImageId) : new HomeCard(hit).ImageUrl;

    /// <summary>The kind word a hit's subtitle leads with — "Song" / "Music video" for a track (§9 gap 7: the loc keys,
    /// never the hardcoded English 0.2.9 used).</summary>
    internal static string KindWordOf(EntityKind kind, SearchHitFlags flags) => Loc.Get(kind switch
    {
        EntityKind.Track => (flags & SearchHitFlags.VideoMedia) != 0 ? Strings.Search.SubtitleMusicVideo : Strings.Search.SubtitleSong,
        EntityKind.Album => Strings.Search.TypeAlbum,
        EntityKind.Artist => Strings.Search.TypeArtist,
        EntityKind.Playlist or EntityKind.Collection => Strings.Search.TypePlaylist,
        EntityKind.Show => Strings.Search.TypePodcast,
        EntityKind.Episode => Strings.Search.TypeEpisode,
        _ => Strings.Search.TypeUser,
    });

    /// <summary>"Song • A, B" / "Album • A" / "Playlist • owner" / "Podcast • publisher" / "Episode • show" / "Artist" /
    /// "Profile" — the kind word, then what the target row knows (0.2.9's server subtitle shape, rebuilt from columns).</summary>
    internal static string SubtitleOf(EntityRef hit, SearchHitFlags flags)
    {
        string word = KindWordOf(hit.Kind, flags);
        if (hit.Kind is EntityKind.Artist or EntityKind.User) return word;
        var card = new HomeCard(hit);
        string? detail = hit.Kind is EntityKind.Playlist or EntityKind.Collection ? card.OwnerName : card.Subtitle;
        return SubtitleText(word, detail);
    }

    /// <summary>Where opening a hit goes: a page for an album / artist / playlist / show (with the search LOOKUP origin, so
    /// the trail reads <c>"q" › X</c>); a track or an episode plays; a profile opens its page (#161 — the same origin, so
    /// the masthead reads <c>"q" › Name</c>).</summary>
    internal static void OpenHit(EntityRef hit, Shell.NavOrigin? origin)
    {
        if (hit.IsNone) return;
        if (hit.Kind is EntityKind.Track or EntityKind.Episode) { PlayHit(hit); return; }
        if (hit.Kind == EntityKind.User)
        {
            var profile = ProfileRoute.For(new User(hit.Slot));
            if (!profile.IsNone) Shell.GoTo(profile, origin);
            return;
        }
        var card = new HomeCard(hit);
        HomeCardNav.Open(in card, origin);
    }

    /// <summary>The hit's play: a track / episode as the item, a container as its context (HomeCardNav's one path).</summary>
    internal static void PlayHit(EntityRef hit)
    {
        if (hit.IsNone || !CanPlay(hit.Kind)) return;
        var card = new HomeCard(hit);
        HomeCardNav.Play(in card);
    }

    /// <summary>The hit's context menu, or null (the "…" and the right-click then do not exist, §6.1): a TRACK gets the
    /// full track menu off its own row; an album / artist / playlist / show the card grammar; episode / profile none.</summary>
    internal static Func<ContextMenuModel?>? MenuOf(EntityRef hit)
    {
        if (hit.IsNone || !HasMenu(hit.Kind)) return null;
        if (hit.Kind == EntityKind.Track)
        {
            int slot = hit.Slot;
            return () =>
            {
                Track.EnsureActions();
                return Track.Menu([new Track(slot)], new Track.MenuOptions(ShowGoToAlbum: true));
            };
        }
        var card = new HomeCard(hit);
        return HomeCardNav.MenuOf(in card);
    }

    /// <summary>The hit's drag source: a track carries itself (depositable on a playlist); anything else the entity
    /// payload the drop target resolves or refuses with a cue. A profile is not a drag source.</summary>
    internal static DragSource? DragOf(EntityRef hit)
    {
        if (hit.IsNone || !SearchHitRules.Drags(hit.Kind)) return null;
        var h = hit;
        return Drag.Source(() =>
        {
            string uri = h.Id.Text;
            string name = TitleOf(h);
            return h.Kind == EntityKind.Track
                ? new DragPayload(DragKind.Track, uri, uri, name, h, Tracks: [new Track(h.Slot)], ArtUrl: CoverOf(h))
                : new DragPayload(Drag.KindOf(h.Kind), uri, uri, name, h, ArtUrl: CoverOf(h));
        });
    }

    /// <summary>The trailing control: Follow for an artist / playlist / show, the heart for a track / album, none else.
    /// Keyed on the uri — the embed's props freeze by design (Controls.cs §4).</summary>
    internal static Element? TrailingOf(EntityRef hit, string uri, string name, bool compact)
        => SearchHitRules.TrailingOf(hit.Kind, hasUri: uri.Length > 0) switch
        {
            SearchHitRules.Trailing.Follow
                => Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name }) with { Key = "follow:" + uri },
            SearchHitRules.Trailing.Save
                => Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = name, Glyph = 16f }) with { Key = "save:" + uri },
            _ => null,
        };


    // ══ 4. THE HIT ROW (W9, §3 "Search rows") ════════════════════════════════════════════════════════════════════════

    /// <summary>The surface's data for a hit of ANY kind — the one adapter behind every search row. Title, the "Song • A, B"
    /// line, cover (circular for people), open and play; the eyebrow "Lyrics match" on a small row whose hit matched
    /// lyrics; the trailing follow / save; the context menu (<see cref="MenuOf"/>) and the drag (<see cref="DragOf"/>) — null
    /// for a hit with none, and the host then grows no "…" and no right-click. A TRACK hit is the track adapter
    /// (<see cref="Track.RowData"/>) under search's own subtitle, menu and drag: it brings the cover fallback, the
    /// hidden-artwork look (a play glyph where the cover was — pair it with <see cref="SearchHitRules.ShapeOf"/>) and the
    /// play leg that toggles pause on the playing track. Runs inside a render: a track hit's table read subscribes it, so a
    /// hydrating row re-describes itself.</summary>
    internal static Controls.CardData HitData(EntityRef hit, SearchHitFlags flags, bool large, in HitContext ctx)
    {
        var h = hit;
        var origin = ctx.Origin;
        string uri = hit.Id.Text;
        string title = TitleOf(hit);
        Action open = () => OpenHit(h, origin);
        Action? play = CanPlay(hit.Kind) ? () => PlayHit(h) : null;

        Controls.CardData data = hit.Kind == EntityKind.Track
            ? Track.RowData(new Track(hit.Slot), new Track.RowDataOptions(
                OnClick: open, OnPlay: play, ShowArtwork: SearchHitRules.ShowsArt(hit.Kind, ctx.HideTrackArt),
                ShowExplicit: false, Draggable: false))
            : new Controls.CardData(uri, title, null, CoverOf(hit), open, play, Circular: RoundArt(hit.Kind));
        return data with
        {
            Subtitle = R.Type.TrackMeta(SubtitleOf(hit, flags)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            Eyebrow = SearchHitRules.LyricsEyebrow(large, flags)
                ? R.Type.Eyebrow(Loc.Get(Strings.Search.LyricsMatch)) with { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
                : null,
            Trailing = TrailingOf(hit, uri, title, compact: true),
            Menu = MenuOf(hit),
            Drag = DragOf(hit),
        };
    }

    /// <summary>A hit's shelf key: kind + slot, stable across hydration (never a data value).</summary>
    internal static string KeyOf(EntityRef hit)
        => ((int)hit.Kind).ToString(CultureInfo.InvariantCulture) + ":" + hit.Slot.ToString(CultureInfo.InvariantCulture);

    /// <summary>The surface's data for a hit: title, the owner / artist line, cover, open, play, drag and the context menu
    /// (null for a hit with none — the "…" and the right-click then do not exist).</summary>
    internal static Controls.CardData CardDataOf(EntityRef hit, Shell.NavOrigin? origin)
    {
        var h = hit;
        var card = new HomeCard(hit);
        string? sub = hit.Kind is EntityKind.Playlist or EntityKind.Collection ? card.OwnerName : card.Subtitle;
        return new Controls.CardData(
            hit.Id.Text, TitleOf(hit),
            sub is { Length: > 0 } ? R.Type.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f } : null,
            CoverOf(hit), () => OpenHit(h, origin), CanPlay(hit.Kind) ? () => PlayHit(h) : null,
            Circular: RoundArt(hit.Kind), Drag: DragOf(hit)) { Menu = MenuOf(hit) };
    }

    /// <summary>Open a genre: its browse page (or a re-committed search on its name), carrying the lookup origin so the
    /// trail reads <c>"q" › Genre</c> with no Browse rung.</summary>
    internal static void OpenGenre(string uri, string name, Shell.NavOrigin? origin)
    {
        var route = GenreRoute(uri, name);
        if (route.IsNone) return;
        Shell.GoTo(route, route.Kind == Shell.RouteKind.Search ? null : origin);
    }

    /// <summary>Commit a related query: a new search.</summary>
    internal static void OpenRelated(string query) => Shell.GoTo(Shell.Parse("search".AsSpan(), query.AsSpan()));
}


/// <summary>The search hit row's decisions, engine-free so a fact pins each (SearchHitDataTests): which art edge and floor a
/// row has (<see cref="ShapeOf"/>), whether a hit's art shows (<see cref="ShowsArt"/>), whether the row wears the "Lyrics
/// match" eyebrow (<see cref="LyricsEyebrow"/>), which trailing control a kind carries (<see cref="TrailingOf"/>) and
/// which kind is a drag source (<see cref="Drags"/>). Whether a kind is circular, plays or has a menu is
/// <see cref="Search.RoundArt"/> / <see cref="Search.CanPlay"/> / <see cref="Search.HasMenu"/> — already pure rules of the
/// search surface (Search.Rules.cs); the surface itself owns the plate, the cursor, the focus ring and the FAB.</summary>
public static class SearchHitRules
{
    /// <summary>The art square of a search row (the media row's default).</summary>
    public const float RowArt = 48f;

    /// <summary>The surface shape of a hit row: <see cref="Shape.RowLarge"/> when <paramref name="large"/> (the lead row of
    /// the no-top-hits fallback list — 84 art, the 112 floor, the page-hero title), else the 48-art row in the 64 floor.
    /// A TRACK row whose artwork is hidden (<paramref name="artHidden"/>) holds the play-glyph square instead of the art
    /// (<see cref="TrackRowRules.ArtEdge"/>), still in the 64 floor. Every shape carries <see cref="MenuPlacement"/>
    /// = <see cref="RowMenu"/>.</summary>
    public static SurfaceShape ShapeOf(bool large, bool artHidden)
        => (large ? Shape.RowLarge : Shape.Row(TrackRowRules.ArtEdge(RowArt, showArtwork: !artHidden))) with { Menu = RowMenu };

    /// <summary>Where a hit row's "…" goes: its OWN lane after the trailing control
    /// (<see cref="MenuPlacement.TrailingLane"/>), never the overlay over the row's end. Every kind that has a menu
    /// (<see cref="Search.HasMenu"/>) also ends in a control (<see cref="TrailingOf"/>: the heart or the Follow pill), so
    /// the overlay would always land on that control — hiding and blocking the heart, cutting "Follow" to "Fol". The Top
    /// Result card already places its "…" in a slot of its own beside the same control; the rows (search page and the
    /// search flyout alike, <c>OmnibarRowRules.RowShape</c>) now do the same.</summary>
    public const MenuPlacement RowMenu = MenuPlacement.TrailingLane;

    /// <summary>A hit's art shows unless the app hides TRACK artwork and the hit is a track — albums, artists, playlists,
    /// shows, episodes and people keep theirs.</summary>
    public static bool ShowsArt(EntityKind kind, bool hideTrackArt) => kind != EntityKind.Track || !hideTrackArt;

    /// <summary>The eyebrow "Lyrics match" sits over the title of a SMALL row whose hit matched lyrics; the large lead row
    /// leaves it to the Top Result's chip.</summary>
    public static bool LyricsEyebrow(bool large, SearchHitFlags flags) => !large && (flags & SearchHitFlags.MatchedLyrics) != 0;

    /// <summary>The control at the end of a hit row.</summary>
    public enum Trailing : byte { None, Follow, Save }

    /// <summary>Follow for an artist / playlist / show, the heart (Save) for a track / album, none for the rest — and none
    /// for a hit with no uri to act on.</summary>
    public static Trailing TrailingOf(EntityKind kind, bool hasUri)
    {
        if (!hasUri) return Trailing.None;
        return kind switch
        {
            EntityKind.Artist or EntityKind.Playlist or EntityKind.Show => Trailing.Follow,
            EntityKind.Track or EntityKind.Album => Trailing.Save,
            _ => Trailing.None,
        };
    }

    /// <summary>Every hit but a profile (a person with no Wavee resource behind them) and an unresolved one is a drag
    /// source.</summary>
    public static bool Drags(EntityKind kind) => kind is not (EntityKind.User or EntityKind.Unknown);
}