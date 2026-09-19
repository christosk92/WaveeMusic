// ── Entities/Home.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the row shell, the greeting, the facet strip, the customize entry, every module shell, the Fold tile, the what's-new
// timeline, the section append preloader, and the UI half of HomeCardNav
//
// Role: UI
// Owner: P (stream P2)
// Wave: 5
// Budget: 1600 lines
// Spec: ch 10 §1-§6 (row shell, greeting, facets, tail), ch 11 §1-§6 (module shells, Fold tile, timeline), ch 12 W18
//       (the customize entry) and §8 (the append preloader)
//
// ── THE SHELLS ARE PURE FUNCTIONS OF (group, width) ──────────────────────────────────────────────────────────────────
//
// Each shell arranges ONE card skin (`Home.Cards.UI.cs`) into the layout the prototype gives it, and nothing else: no
// data access beyond the handles it is given, no hooks, no state. Every width-adaptive shell reads its column count
// from `HomeModuleLayout` — the SAME member the page's estimator reads — because an estimate that disagrees with the
// rendered height re-pins the scroll anchor mid-scroll (ch 11 §0.8).
//
// Every multi-column module is a real star GRID, never a flex row of Grow cells: a row divides no space during
// measure, so wrapping text reports a one-line height and an ancestor clip cuts glyphs permanently (ch 11 §9.2).
//
// ── CHROME BELONGS TO THE ENTITY ─────────────────────────────────────────────────────────────────────────────────────
//
// Drag-out and the context menu are applied ONCE per card by `Keyed`, so right-click and drag work identically on a mix
// cell, a station row, a quick tile and a book row (ch 11 §0.10). Track and Episode cards are never drag sources: the
// feed carries only a uri for either (ch 10 W15). Every navigation out of Home carries `HomeCardNav.HomeOrigin`, so a
// destination's masthead reads `Home › Browse › X` (ch 10 §0.13).
//
// ── PAGED SHELVES ────────────────────────────────────────────────────────────────────────────────────────────────────
//
// `PagedShelf` is retained: its item snapshot is re-pushed and compared by value, so every shelf item is a
// `HomeCards.ShelfItem` carrying what the card PAINTS. The ‹ chevron's glyph is PASSED (the engine default is "", which
// shipped a blank puck on every 0.2.9 shelf — ch 11 §9.2), and Recents / Podcasts / the facet Shelf carry the same
// content-fingerprint Key the Feed and the Fold deck do (ch 11 parity 85).

