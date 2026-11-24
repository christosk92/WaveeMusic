// ── Themes/Common/CommonLocalCollection.Split.cs — "On this PC"'s Albums and Artists as the library's list-detail (WinUI and Classic, #240) ─────
//
// The owner (2026-11-24): the Albums and Artists LIST views behave as Wavee's Library does (W-LibraryList: ref-albums, ref-artists): where the library
// splits, the list stands in a navigator column on the left and the picked row's detail fills the right; a pick never navigates.
//
//   SplitHost (one per pivot; measures the body's width)
//   └─ the arm, keyed and cross-faded (the declared swap, `L.SplitSwap`) when the layout tier crosses or the view switches list <-> grid:
//      ├─ "lc-split"  [gutter] NAVIGATOR "lc-nav" [toolbar (stacked) · the bound list, AccentPill rows] │ SEAM (splitter) │ PANE "lc-pane" [gutter]
//      └─ "lc-whole"  the toolbar over the cards or rows, as before (a row or card opens today's detail: the album's folder, its cover flying)
//
// WHERE IT SPLITS is the library's own decision: the structure's layout ladder (`LibraryLadders.Layout`: WinUI and Classic split from 600 of the
// page's width, held 24) over the body's measured width, seeded from the same estimate the library seeds with, so a first mount is in its arm.
// Only the list views split (the library's navigator is a list); the grids keep the drill-in.
// THE NAVIGATOR is the library navigator's: the WinUI ListView item chrome (`SelectorVisualsBound.AccentPill`: the plate and the accent pill) over
// bound rows (40 art, the 14/20 semibold title over the 12/16 line), one ItemsView mount per row form; its width rests at the library's own (the
// user's library width once they dragged it there, else the structure's width for the tier); a drag here is this session's.
// THE SELECTION is the core's (`LocalCollectionPageProps.Selected/Pick`, remembered): a pick writes it, the list's selection model follows it, a key
// the shown set lacks adopts the first row (the library's rule), and the pane (`Pane`: the library album pane over the local data) re-pushes its key.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using L = Wavee.Themes.Common.CommonLocalCollectionRules;
using K = Wavee.Themes.Common.CommonLocalCollectionRules;
using P = Wavee.Strings.LocalFiles.Page;

namespace Wavee.Themes.Common;

internal static partial class CommonLocalCollectionParts
{
    /// <summary>One navigator row: the item's key, its two lines and its picture.</summary>
    readonly record struct NavItem(string Key, string Title, string Sub, string Cover);

    sealed record SplitProps(LocalPivot Pivot, string Kind, Element Sort, ListProps List, LibraryControlsStyle? Controls)
    {
        public bool Equals(SplitProps? o) => ReferenceEquals(this, o);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    /// <summary>Albums or Artists: the list beside its detail where the library splits, else the list alone (see the file header).</summary>
    static Element Split(LocalPivot pivot, string kind, Element sort, ListProps list, LibraryControlsStyle? controls)
        => Embed.Comp(new SplitProps(pivot, kind, sort, list, controls), static () => new SplitHost()) with { Key = list.Key + ":split" };

    sealed class SplitHost : Component
    {
        SplitProps? _sp;
        readonly Signal<float> _w = new(0f);
        readonly Action<RectF> _onBounds;
        readonly Func<float> _seed;
        IReadSignal<ThemeFacts>? _facts;
        IReadSignal<Size2>? _viewport;
        Signal<float>? _navW;
        bool _navDragged, _armSeen, _bring = true;
        string _arm = "";
        readonly Signal<bool> _dragging = new(false);
        readonly Action _syncDrag, _restNav, _syncSel, _onSel, _reassert;
        readonly Action<NodeHandle> _onListMounted;
        Action<Action>? _post;
        LibraryNavMetrics? _nav;
        Element? _seam;
        Memo<NavItem[]>? _shown;
        BoundItemsSource<NavItem>? _items;
        readonly SelectionModel _sel = new();
        readonly ItemsViewController _ctl = new();
        bool _syncing;
        ListOptions<NavItem>? _options;
        Func<BoundItemScope<NavItem>, Element>? _row, _rowCompact;
        readonly Func<NavItem[]> _compute;
        // The artists by most songs, for the view they were sorted from.
        LocalCollectionView? _bySongsFor;
        List<LocalArtistCard>? _bySongs;

