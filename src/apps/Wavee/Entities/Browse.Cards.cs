// ── Entities/Browse.Cards.cs ───────────────────────────────────────────────────────────────────────────────────────
// The Home card vocabulary members Browse and Search still call directly, and the UI half of HomeCardNav (open/play/
// drag/menu). Split out of the superseded Wave-5 Home UI (Home.Cards.UI.cs, Home.UI.cs) when the Home page itself was
// rebuilt (docs/plans/wavee/home-rebuild-implementation.md §7): these members render entity CARDS wherever Browse or
// Search hosts a shelf/grid, and outlive the Home page redesign. Moved UNCHANGED — same names, same namespace — so
// Browse.Page.cs / Browse.UI.cs / Search.UI.cs need no call-site edits. The CORE half of HomeCardNav (RouteFor,
// SectionRoute, OneCardOpensCard) stays in Entities/Home.Rules.cs.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The Home card vocabulary members still used outside Home: the shelf card adapter (Browse's and Search's
/// paged shelves render over this, never a hand-rolled cell), the grid card adapter (the section drill page and Browse's
/// category grid — the same ONE surface, shared-media-surface-implementation.md Appendix A.3) and the raw artwork slot
/// the fold tile borrows.</summary>
public static partial class HomeCards
{
    /// <summary>Art, always through the app's one artwork slot — never a hand-rolled image. <paramref name="decodePx"/>
    /// is the SQUARE decode target so several sizes of one cover share a texture (ch 10 §9 decode budget).</summary>
    public static Element Art(in HomeCard c, float w, float h, float corners, int decodePx = 0)
        => Controls.Artwork(c.ImageUrl, w, h, corners, decodePx: decodePx > 0 ? decodePx : (int)MathF.Max(w, h));

    /// <summary>A possibly-HTML description as plain text for the string-typed consumers (a menu subtitle, a one-line
    /// caption) — P1's ported <see cref="HomeCardText.PlainText"/> (0.2.9 <c>SpotifyExportMapper.ToPlainText</c>),
    /// never null. The markup-free common case allocates nothing.</summary>
    public static string PlainText(string? html) => HomeCardText.PlainText(html) ?? "";

    /// <summary>What a shelf card PAINTS, as a value: <c>PagedShelf</c>'s props gate compares items by VALUE and ignores
    /// the card builder, so the strings a card shows must be IN the item or a hydrated title would never reach a retained
    /// card (component-props-contract "Retained shelf authoring").</summary>
    /// <para><paramref name="WideArt"/> is the card's header (16:9) image, used only when the cell renders at a
    /// non-square <c>coverAspect</c> (falling back to <paramref name="Art"/>); <paramref name="CaptionLines"/> caps the
    /// second line (1 — the shelf's own — or 2 for a lead card); <paramref name="Meta"/> is the optional tertiary meta:
    /// its own third line, or — with <paramref name="MetaInline"/> — a tertiary " · meta" tail INSIDE the second line
    /// (the prototype's <c>.lead-meta</c> in <c>.g-cap</c>), so the card shows exactly <paramref name="CaptionLines"/>
    /// caption lines and no meta line. All default to "what every shelf has always shown".</para></summary>
    public readonly record struct ShelfItem(HomeCard Card, string Title, string Second, string? Art, bool Circular,
                                            string? WideArt = null, int CaptionLines = 1, string? Meta = null,
                                            bool MetaInline = false);

    /// <summary>Snapshot a card for a shelf. <paramref name="second"/> is the card's second line (a kind word, a
    /// subtitle, a reason) — resolved by the module, because what that line NAMES differs per module (ch 11 W9 vs W27).</summary>
    public static ShelfItem ShelfItemOf(in HomeCard c, string second, bool circular, string? wideArt = null,
                                        int captionLines = 1, string? meta = null, bool metaInline = false)
        => new(c, c.Title, second, c.ImageUrl, circular, wideArt, captionLines, meta, metaInline);

    /// <summary>The caption lines a shelf of <paramref name="items"/> must reserve: the most any card shows (an inline
    /// meta rides those lines, so it adds none). The row is one height, so the tallest caption sets it.</summary>
    public static int CaptionLinesOf(ReadOnlySpan<ShelfItem> items)
    {
        int lines = 1;
        foreach (ref readonly var item in items)
            if (item.CaptionLines > lines) lines = item.CaptionLines;
        return lines;
    }