using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ══ 1. THE CARD NAVIGATION — the UI half (contract §3; the CORE half is P1's `Home.cs`) ══════════════════════════════

public static partial class HomeCardNav
{
    /// <summary>The origin every drill out of Home hands over (ch 10 §0.13). Resolved per read: the label is live Loc.</summary>
    public static Shell.NavOrigin HomeOrigin => new(Loc.Get(Strings.Nav.Home), new Shell.Route(Shell.RouteKind.Home));

    /// <summary>Open a card: a Track/Episode PLAYS (there is no episode page, and a track is not a destination); anything
    /// else navigates to <see cref="RouteFor"/>. <paramref name="origin"/> is what the destination's masthead composes
    /// with — the landing passes <see cref="HomeOrigin"/>, a drill page passes its own or none.</summary>
    public static void Open(in HomeCard card, Shell.NavOrigin? origin = null)
    {
        if (card.IsBlank) return;
        if (HomeCardPlayRouting.PlaysAsItem(card.Kind)) { Play(in card); return; }
        var route = RouteFor(in card);
        if (route.IsNone) return;
        Shell.GoTo(route, origin);
    }

    /// <summary>Drill into a HOME section (<c>home-section:</c>). A one-card Home section still opens a one-cell page —
    /// the one-card shortcut is Browse's rule only (ch 12 §0.19).</summary>
    public static void OpenSection(HomeSectionView s, Shell.NavOrigin? origin = null)
    {
        var route = SectionRoute(s, browse: false);
        if (route.IsNone) return;
        Shell.GoTo(route, origin ?? HomeOrigin);
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
    /// is the one outcome the user can act on, so it also reaches them as a toast (ch 10 §6).</summary>
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

    /// <summary>Shuffle ARMS the mode before starting the context — without it the hero's two buttons did the identical
    /// thing (ch 10 §6, parity 19).</summary>
    public static void Shuffle(in HomeCard card)
    {
        Playback.SetShuffle(true);
        Play(in card);
    }

    /// <summary>The hero's ♥: a save toggle through the library seam. The glyph never re-skins (parity 77).</summary>
    public static void Like(in HomeCard card)
    {
        if (card.Uri.Length == 0 || Actions.Services.SetSaved is not { } set) return;
        var uri = EntityUri.Parse(card.Uri);
        set(uri, !(Actions.Services.IsSaved?.Invoke(uri) ?? false));
    }

    /// <summary>The card as a drag SOURCE, or null for a Track/Episode/blank card (ch 11 §6.4). The payload factory is
    /// gesture-cold: it runs once, at promotion.</summary>
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
    /// card, an unknown scheme — ch 11 §6.5).</summary>
    public static Func<ContextMenuModel?>? MenuOf(in HomeCard card)
    {
        if (card.IsBlank || card.Uri.Length == 0 || card.Kind == HomeCardKind.Episode) return null;
        var c = card;
        return () => CardMenu(c);
    }

    // The card grammar (ch 11 §6.5): playlist / album — strip Play · Play next · Add to queue, rows Save · Add to
    // playlist ▸ · Open · Pin · Share ▸; Liked drops Save; artist — strip Play, rows Follow · Open · Pin · Share ▸ · Go
    // to artist radio (no queue pair, no Add to playlist); show — Play · Open · Pin · Share ▸; a track uri — the thin
    // track menu. Registered AppActions render through `Actions.Menu`; the container verbs the action table carries only
    // as descriptors are explicit rows over the same `Actions.Services` seams (the sidebar's ShowModel precedent).
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

// ══ 2. THE MODULE SHELLS (contract §3 + ch 11 §1.1) ══════════════════════════════════════════════════════════════════

public static partial class HomeModules
{
    // ── 2.1 headers ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE module header grammar (ch 11 W28): the display-face 20/28 title with its subtitle in the SAME
    /// paragraph (the 12-px run sits on the 20-px baseline), a 12-DIP chevron ONLY when there is somewhere to drill, and
    /// a trailing tools slot. A non-drillable header is the identical label with no chevron and no click wrapper.</summary>
    public static Element ModuleHeader(string title, string? subtitle, Element? tools, Action? open)
    {
        Element label = subtitle is { Length: > 0 } sub
            ? Design.Type.ModuleHeader(title, sub) with
              { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis }
            : Design.Type.ModuleHeader(title) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        Element titleEl = open is null
            ? new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, Children = [label] }
            : new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f,
                OnClick = open, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                Children = [label, Icon(Icons.ChevronRight, 12f, Tok.TextTertiary)],
            };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = [titleEl, new BoxEl { Grow = 1f, MinWidth = 0f }, tools ?? new BoxEl()],
        };
    }

    /// <summary>The chevron header the Fold deck and Browse's shelves share. A blank title is no header at all — an
    /// empty box, never an empty row that still pays the band's height.</summary>
    public static Element DrillHeader(string title, Action? open)
        => string.IsNullOrWhiteSpace(title) ? new BoxEl() : ModuleHeader(title, null, null, open);

    /// <summary>The drill gate (ch 11 W28): the chevron is armed only when the group names something the section page
    /// can open — a uri, a total to page, or cards in hand.</summary>
    static Action? OpenOf(HomeGroup g, Action<HomeGroup>? openSection)
        => openSection is null || (g.Uri is not { Length: > 0 } && g.TotalCount <= 0 && g.Cards.Count == 0)
            ? null
            : () => openSection(g);

    /// <summary>Head + content at the 12-DIP head gap. A group with no title renders NO header; the skeleton seed's " "
    /// title DOES render one (its bone is the loaded header's silhouette, and the estimator counts it as titled).</summary>
    static Element Module(HomeGroup g, string? sub, Element content, Action<HomeGroup>? openSection) => new BoxEl
    {
        Direction = 1, Gap = HomeModuleLayout.HeadGap, MinWidth = 0f,
        Children = g.Title is { Length: > 0 } title
            ? [ModuleHeader(title, sub, null, OpenOf(g, openSection)), content]
            : [content],
    };

    /// <summary>A server-titled module around arbitrary content (the hero band's wrapper).</summary>
    public static Element SourceModule(HomeGroup g, Element content, Action<HomeGroup>? openSection = null)
        => Module(g, g.Subtitle, content, openSection);

    // ── 2.2 the chrome + grid plumbing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Key a card by (kind, uri) so a recycled row rebinds to the right subtree, and apply the entity chrome
    /// (drag + menu) once. A blank skeleton card keys by position — every blank uri is "".</summary>
    static Element Keyed(Element el, HomeGroupKind kind, in HomeCard c, int index, IOverlayService? host)
    {
        if (el is not BoxEl b) return el;
        string key = c.Uri.Length > 0
            ? HomeModuleLayout.RowKey(kind, c.Uri)
            : "home-" + kind + ":blank:" + index.ToString(CultureInfo.InvariantCulture);
        var keyed = b with { Key = key, Draggable = HomeCardNav.DragOf(in c) };
        return HomeCardNav.MenuOf(in c) is { } menu && !Controls.IsNullOverlay(host) ? keyed.WithContextMenu(host!, menu) : keyed;
    }

    /// <summary>A uniform column grid of equal STAR tracks. <c>AlignSelf = Stretch</c> is load-bearing: a star grid
    /// measured against hug width collapses to content (ch 11 §9.2).</summary>
    static Element Grid(int columns, float colGap, float rowGap, Element[] cards)
    {
        if (cards.Length == 0) return new BoxEl();
        var tracks = new TrackSize[Math.Max(1, columns)];
        for (int i = 0; i < tracks.Length; i++) tracks[i] = TrackSize.Star();
        return Ui.Grid(tracks, colGap, rowGap, float.NaN, cards) with { AlignSelf = FlexAlign.Stretch };
    }

    /// <summary>Two star tracks at explicit weights — the editorial 1.08 : 1 and the even split.</summary>
    static Element TwoColumn(float leftWeight, float gap, Element left, Element right)
        => Ui.Grid([TrackSize.Star(leftWeight), TrackSize.Star(1f)], gap, gap, float.NaN, left, right);

    // ── 2.3 A2 · B · D · F · G — the width-adaptive grids ────────────────────────────────────────────────────────────

    /// <summary>Discover Weekly + Release Radar as a deliberate 2-up (1 column at ≤ 760).</summary>
    public static Element WeeklyPair(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            var cells = new Element[g.Cards.Count];
            for (int i = 0; i < cells.Length; i++)
            {
                var c = g.Cards[i];
                cells[i] = Keyed(HomeCards.WeeklyCard(in c, () => HomeCardNav.Open(in c, HomeCardNav.HomeOrigin)), g.Kind, in c, i, host);
            }
            return Module(g, g.Subtitle, Grid(HomeModuleLayout.Columns(g.Kind, width), Spacing.M, Spacing.M, cells), openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    /// <summary>Jump back in: at most <see cref="HomeModuleLayout.QuickShown"/> tiles, "Your 8 most-opened of N".</summary>
    public static Element Quick(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            int shown = Math.Min(g.Cards.Count, HomeModuleLayout.QuickShown);
            var cells = new Element[shown];
            for (int i = 0; i < shown; i++)
            {
                var c = g.Cards[i];
                cells[i] = Keyed(HomeCards.QuickTile(in c, () => HomeCardNav.Open(in c, HomeCardNav.HomeOrigin), () => HomeCardNav.Play(in c)),
                                 g.Kind, in c, i, host);
            }
            return Module(g, Strings.Home.MostOpenedOf(shown, g.Cards.Count),
                          Grid(HomeModuleLayout.Columns(g.Kind, width), Spacing.M, Spacing.M, cells), openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    /// <summary>The daily-mix band: ONE plate, gap 0, per-cell rules — the numeral carries the identity (ch 11 §0.5).</summary>
    public static Element MixBand(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            int columns = HomeModuleLayout.Columns(g.Kind, width);
            var cells = new Element[g.Cards.Count];
            for (int i = 0; i < cells.Length; i++)
            {
                var c = g.Cards[i];
                cells[i] = Keyed(HomeCards.MixSegment(in c, i + 1, () => HomeCardNav.Open(in c, HomeCardNav.HomeOrigin),
                                                      leading: i % columns != 0, above: i >= columns), g.Kind, in c, i, host);
            }
            var tracks = new TrackSize[Math.Max(1, columns)];
            for (int i = 0; i < tracks.Length; i++) tracks[i] = TrackSize.Star();
            var band = new BoxEl
            {
                Direction = 1, MinWidth = 0f, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children = [cells.Length == 0 ? new BoxEl() : (Element)Ui.Grid(tracks, 0f, 0f, float.NaN, cells)],
            };
            return Module(g, Strings.Home.OneSeries(g.Cards.Count), band, openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    /// <summary>Made for you: at most <see cref="HomeModuleLayout.ChipCardsShown"/> chip cards.</summary>
    public static Element ChipCards(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            int shown = Math.Min(g.Cards.Count, HomeModuleLayout.ChipCardsShown);
            var cells = new Element[shown];
            for (int i = 0; i < shown; i++)
            {
                var c = g.Cards[i];
                cells[i] = Keyed(HomeCards.ChipCard(in c, () => HomeCardNav.Open(in c, HomeCardNav.HomeOrigin)), g.Kind, in c, i, host);
            }
            return Module(g, Strings.Home.MixesFromArtists(g.Cards.Count),
                          Grid(HomeModuleLayout.Columns(g.Kind, width), Spacing.M, Spacing.M, cells), openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    /// <summary>The radio dial: a COLUMN gap only, no row gap — twenty stations read as one dial folded in half. The
    /// grid is keyed on its column count so a column flip rebuilds rather than rebinds (ch 11 §9.2).</summary>
    public static Element Radio(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            int shown = Math.Min(g.Cards.Count, HomeModuleLayout.RadioShown);
            var cells = new Element[shown];
            for (int i = 0; i < shown; i++)
            {
                var c = g.Cards[i];
                cells[i] = Keyed(HomeCards.RadioRow(in c, () => HomeCardNav.Open(in c, HomeCardNav.HomeOrigin), () => HomeCardNav.Play(in c)),
                                 g.Kind, in c, i, host);
            }
            int columns = HomeModuleLayout.Columns(g.Kind, width);
            return Module(g, Strings.Home.StationCount(g.Cards.Count),
                          Grid(columns, Spacing.XXL, 0f, cells) with { Key = "radio-grid:" + columns.ToString(CultureInfo.InvariantCulture) },
                          openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    // ── 2.4 H1 · H2 — the tabular stacks and their pairing ───────────────────────────────────────────────────────────

    /// <summary>Up next: at most <see cref="HomeModuleLayout.QueueShown"/> queue rows at Gap 0 with a divider on all but
    /// the last — a table, not six cards.</summary>
    public static Element UpNext(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
    {
        int shown = Math.Min(g.Cards.Count, HomeModuleLayout.QueueShown);
        long queued = 0;
        var rows = new Element[shown];
        for (int i = 0; i < shown; i++)
        {
            var c = g.Cards[i];
            queued += c.DurationMs;
            rows[i] = Keyed(HomeCards.QueueRow(in c, () => HomeCardNav.Open(in c, HomeCardNav.HomeOrigin), last: i == shown - 1), g.Kind, in c, i, host);
        }
        return Module(g, Strings.Home.QueuedSuggestions(HomeCardText.Duration(queued), g.Cards.Count),
                      new BoxEl { Direction = 1, Gap = 0f, MinWidth = 0f, Children = rows }, openSection);
    }

    /// <summary>Audiobooks: at most <see cref="HomeModuleLayout.BooksShown"/> rows at the DELIBERATE 2-DIP stack gap
    /// (ch 11 §9.1 #5).</summary>
    public static Element Audiobooks(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
    {
        int shown = Math.Min(g.Cards.Count, HomeModuleLayout.BooksShown);
        var rows = new Element[shown];
        for (int i = 0; i < shown; i++)
        {
            var c = g.Cards[i];
            rows[i] = Keyed(HomeCards.BookRow(in c, () => HomeCardNav.Open(in c, HomeCardNav.HomeOrigin)), g.Kind, in c, i, host);
        }
        return Module(g, Strings.Home.IncludedWithPremium(g.Cards.Count),
                      new BoxEl { Direction = 1, Gap = Spacing.XXS, MinWidth = 0f, Children = rows }, openSection);
    }

    /// <summary>Episodes and audiobooks SIDE BY SIDE at ≥ <see cref="HomeModuleLayout.SplitEvenMin"/>, stacked below at
    /// the module gap.</summary>
    public static Element SplitEven(Element left, Element right)
        => Responsive.Of(width => width >= HomeModuleLayout.SplitEvenMin
            ? TwoColumn(1f, Spacing.XXL, left, right)
            : new BoxEl { Direction = 1, Gap = HomeModuleLayout.Gap(width), MinWidth = 0f, Children = [left, right] },
            fallback: HomeModuleLayout.FallbackWidth);

    /// <summary>A degraded split keeps the survivor in its original half-column above the split threshold.</summary>
    public static Element SplitSingle(Element survivor)
        => Responsive.Of(width => width >= HomeModuleLayout.SplitEvenMin
            ? TwoColumn(1f, Spacing.XXL, survivor, new BoxEl())
            : survivor,
            fallback: HomeModuleLayout.FallbackWidth);

    // ── 2.5 J · Editors' picks ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One feature card beside a column of three crowd rows at 1.08 : 1 (≥ 980), stacked below.</summary>
    public static Element Editorial(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
        => Responsive.Of(width =>
        {
            if (g.Cards.Count == 0) return new BoxEl();
            var feature = g.Cards[0];
            int companions = Math.Min(HomeModuleLayout.EditorialCompanions, g.Cards.Count - 1);
            Element left = Keyed(HomeCards.FeatureCard(in feature, CardMeta(in feature),
                                                       () => HomeCardNav.Open(in feature, HomeCardNav.HomeOrigin),
                                                       () => HomeCardNav.Play(in feature)), g.Kind, in feature, 0, host);
            var crowd = new Element[companions];
            for (int i = 0; i < companions; i++)
            {
                var c = g.Cards[i + 1];
                crowd[i] = Keyed(HomeCards.CrowdRow(in c, () => HomeCardNav.Open(in c, HomeCardNav.HomeOrigin), () => HomeCardNav.Play(in c)),
                                 g.Kind, in c, i + 1, host);
            }
            Element right = new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = crowd };
            Element content = width >= HomeModuleLayout.EditorialMin && companions > 0
                ? TwoColumn(1.08f, Spacing.L, left, right)
                : new BoxEl { Direction = 1, Gap = Spacing.L, MinWidth = 0f, Children = companions > 0 ? [left, right] : [left] };
            return Module(g, Loc.Get(Strings.Home.EditorsPicksSub), content, openSection);
        }, fallback: HomeModuleLayout.FallbackWidth);

    // ── 2.6 C · K — the paged shelves ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The recents rail. The header drills to the app's OWN Recents page and is armed unconditionally; the
    /// second line names the entity TYPE; artists are circular (ch 11 W9).</summary>
    public static Element Recents(HomeGroup g, IOverlayService? host, Action? openAll)
        => ShelfOf(g, host, ItemsOf(g, ShelfSecond.Kind, circularArtists: true),
                   g.Title is { Length: > 0 } t ? ModuleHeader(t, null, null, openAll) : new BoxEl(), ":recents");

    /// <summary>A source-owned show shelf.</summary>
    public static Element Podcasts(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
        => ShelfOf(g, host, ItemsOf(g, ShelfSecond.Subtitle, circularArtists: false),
                   g.Title is { Length: > 0 } t ? ModuleHeader(t, g.Subtitle, null, OpenOf(g, openSection)) : new BoxEl(), ":podcasts");

    /// <summary>The facet page's "any server section" shelf: mixed entities, artists circular.</summary>
    public static Element Shelf(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
        => ShelfOf(g, host, ItemsOf(g, ShelfSecond.Subtitle, circularArtists: true),
                   g.Title is { Length: > 0 } t ? ModuleHeader(t, g.Subtitle, null, OpenOf(g, openSection)) : new BoxEl(), ":shelf");

    /// <summary>The discover feed: one shelf of the coalesced baseline recommendations, each card's second line the
    /// section's REASON when it has no description (ch 11 W27).</summary>
    public static Element Feed(HomeGroup g, IOverlayService? host, Action<HomeGroup>? openSection = null)
        => ShelfOf(g, host, ItemsOf(g, ShelfSecond.SubtitleOrEyebrow, circularArtists: false),
                   g.Title is { Length: > 0 } t
                       ? ModuleHeader(t, Strings.Home.RecommendationsWithReason(g.Cards.Count), null, OpenOf(g, openSection))
                       : new BoxEl(), ":feed");

    enum ShelfSecond : byte { Kind, Subtitle, SubtitleOrEyebrow }

    static HomeCards.ShelfItem[] ItemsOf(HomeGroup g, ShelfSecond second, bool circularArtists)
    {
        var items = new HomeCards.ShelfItem[g.Cards.Count];
        for (int i = 0; i < items.Length; i++)
        {
            var c = g.Cards[i];
            string line = second switch
            {
                ShelfSecond.Kind => KindLabel(c.Kind),
                ShelfSecond.SubtitleOrEyebrow => HomeCards.PlainText(c.Subtitle) is { Length: > 0 } s ? s : c.Eyebrow ?? "",
                _ => HomeCards.PlainText(c.Subtitle),
            };
            items[i] = HomeCards.ShelfItemOf(in c, line, circularArtists && c.Kind == HomeCardKind.Artist);
        }
        return items;
    }

    static Element ShelfOf(HomeGroup g, IOverlayService? host, HomeCards.ShelfItem[] items, Element header, string keySuffix)
        => PagedShelf.Create<HomeCards.ShelfItem>(items,
            (item, i, cardW) =>
            {
                var c = item.Card;
                return HomeCards.ShelfCell(in item, cardW, () => HomeCardNav.Open(in c, HomeCardNav.HomeOrigin),
                                           () => HomeCardNav.Play(in c), HomeCardNav.DragOf(in c), host, HomeCardNav.MenuOf(in c));
            },
            cardHeight: HomeModuleLayout.ShelfCardHeight,
            header: header,
            minCardW: HomeModuleLayout.ShelfCardMin, maxCardW: HomeModuleLayout.ShelfCardMax,
            gap: Spacing.M, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            keyOf: (item, i) => HomeModuleLayout.SourceCardKey(g, item.Card))
           with { Key = HomeModuleLayout.SourceGroupKey(g) + keySuffix };

    // ── 2.7 L · THE FOLD DECK ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Home's section directory, Home's Charts row and Browse's Charts band — ONE row factory: one card height,
    /// one key shape, rows 1, at most two tiles filling the row (two-up at ≥ 892 of content). No tile eyebrow: the
    /// 0.2.9 eyebrow parameters had no caller (ch 10 §9 "dead rule" — code wins).</summary>
    public static Element FoldDeck(IReadOnlyList<HomeSectionView> sections, string title, Action<HomeSectionView> openTile,
                                   Action? openHeader = null)
        => PagedShelf.Create(sections,
            (section, i, cardW) => HomeFoldTile.Create(section, cardW, null, openTile),
            cardHeight: static _ => HomeModuleLayout.FoldCardHeight,
            header: DrillHeader(title, openHeader),
            minCardW: HomeModuleLayout.FoldCardMin, maxCardW: HomeModuleLayout.FoldCardMax,
            gap: Spacing.M, rows: 1, maxColumns: 2, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            keyOf: static (section, i) => "home-fold-tile:" + (section.Uri is { Length: > 0 } u ? u : i.ToString(CultureInfo.InvariantCulture)))
           with { Key = HomeModuleLayout.SectionSetKey(sections) + ":fold" };

    // ── 2.8 THE DRILL GRID (ch 11 W33, ch 12 W3-W8) ──────────────────────────────────────────────────────────────────

    /// <summary>The drill grid — the Home section page's and Browse's FlattenOne body. Fit decides the COLUMN COUNT
    /// only; the covers are square because <see cref="AspectGridVirtualLayout"/> derives the row height from the ARRANGED
    /// cell width (ch 12 §0.3). <paramref name="charts"/> is the DECODED <c>SectionFlags.Chart</c> of the section,
    /// threaded in — never a uri lookup (ch 12 trap 10): it blanks the subtitle and drops that rung from the cell
    /// reserve, the same bool in both places.</summary>
    public static Element SectionGrid(IReadOnlyList<HomeCard> cards, string? sectionKey, float width,
        Action<HomeCard> open, (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? onScrollGeometryChanged = null,
        string? highlightQuery = null, int titleLines = 1, bool charts = false)
    {
        // Subscribing reads, taken by WHICHEVER render calls this (the page's responsive box): a hydrated card row must
        // re-describe its cell, and the grid host's props gate otherwise sees the same card list instance.
        var scope = Entities.Current;
        uint epoch = scope.Playlists.Changed.Value + scope.Albums.Changed.Value * 3u + scope.Artists.Changed.Value * 5u
                     + scope.Shows.Changed.Value * 7u + scope.Tracks.Changed.Value * 11u + scope.Episodes.Changed.Value * 13u;
        return Embed.Comp(new GridProps(cards, sectionKey, width, open, onScrollGeometryChanged, highlightQuery, titleLines, charts, epoch),
                          static () => new SectionGridHost());
    }

    sealed record GridProps(IReadOnlyList<HomeCard> Cards, string? SectionKey, float Width, Action<HomeCard> Open,
        (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? Scroll, string? Query, int TitleLines,
        bool Charts, uint Epoch)
    {
        // Delegates are behaviour, not data (component-props-contract): the gate compares the card list by reference,
        // the scalars by value, and only whether a scroll watch exists.
        public bool Equals(GridProps? o) => o is not null && ReferenceEquals(Cards, o.Cards)
            && string.Equals(SectionKey, o.SectionKey, StringComparison.Ordinal) && Width == o.Width
            && string.Equals(Query, o.Query, StringComparison.Ordinal) && TitleLines == o.TitleLines && Charts == o.Charts
            && Epoch == o.Epoch && Scroll.HasValue == o.Scroll.HasValue;

        public override int GetHashCode() => HashCode.Combine(Cards.Count, Width, TitleLines, Charts, Epoch);
    }

    sealed class SectionGridHost : Component
    {
        GridProps? _latest;

        public override Element Render()
        {
            var p = UseProps<GridProps>();
            _latest = p;
            var overlay = UseContext(Overlay.Service);
            IOverlayService? host = Controls.IsNullOverlay(overlay) ? null : overlay;
            var (columns, _) = FillRowVirtualLayout.Fit(p.Width, HomeModuleLayout.ShelfCardMin, HomeModuleLayout.ShelfCardMax,
                                                        HomeModuleLayout.GridGap);
            columns = Math.Max(1, columns);
            string tier = columns.ToString(CultureInfo.InvariantCulture) + ":" + p.TitleLines.ToString(CultureInfo.InvariantCulture);
            var cards = p.Cards;
            string key = p.SectionKey ?? "";
            return new VirtualListEl
            {
                ItemCount = cards.Count,
                ItemLayout = new AspectGridVirtualLayout(columns, 1f,
                    HomeModuleLayout.GridCardChromeFor(p.TitleLines, hasSubtitle: !p.Charts), HomeModuleLayout.GridGap),
                RenderItem = i => (uint)i < (uint)cards.Count ? Cell(cards[i], i, tier, p, host) : new BoxEl(),
                KeyOf = i => key + "" + ((uint)i < (uint)cards.Count && cards[i].Uri.Length > 0
                    ? cards[i].Uri : i.ToString(CultureInfo.InvariantCulture)),
                Overscan = 2,
                Grow = 1f, Shrink = 1f, MinHeight = 0f,
                OnScrollGeometryChanged = p.Scroll,
            };
        }

        Element Cell(HomeCard card, int index, string tier, GridProps p, IOverlayService? host)
        {
            var c = card;
            bool circular = c.Kind == HomeCardKind.Artist;
            ChartTitleMatch.TryFind(c.Title, p.Query, out int matchStart, out int matchLen);
            Element titleEl = matchLen > 0
                ? Controls.SearchHighlight(c.Title, matchStart, matchLen, 14f, 600, Tok.TextPrimary, p.TitleLines)
                : Design.Type.CardTitle(c.Title) with
                {
                    MaxLines = p.TitleLines, Wrap = p.TitleLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                };
            string sub = p.Charts ? "" : HomeCards.PlainText(c.Subtitle);
            var labels = new BoxEl
            {
                Direction = 1, Gap = 2f, MinWidth = 0f, AlignItems = circular ? FlexAlign.Center : FlexAlign.Start,
                Children = sub.Length > 0
                    ? [titleEl, Design.Type.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }]
                    : [titleEl],
            };
            var menu = HomeCardNav.MenuOf(in c);
            bool hasMenu = menu is not null && host is not null;
            var open = _latest!.Open;
            BoxEl shell = Controls.CardShell(new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Grow = 1f,
                Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, ClipToBounds = !circular,
                        Children = hasMenu
                            ? [Controls.ArtworkFill(c.ImageUrl, circular ? Radii.Full : Radii.Card),
                               Controls.NowPlayingOverlay(c.Uri, () => HomeCardNav.Play(in c), 44f, centred: true),
                               Controls.MoreCorner()]
                            : [Controls.ArtworkFill(c.ImageUrl, circular ? Radii.Full : Radii.Card),
                               Controls.NowPlayingOverlay(c.Uri, () => HomeCardNav.Play(in c), 44f, centred: true)],
                    },
                    labels,
                ],
            }, () => (_latest?.Open ?? open)(c), HomeCardNav.DragOf(in c));
            shell = shell with
            {
                Key = "home-section-card:" + tier + ":" + (c.Uri.Length > 0 ? c.Uri : index.ToString(CultureInfo.InvariantCulture)),
            };
            return hasMenu ? shell.WithContextMenu(host!, menu!) : shell;
        }
    }

    // ── 2.9 the page chrome the landing composes ─────────────────────────────────────────────────────────────────────

    /// <summary>The row shell (ch 10 §0.9): content capped at <see cref="Design.Size.PageMaxW"/> and centred, the 36-DIP
    /// page gutter, and the row's own top/bottom. The list keeps measuring at its full cross size (the scrollbar stays at
    /// the window edge). The single child is KEYED so a shell recycled for another row replaces, never positionally
    /// rebinds, an incompatible subtree (ch 10 trap 2).</summary>
    public static Element RowShell(Element child, string contentKey, float top, float bottom) => new BoxEl
    {
        Direction = 0, Justify = FlexJustify.Center, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = Design.Size.PageMaxW,
                Padding = new Edges4(Spacing.PageWide, top, Spacing.PageWide, bottom),
                Children = [child with { Key = contentKey }],
            },
        ],
    };

    /// <summary>"{n} songs · by {owner}" — the owner from <see cref="HomeCard.OwnerName"/>, never the subtitle (which is
    /// `description ?? owner` and put the whole blurb after "by"); an episode's duration instead.</summary>
    public static string CardMeta(in HomeCard c)
    {
        int n = c.TrackCount;
        string count = c.Kind == HomeCardKind.Episode ? HomeCardText.Duration(c.DurationMs) : n > 0 ? Strings.Detail.SongCount(n) : "";
        string? owner = c.OwnerName;
        if (count.Length == 0) return owner ?? "";
        return owner is { Length: > 0 } o && c.Kind != HomeCardKind.Episode ? Strings.Home.SongsBy(count, o) : count;
    }

    /// <summary>The recents rail's caption: the entity TYPE (Liked reads "Playlist" — its title already says Liked).</summary>
    public static string KindLabel(HomeCardKind kind) => kind switch
    {
        HomeCardKind.Artist => Loc.Get(Strings.Home.Artist),
        HomeCardKind.Album => Loc.Get(Strings.Home.Album),
        HomeCardKind.Podcast or HomeCardKind.Audiobook => Loc.Get(Strings.Podcast.Show),
        HomeCardKind.Episode => Loc.Get(Strings.Podcast.Episodes),
        HomeCardKind.Track => Loc.Get(Strings.Detail.Column.Song),
        _ => Loc.Get(Strings.Nav.Playlist),
    };

    /// <summary>The greeting word, localized: the rule is <see cref="HomeLandingRules.GreetingWord"/> (server first, the
    /// local clock second); this only resolves its answer.</summary>
    public static string GreetingPart(string? serverGreeting, int localHour) => HomeLandingRules.GreetingWord(serverGreeting, localHour) switch
    {
        HomeGreetingWord.Server => serverGreeting!,
        HomeGreetingWord.Morning => Loc.Get(Strings.Home.GoodMorning),
        HomeGreetingWord.Afternoon => Loc.Get(Strings.Home.GoodAfternoon),
        _ => Loc.Get(Strings.Home.GoodEvening),
    };

    /// <summary>The signed-in account's greetable name, or null. A SUBSCRIBING read (the users table + the scope), so the
    /// row closure that calls it re-describes on a late login.</summary>
    public static string? GreetedName()
    {
        _ = Entities.ScopeEpoch.Value;
        _ = Entities.Current.Users.Changed.Value;
        var me = User.Me;
        if (!me.IsValid || !me.Knows(UserFields.Identity)) return null;
        string name = Entities.Strings.Resolve(me.NameId);
        return name.Length > 0 && !HomeLandingRules.LooksLikeHandle(name) ? name : null;
    }

    /// <summary>The hero eyebrow: "Good morning, Christos", plus " · your daylist" ONLY for a real daylist card
    /// (`Format == "daylist"`) — a spotlight album must never be captioned "your daylist" (ch 10 §0.3).</summary>
    public static string HeroEyebrow(in HomeCard hero, string? serverGreeting)
    {
        string part = GreetingPart(serverGreeting, DateTime.Now.Hour);
        string? who = GreetedName();
        if (!string.Equals(hero.Format, "daylist", StringComparison.Ordinal))
            return who is null ? part : Strings.Home.Greeting(part, who);
        string daylist = Loc.Get(Strings.Home.YourDaylist);
        return who is null ? part + " · " + daylist : Strings.Home.HeroEyebrow(part, who, daylist);
    }

    /// <summary>The standalone two-line greeting — ONLY when the page has no hero (ch 10 §0.3).</summary>
    static Element GreetingHero(string? serverGreeting)
    {
        string part = GreetingPart(serverGreeting, DateTime.Now.Hour);
        string greet = GreetedName() is { } who ? Strings.Home.Greeting(part, who) : part;
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Padding = new Edges4(0f, Spacing.S, 0f, 0f), MinWidth = 0f,
            Children = [Design.Type.PageHero(greet) with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                        Design.Type.TrackMeta(Loc.Get(Strings.Home.OnRotation))],
        };
    }

    /// <summary>The chips row's four arms (ch 10 W19): (a) hero + chips; (b) no hero + chips — the greeting above the
    /// strip; (c) hero, NO chips — the customize "⋯" alone, end-justified; (d) no customize — the body or a bare box.
    /// The strip reads the selection from <c>Home.SelectedFacet</c> itself; it is never a prop (ch 10 §1.2).</summary>
    public static Element GreetingBlock(string? serverGreeting, bool hasHero, IReadOnlyList<HomeChip>? chips, bool customize = true)
    {
        Element? hero = hasHero ? null : GreetingHero(serverGreeting);
        Element? chipRow = chips is { Count: > 0 }
            ? Embed.Comp(new HomeFacetStripProps(chips), static () => new HomeFacetStripView())
            : null;
        Element? body = hero is null ? chipRow : chipRow is null ? hero : new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f, Children = [hero, chipRow],
        };
        Element? entry = customize ? Embed.Comp(static () => new HomeCustomizeEntry()) with { Key = "home-customize-entry" } : null;
        if (entry is null) return body ?? new BoxEl();
        if (body is null)
            return new BoxEl { Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [entry] };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Start, Gap = Spacing.S, MinWidth = 0f,
            Children = [new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Children = [body] }, entry],
        };
    }

    /// <summary>The what's-new timeline row (a component reading the notification centre).</summary>
    public static Element Timeline() => Embed.Comp(static () => new HomeTimelineView()) with { Key = "home-timeline" };

    /// <summary>The page's closing tail: two full-width editorial destinations 20 apart — Concerts, then Browse
    /// (ch 10 W5).</summary>
    public static Element Tail() => new BoxEl
    {
        Direction = 1, Gap = Spacing.XL, MinWidth = 0f,
        Children =
        [
            Destination("home-concerts-editorial", Loc.Get(Strings.Concerts.LiveMusic), Loc.Get(Strings.Concerts.HomeTitle),
                        Loc.Get(Strings.Concerts.HomeSubtitle), Loc.Get(Strings.Concerts.Explore), Icons.Calendar,
                        static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Concerts), HomeCardNav.HomeOrigin)),
            Destination("home-browse-editorial", Loc.Get(Strings.Browse.Eyebrow), Loc.Get(Strings.Browse.HomeTitle),
                        Loc.Get(Strings.Browse.HomeSubtitle), Loc.Get(Strings.Browse.ExploreAll), Icons.Globe,
                        static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse), HomeCardNav.HomeOrigin)),
        ],
    };

    static Element Destination(string key, string eyebrow, string title, string subtitle, string action, string glyph, Action onClick)
        => Responsive.Of(width =>
        {
            var m = HomeLandingRules.WideEditorial(width);
            float artW = Math.Clamp(width * m.ArtFraction, m.ArtMin, MathF.Min(m.ArtMax, width));
            float copyW = MathF.Max(180f, width - artW + MathF.Min(96f, artW * 0.42f));
            return new BoxEl
            {
                Key = key, Height = m.Height, MinWidth = 0f, ZStack = true, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Card),
                Fill = Tok.FillCardDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
                Cursor = CursorId.Hand, OnClick = onClick,
                Children =
                [
                    // The art slot: a quiet accent ground with the destination's glyph. The procedural EditorialArt of
                    // 0.2.9 (#86) is the concert surface's component, not Home's — see the P2 report.
                    new BoxEl
                    {
                        Direction = 0, Justify = FlexJustify.End, HitTestVisible = false,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = artW, Height = m.Height, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Gradient = GradientDown(new GradientStop(0f, Tok.AccentDefault with { A = 0.22f }),
                                                        new GradientStop(1f, Tok.AccentDefault with { A = 0.06f })),
                                Children = [Icon(glyph, 64f, Tok.AccentTextPrimary)],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        HitTestVisible = false,
                        Gradient = GradientRight(new GradientStop(0f, Tok.FillCardDefault), new GradientStop(0.52f, Tok.FillCardDefault),
                                                 new GradientStop(1f, Tok.FillCardDefault with { A = 0f })),
                    },
                    new BoxEl
                    {
                        Direction = 1, Width = copyW, Padding = Edges4.All(m.Padding), Gap = Spacing.S,
                        Justify = FlexJustify.Center, HitTestVisible = false,
                        Children =
                        [
                            Design.Type.Eyebrow(eyebrow) with { Color = Design.Accent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            Design.Type.PageHero(title) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                            Body(subtitle) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = m.Lines, Trim = TextTrim.CharacterEllipsis },
                            new BoxEl
                            {
                                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                                Children = [BodyStrong(action) with { Color = Design.Accent.Decor }, Icon(Icons.ChevronRight, 14f, Tok.TextSecondary)],
                            },
                        ],
                    },
                ],
            };
        }, fallback: HomeModuleLayout.FallbackWidth);
}

// ══ 3. THE FACET STRIP (ch 10 W9, §3 facet rows) ═════════════════════════════════════════════════════════════════════

/// <summary>The facet strip's re-pushed props: the server's chips, nothing else. The SELECTION is not a prop — the strip
/// reads <c>Home.SelectedFacet</c> inside Render (ch 10 §1.2).</summary>
public sealed record HomeFacetStripProps(IReadOnlyList<HomeChip> Chips);

/// <summary>An underline TAB strip, never pills: 14/600 labels secondary → primary, a 3-DIP accent underline inset 12
/// whose slot is always reserved, a 1-DIP divider under the whole strip, WRAPPING when too long (never compressing). A
/// selected parent spills its sub-chips inline inside its own group; selecting a sub FUSES the parent into a segmented
/// pill under the SAME node key, so the label morphs in place (ch 10 §0.6). Tapping the pill steps back ONE level.</summary>
public sealed class HomeFacetStripView : Component
{
    public override Element Render()
    {
        var p = UseProps<HomeFacetStripProps>();
        if (p.Chips.Count == 0) return new BoxEl();
        string selected = Home.SelectedFacet.Value;
        var slots = HomeFacetStrip.Slots(p.Chips, selected.Length == 0 ? null : selected);

        var items = new List<Element>(slots.Count);
        HomeChip? group = null;
        var groupItems = new List<Element>(3);
        void Flush()
        {
            if (group is not { } parent) return;
            items.Add(new BoxEl
            {
                Key = "facet-group:" + parent.Id, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Shrink = 0f,
                Children = groupItems.ToArray(),
            });
            group = null;
            groupItems.Clear();
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            string? pick = slot.Select;
            switch (slot.Kind)
            {
                case FacetSlotKind.All:
                    items.Add(Tab(slot.Key, Loc.Get(Strings.Detail.Filter.All), slot.Selected, () => Select(pick)));
                    break;
                case FacetSlotKind.Tab:
                    Flush();
                    group = slot.Chip;
                    groupItems.Add(Tab(slot.Key, slot.Chip!.Label, slot.Selected, () => Select(pick)));
                    break;
                case FacetSlotKind.Fused:
                    Flush();
                    group = slot.Chip;
                    groupItems.Add(FusedTab(slot.Key, slot.Chip!.Label, slot.Sub!.Label, () => Select(pick)));
                    break;
                case FacetSlotKind.Sub:
                    groupItems.Add(SubToken(slot.Key, slot.Sub!.Label, () => Select(pick)));
                    break;
            }
        }
        Flush();

        return new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f, Wrap = true, Children = items.ToArray() },
                new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
            ],
        };
    }

    // Writing the signal is the whole mutation; the page owns the demand (and the revert on failure). Peek-compare first
    // so re-picking the current chip is a no-op. "All" writes "" — the app's own position; the server never models
    // clearing.
    static void Select(string? facetId)
    {
        string next = facetId ?? "";
        if (string.Equals(Home.SelectedFacet.Peek(), next, StringComparison.Ordinal)) return;
        Home.SelectedFacet.Value = next;
    }

    static readonly LayoutTransition s_reflow = new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(260f, Easing.SmoothOut),
        Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    /// <summary>`.selitem` — the key rides the LABEL box (the loose half of the morph), carrying the 260-ms width reflow
    /// every tab animates on a wrap or a spill (ch 10 §5).</summary>
    static Element Tab(string key, string label, bool selected, Action onClick) => new BoxEl
    {
        Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Stretch,
        Corners = new CornerRadius4(Radii.Control, Radii.Control, 0f, 0f),
        Cursor = CursorId.Hand, Role = AutomationRole.Tab, Focusable = true, OnClick = onClick,
        Children =
        [
            new BoxEl
            {
                Key = key, Animate = s_reflow,
                Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S), AlignItems = FlexAlign.Center,
                Children = [BodyStrong(label) with { Color = selected ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
            },
            Underline(selected),
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary>The same tab shell, underline lit, holding the fused pill under the tab's own key. The shell is inert:
    /// the pill IS the control, so a second handler would double-fire the step back.</summary>
    static Element FusedTab(string key, string parentLabel, string subLabel, Action onClick) => new BoxEl
    {
        Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Stretch,
        Corners = new CornerRadius4(Radii.Control, Radii.Control, 0f, 0f),
        Children = [FusedPill(key, parentLabel, subLabel, onClick), Underline(true)],
    };

    static Element Underline(bool selected) => new BoxEl
    {
        Height = 3f, Margin = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
        Corners = new CornerRadius4(2f, 2f, 0f, 0f),
        Fill = selected ? Tok.AccentDefault : ColorF.Transparent,
        BrushTransitionMs = MotionTok.ControlFast.DurationMs,
    };

    /// <summary>A spilled sub-chip: a tertiary caption one level down, exiting −56 DIP toward the pill it becomes.</summary>
    static Element SubToken(string key, string label, Action onClick) => new BoxEl
    {
        Key = key, Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Corners = Radii.ControlAll, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = onClick,
        Children = [Caption(label) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextTertiary }],
        Animate = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(220f, Easing.FluentAccelerate), Exit: new EnterExit(Dx: -56f, Opacity: 0f, Active: true)),
    }.Interactive(Interaction.Subtle);

    /// <summary>The strip-register FUSED pill (0.2.9 <c>ConcertUi.SegmentedPill</c> + <c>SegmentedPillStyle.Strip</c>):
    /// a neutral bordered 28-DIP capsule whose raised ACCENT segment (✓ parent) enters from the chip's side, then the sub
    /// value and a ✕ — tapping it clears the sub-facet, it opens no menu.</summary>
    static Element FusedPill(string key, string name, string value, Action onClick) => new BoxEl
    {
        Key = key, Animate = s_reflow,
        Direction = 0, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(3f, 3f, 10f, 3f), Corners = Radii.FullAll,
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new BoxEl
            {
                Key = key + ":seg",
                Animate = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
                    TransitionDynamics.Tween(300f, Easing.SmoothOut), Enter: new EnterExit(Dx: 56f, Opacity: 0.4f, Active: true)),
                Direction = 0, Height = 22f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 4f,
                Padding = new Edges4(9f, 0f, 9f, 0f), Corners = CornerRadius4.All(11f),
                Fill = Tok.AccentDefault, Shadow = Elevation.Card,
                Children =
                [
                    Icon(Icons.Check, 11f, Tok.OnAccent) with { Shrink = 0f },
                    new TextEl(name) { Size = 14f, Weight = 600, Color = Tok.OnAccent, MaxLines = 1 },
                ],
            },
            new TextEl(value) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 },
            Icon(Icons.Cancel, 9f, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };
}