        public SplitHost()
        {
            _onBounds = r => { if (r.W > 0f && r.W != _w.Peek()) _w.Value = r.W; };
            _seed = () =>
            {
                float s = _facts is null ? 0f : Detail.Breakpoints.EstimatePageWidth(_facts.Peek().Tiers, _viewport?.Peek().Width ?? 0f);
                return s > 0f ? s : float.PositiveInfinity;
            };
            var dragging = _dragging;
            _syncDrag = () => FluentGpu.Dsl.Motion.SetLayoutTransitionsSuppressed(MotionSuppressionSource.AppResize, dragging.Value);
            _restNav = () =>
            {
                var facts = _facts?.Value;
                if (_navW is null || facts is null || _navDragged) return;
                _navW.SetIfChanged(RestingNav(facts));
            };
            _compute = Compute;
            _syncSel = SyncSelection;
            _onSel = OnPicked;
            // A fresh list mount starts with nothing highlighted however the model stands (the library's rule): re-assert the pick on the next frame.
            _reassert = () =>
            {
                if (_sp is not { } sp || _shown is null) return;
                int idx = IndexOf(_shown.Peek(), sp.List.Page.Selected(sp.Pivot));
                if (idx < 0) return;
                _syncing = true;
                _sel.DeselectAll();
                _sel.Select(idx);
                _syncing = false;
                _ctl.StartBringItemIntoView(idx, float.NaN);
            };
            _onListMounted = _ => _post?.Invoke(_reassert);
        }

        string KindKey => _sp?.Kind ?? "albums";

        /// <summary>The navigator's resting width: the library's own for this kind (the user's, once they dragged the library's), else the structure's for
        /// the content tier, else the library key's default.</summary>
        float RestingNav(ThemeFacts facts)
        {
            string kind = KindKey;
            var key = LibraryViewPrefs.LeftWKey(kind);
            bool custom = LibraryViewPrefs.LeftWCustomized(kind);
            return LibraryViewPrefs.RestingLeftW(kind, custom ? LibraryViewPrefs.Read(kind).LeftW : key.Default, custom, facts.Tiers.Content, facts.Policies.LibraryTiers);
        }

        public override Element Render()
        {
            var sp = UseProps<SplitProps>();
            _sp = sp;
            var page = sp.List.Page;
            _facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
            _viewport = UseContextSignal(Viewport.Size);
            var tier = UseTier(LibraryLadders.Of(_facts.Peek().Policies.Tiers), TierSource.Measured(_w, _seed));
            _post = UsePost();
            // The structure's navigator leads (WinUI's: the row plates hang left of the page's line so the art stands on it); the core's otherwise.
            _nav = sp.Controls?.Nav is { } leads ? leads(_facts.Peek().Tiers) : null;
            _navW ??= new Signal<float>(RestingNav(_facts.Peek()));
            _shown = UseComputed(_compute);
            _items ??= BoundItems.Project(_shown, static s => s.Length, static (s, i) => (uint)i < (uint)s.Length ? s[i] : default, default(NavItem));
            UseEffect(_syncSel);
            UseEffect(_restNav);
            UseEffect(_syncDrag);

            bool list = page.ListView().Value < 2;
            // A pivot with nothing in it (no filter typed) keeps the page's own vacancy, whole: a navigator saying "no albums" beside "pick an album" said it twice.
            bool any = _shown.Value.Length > 0 || page.Filter().Value.Trim().Length > 0;
            bool split = list && any && !LibraryLadders.IsSingleColumn(tier.Value);
            string arm = split ? K.SplitKey : K.WholeKey;
            // The arm swap cross-fades (the declared swap); the first arm arrives with the page and its body's own entrance.
            var enter = _armSeen && arm != _arm ? K.SplitSwap : default;
            _armSeen = true;
            _arm = arm;
            Element body = split ? SplitArm(sp, page) : WholeArm(sp, page);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, OnBoundsChanged = _onBounds,
                Children = [body with { Key = arm + ":" + sp.Kind, Enter = enter, Exit = K.SplitSwap, Transition = K.SplitSwapMotion }],
            };
        }