    /// <summary>A shelf cell: the app's ONE media surface (<see cref="Controls.Surface"/>) at the shelf's card width — the
    /// shared plate, hover physics, "…" corner and play FAB. The drag and the menu ride IN the data; the surface's host
    /// attaches the menu to its shell (skipped under the null overlay).
    /// <para><paramref name="coverAspect"/> (width ÷ height) is the cover's ratio for THIS cell — 1 is the square every
    /// entity card has; a wide cell takes the item's <see cref="ShelfItem.WideArt"/> when it has one. The shape reserves
    /// what the card shows (its caption lines, its separate meta line), so its seed face is the live card's height; the
    /// shelf's own extent stays the caller's <c>cardHeight</c>.</para></summary>
    public static Element ShelfCell(in ShelfItem item, float cardW, Action onNav, Action onPlay,
                                    DragSource? drag, IOverlayService? menuHost, Func<ContextMenuModel?>? menu,
                                    float coverAspect = 1f)
    {
        bool hasMenu = menu is not null && !Controls.IsNullOverlay(menuHost);
        int lines = item.CaptionLines < 1 ? 1 : item.CaptionLines;
        // An inline meta makes the card render its own caption paragraph (second + " · meta") from STRINGS — the
        // element subtitle is then absent, and no separate meta line exists.
        Element? second = !item.MetaInline && item.Second.Length > 0
            ? Design.Type.TrackMeta(item.Second) with
                { MaxLines = lines, Wrap = lines > 1 ? TextWrap.Wrap : TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
            : null;
        string? art = coverAspect != 1f ? item.WideArt ?? item.Art : item.Art;
        var data = new Controls.CardData(item.Card.Uri, item.Title, second, art, onNav, onPlay,
                                         Circular: item.Circular, Drag: drag, ShowMenu: hasMenu)
        {
            CoverAspect = coverAspect, Meta = item.Meta, Caption = item.MetaInline ? item.Second : null, CaptionLines = lines,
            Menu = hasMenu ? menu : null,
        };
        bool metaLine = !item.MetaInline && item.Meta is { Length: > 0 };
        return Controls.Surface(data, Shape.Shelf(lines, metaLine), cardW);
    }

    /// <summary>The GRID surface's data for a Home/Browse card — the one adapter every HomeCard grid uses (the drill page's
    /// bound slots, Browse's category grid). Null for a blank/seed card. Runs inside a render: the subscribing reads below
    /// re-describe the card when its title/cover hydrate (a HomeCard is a (Target, SectionSlot) value — the slot's item
    /// memo does not fire on a column landing). <paramref name="charts"/> blanks the subtitle (a chart's cards carry none).
    /// <paramref name="open"/> defaults to <see cref="HomeCardNav.Open"/> from the Home origin.</summary>
    public static Controls.CardData? GridCardData(in HomeCard c, IOverlayService? menuHost, bool charts = false,
                                                  Action<HomeCard>? open = null)
    {
        if (c.IsBlank) return null;
        var card = c;                                                   // a struct copy the closures can hold
        _ = Entities.ScopeEpoch.Value;                                  // FIRST: a scope switch re-points the table below
        _ = SectionTable.CardTable(card.Target.Kind)?.Changed.Value;    // Liked lives in Playlists: CardTable, not TableFor
        var shape = HomeUi.HomeCardGridShape.Of(card.Kind, charts);
        var menu = shape.CanMenu ? HomeCardNav.MenuOf(in card) : null;
        bool hasMenu = menu is not null && !Controls.IsNullOverlay(menuHost);
        string sub = shape.ShowSubtitle ? PlainText(card.Subtitle) : "";
        Action onClick = open is null ? () => HomeCardNav.Open(in card, HomeCardNav.HomeOrigin) : () => open(card);
        return new Controls.CardData(card.Uri, card.Title,
            sub.Length > 0
                ? Design.Type.TrackMeta(sub) with
                    { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                : null,
            card.ImageUrl, onClick, () => HomeCardNav.Play(in card),
            Circular: shape.Circular, Drag: shape.CanDrag ? HomeCardNav.DragOf(in card) : null, ShowMenu: hasMenu)
            { Menu = hasMenu ? menu : null };
    }
}

/// <summary>The UI half of card navigation still used outside Home (Browse's tiles/shelves, Search's cells): open,
/// play, drag and the context menu. The CORE half — <c>RouteFor</c>, <c>SectionRoute</c>, <c>OneCardOpensCard</c> — is
/// the OTHER partial declaration, in Entities/Home.Rules.cs §12.</summary>
public static partial class HomeCardNav
{
    /// <summary>The origin every drill out of a card hands over. Resolved per read: the label is live Loc.</summary>
    public static Shell.NavOrigin HomeOrigin => new(Loc.Get(Strings.Nav.Home), new Shell.Route(Shell.RouteKind.Home));

    /// <summary>Open a card: a Track/Episode PLAYS (there is no episode page, and a track is not a destination); anything
    /// else navigates to <see cref="RouteFor"/>. <paramref name="origin"/> is what the destination's masthead composes
    /// with.</summary>
    public static void Open(in HomeCard card, Shell.NavOrigin? origin = null)
    {
        if (card.IsBlank) return;
        if (HomeCardPlayRouting.PlaysAsItem(card.Kind)) { Play(in card); return; }
        var route = RouteFor(in card);
        if (route.IsNone) return;
        Shell.GoTo(route, origin);
    }

    /// <summary>Drill into a BROWSE section (<c>browse-section:</c>, paging through <c>browseSection</c>). A section that
    /// is exactly one card IS that card — never the one-tile intermediate page (ch 12 §0.19).</summary>
    public static void OpenBrowseSection(HomeSectionView s, Shell.NavOrigin? origin = null)
    {
        if (OneCardOpensCard(s.Cards.Count, browse: true))
        {
            var only = s.Cards[0];
            Open(in only, origin);
            return;
        }
        var route = SectionRoute(s, browse: true);
        if (route.IsNone) return;
        Shell.GoTo(route, origin);
    }

    /// <summary>The card ▶: item vs context by <see cref="HomeCardPlayRouting.PlaysAsItem"/>, both through the host's one
    /// context path. One <c>card.play</c> log line per press names the card and how the request ended; a thrown failure
    /// is the one outcome the user can act on, so it also reaches them as a toast.</summary>
    public static void Play(in HomeCard card)
    {
        string uri = card.Uri;
        if (uri.Length == 0) return;
        string outcome = "completed";
        try
        {
            if (!HomeCardPlayRouting.PlaysAsItem(card.Kind) && Actions.Services.Play is { } play) play(EntityUri.Parse(uri));
            else Playback.PlayContext(uri);
        }
        catch (Exception ex)
        {
            outcome = "failed";
            Log.Warn("ui", "card.play uri=" + uri + " kind=" + card.Kind + " outcome=failed", ex);
            Notify.Say(Loc.Get(Strings.Home.PlayFailed), InfoBarSeverity.Error);
            return;
        }
        Log.Info("ui", "card.play uri=" + uri + " kind=" + card.Kind + " outcome=" + outcome);
    }

    /// <summary>The card as a drag SOURCE, or null for a Track/Episode/blank card. The payload factory is gesture-cold:
    /// it runs once, at promotion.</summary>
    public static DragSource? DragOf(in HomeCard card)
    {
        if (card.IsBlank || card.Uri.Length == 0 || card.Kind is HomeCardKind.Track or HomeCardKind.Episode) return null;
        var c = card;
        return Drag.Source(() => PayloadOf(c));
    }

    static DragPayload? PayloadOf(HomeCard card)
    {
        string uri = card.Uri;
        if (uri.Length == 0) return null;
        var kind = Drag.KindOf(card.Target.Kind);
        Func<CancellationToken, Task<Track[]>>? resolver = null;
        if (kind is DragKind.Playlist or DragKind.Album or DragKind.Show && Sidebar.LibraryWrites?.ResolveTracks is { } resolve)
            resolver = ct => resolve(uri, ct);
        return new DragPayload(kind, uri, uri, card.Title, card.Target, ArtUrl: card.ImageUrl, TrackResolver: resolver);
    }

    /// <summary>The card's context menu factory, built at OPEN time, or null when the card has none (an episode, a blank
    /// card, an unknown scheme).</summary>
    public static Func<ContextMenuModel?>? MenuOf(in HomeCard card)
    {
        if (card.IsBlank || card.Uri.Length == 0 || card.Kind == HomeCardKind.Episode) return null;
        var c = card;
        return () => CardMenu(c);
    }

    // The card grammar: playlist / album — strip Play · Play next · Add to queue, rows Save · Add to playlist ▸ · Open ·
    // Pin · Share ▸; Liked drops Save; artist — strip Play, rows Follow · Open · Pin · Share ▸ · Go to artist radio (no
    // queue pair, no Add to playlist); show — Play · Open · Pin · Share ▸; a track uri — the thin track menu. Registered
    // AppActions render through `Actions.Menu`; the container verbs the action table carries only as descriptors are
    // explicit rows over the same `Actions.Services` seams (the sidebar's ShowModel precedent).
    static ContextMenuModel? CardMenu(HomeCard card)
    {
        var s = Actions.Services;
        var uri = EntityUri.Parse(card.Uri);
        if (!uri.IsValid) return null;
        string title = card.Title;
        string? art = card.ImageUrl;
        string plain = HomeCards.PlainText(card.Subtitle);

        if (card.Kind == HomeCardKind.Track)
        {
            if (uri.Kind != EntityKind.Track) return null;
            Track.EnsureActions();
            return Track.Menu([Entities.Track(uri)], new Track.MenuOptions(ShowGoToAlbum: true));
        }

        var route = RouteFor(in card);
        bool liked = card.Kind == HomeCardKind.Liked;
        var targetKind = card.Kind switch
        {
            HomeCardKind.Artist => TargetKind.Artist,
            HomeCardKind.Album => TargetKind.Album,
            HomeCardKind.Playlist or HomeCardKind.Liked => TargetKind.Playlist,
            _ => TargetKind.None,
        };
        var ctx = new ActionContext(new ActionTarget(targetKind, Array.Empty<Track>(), uri, title, PlaylistHost.None), s);
        var rows = new List<MenuFlyoutItem>(8);
        AppBarCommand[] strip;
        string subtitle;
        bool circular = card.Kind == HomeCardKind.Artist;

        switch (card.Kind)
        {
            case HomeCardKind.Artist:
                strip = Actions.Menu.Strip(in ctx, [ActionId.PlayContext]);
                rows.Add(SaveRow(s, uri, follow: true));
                rows.Add(OpenRow(route));
                if (PinRow(s, route) is { } pinA) rows.Add(pinA);
                if (Actions.Menu.Share(in ctx) is { } shareA) rows.Add(shareA);
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.GoToArtistRadio), ActionIcons.Resolve(ActionIcons.Radio),
                    s.StartRadio is not null, () => Actions.Services.StartRadio?.Invoke(uri)));
                subtitle = Actions.Menu.KindWord(TargetKind.Artist);
                break;
            case HomeCardKind.Podcast or HomeCardKind.Audiobook:
                strip = [];
                if (Actions.Menu.Row(ActionId.PlayContext, in ctx) is { } playS) rows.Add(playS);
                rows.Add(OpenRow(route));
                if (PinRow(s, route) is { } pinS) rows.Add(pinS);
                if (Actions.Menu.Share(in ctx) is { } shareS) rows.Add(shareS);
                subtitle = plain.Length > 0 ? plain : Loc.Get(Strings.Menu.KindPodcast);
                break;
            default:
                strip = Actions.Menu.Strip(in ctx, [ActionId.PlayContext, ActionId.PlayContextNext, ActionId.AddContextToQueue]);
                if (!liked) rows.Add(SaveRow(s, uri, follow: false));
                if (Actions.Menu.Row(ActionId.AddContextToPlaylist, in ctx) is { } add) rows.Add(add);
                rows.Add(OpenRow(route));
                if (PinRow(s, route) is { } pin) rows.Add(pin);
                if (Actions.Menu.Share(in ctx) is { } share) rows.Add(share);
                subtitle = plain.Length > 0 ? plain : Actions.Menu.KindWord(targetKind);
                break;
        }
        return new ContextMenuModel(strip, rows, Actions.Menu.Header(art, title, subtitle, circular));
    }

    static MenuFlyoutItem SaveRow(ActionServices s, EntityUri uri, bool follow)
    {
        bool saved = s.IsSaved?.Invoke(uri) ?? false;
        string label = follow
            ? Loc.Get(saved ? Strings.Artist.Following : Strings.Artist.Follow)
            : Loc.Get(saved ? Strings.Menu.Saved : Strings.Menu.SaveToLibrary);
        return new MenuFlyoutItem(label, ActionIcons.Resolve(ActionIcons.Save, saved), s.SetSaved is not null,
            () => Actions.Services.SetSaved?.Invoke(uri, !(Actions.Services.IsSaved?.Invoke(uri) ?? false)));
    }

    static MenuFlyoutItem OpenRow(Shell.Route route)
        => new(Loc.Get(Strings.Menu.Open), ActionIcons.Resolve(ActionIcons.Open), !route.IsNone,
               () => Shell.GoTo(route, HomeOrigin));

    static MenuFlyoutItem? PinRow(ActionServices s, Shell.Route route)
    {
        if (route.IsNone || s.SetPinned is null) return null;
        bool pinned = s.IsPinned?.Invoke(route) ?? false;
        return Actions.Menu.Pin(pinned,
            () => Actions.Services.SetPinned?.Invoke(route, true),
            () => Actions.Services.SetPinned?.Invoke(route, false));
    }
}