// ══ 4. THE CUSTOMIZE ENTRY (ch 12 W18) ═══════════════════════════════════════════════════════════════════════════════

/// <summary>The 28 × 28 "⋯" at the end of the greeting/chips row: a one-item flyout "Customize Home" anchored bottom-right,
/// light-dismiss, focus-trapped; a second press on an open flyout CLOSES it. With no overlay host it navigates straight
/// to <c>home-customize</c>.</summary>
public sealed class HomeCustomizeEntry : Component
{
    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        void Go() => Shell.GoTo(new Shell.Route(Shell.RouteKind.HomeCustomize), HomeCardNav.HomeOrigin);

        void Toggle()
        {
            if (Controls.IsNullOverlay(svc)) { Go(); return; }
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            MenuFlyoutItem[] items =
            [
                new(Loc.Get(Strings.Home.Customizer.Title), new IconRef { Glyph = Icons.Edit, Font = Theme.IconFont }, true,
                    () => { handle.Value?.Close(); Go(); }),
            ];
            var opened = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close(), 200f),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup));
            opened.ClosedAction = () => handle.Value = null;
            handle.Value = opened;
        }

        return ToolTip.Wrap(new BoxEl
        {
            Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [Icon(Icons.More, 14f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle), Loc.Get(Strings.Home.Customize));
    }
}