        static BoxEl WholeArm(SplitProps sp, LocalCollectionPageProps page)
        {
            var lp = sp.List;
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
                Children = [Toolbar(sp.Kind, sp.Sort, page, lp.Lay, lp.Gutter, sp.Controls), Embed.Comp(lp, static () => new ListHost()) with { Key = lp.Key }],
            };
        }

        BoxEl SplitArm(SplitProps sp, LocalCollectionPageProps page)
        {
            var lp = sp.List;
            float g = lp.Gutter;
            bool compact = page.ListView().Value == 0;
            var shown = _shown!.Value;
            // The column stands the item lead left of the page's line (the structure's navigator leads, the library's): the toolbar pads it back, the row
            // plates hang in it, so the art stands on the line.
            float lead = MathF.Min(g, _nav?.ItemLead ?? K.NavItemLead);
            Element items = shown.Length == 0
                ? new BoxEl
                {
                    Direction = 1, Grow = 1f, MinHeight = 0f,
                    Children = [Controls.Vacancy(page.Filter().Value.Trim().Length > 0 ? Controls.VacancyVoice.NoMatch : Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                        page.Filter().Value.Trim().Length > 0 ? Loc.Get(Strings.Library.NoMatch) : Loc.Get(P.NoAlbums), "")],
                }
                : new BoxEl
                {
                    // One mount per row form: the template and the layout are frozen at mount.
                    Key = K.NavKey + ":list:" + (compact ? "compact" : "rows"), Direction = 1, Grow = 1f, MinHeight = 0f, OnRealized = _onListMounted,
                    Children = [ItemsView.CreateBound(_items!, compact ? (_rowCompact ??= RowTemplate(compact: true, sp.Pivot == LocalPivot.Artists, ArtLead()))
                                                                         : (_row ??= RowTemplate(compact: false, sp.Pivot == LocalPivot.Artists, ArtLead())),
                                                      RepeatLayout.Stack((compact ? K.NavRowCompactPlate : K.NavRowPlate) + 2f * K.NavRowMarginY), Options(page))],
                };
            Element nav = new BoxEl
            {
                Key = K.NavKey, Direction = 1, Width = _navW!, Shrink = 1f, MinWidth = K.NavMin, MinHeight = 0f, ClipToBounds = true, Animate = Design.Reveal.Resize,
                Children =
                [
                    new BoxEl
                    {
                        // The 40 Compact controls (a content-tier crossing) ease the toolbar's height, and the list under it glides.
                        Key = K.NavKey + ":toolbar", Direction = 1, MinWidth = 0f, Padding = new Edges4(lead, K.ToolbarTop, K.NavContentPadRight, K.NavToolbarBottom),
                        Layout = K.HeightReflow,
                        Children = [ToolbarBody(sp.Sort, page, lp.Lay, sp.Controls, stacked: true)],
                    },
                    items,
                ],
            };
            Element pane = new BoxEl
            {
                Key = K.PaneKey, Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                Children = [Embed.Comp(new PaneSlotProps(page, sp.Pivot, _shown), static () => new PaneSlot())],
            };
            return new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
                Children = [Air(K.SplitKey + ":l", MathF.Max(0f, g - lead)), nav, _seam ??= Seam(), pane, Air(K.SplitKey + ":r", g)],
            };
        }

        ListOptions<NavItem> Options(LocalCollectionPageProps page) => _options ??= new ListOptions<NavItem>
        {
            SelectionMode = ItemsSelectionMode.Single, Selection = _sel, Controller = _ctl, Grow = 1f,
            Scroll = new ScrollOptions { ScrollKey = page.RouteKey + ":" + KindKey + ":nav" },
            OnChange = _onSel, IsItemInvokedEnabled = true, InvokeOnTap = true,
            ItemTextTyped = static (_, it) => it.Title,
        };

        /// <summary>The seam: the library's column grip (a splitter strip with the divider hairline behind it), built once (its splitter is bound to the
        /// navigator's width).</summary>
        Element Seam() => new BoxEl
        {
            Key = K.SeamKey, Width = Splitter.StripW, Shrink = 0f, ZStack = true,
            Children =
            [
                new BoxEl { Width = K.SeamHairline, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Center, HitTestVisible = false, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                new BoxEl
                {
                    Direction = 1, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    Children = [Splitter.Create(_navW!, () => _navDragged = true, new Splitter.SplitterOptions { Min = K.NavMin, Max = K.NavMax }, dragging: _dragging)],
                },
            ],
        };

        /// <summary>The navigator row (the library navigator's): the bound art and two lines inside the WinUI ListView item chrome, the plate the row's height.</summary>
        /// <summary>Where a row's art stands in from its plate (the structure's lead, else the core's).</summary>
        float ArtLead() => _nav?.RowArtLead ?? K.NavRowArtLead;

        static Func<BoundItemScope<NavItem>, Element> RowTemplate(bool compact, bool round, float artLead) => scope =>
        {
            float corner = round ? K.NavArt / 2f : K.NavArtCorner;
            var title = new TextEl(scope.Text(static it => it.Title))
            {
                Size = K.NavTitleSize, LineHeight = K.NavTitleLine, Weight = K.NavTitleWeight, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
            var text = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = K.NavTextGap, MinWidth = 0f,
                Children = compact
                    ? [title]
                    : [title, new TextEl(scope.Text(static it => it.Sub))
                    {
                        Size = K.NavSubSize, LineHeight = K.NavSubLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    }],
            };
            Element content = new BoxEl
            {
                Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = K.NavArtGap, Padding = new Edges4(0f, 0f, K.NavContentPadRight, 0f), MinWidth = 0f,
                // The chrome pads its content 16 in; the art stands the lead in from the plate.
                Margin = new Edges4(artLead - LibraryNavMetrics.ChromeContentPad, 0f, 0f, 0f),
                Children = compact
                    ? [text]
                    : [new BoxEl
                    {
                        Width = K.NavArt, Height = K.NavArt, Shrink = 0f, Corners = CornerRadius4.All(corner), ClipToBounds = true,
                        Children =
                        [
                            new ImageEl
                            {
                                Source = scope.Image(static it => it.Cover), Width = K.NavArt, Height = K.NavArt, Fit = ImageFit.Cover, Corners = CornerRadius4.All(corner),
                                DecodePx = K.NavArt, Placeholder = scope.Color(static it => Design.PlaceholderFor(it.Cover)),
                            },
                        ],
                    }, text],
            };
            return SelectorVisualsBound.AccentPill(scope.Row, content) with { Cursor = CursorId.Hand, Height = compact ? K.NavRowCompactPlate : K.NavRowPlate };
        };

        /// <summary>The rows the navigator shows: the pivot's items in the sort in force, filtered as typed (a memo over the view, the sort and the filter).</summary>
        NavItem[] Compute()
        {
            var sp = _sp!;
            var page = sp.List.Page;
            var v = page.View.Value;
            string filter = page.Filter().Value.Trim();
            if (sp.Pivot == LocalPivot.Artists)
            {
                var sort = page.ArtistSort.Value;
                IReadOnlyList<LocalArtistCard> all = sort == LocalArtistSort.Name ? v.Artists : BySongs(v);
                var rows = new List<NavItem>(all.Count);
                foreach (var a in all)
                    if (filter.Length == 0 || Has(a.Name, filter)) rows.Add(new NavItem(a.Key, a.Name, CountsText(a.Albums, a.Tracks), a.Cover ?? ""));
                return rows.ToArray();
            }
            var albums = v.AlbumsBy(page.Sort.Value);
            var list = new List<NavItem>(albums.Count);
            foreach (var a in albums)
                if (filter.Length == 0 || Has(a.Title, filter) || Has(a.Artist, filter)) list.Add(new NavItem(a.Key, a.Title, AlbumLine(a, year: true), a.Cover ?? ""));
            return list.ToArray();
        }

        List<LocalArtistCard> BySongs(LocalCollectionView v)
        {
            if (!ReferenceEquals(_bySongsFor, v) || _bySongs is null)
            {
                var list = new List<LocalArtistCard>(v.Artists);
                list.Sort(static (a, b) => b.Tracks != a.Tracks ? b.Tracks.CompareTo(a.Tracks) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                _bySongs = list;
                _bySongsFor = v;
            }
            return _bySongs;
        }

        /// <summary>The list's selection follows the core's pick (an effect: the shown rows and the pick are tracked); a pick the shown set lacks adopts
        /// the first row, as the library's navigator does. The first sync brings the picked row into view.</summary>
        void SyncSelection()
        {
            var sp = _sp;
            if (sp is null || _shown is null) return;
            var shown = _shown.Value;
            string key = sp.List.Page.Selected(sp.Pivot);
            if (shown.Length == 0) return;
            int idx = IndexOf(shown, key);
            if (idx < 0)
            {
                sp.List.Page.Pick(sp.Pivot, shown[0].Key);
                return;   // the write re-runs this effect with the adopted key
            }
            if (_sel.FirstSelectedIndex != idx)
            {
                _syncing = true;
                _sel.DeselectAll();
                _sel.Select(idx);
                _syncing = false;
            }
            if (_bring)
            {
                _bring = false;
                _ctl.StartBringItemIntoView(idx, float.NaN);
            }
        }

        /// <summary>A row picked in the list (a click, the arrows): the core's pick, never a navigation.</summary>
        void OnPicked()
        {
            if (_syncing || _sp is not { } sp || _shown is null) return;
            int i = _sel.FirstSelectedIndex;
            var shown = _shown.Peek();
            if ((uint)i < (uint)shown.Length) sp.List.Page.Pick(sp.Pivot, shown[i].Key);
        }
    }

    static int IndexOf(NavItem[] shown, string key)
    {
        if (key.Length == 0) return -1;
        for (int i = 0; i < shown.Length; i++) if (string.Equals(shown[i].Key, key, StringComparison.Ordinal)) return i;
        return -1;
    }

    sealed record PaneSlotProps(LocalCollectionPageProps Page, LocalPivot Pivot, Memo<NavItem[]> Shown);

    /// <summary>The pane column's content: the pick's pane (re-pushed per pick: the library pane's own crossfade), or the library's "select" line.</summary>
    sealed class PaneSlot : Component
    {
        public override Element Render()
        {
            var p = UseProps<PaneSlotProps>();
            var shown = p.Shown.Value;
            string key = p.Page.Selected(p.Pivot);
            if (IndexOf(shown, key) < 0) key = shown.Length > 0 ? shown[0].Key : "";
            if (key.Length == 0)
                return new BoxEl
                {
                    Direction = 1, Grow = 1f, MinHeight = 0f,
                    Children = [Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                        Loc.Get(p.Pivot == LocalPivot.Artists ? Strings.Library.SelectArtist : Strings.Library.SelectAlbum), "")],
                };
            return p.Page.Pane(p.Pivot, key);
        }
    }
}
