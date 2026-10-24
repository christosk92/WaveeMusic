// ── Themes/Zune/Structure/Views/Library/ZuneLibraryPage.cs — Zune's `Pages.Library` (themes plan 6.3; P5 Z7) ───────────────────────────────
//
// Boards Z-Library-Albums, -Artists, -Podcasts, -Audiobooks (+ the Compact / Small / Huge twins). The kinds (liked · playlists · albums · artists · podcasts ·
// audiobooks) are the band's row 2, so the page draws no title: under the band, on the frame's gutter, it is a HEAD of words (sort, view, find, the count) over a
// BODY of two columns, 40 apart:
//
//   Albums · Podcasts · Audiobooks   the navigator takes the width (a grid of covers, or a list), the pane is 380 (the selected record: its title, the verbs as
//                                    words, its songs). A filter typed in the find field swaps both for the search columns (hits | the hit's songs). The grouped
//                                    albums' pane stands beside them only from the layout ladder's pane rung (`LibraryPageProps.PaneBeside`): under it the pane
//                                    steps aside as Small's board draws it, the albums take its width, and a picked album opens its page.
//   Artists                          a 220 list (letter · name · count) | the reader, which takes the width: the artist's band, then a block per release. A filter
//                                    typed swaps them for three columns (artists | albums | songs).
//   Compact                          the columns fold into a breadcrumb drill-in: the navigator, then the pane or the reader, one at a time.
//
// The host (`User.LibraryPage`, a `[CoreHost]`) keeps the persisted signals, the shape memo, the selection, the search matcher, the collapse state and every verb;
// the navigator's ROW EXTENTS (a 56 plate in a 60 pitch, 40 in 44) and the letters' arithmetic are its too, so the rows keep those pitches (the artists' list reads
// 44 apart, not the board's 34). There are no column grips: the pane has a width of its own per tier (`ZuneLibraryRules`).
//
// EVERY BRANCH ROOT IS KEYED. The reconciler matches a KEYED child only against the same key and an UNKEYED child by ordinal, so two unkeyed branch roots at the same
// ordinal would be ONE node that survives the swap, subtree and all; a navigator bound to a persisted signal then keeps the wrong wiring. Keys, not props, are what turn
// a branch swap into the remount a bind needs.
//
// THE PLACE. A crossing between the arms keeps the user's place: the host captures the navigator's first visible item on the render that swaps the arms and brings
// the arriving list to it (`ApplyNavPlace`); this view only keeps the navigator's mount keyed ("nav:list") and its two arms cross-fading
// (`s_arm`), so the arriving list is the host's to place.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneLibraryKit;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's library page.</summary>
internal static partial class ZuneLibraryPage
{
    /// <summary>The registered template of <see cref="ViewKeys.LibraryPage"/>.</summary>
    public static Element Template(LibraryPageProps props, in ViewContext ctx)
    {
        var cache = ctx.Hooks.UseRef(new PageCache()).Value;
        // A pane that steps in fades in only once the page has drawn (the page's own entrance brings the first one).
        cache.Rendered = ZuneMotion.Rendered(ctx.Hooks);
        cache.Props = props;
        // The layout tier's record: the structure's ladder (the host's tier signal, `props.Collapsed`) decides the one-column drill-in, the content tier the split's numbers.
        cache.Layout = ZuneLibraryTiers.Of(ctx.Tier(), props.Collapsed);
        cache.Prepare();

        Element inner;
        if (props.Collapsed)
            inner = Collapsed(props, cache);
        // Your Episodes: the podcasts mode words swap the WHOLE body for the flat episode table, but the head (and the word that switches back) stays put.
        // (A structure that pins "your episodes" over the shows list keeps its columns in both modes: the feed is the right column.)
        else if (props.SavedEpisodesMode && !HasEpisodes(props))
            inner = new BoxEl
            {
                Key = LR.SavedKey, Animate = s_arm, Direction = 1, Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Stretch, Gap = cache.Layout.BodyGap,
                Children = [Head(props, cache), props.SavedEpisodes()],
            };
        else
        {
            bool artists = props.Entity == EntityKind.Artist;
            bool groups = HasGroups(props);
            var l = cache.Layout;
            // The right column. The album's songs pane is a SLOT that carries the gap before it (one node at every width: it steps aside to 0 under the pane rung, as
            // Small's board draws the grouped albums, and back); every other right column stands after a gap box.
            bool slot = !props.FullSearch && !artists && !HasEpisodes(props);
            Element right = props.FullSearch
                ? (artists ? SearchArtistColumns(props, cache) : SearchAlbumDetail(props, cache))
                : artists ? ReaderColumn(props, l)
                : HasEpisodes(props) ? (props.SavedEpisodesMode ? EpisodesFeed(props, l) : ShowColumn(props))
                : DetailSlot(props, cache, beside: !groups || props.PaneBeside);
            var columns = new List<Element>(6);
            if (groups) { columns.Add(GroupColumn(props, l)); columns.Add(K.ColGap("lib:gap:groups", l.ColGap)); }
            if (groups && l.HeroColW > 0f) { columns.Add(GroupHeroColumn(props, l)); columns.Add(K.ColGap("lib:gap:hero", l.ColGap)); }
            columns.Add(NavColumn(props, cache));
            if (!slot) columns.Add(K.ColGap("lib:gap:right", l.ColGap));
            columns.Add(right);
            inner = new BoxEl
            {
                Key = LR.WideKey, Animate = s_arm, Direction = 1, Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
                Children =
                [
                    Head(props, cache),
                    // The head-to-body air is a spacer (the Short rung tightens it 16 → 8), so it eases where a Gap would snap.
                    ZuneMotion.Air("lib:body-air", cache.Layout.BodyGap),
                    // The gaps are boxes (K.ColGap, the slot's own), so a tier that changes them eases them.
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, MinHeight = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                        Children = columns.ToArray(),
                    },
                ],
            };
        }
        // The page publishes no shell material: the library is the app's accent-neutral browser. The page column (A1): capped at 2400 + the
        // gutters and centred, so the page shares the band's x at every width.
        return ZuneFrameRules.PageRow(new BoxEl
        {
            Key = LR.PageKey, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
            // The page's top is a spacer (the Short rung tightens it 8 → 4), so it eases where padding would snap.
            OnBoundsChanged = cache.OnBounds, Children = [ZuneMotion.Air("lib:top-air", cache.Layout.PageTop), inner],
        }, cache.Layout.Gutter);
    }

    // ══ THE PER-INSTANCE CACHE ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the page keeps across renders: the latest props, the elements built once (the sticky letter and the strip) and the stable delegates the navigator's
    /// region and list were handed. One per mounted template.</summary>
    sealed class PageCache
    {
        public LibraryPageProps Props = null!;
        public ZuneLibraryLayout Layout = null!;
        public bool Rendered;
        public Element? Sticky, Strip;
        public Action<RectF>? OnBounds;
        public Func<int, Element>? NavContent;
        public Func<Element>? NavNoMatch, NavShimmer;
        Func<BoundItemScope<LibraryNavItem>, Element>? _slotT, _slotCompactT, _cardT, _cardCompactT;

        /// <summary>Dark-selection thunk for the shows list while Your Episodes owns the highlight (read live through the props: a template is frozen per slot).</summary>
        Func<bool>? _hideSel;
        Func<bool> HideSel => _hideSel ??= () => Props.Episodes is { } e && e.Active();

        public void Prepare()
        {
            OnBounds ??= Measured;
            NavContent ??= _ => NavList(this);
            // An empty navigator says why: a filter that matched nothing, or a library that holds none of this kind yet (no filter typed). A show tab with
            // nothing saved is the Empty vacancy with its kind's words and Browse (audit B7 r2, V22: Audiobooks was one bare line).
            NavNoMatch ??= () => Props.Filter().Peek().AsSpan().Trim().Length > 0 ? EmptyCompact(Loc.Get(Strings.Library.NoMatch))
                : Props.Entity == EntityKind.Show ? EmptyShows(Props.IsPodcasts)
                : EmptyCompact(Strings.Theme.Zune.Collection.Library.EmptyKind(Props.Title.ToLower(System.Globalization.CultureInfo.CurrentCulture)));
            // The persisted view cell, never the mount: a pending navigator is never asked for its mount.
            NavShimmer ??= () => NavSkeleton(Props.View().Value, Props.Size().Value, Props.Entity, Layout.CardCellMin, Layout.GridGaps);
        }

        /// <summary>The page's measured bounds, reported to the host: its layout ladder over this width decides the drill-in (the host's value gate drops every report that did
        /// not move). The ladder reads the page column with its gutters (the page's box stands between the column's two gutter spacers).</summary>
        void Measured(RectF r)
        {
            if (r.W > 0f) Props.SetPageWidth(r.W + 2f * Layout.Gutter);
        }

        /// <summary>The slot template of a mount's template kind: one stable delegate per kind for the page's life.</summary>
        public Func<BoundItemScope<LibraryNavItem>, Element> TemplateOf(LibraryNavTemplate template)
        {
            var entity = Props.Entity;
            return template switch
            {
                LibraryNavTemplate.RowCompact => _slotCompactT ??= scope => NavSlot(scope, entity, compact: true, HideSel),
                LibraryNavTemplate.Card => _cardT ??= scope => NavCard(scope, entity, compact: false),
                LibraryNavTemplate.CardCompact => _cardCompactT ??= scope => NavCard(scope, entity, compact: true),
                _ => _slotT ??= scope => NavSlot(scope, entity, compact: false, HideSel),
            };
        }
    }

    // ══ THE COLUMNS ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A column of the page: plain, clipped (no plate of its own: the page is the ground).</summary>
    static BoxEl Section => new() { Direction = 1, ClipToBounds = true };

    /// <summary>The navigator column. KEYED: a bound list lives inside, and this node may only ever be paired with another <c>lib:nav</c>. The artists' list has a width of its
    /// own; every other kind's navigator takes what the pane leaves. Widths are the tier record's, live props (a crossing eases them, never remounts).</summary>
    static Element NavColumn(LibraryPageProps p, PageCache c)
    {
        bool artists = p.Entity == EntityKind.Artist;
        var l = c.Layout;
        // A GRID's cells fill whatever column it gets; a LIST (and the search hits) stop at the record's cap so a 2400 page is never one 1960-wide list. The view cell is
        // read here so the cap follows a grid <-> list switch (the column eases its width, the node is the same one).
        bool episodes = HasEpisodes(p);
        float cap = artists || episodes ? float.NaN : (!p.FullSearch && User.IsGridView(p.View().Value) ? l.GridNavMaxW : l.NavMaxW);
        return Section with
        {
            Key = LR.NavKey, Direction = 1,
            Width = episodes ? l.EpisodesNavW : artists ? l.ReaderNavW : float.NaN, Grow = artists || episodes ? 0f : l.NavGrow, Basis = artists || episodes ? float.NaN : 0f,
            Shrink = artists || episodes ? 0f : 1f,
            // A declared width carries no MinWidth (Shrink 0 holds it): a floor at the NEW width would clamp the eased one and land a widening crossing at once.
            MinWidth = artists || episodes ? float.NaN : l.NavMinW, MaxWidth = cap, Animate = LR.ColumnResize,
            Children = episodes
                ? [EpisodesRow(p, l), ShowsLabel(), new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, Children = [ListBody(p, c)] }]
                : HasGroups(p)
                // The picked artist's name over their albums: the list is the one flexing child.
                ? [GroupHeading(p), new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, Children = [ListBody(p, c)] }]
                : [p.FullSearch ? LeftSearchBody(p, c) : ListBody(p, c)],
        };
    }

    /// <summary>The search pane's column (the hit album's songs): a width of its own beside the hits, the 380 of Standard.</summary>
    static BoxEl PaneSection(string key, ZuneLibraryLayout l) => l.PaneGrow > 0f
        // The growing form (Huge): rests at PaneW, takes its share of what the navigator leaves, up to PaneMaxW.
        ? Section with { Key = key, Basis = l.PaneW, Grow = l.PaneGrow, Shrink = 0f, MinWidth = l.PaneW, MaxWidth = l.PaneMaxW, Animate = Design.Reveal.Resize }
        : Section with { Key = key, Width = l.PaneW, Shrink = 0f, MinWidth = l.PaneW, Animate = Design.Reveal.Resize };

    /// <summary>The album's songs pane in its slot (<see cref="ZuneLibraryKit.PaneSlot"/>): 380 at Standard beside the navigator, the growing form at Huge, 0 while it steps
    /// <paramref name="beside"/> no more (the grouped albums under the pane rung). The empty pane asks for a pick.</summary>
    static Element DetailSlot(LibraryPageProps p, PageCache c, bool beside)
    {
        var l = c.Layout;
        Element? content = !beside ? null : p.HasSelection ? p.Pane() : EmptyCompact(Loc.Get(PickKey(p)));
        // NO `Key` on the pane itself: a selection change RE-PUSHES its props and the pane re-skins in place (it owns its own crossfade).
        return K.PaneSlot("lib:pane-slot", l.PaneW, l.ColGap, beside, c.Rendered, p.HasSelection ? LR.PaneKey : LR.EmptyKey, content, l.PaneGrow, l.PaneMaxW);
    }

    /// <summary>Beside the pinned "your episodes" row the show pane takes all the width the shows list leaves.</summary>
    static Element ShowColumn(LibraryPageProps p) => Section with
    {
        Key = p.HasSelection ? LR.PaneKey : LR.EmptyKey, Grow = 1f, Basis = 0f, MinWidth = 0f, Animate = Design.Reveal.Resize,
        Children = [p.HasSelection ? p.Pane() : EmptyCompact(Loc.Get(PickKey(p)))],
    };

    /// <summary>What the empty pane asks for: a show (Podcasts), an audiobook (Audiobooks, the show entity's other library page), an album.</summary>
    static string PickKey(LibraryPageProps p) => p.Entity != EntityKind.Show ? Strings.Library.SelectAlbum
        : p.IsPodcasts ? Strings.Library.SelectShow : Strings.Library.SelectAudiobook;

    /// <summary>The artists view's ONE right-hand column: the reader (its board draws the picked album's pane itself, beside its middle column while the page stands at the
    /// pane rung: <see cref="ZuneLibraryKit.PaneBeside"/> carries <see cref="LibraryPageProps.PaneBeside"/> to it).</summary>
    static Element ReaderColumn(LibraryPageProps p, ZuneLibraryLayout l)
    {
        if (!p.HasSelection) return Section with { Key = LR.EmptyKey, Grow = 1f, Basis = 0f, Children = [EmptyCompact(Loc.Get(Strings.Library.SelectArtist))] };
        return Section with
        {
            Key = LR.ReaderKey, Grow = 1f, Basis = 0f, MinWidth = l.RightMin > 0f ? l.RightMin : 0f,
            Children = [Ctx.Provide(ZuneLibraryKit.PaneBeside, p.PaneBeside, p.Pane())],
        };
    }

    static Element EmptyCompact(string text)
        => Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact, title: text, subtitle: "");

    /// <summary>Podcasts or Audiobooks with nothing saved: the kind's title, body and the way to find some (Browse).</summary>
    static Element EmptyShows(bool podcasts)
    {
        var (title, body, action) = EmptyFacetCopy.LibraryShows(podcasts);
        return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(title), subtitle: Loc.Get(body),
            actionLabel: Loc.Get(action), onAction: HomeVerbs.OpenBrowse);
    }

    // ══ THE COLLAPSED DRILL-IN ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The drill: opacity only, 160 ms, and the arriving leaf slides 12 DIP in from the right. A depth swap is a mount/unmount of a keyed wrapper, so the engine's
    /// enter/exit terminals do the work: no frame clock.</summary>
    static readonly LayoutTransition s_drillIn = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(LR.DrillMs, Easing.SmoothOut),
        Enter: new EnterExit(Dx: LR.DrillDx, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(LR.DrillMs, Easing.SmoothOut));

    /// <summary>The two arms (the split columns and the one-column drill-in) cross-fade when the layout tier crosses: the arriving arm fades in while the leaving one fades out
    /// as an exit orphan under it, never a snap. Opacity only.</summary>
    static readonly LayoutTransition s_arm = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(LR.DrillMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(LR.DrillMs, Easing.SmoothOut));

    static readonly LayoutTransition s_drillRoot = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(LR.DrillMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(LR.DrillMs, Easing.SmoothOut));

    /// <summary>TWO rungs when BROWSING, for every kind: depth 0 the navigator, depth 1 the pane (albums / podcasts) or the reader (artists). SEARCH mode keeps its columns,
    /// so the artists view keeps a third rung for them: depth 0 artist hits, 1 album hits, 2 track hits.</summary>
    static Element Collapsed(LibraryPageProps p, PageCache c)
    {
        int depth = p.Depth;
        bool artists = p.Entity == EntityKind.Artist;
        Element body;
        if (depth == 0)
            body = new BoxEl
            {
                Key = "col:nav", Direction = 1, Grow = 1f, MinHeight = 0f, Gap = c.Layout.BodyGap,
                Children = [Head(p, c), p.SavedEpisodesMode
                    ? p.SavedEpisodes()
                    : (p.FullSearch ? LeftSearchBody(p, c) : ListBody(p, c))],
            };
        else if (depth >= 2)
            body = Section with { Key = "col:tracks", Grow = 1f, Basis = 0f, Children = [TrackHits(p)] };
        else if (artists)
            body = Section with
            {
                Key = "col:reader", Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [p.FullSearch ? AlbumHits(p, explain: false) : p.Pane()],
            };
        else
            body = Section with
            {
                Key = "col:detail", Grow = 1f, Basis = 0f,
                Children = [p.FullSearch ? TrackHits(p) : p.Pane()],
            };
        Element wrapper = new BoxEl
        {
            Key = "depth:" + depth, Animate = depth > 0 ? s_drillIn : s_drillRoot,
            Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, Children = [body],
        };
        // KEYED against the wide arm's key: see the file header.
        return new BoxEl
        {
            Key = LR.CollapsedKey, Animate = s_arm, Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = depth == 0 ? [wrapper] : [CrumbBar(p.Crumbs, p.PickDepth, c.Layout.BodyGap), wrapper],
        };
    }

    /// <summary>The collapsed layout's breadcrumb: a plain band, a 1-DIP divider under it.
    /// <para>KEYED. It is the first child of the collapsed root, as the head is of the wide one: unkeyed, the reconciler would pair the two by ordinal.</para></summary>
    static Element CrumbBar(IReadOnlyList<string> crumbs, Action<int> onPick, float gap) => new BoxEl
    {
        Key = "lib:crumbs", Direction = 1, Shrink = 0f, Padding = new Edges4(0f, 0f, 0f, gap),
        Children =
        [
            BreadcrumbBar.Create(crumbs, onPick),
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };
}