// ══ 5. THE WHAT'S-NEW TIMELINE (ch 11 W23, §6.1) ═════════════════════════════════════════════════════════════════════

/// <summary>New releases and concert announcements grouped by LOCAL day under a fixed 96-DIP day column, the pips on a
/// 1-DIP rule. ONE subscription (<c>Notify.Items</c>): a landed feed and a mark-read both republish it, so the header's
/// "N unheard of M" comes out of the merge. No refresh on mount — go-live primed the centre (ch 11 §7.1). An empty feed
/// renders NOTHING and pays no gap; otherwise the row pays its own bottom gap (ch 10 §3 "Who pays the module gap").</summary>
public sealed class HomeTimelineView : Component
{
    const float DayColumn = 96f;

    public override Element Render()
    {
        var items = Notify.Items.Value;
        var feed = HomeTimelineMerge.Build(items.Items);
        if (feed.IsEmpty) return new BoxEl();
        int unread = feed.Unread, total = feed.Total;

        var groups = new Element[feed.Groups.Length];
        for (int g = 0; g < groups.Length; g++)
        {
            var group = feed.Groups[g];
            var rows = new Element[group.Rows.Length];
            for (int r = 0; r < rows.Length; r++) rows[r] = RowFor(group.Rows[r]);
            groups[g] = new BoxEl
            {
                Direction = 0, Gap = Spacing.M, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Children =
                [
                    new BoxEl
                    {
                        Width = DayColumn, Shrink = 0f, Direction = 1, Gap = 0f, AlignItems = FlexAlign.End,
                        Padding = new Edges4(0f, Spacing.M, 0f, 0f), Children = DayLabel(group.DayTicks),
                    },
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                            new BoxEl { Direction = 1, Gap = 0f, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = rows },
                        ],
                    },
                ],
            };
        }

        Element module = new BoxEl
        {
            Direction = 1, Gap = HomeModuleLayout.HeadGap, MinWidth = 0f,
            Children =
            [
                Controls.SectionHeader(Loc.Get(Strings.Home.NewReleases),
                    unread > 0 ? Strings.Home.UnheardOf(unread, total) : null,
                    unread > 0 ? InfoBadge.Count(unread) : null),
                new BoxEl { Direction = 1, Gap = 0f, MinWidth = 0f, Children = groups },
            ],
        };
        return Responsive.Of(width => new BoxEl
        {
            Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, HomeModuleLayout.Gap(width)), Children = [module],
        }, fallback: HomeModuleLayout.FallbackWidth);
    }

    // ONE anatomy, two legs: only the badge word, the source line, the art shape and the click differ.
    static Element RowFor(HomeTimelineRow entry)
    {
        var n = entry.Source;
        Element row = entry.Kind == HomeTimelineKind.Concert
            ? HomeCards.TimelineRow(n.ImageUrl, SpotifyUpdates.CleanTitle(n.Title), Loc.Get(Strings.Concerts.Detail.Concert),
                n.ActName is { Length: > 0 } act ? act : Loc.Get(Strings.Concerts.LiveMusic), entry.IsUnread,
                () => OpenConcert(n), artRadius: Design.Size.Thumb40 / 2f)
            : HomeCards.TimelineRow(n.ImageUrl, n.Title,
                Loc.Get(n.ReleaseKind == NewReleaseKind.Episode ? Strings.Podcast.Show : Strings.Detail.FactReleases),
                n.Creator ?? "", entry.IsUnread, () => OpenRelease(n));
        return row is BoxEl b ? b with { Key = "home-timeline:" + entry.Id } : row;
    }

    /// <summary>A CONCERT row marks itself read, then opens the centre's own destination: an in-app route when the target
    /// resolves, else the web page.</summary>
    static void OpenConcert(Notification n)
    {
        Notify.MarkRead(n.Id);
        if (n.ActionType == SocialActionType.Navigate && n.ActionUri is { Length: > 0 } uri
            && !uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && Shell.For(EntityUri.Parse(uri)) is { IsNone: false } route)
        {
            Shell.GoTo(route, HomeCardNav.HomeOrigin);
            return;
        }
        OpenWeb(n.ActionUri);
    }

    /// <summary>A RELEASE row navigates and stays UNREAD (ch 11 §6.1, parity 81). An album opens its page; an episode has
    /// no route of its own, so it opens the web player.</summary>
    static void OpenRelease(Notification n)
    {
        if (n.ReleaseKind != NewReleaseKind.Episode && Shell.For(n.Subject, n.Title) is { IsNone: false } route)
        {
            Shell.GoTo(route, HomeCardNav.HomeOrigin);
            return;
        }
        OpenWeb(n.Subject.IsValid ? n.Subject.Text : null);
    }

    static void OpenWeb(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        string web = uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? uri : Actions.WebLinkOf(EntityUri.Parse(uri));
        if (web.Length > 0) Actions.Services.OpenExternal?.Invoke(web);
    }

    // Today / Yesterday alone; older days get the weekday + date with "N days ago" beneath, so the column scans without
    // arithmetic.
    static Element[] DayLabel(long dayTicks)
    {
        var day = new DateTime(dayTicks, DateTimeKind.Local);
        int delta = (int)(DateTime.Now.Date - day).TotalDays;
        string head = delta <= 0 ? Loc.Get(Strings.Detail.Today)
                    : delta == 1 ? Loc.Get(Strings.Detail.Yesterday)
                    : day.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        Element headEl = Caption(head) with { Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        return delta > 1
            ? [headEl, Caption(Strings.Detail.DaysAgo(delta)) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
            : [headEl];
    }
}

// ══ 6. THE FOLD TILE (ch 10 W13, ch 11 W24-W26) ══════════════════════════════════════════════════════════════════════

/// <summary>THE Fold tile: a card plate on mica, a display-type title running under the art, and up to three 124-DIP
/// covers hanging off the right at authored rest poses that FAN on hover by additive deltas. A static factory, not a
/// component. Hover changes only the plate's FILL at the same elevation — never a lift (ch 10 §4.3).</summary>
public static class HomeFoldTile
{
    /// <summary>Build a tile for <paramref name="s"/> at the shelf's fitted <paramref name="cardW"/>. The width is baked
    /// into the key (a re-fit REPLACES the tile, ch 10 trap 2); the loading shape is ONE explicit card-silhouette bone,
    /// because a derived shimmer would zero the covers' authored pose and drop the plate.</summary>
    public static Element Create(HomeSectionView s, float cardW, string? eyebrow, Action<HomeSectionView> open)
    {
        var cards = s.Cards;
        // The wash is the FIRST card's RAW payload accent, never lifted, never graded, never invented: 0 → no layer.
        uint accent = cards.Count > 0 ? cards[0].Accent : 0u;
        var children = new List<Element>(5);
        if (accent != 0u)
        {
            ColorF c = Design.Palette.ToColor(accent) with { A = 1f };
            children.Add(new BoxEl
            {
                HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                Height = HomeModuleLayout.FoldCardHeight,
                Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, c with { A = 0.22f }), new GradientStop(0.72f, c with { A = 0f })])
                {
                    RadialCenter = new Point2(1.0f, 0.48f), RadialRadius = new Point2(0.70f, 1.10f),
                },
            });
        }

        // A missing slot is OMITTED, never padded with a repeated cover.
        int coverCount = Math.Min(3, cards.Count);
        for (int i = 0; i < coverCount; i++)
        {
            var card = cards[i];
            HomeModuleLayout.FoldRest(i, cardW, out float x, out float y, out float rot);
            HomeModuleLayout.FoldFan(i, out float dx, out float dy, out float drot);
            children.Add(new BoxEl
            {
                Width = HomeModuleLayout.FoldCover, Height = HomeModuleLayout.FoldCover,
                OffsetX = x, OffsetY = y, Rotation = rot,
                // DELTAS on the rest pose; the token carries the reduced-motion policy — no branch anywhere here.
                WhileHover = new MotionTarget { OffsetX = dx, OffsetY = dy, Rotation = drot },
                Transition = MotionTok.ControlNormal,
                HitTestVisible = false, Shadow = Elevation.Card, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Control),
                Children = [HomeCards.Art(in card, HomeModuleLayout.FoldCover, HomeModuleLayout.FoldCover, Radii.Control, decodePx: 128)],
            });
        }

        string title = !string.IsNullOrWhiteSpace(s.Title) ? s.Title! : Loc.Get(Strings.Home.Sections);
        Element titleEl = Design.Type.FoldTitle(title) with { Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        // The copy column is painted LAST, over the covers; hit-test off so the ROOT owns the one hyperlink.
        children.Add(new BoxEl
        {
            HitTestVisible = false, Direction = 1, Justify = FlexJustify.End, Gap = Spacing.XS,
            Height = HomeModuleLayout.FoldCardHeight, AlignSelf = FlexAlign.Stretch,
            // Edges4 is POSITIONAL (L, T, R, B): left 20, top 20, right 12, bottom 18 — NOT the prototype's CSS order.
            Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.M, 18f),
            MaxWidth = cardW * HomeModuleLayout.FoldCopyMaxFrac, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children = eyebrow is { Length: > 0 }
                ? [Design.Type.Eyebrow(eyebrow) with { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, titleEl]
                : [titleEl],
        });

        Element root = new BoxEl
        {
            ZStack = true, Width = cardW > 0f ? cardW : float.NaN, Height = HomeModuleLayout.FoldCardHeight, MinWidth = 0f,
            ClipToBounds = true, Corners = Radii.CardAll, Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            OnClick = () => open(s), Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
            FocusVisualMargin = Design.FocusInsetBordered,
            Key = "home-fold-tile:" + (s.Uri ?? "") + ":" + cardW.ToString(CultureInfo.InvariantCulture),
            Children = children.ToArray(),
        };
        Element bone = new BoxEl
        {
            Width = cardW > 0f ? cardW : HomeModuleLayout.FoldCardMin, Height = HomeModuleLayout.FoldCardHeight, MinWidth = 0f,
            Corners = Radii.CardAll, Fill = SkeletonStyle.Default.BarColor, IsEnabled = false, HitTestVisible = false,
        };
        return root.Skel(bone);
    }
}

