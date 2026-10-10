// ── Shell/Sidebar.UI.Zune.cs ───────────────────────────────────────────────────────────────────────────────────────
// The Zune navigation style's band (design NAV 3): the big pivots across the top, row 2 under them (Library's sub-pivots,
// the page's views, the Browse categories or an entity's title row; none for a route with nothing of its own), the pin
// tiles beside the pivots, and the band's right-click menu (Layout ▸ · Pins in the title bar · Reset everything)
//
// Role: UI · Spec: sidebar-rework-implementation.md §P11 (NAV 3 UI) · the rules are Sidebar.Zune.cs (ZuneNavRules)
//
// LAYOUT STABILITY. The band root is ONE keyed node in every nav style. Its Height is ZuneNavRules.BandHeight(NavStyle,
// Shell.Ui.PresentedSubRow) (84 under Zune with a row 2, 52 without one, 0 otherwise: PivotTop + PivotLine + PivotToSub +
// SubRowHeight + SubToCard, every term a named constant) and it carries Shell.ZuneBandAnim: a Size RELAYOUT on the content
// card's own tween, never a Reflow. The band is the PAGE COLUMN's first child (the column is [band, card stack] beside the
// inline right panel, which therefore runs from the title bar's bottom to the dock), so on a rail toggle the band's width
// eases with the card's tween and the pins slide instead of snapping. The card stack under it (Shell.PageColumnCardAnim: a
// Y-only Position FLIP, parent-relative, plus a Height Relayout) eases down in the same tween, so the card's top travels with
// the band's bottom edge, its bottom edge stays on the dock, and a nav-style switch moves the card one time: the card inside
// the stack FLIPs in X only (it is anchored at Shell's zero-size ContentCardAnchor, which moves in y with the stack), so the
// two FLIPs never compose. Whether the pins show is decided from the page column's width (Shell.PageColumnWidth), not the
// viewport's, so the pivots keep their room while the panel is inline.
// Row 2 is ALWAYS mounted at SubRowHeight (the band's clip decides how much of it shows) and never an empty strip
// (ZuneNavRules.SubRowOf decides from the ROUTE what it carries), and a skeleton-to-words swap is an opacity cross-fade
// inside slots of the final size. A navigation between a route with a row 2 and one without moves the card ONCE, on its own
// tween, one commit after the page mounted (see ROW 2 MOTION).
// The inset is DERIVED (FrameRules.ZuneBandInset: the page gutter the pages read, card-relative because the band starts at
// the card's x, which is 0 under Zune where the page bleeds to the window edge), so the first pivot word and the page title
// share an x.
//
// ROW 2 BY KIND (ZuneSubRow). Library: the sub-pivots (one stable key across the library pages, so only the selected
// word's weight changes). Views: the words Shell.PageViews holds for the route, else the route's SEED (the loc keys of
// ZuneNavRules.ViewSeedKeys, selected by SeedSelected); Search, whose facets depend on its results, seeds skeleton words.
// Categories: the four top Browse categories, which navigate. Context: the entity's title, then same-size skeleton pivots
// and action pills until the page publishes its band (Shell.PageBands). None: nothing (album, playlist, concerts, history,
// the Browse sections and every other route whose head already shows its title), so row 2 never just repeats the page title.
//
// ROW 2 MOTION. The band's height reads Shell.Ui.PresentedSubRow, which Shell's SubRowPresenter POSTS after a navigation (the
// boot route seeds it synchronously, so Home opens at 84). The incoming page therefore mounts in one commit and the band and
// the card ease in the next. The row-2 host (Row2HostView) stays MOUNTED: it latches the last non-None content and, while the
// presented row is None, keeps drawing it with its opacity eased to 0 (an opacity Transition on a live node, not an Exit, so
// ZuneBandAnim's SuppressDescendantTransitions cannot cull it) while the band's clip closes over it. The latch clears once the
// band and the fade are done. When a row enters (52 to 84) the new content draws at opacity 0 and fades in as the band opens.
//
// TIMING. Band HEIGHT follows NavStyle (the frame commit). The Views kind follows Shell.Ui.PresentedNavStyle (the later,
// quiet hoist commit): until then the page head still draws its views, and they would otherwise show twice. Library,
// Categories and Context have no in-page twin before the floor latches (the A2 entity pages' own sticky band),
// so they render from the frame commit and reveal with the band. On a navigation row 2 is never empty; on a style switch it
// is never doubled, except for A2's documented latch window.
//
// THE CONTEXT ROW IS THE PAGE'S BAND (A2). Row 2 of an entity route is Detail.BandCluster at the row's 32 (= BandLayout.ItemHeight): the
// title (ZuneSubPivot selected, primary), a 1x16 divider, the page's scroll-spy tabs (ZuneSubPivot at a constant 400, ink plus the
// shared 2-DIP underline at BandLayout.UnderlineY in the page's accent, the band falling back to Tok.AccentDefault) and the page's
// own action cluster. The artist, profile, episode and show pages publish it (Shell.PageBands) and, under Zune, do not compose their
// in-page band (the hero collapses to the latched floor, Detail.BandLayout.FloorLatch). A tab click calls the publication's
// OnPivot, which scrolls the page exactly as the band's own tab does.
//
// Album, prerelease and playlist have no row 2 (None): they keep their own in-page sticky band (title + Play), their
// search and selection swaps and their crumbs, and publish nothing here.
//
// ROLES. The pivots carry Role Tab, like the stock SelectorBar items (it was NavigationItem). They stay a band-private item
// builder rather than an engine SelectorBar because SelectorBar auto-selects on focus entry with no selection, which
// would navigate. Their text is the two Zune type roles and nothing else (Design.Type.ZunePivot 28/36 for row 1,
// Design.Type.ZuneSubPivot 14/20 for row 2). The pin tiles keep Role Button. The pivots are PLATELESS: ink alone carries
// state (secondary at rest, primary on hover and when selected, tertiary on press) and the selected word is heavier, so a
// selected word can never look pressed; the box exists for the click, the cursor and the focus ring.
//
// PINS RESOLVE. A pin's cover comes from the projection binder. Under Zune no pane is mounted (the sidebar presents no
// pane), so the band pumps the binder itself (PumpWhenZune: Sync is idempotent, a mounted pane and the band may both pump
// it, and the effect is disposed with the band). Until a pin resolves its tile shows the kind's glyph
// (SidebarKindGlyph.For), never a generic library icon.
//
// CHROME AUDIT (the card edge is the only boundary above the content). Audited between the window top and the card's top
// edge: the material layer (Shell.Masthead.UI.cs: ONE tint box that spans the whole window column, Grow, no inset, gated by
// Prefs.Appearance.SurfaceWash at Design.Wash.TintAlpha in every nav style), TitleBar (no fill), the ChromeRow islands
// (Fill, Gradient, Shadow and BorderColor: none), this band and its rows (none; the pins keep a tile plate and the skeleton
// words are small PendingBars, both content, not chrome), the pivot and row-2 ScrollViews' AutoEdgeFade (masks only its own
// content), the content region's stroke box (StrokeOverhang), and the former lead-gap box (gone). No painter ends at the
// band or row-2 boundary: the only boundary in the chrome is the card edge (the FileArea fill and the StrokeCardDefault
// stroke), and the visible step was the empty row-2 strip, which the filled row 2 closes. Softening the card edge itself
// (a lower-alpha or tinted FileArea over the tint) is a separate design decision, not made here.

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    /// <summary>The Zune band: <see cref="ZuneNavRules.BandHeight"/> tall under Zune, zero height otherwise. Mounted in the
    /// page column, above the card.</summary>
    public static Element ZuneBand() => Embed.Comp(static () => new ZuneBandView()) with { Key = "zune-band" };

    /// <summary>The binder's pump while the Zune frame is up: the pane is not mounted then, so the band keeps the projection
    /// (and with it the pins' covers) alive. A no-op in the other styles, where the pane pumps; the read of the style makes
    /// the effect re-run when it switches.</summary>
    static void PumpWhenZune()
    {
        if (NavStyle.Value != ShellNavStyle.Zune) return;
        PumpBinder();
    }

    internal sealed class ZuneBandView : Component
    {
        readonly List<SidebarPin> _tiles = new(ZuneNavRules.MaxPins);
        readonly Signal<int> _pinsTick = new(0);
        readonly Action _settlePins;
        bool _pinsInit, _pinsTarget, _pinsMounted, _pinsSettled;

        public ZuneBandView() => _settlePins = SettlePins;

        void SettlePins()
        {
            if (_pinsSettled) return;
            _pinsSettled = true;
            _pinsTick.Value++;
        }

        public override Element Render()
        {
            // Every hook runs before anything that depends on the style: a Zune switch must not change the hook count between renders.
            var overlay = UseContext(Overlay.Service);
            var vp = UseContextSignal(Viewport.Size);
            // Decided from the PAGE COLUMN's width (the band stops at the inline rail), not the viewport's.
            bool pinsTarget = UseComputed(() => ZuneNavRules.ShowsPins(ZunePins.Value, Shell.PageColumnWidth(vp.Value.Width))).Value;
            // The target reads the FINAL column width while the band eases to it: the group mounts after the band has settled and,
            // going away, fades by opacity and unmounts after it (ZuneNavRules.PinsMounted), so a rail toggle never re-solves the row.
            if (!_pinsInit) { _pinsInit = true; _pinsTarget = pinsTarget; _pinsMounted = pinsTarget; _pinsSettled = true; }
            else if (pinsTarget != _pinsTarget) { _pinsTarget = pinsTarget; _pinsSettled = false; }
            UseTimeout(_settlePins, Shell.FrameRules.CardMotionMs + 40f, DepKey.From(pinsTarget ? 1 : 0));
            _ = _pinsTick.Value;
            bool pinsMounted = _pinsMounted = ZuneNavRules.PinsMounted(pinsTarget, _pinsMounted, _pinsSettled);
            // The pins' covers: the band pumps the binder (the pane is not mounted under Zune) and re-renders on its first projection.
            EnsureBinder();
            UseSignalEffect(PumpWhenZune);
            _ = s_binderEpoch.Value;
            var style = NavStyle.Value;
            bool zune = style == ShellNavStyle.Zune;
            float gutter = Shell.Ui.PageGutter.Value;

            Element[] rows = [];
            if (zune)
            {
                var route = Shell.Current.Value;
                string name = Shell.NameOf(route);
                rows = [TopRow(name, pinsMounted, pinsTarget), Row2Host(in route, name), Spacer(ZuneNavRules.SubToCard)];
            }

            // Band HEIGHT follows the live style (the frame commit) and the PRESENTED row-2 kind (posted after a navigation's
            // commit), and reveals on the card's own tween.
            return new BoxEl
            {
                Key = "zune:band", Direction = 1, Shrink = 0f, ClipToBounds = true,
                Height = ZuneNavRules.BandHeight(style, Shell.Ui.PresentedSubRow.Value), Animate = Shell.ZuneBandAnim,
                Padding = new Edges4(Shell.FrameRules.ZuneBandInset(gutter), 0f, Spacing.L, 0f), Children = rows,
            }.WithContextMenu(overlay, () => ZuneMenu(overlay));
        }

        /// <summary>Row 1: the top pivots, and the pin tiles beside them when the setting is on and the page column is wide enough
        /// (<paramref name="pinsMounted"/>: in the row; <paramref name="pinsVisible"/>: the target, faded by opacity).
        /// <see cref="ZuneNavRules.PivotRowHeight"/> tall: the pivot line (<see cref="ZuneNavRules.PivotLine"/>) with its air
        /// above and below.</summary>
        Element TopRow(string name, bool pinsMounted, bool pinsVisible)
        {
            string? top = ZuneNavRules.TopOf(name);
            // The band re-renders when the pin list changes and when the binder resolves the entries that give tiles their covers.
            _ = (Binder?.Entries ?? Entries).Version.Value + PinsVersion.Value;

            var pivots = new Element[ZuneNavRules.Top.Length];
            for (int i = 0; i < pivots.Length; i++)
            {
                string key = ZuneNavRules.Top[i];
                pivots[i] = PivotItem(key, Title(key), key == top, sub: false, () => Shell.GoTo(Shell.Parse(ZuneNavRules.LandingOf(key))));
            }
            Element strip = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = ZuneNavRules.PivotRowHeight,
                Padding = new Edges4(0f, ZuneNavRules.PivotTop, 0f, ZuneNavRules.PivotToSub),
                Children =
                [
                    ScrollView(new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.PivotGap, MinWidth = 0f, Children = pivots,
                    }, horizontal: true) with
                    {
                        Height = ZuneNavRules.PivotLine, AlignSelf = FlexAlign.Stretch, Shrink = 0f, MinWidth = 0f, AutoEdgeFade = true,
                        SuppressScrollBar = true, ScrollKey = "zune.pivots",
                    },
                ],
            };

            var row = new List<Element>(2) { strip };
            if (pinsMounted)
            {
                ZuneNavRules.PinTiles(Pins.Items, _tiles);
                if (_tiles.Count > 0)
                {
                    var items = new List<Element>(_tiles.Count + 1)
                    {
                        Design.Type.MicroMeta(Loc.Get("sidebar.zune.pinned")) with { Color = Shell.Ui.ChromeTertiary, Shrink = 0f },   // ink: see Shell.Ui.ChromeInkMix
                    };
                    for (int i = 0; i < _tiles.Count; i++) items.Add(PinColumn(_tiles[i]));
                    row.Add(new BoxEl
                    {
                        Key = "zune:pins", Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.PinGap, Shrink = 0f, Children = [.. items],
                        Opacity = pinsVisible ? 1f : 0f, HitTestVisible = pinsVisible,
                        Enter = PageHead.FadeIn, Transition = PageHead.FadeMotion,
                    });
                }
            }

            return new BoxEl
            {
                Key = "zune:top", Direction = 0, Height = ZuneNavRules.PivotRowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
                Children = [.. row], Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
            };
        }

        // ══ ROW 2 ═════════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Row 2: ALWAYS mounted (key <c>zune:row2</c>) as a <see cref="ZuneNavRules.SubRowHeight"/> box, whatever the route
        /// carries, so a page publishing never moves the content card. Its content is chosen from the ROUTE
        /// (<see cref="ZuneNavRules.SubRowOf"/>) and keyed by kind, so pivoting cross-fades the words while the row's height never
        /// changes. A route without a row 2 collapses the BAND (<see cref="Shell.Ui.PresentedSubRow"/>), not this host.</summary>
        static Element Row2Host(in Shell.Route route, string name)
            => Embed.Comp(new RowProps(route, name), static () => new Row2HostView()) with { Key = "zune:row2" };

        /// <summary>The row-2 host. It LATCHES the last non-None content (the row kind plus the route it was built for). While the
        /// route has a row 2 it renders that content; when the route has none it keeps rendering the latched content, inert, with
        /// its opacity eased to 0 as <see cref="Shell.Ui.PresentedSubRow"/> catches up, while the band's height closes (a Relayout)
        /// under its clip. That is an opacity Transition on a node that stays mounted, not an Exit, so ZuneBandAnim's descendant
        /// suppression (which snaps geometry tracks only) cannot cull it. The latch clears after the band has closed, so an empty
        /// strip is never visible: in the one commit before the presented row catches up, the old content is still at full opacity.
        /// When a row enters (52 to 84) the new content draws at opacity 0 and fades in when the presented row flips.</summary>
        sealed class Row2HostView : Component
        {
            /// <summary>The band's close (<see cref="Shell.FrameRules.CardMotionMs"/>) plus the fade's tail.</summary>
            const float LatchClearMs = Shell.FrameRules.CardMotionMs + 120f;

            (ZuneSubRow Kind, Shell.Route Route, string Name)? _shown;
            readonly Signal<int> _cleared = new(0);
            readonly Action _clear;

            public Row2HostView() => _clear = Clear;

            void Clear()
            {
                if (Shell.Ui.PresentedSubRow.Peek() != ZuneSubRow.None || _shown is null) return;
                _shown = null;
                _cleared.Value++;
            }

            public override Element Render()
            {
                var p = UseProps<RowProps>();
                _ = _cleared.Value;
                var presented = Shell.Ui.PresentedSubRow.Value;
                var kind = ZuneNavRules.SubRowOf(p.Route);
                UseTimeout(_clear, LatchClearMs, DepKey.From(presented == ZuneSubRow.None ? 1 : 0));
                if (kind != ZuneSubRow.None) _shown = (kind, p.Route, p.Name);

                bool live = kind != ZuneSubRow.None && presented != ZuneSubRow.None;
                Element[] kids = [];
                if (_shown is { } held)
                {
                    var r = held.Route;
                    kids = [held.Kind switch
                    {
                        ZuneSubRow.Library => LibraryRow(held.Name),
                        ZuneSubRow.Views => ViewsRow(in r, held.Name),
                        ZuneSubRow.Categories => CategoriesRow(in r),
                        _ => ContextRow(in r, held.Name),
                    }];
                }
                return new BoxEl
                {
                    Key = "zune:sub", Direction = 0, ZStack = true, Height = ZuneNavRules.SubRowHeight, Shrink = 0f, MinWidth = 0f,
                    Opacity = presented != ZuneSubRow.None ? 1f : 0f, HitTestVisible = live,
                    Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = PageHead.FadeMotion,
                    Children = kids,
                };
            }
        }

        /// <summary>A horizontal strip of row-2 words, scrolling rather than wrapping or clipping a long set.</summary>
        static Element WordStrip(Element[] words, string scrollKey) => ScrollView(new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.SubPivotGap, MinWidth = 0f, Children = words,
        }, horizontal: true) with
        {
            Height = ZuneNavRules.SubRowHeight, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AutoEdgeFade = true,
            SuppressScrollBar = true, ScrollKey = scrollKey,
        };

        /// <summary>A skeleton slot: a bar of the final word's size on the row's 20 line, in a box the row's height. Row 2 swaps
        /// it for the word by opacity, so the swap never changes the row.</summary>
        static Element SkeletonSlot(float width, float height) => new BoxEl
        {
            Height = ZuneNavRules.SubRowHeight, Shrink = 0f, AlignItems = FlexAlign.Center, HitTestVisible = false,
            Children = [Controls.PendingBar(width, height)],
        };

        // ── Library ──

        /// <summary>Library's sub-pivots. Their words are the same on every Library page, so the content key carries no route:
        /// pivoting between Library pages changes ink and weight in place and never cross-fades the words.</summary>
        static Element LibraryRow(string name)
        {
            var pages = ZuneNavRules.LibraryPages;
            var subs = new Element[pages.Length];
            for (int i = 0; i < pages.Length; i++)
            {
                string page = pages[i];
                subs[i] = PivotItem(page, Shell.Dest(Shell.Parse(page)).Title.ToLower(CultureInfo.CurrentCulture), page == name, sub: true,
                    () => Shell.GoTo(Shell.Parse(ZuneNavRules.LandingOf(page))));
            }
            return WordStrip(subs, "zune.sub") with
            {
                Key = "zune:sub:library", Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
            };
        }

        // ── Categories ──

        /// <summary>The four top Browse categories, in <see cref="BrowseTaxonomy.TopUris"/> order. A word navigates to its page
        /// (Live events to the Concerts hub); it is on when the route is that category's page, and none is on at the Browse root.
        /// One stable key, so moving between the four only re-weights a word.</summary>
        static Element CategoriesRow(in Shell.Route route)
        {
            var uris = BrowseTaxonomy.TopUris;
            var keys = ZuneNavRules.CategoryLabelKeys;
            string current = route.Kind == Shell.RouteKind.BrowseCategory ? route.Subject.Text : "";
            var words = new Element[uris.Count];
            for (int i = 0; i < words.Length; i++)
            {
                string uri = uris[i];
                string label = Loc.Get(keys[i]);
                words[i] = PivotItem("cat:" + i, label.ToLower(CultureInfo.CurrentCulture), uri == current, sub: true, () =>
                {
                    var target = BrowseTiles.FeatureRoute(uri);
                    Shell.GoTo(target.IsNone ? BrowseTiles.PageRoute(uri, label) : target);
                });
            }
            return WordStrip(words, "zune.categories") with
            {
                Key = "zune:sub:categories", Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
            };
        }

        // ── Views ──

        /// <summary>What a row-2 component needs from the route: the route itself (its facet, its display name) and its name,
        /// which is the key a page publishes under.</summary>
        sealed record RowProps(Shell.Route Route, string Name);

        /// <summary>A page's own views (Home's facet words, Settings' tabs) and its trailing control (Following), as the page
        /// published them under its route name; until it does, the route's SEED. Its own component, so a facet change
        /// re-renders row 2's words, not the band.</summary>
        static Element ViewsRow(in Shell.Route route, string name)
            => Embed.Comp(new RowProps(route, name), static () => new ViewsRowView()) with { Key = "zune:views-comp:" + name };

        sealed class ViewsRowView : Component
        {
            public override Element Render()
            {
                var p = UseProps<RowProps>();
                return ViewsContent(p.Route, p.Name);
            }
        }

        static Element ViewsContent(in Shell.Route route, string name)
        {
            // Seeded from the first frame of the reveal, like LibraryRow: row 2 is never an empty strip. The words
            // are drawn at opacity 0 until the style is PRESENTED (the page's own words are still on screen until then), so
            // they show in exactly one place per commit and fade in with the head's reflow instead of mounting late.
            bool presented = Shell.Ui.PresentedNavStyle.Value == ShellNavStyle.Zune;
            float shown = presented ? 1f : 0f;
            var pub = Shell.PageViews.For(name);
            var kind = route.Kind;
            var kids = new List<Element>(2);
            var seedKeys = ZuneNavRules.ViewSeedKeys(route.Kind);
            bool published = pub is not null && pub.Labels.Count > 0;
            if (published || seedKeys is not null)
            {
                int n = published ? pub!.Labels.Count : seedKeys!.Length;
                int selected = published ? pub!.Selected.Value : ZuneNavRules.SeedSelected(in route);
                var words = new Element[n];
                for (int i = 0; i < n; i++)
                {
                    int at = i;
                    string label = published ? pub!.Labels[i] : Loc.Get(seedKeys![i]);
                    // A click resolves the LATEST delegate (a re-publish that only changes it does not bump the store's Version).
                    // Before any publication it is ignored, except Settings, whose tab is a plain call.
                    words[i] = PivotItem("view:" + i, label.ToLower(CultureInfo.CurrentCulture), i == selected, sub: true, () =>
                    {
                        if (Shell.PageViews.Peek(name) is { } live) live.OnSelect(at);
                        else if (kind == Shell.RouteKind.Settings) Settings.Open((Settings.Tab)at);
                    });
                }
                kids.Add(new BoxEl
                {
                    Key = "zune:view:bar", Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Shrink = 1f, Basis = 0f,
                    MinWidth = 0f, Opacity = shown, HitTestVisible = presented,
                    Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
                    Children = [WordStrip(words, "zune.views")],
                });
            }
            else
            {
                // Search before its first answer: skeleton words of the facet row's size.
                var bars = new Element[5];
                for (int i = 0; i < bars.Length; i++)
                    bars[i] = SkeletonSlot(Detail.BandLayout.EstimateLabelWidth(8, 0f), 12f);
                kids.Add(new BoxEl
                {
                    Key = "zune:view:skeleton", Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.SubPivotGap,
                    Grow = 1f, MinWidth = 0f, Opacity = shown, Children = bars,
                    Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
                });
            }
            if (published && pub!.Trailing?.Invoke() is { } trailing)
                kids.Add(new BoxEl
                {
                    Key = "zune:view:trailing", Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [trailing],
                    Opacity = shown, HitTestVisible = presented, Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
                });
            return new BoxEl
            {
                Key = "zune:sub:views:" + name, Direction = 0, Grow = 1f, Height = ZuneNavRules.SubRowHeight, AlignItems = FlexAlign.Center,
                MinWidth = 0f, Gap = Spacing.M, Children = [.. kids],
                Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
            };
        }

        // ── Context ──

        /// <summary>An entity's title row: its title, its pivots and its actions as the page published them under its route
        /// name; until then the title from the route's display name plus skeleton pivots and action pills of the final size.
        /// It is the in-page band's OWN cluster (<see cref="Detail.BandCluster"/>) at the row's 32 (= <see cref="Detail.BandLayout.ItemHeight"/>),
        /// so the title, the tabs, the underline and the actions have identical geometry in the page and here.</summary>
        static Element ContextRow(in Shell.Route route, string name)
            => Embed.Comp(new RowProps(route, name), static () => new ContextRowView()) with { Key = "zune:context-comp:" + name };

        sealed class ContextRowView : Component
        {
            public override Element Render()
            {
                var p = UseProps<RowProps>();
                return ContextContent(p.Route, p.Name);
            }
        }

        static Element ContextContent(in Shell.Route route, string name)
        {
            var pub = Shell.PageBands.For(name);
            bool live = pub is not null;
            var (seedPivots, seedActions) = ZuneNavRules.ContextSeed(route.Kind);
            float rowH = ZuneNavRules.SubRowHeight;

            // The title. The same keyed node for the skeleton and the live band, so the swap is a text change, not a re-mount.
            string title = pub?.Title ?? Entities.Strings.Resolve(route.Arg);
            Element titleEl = title.Length == 0
                ? SkeletonSlot(Detail.BandLayout.EstimateLabelWidth(12, 0f), 12f) with { Key = "zune:ctx:title" }
                : TitleWord(title, pub?.OnTitle);

            // The title: the cluster's lead slot (the live band's search swap is the table toolbar's now, not row 2's).
            Element lead = new BoxEl
            {
                Key = "zune:ctx:lead", Direction = 0, MinWidth = 0f, Shrink = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children = [titleEl],
            };

            // The pivots: a skeleton layer and the live tabs share one lane and cross-fade in place. The divider is part of the
            // cluster, so it is present exactly when the lane is.
            bool hasPivots = live ? pub!.Pivots.Count > 0 : seedPivots > 0;
            Element? pivotsEl = null;
            if (hasPivots)
            {
                var seedKids = new Element[seedPivots];
                for (int i = 0; i < seedKids.Length; i++) seedKids[i] = SkeletonSlot(Detail.BandLayout.EstimateLabelWidth(8, 0f), 12f);
                Element seedLayer = new BoxEl
                {
                    Key = "zune:ctx:seed", Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.SubPivotGap, Height = rowH,
                    HitTestVisible = false, Opacity = live ? 0f : 1f, Transition = s_viewsFade, Children = seedKids,
                };
                var liveKids = new List<Element>(1);
                if (pub is not null)
                {
                    int current = Detail.BandLayout.PivotCurrent(pub.Active.Value, pub.Pivots.Count);
                    var accent = pub.Accent ?? s_defaultAccent;
                    var words = new Element[pub.Pivots.Count];
                    for (int i = 0; i < words.Length; i++)
                    {
                        int at = i;
                        words[i] = SpyTab("ctx:" + i, pub.Pivots[i].ToLower(CultureInfo.CurrentCulture), i == current, accent,
                            () => Shell.PageBands.Peek(name)?.OnPivot(at));
                    }
                    liveKids.Add(WordStrip(words, "zune.context"));
                }
                Element liveLayer = new BoxEl
                {
                    Key = "zune:ctx:live", Direction = 0, AlignItems = FlexAlign.Center, Height = rowH,
                    HitTestVisible = live, Opacity = live ? 1f : 0f, Transition = s_viewsFade, Children = [.. liveKids],
                };
                pivotsEl = new BoxEl
                {
                    Key = "zune:ctx:pivots", Direction = 0, ZStack = true, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Height = rowH,
                    Children = [seedLayer, liveLayer],
                };
            }

            // The actions: skeleton pills, then the page's own cluster, end-aligned so a width difference never leaves a gap.
            // The lane stays while the seed OR the publication has it, so pills the page never fills (the own profile) fade out in place.
            bool hasActions = seedActions > 0 || (live && pub!.Actions is not null);
            Element? actionsEl = null;
            if (hasActions)
            {
                var pills = new Element[seedActions];
                for (int i = 0; i < pills.Length; i++)
                    pills[i] = SkeletonSlot(Detail.BandLayout.EstimateLabelWidth(6, Detail.BandLayout.ActionPadX), 20f);
                Element seedPills = new BoxEl
                {
                    Key = "zune:ctx:pills", Direction = 0, AlignItems = FlexAlign.Center, Gap = Detail.BandLayout.ActionGap, Height = rowH,
                    HitTestVisible = false, Opacity = live ? 0f : 1f, Transition = s_viewsFade, Children = pills,
                };
                Element liveActions = new BoxEl
                {
                    Key = "zune:ctx:actions", Direction = 0, Shrink = 0f, Height = rowH, AlignItems = FlexAlign.Center,
                    HitTestVisible = live, Opacity = live ? 1f : 0f, Transition = s_viewsFade,
                    Children = pub?.Actions?.Invoke() is { } actions ? [actions] : [],
                };
                actionsEl = new BoxEl
                {
                    Key = "zune:ctx:actions-stack", Direction = 0, ZStack = true, Justify = FlexJustify.End, Shrink = 0f, Height = rowH,
                    Children = [seedPills, liveActions],
                };
            }

            Element cluster = new BoxEl
            {
                Key = "zune:sub:context:" + name, Direction = 0, Grow = 1f, Height = rowH, MinWidth = 0f, AlignItems = FlexAlign.Center,
                Children = Detail.BandCluster(lead, pivotsEl, actionsEl, rowH, dividerInk: Shell.Ui.ChromeOnMedia ? s_dividerOnMedia : (Prop<ColorF>?)null),
                Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = s_viewsFade,
            };
            return cluster;
        }

        /// <summary>Row 2's divider over the artist bleed: the theme's hairline cross-fading to the on-media one with the chrome ink mix
        /// (<c>Shell.Ui.ChromeInkMix</c>, the one source of truth).</summary>
        static readonly Prop<ColorF> s_dividerOnMedia = Prop.Of(static () =>
            ArtistBleed.Ink(Tok.StrokeDividerDefault, Design.OnMedia.Stroke, Shell.Ui.ChromeInkMix()));

        /// <summary>The page accent when a publication names none.</summary>
        static readonly Func<ColorF> s_defaultAccent = static () => Tok.AccentDefault;

        /// <summary>The entity's title word: <c>ZuneSubPivot</c> selected (600), primary ink, capped at the band's title width so a
        /// long name never pushes the tabs. When the page offers a title action (scroll to top) the word is a button.</summary>
        static Element TitleWord(string title, Action? onTitle)
        {
            Element word = Design.Type.ZuneSubPivot(title, selected: true) with
            {
                Color = Shell.Ui.ChromePrimary, MinWidth = 0f, Shrink = 1f, Trim = TextTrim.CharacterEllipsis,   // ink: see Shell.Ui.ChromeInkMix
            };
            var box = new BoxEl
            {
                Key = "zune:ctx:title", Direction = 0, Height = ZuneNavRules.SubRowHeight, AlignItems = FlexAlign.Center,
                MaxWidth = Detail.BandLayout.TitleCap, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Children = [word],
            };
            return onTitle is null
                ? box with { HitTestVisible = false }
                : box with
                {
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, Corners = Radii.ControlAll,
                    FocusVisualMargin = Design.FocusInsetRow, OnClick = onTitle,
                };
        }

        static readonly Func<ColorF> s_noUnderline = static () => ColorF.Transparent;

        /// <summary>A scroll-spy tab: the word at a CONSTANT weight (400), ink carrying the state (primary when active), and the
        /// page-accent underline an overlay at <see cref="Detail.BandLayout.UnderlineY"/> in a slot of <see cref="Detail.BandLayout.ItemHeight"/> —
        /// the in-page band's own tab geometry.</summary>
        static Element SpyTab(string key, string label, bool on, Func<ColorF> accent, Action go) => new BoxEl
        {
            Key = "zune:" + key, ZStack = true, Role = AutomationRole.Tab, Focusable = true, Cursor = CursorId.Hand, Shrink = 0f,
            Height = ZuneNavRules.SubRowHeight, Corners = Radii.ControlAll, FocusVisualMargin = Design.FocusInsetRow, OnClick = go,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Children =
                    [
                        Design.Type.ZuneSubPivot(label, selected: false) with
                        {
                            // ink: see Shell.Ui.ChromeInkMix (rest is the bound mix; hover and pressed are the static arms)
                            Color = WordInk(on), HoverColor = HoverInk, PressedColor = PressedInk,
                            BrushTransitionMs = Design.Motion.Faster,
                        },
                    ],
                },
                new BoxEl
                {
                    Height = Detail.BandLayout.UnderlineHeight, AlignSelf = FlexAlign.Stretch,
                    Margin = new Edges4(0f, Detail.BandLayout.UnderlineY, 0f, 0f),
                    Fill = on ? accent : s_noUnderline, BrushTransitionMs = Design.Motion.Faster, HitTestVisible = false,
                },
            ],
        };

        static readonly MotionTokenDef s_viewsFade =
            MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

        /// <summary>A row word's rest ink (primary when it is the lit one, else secondary): the paint-rate bind while a backdrop shows
        /// (<see cref="Shell.Ui.ChromeOnMedia"/>, a navigation-rate read in the band's render), the theme token otherwise, which keeps
        /// the word's brush transition on a selection change.</summary>
        static Prop<ColorF> WordInk(bool on) => on ? Shell.Ui.ChromePrimary : Shell.Ui.ChromeSecondary;

        /// <summary>The words' static hover and pressed arms: the theme's until the mix passes one half
        /// (<see cref="Shell.Ui.ChromeArmsOnMedia"/>), the on-media ink's after, and back to the theme's once the hero has scrolled
        /// mostly away.</summary>
        static ColorF HoverInk => Shell.Ui.ChromeArmsOnMedia.Value ? Design.OnMedia.Ink : Tok.TextPrimary;
        /// <inheritdoc cref="HoverInk"/>
        static ColorF PressedInk => Shell.Ui.ChromeArmsOnMedia.Value ? Design.OnMedia.InkTertiary : Tok.TextTertiary;

        /// <summary>A pivot: its text is the target, so the item has no plate and no scale. The word carries the state in ink
        /// alone (secondary at rest, primary on hover and when selected, tertiary on press) and in weight (the selected one
        /// reads heavier, through the role). The box exists for the click, the cursor and the focus ring.</summary>
        static Element PivotItem(string key, string label, bool on, bool sub, Action go) => new BoxEl
        {
            Key = "zune:" + key, Role = AutomationRole.Tab, Focusable = true, Cursor = CursorId.Hand, Shrink = 0f,
            AlignItems = FlexAlign.Center, Corners = Radii.ControlAll, FocusVisualMargin = Design.FocusInsetRow, OnClick = go,
            Children =
            [
                (sub ? Design.Type.ZuneSubPivot(label, on) : Design.Type.ZunePivot(label, on)) with
                {
                    // ink: see Shell.Ui.ChromeInkMix (rest is the bound mix; hover and pressed are the static arms)
                    Color = WordInk(on), HoverColor = HoverInk, PressedColor = PressedInk,
                    BrushTransitionMs = Design.Motion.Faster,
                },
            ],
        };

        static string Title(string key) => (key == ZuneNavRules.LibraryPivot ? Loc.Get("sidebar.library.title")
            : Shell.Dest(Shell.Parse(key)).Title).ToLower(CultureInfo.CurrentCulture);

        // ══ PINS ══════════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>A pin: its 32-DIP tile over the now-playing dot. The dot's box is ALWAYS laid out (4 DIP), so a pin starting
        /// to play only changes its ink. 32 + 2 + 4 = 38, inside the 44-DIP pivot row.</summary>
        static Element PinColumn(SidebarPin pin) => new BoxEl
        {
            // The top margin balances the dot hanging below the tile, so the TILE (not the column) sits on the row's centre line,
            // level with the caption and the pivot words.
            Direction = 1, AlignItems = FlexAlign.Center, Gap = ZuneNavRules.PinDotGap, Shrink = 0f,
            Margin = new Edges4(0f, ZuneNavRules.PinDotGap + ZuneNavRules.PinDot, 0f, 0f),
            Children =
            [
                PinTile(pin),
                Embed.Comp(() => new PinDotView(pin.Uri)) with { Key = "zune:dot:" + pin.Id },
            ],
        };

        /// <summary>A 32-DIP pin tile: the route's glyph for an app-route pin, the entry's cover once resolved, else the kind's
        /// glyph. The tile keeps its plate (a tile is a button) with no padding inside its fixed 32, so the plate hugs the tile.</summary>
        static Element PinTile(SidebarPin pin)
        {
            string route = pin.RouteKey;
            Element art = pin.Kind == SidebarEntryKind.AppRoute
                ? Icon(Shell.Dest(Shell.Parse(pin.Id)).Glyph, 16f) with { Color = Shell.Ui.ChromeSecondary }   // ink: see Shell.Ui.ChromeInkMix
                : ResolvedPin(pin.Id) is { } entry ? Cover.ForEntry(in entry, ZuneNavRules.PinTile)
                : Icon(SidebarKindGlyph.For(pin.Kind, pin.Id, pin.Uri), 16f) with { Color = Shell.Ui.ChromeSecondary };
            var tile = new BoxEl
            {
                Width = ZuneNavRules.PinTile, Height = ZuneNavRules.PinTile, Shrink = 0f, Corners = Radii.ControlAll,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Role = AutomationRole.Button, Focusable = true,
                Cursor = CursorId.Hand, OnClick = () => Shell.GoTo(Shell.Parse(route)), Children = [art],
            }.Interactive(Interaction.Subtle);
            return ToolTip.Wrap(tile, SidebarMenus.PinName(pin.Id));
        }

        /// <summary>The now-playing dot under a pin. Read COARSE-FIRST like the pane's rows (<c>RefreshPlayState</c>): an idle
        /// app never asks the per-pin relation. Its own component, so a track skip re-renders four-DIP dots, not the band.</summary>
        sealed class PinDotView(string uri) : Component
        {
            public override Element Render()
            {
                var seam = Controls.NowPlaying;
                bool active = seam is not null && seam.HasActiveContext.Value;
                bool lit = ZuneNavRules.PinShowsPlaying(uri, active, active && uri.Length > 0 && seam!.RelatesTo(uri));
                var dot = new BoxEl
                {
                    Width = ZuneNavRules.PinDot, Height = ZuneNavRules.PinDot, Shrink = 0f, Corners = Radii.FullAll,
                    HitTestVisible = false, BrushTransitionMs = Design.Motion.Faster,
                };
                return lit ? dot with { Fill = Tok.AccentDefault } : dot with { Fill = ColorF.Transparent };
            }
        }

        /// <summary>The projection's resolved row for a pin (its cover, its kind), or null until the projection has it.</summary>
        static SidebarLibraryEntry? ResolvedPin(string id)
        {
            var pins = Binder?.CurrentInput.Pins;
            if (pins is null) return null;
            for (int i = 0; i < pins.Count; i++) if (pins[i].Id == id) return pins[i];
            return null;
        }

        /// <summary>The band's menu: the pane menu's Zune rows, on the overlay host the band opened on.</summary>
        static ContextMenuModel? ZuneMenu(IOverlayService overlay)
        {
            SidebarMenus.Overlay = overlay;
            return new ContextMenuModel(SidebarMenus.Map(SidebarMenuModel.Pane(Layout.Peek(), State, Density.Peek(), Editing.Peek(),
                SidebarMenus.LockingNames(), ClassicCovers.Peek(), zune: true, zunePins: ZunePins.Peek())));
        }
    }
}