// ══ 7. THE APPEND PRELOADER (ch 10 §8, ch 12 W12) ════════════════════════════════════════════════════════════════════

/// <summary>A grid's tail: silent infinite scroll onto the host's existing "Show all" pipeline, through three gates —
/// (C) the tail is NEAR (<see cref="NearTail"/>, published from the grid's own scroll geometry), (B) a 300-ms arm
/// debounce re-checked when it fires, (A) never concurrently (<see cref="Loading"/>) — with a bounded 3-attempt collapse.
/// No visual footprint at all: the grid it trails is a self-scrolling virtual viewport with nothing to append a loading
/// row to. The host remounts it on every cursor change (its <c>Key</c> is the prop channel, ch 12 trap 2), so the attempt
/// counter is per (uri, cursor).</summary>
public sealed class HomeSectionAppendPreloader : Component
{
    const int MaxAttempts = 3;
    const float ArmDelayMs = 300f;

    /// <summary>The host's append-in-flight signal; its false edge re-arms a retry.</summary>
    public required IReadSignal<bool> Loading;
    /// <summary>True while the grid's bottom edge is within ~1.5 viewport heights of the content end.</summary>
    public required IReadSignal<bool> NearTail;
    /// <summary>The host's append verb.</summary>
    public required Action Start;

    /// <summary>The near-tail scroll-geometry watch: the 24-px-floored offset × 48-px-floored content-height projection
    /// (so an append's own growth re-evaluates nearness) and the nearness write, both through
    /// <see cref="HomeNearTail"/>. <paramref name="offset"/> is an optional second writer for a host that also publishes
    /// the page scroll.</summary>
    public static (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action) NearTailWatch(
        Signal<bool> nearTail, Signal<float>? offset = null)
        => (static g => HomeNearTail.Project(g.OffsetY, g.ViewportH, g.ContentH),
            g =>
            {
                if (offset is not null) offset.Value = g.OffsetY;
                bool near = HomeNearTail.IsNear(g.OffsetY, g.ViewportH, g.ContentH);
                if (nearTail.Peek() != near) nearTail.Value = near;
            });

    int _attempts;
    TimerHandle _arm;
    readonly Action _fire;

    public HomeSectionAppendPreloader() => _fire = Fire;

    public override Element Render()
    {
        _arm = UseTimeout(_fire, ArmDelayMs);
        UseSignalEffect(() =>
        {
            bool near = NearTail.Value, loading = Loading.Value;
            if (!near || loading || _attempts >= MaxAttempts) { _arm.Cancel(); return; }
            _arm.Restart();   // cancel-and-restart on every gate re-evaluation
        });
        return new BoxEl();
    }

    void Fire()
    {
        if (Loading.Peek() || !NearTail.Peek() || _attempts >= MaxAttempts) return;
        _attempts++;
        Start();
    }
}
