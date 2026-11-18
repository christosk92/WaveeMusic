// ── Themes/Classic/Structure/Views/Podcast/ClassicPodcastShow.List.cs — the show page's list column as ONE virtualised list (Classic) ──────────────────
//
// The podcast pages list model (feat/podcast-pages "Podcast pages list:"): the list column is one ItemsView over the core's flattened `PodcastShowView.Items`,
// so only the items on screen mount (a show of 500 episodes scrolls and filters at the cost of what is visible). Classic's form of the WinUI plan:
//
//   ZStack (the list column)
//   ├─ ItemsView (the list column's scroller)   items = 2 prefix + Items + 1 foot
//   │  ├─ [0] the cover column at Compact (stacked over the list); nothing from Small up (the column stands in its frame beside the list)
//   │  ├─ [1] the list bar: a RAW sticky band (filters, search, sort), a backing only while pinned
//   │  ├─ [2..] Items: a lowercase group label · an episode row (expands in place; at Compact it opens) · Load more · the empty line
//   │  └─ [last] the foot: More like this and the page's foot room
//   └─ the pinned group label: an overlay under the list bar naming the group the list is in at that line (the list's own measured layout says which
//      item sits there), backed only while it shows
//
// Every slot reads the tier record from `_lay` (a signal the page writes on render), so a crossing re-pads the slots in place.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using P = Wavee.Themes.Classic.ClassicPodcastRules;

namespace Wavee.Themes.Classic;

internal static partial class ClassicPodcastShow
{
    /// <summary>One slot's item: a part of the page (0 the stacked column, 1 the list bar, 2 the foot) or a list item resolved against the snapshot. Records
    /// compare by value, so a slot whose episode did not change does not re-render when the list around it does.</summary>
    sealed record ShowSlot(int Part, PodcastItemKind Kind, PodcastGroup? Group, PodcastEpisodeVm? Vm, bool First)
    {
        public static readonly ShowSlot None = new(-1, PodcastItemKind.Empty, null, null, false);
    }

    sealed partial class PageHost
    {
        const int PrefixCount = 2, ColumnPart = 0, BarPart = 1, FootPart = 2;

        readonly ItemsViewController _ctl = new();
        readonly Signal<ClassicPodcastLayout> _lay = new(ClassicPodcastTiers.Standard);
        readonly Signal<uint> _tone = new(0u);
        readonly Signal<ColorF> _accent = new(ColorF.Transparent);
        readonly Signal<float> _colH = new(0f);
        readonly Signal<int> _filterIx = new(0);
        readonly Signal<bool> _barStuck = new(false), _searchOpen = new(false);
        readonly Dictionary<string, NodeHandle> _groupNodes = [];
        BoundItemsSource<ShowSlot>? _items;
        ListOptions<ShowSlot>? _listOptions;
        RepeatLayout? _listLayout;
        MeasuredStackVirtualLayout? _measured;
        Func<BoundItemScope<ShowSlot>, Element>? _template;
        Func<int>? _pinnedOf;
        Memo<int>? _pinned;
        Func<uint>? _toneOf;
        Action<RectF>? _measureColumn;
        Controls.Words.Word[]? _words;
        ClassicRowVerbs? _verbs;
        IOverlayService? _overlay;
        OverlayHandle? _jump;
        Action? _sync;

        /// <summary>The list's hooks and cells (called by the page's render, in a stable order).</summary>
        void UseList(ClassicPodcastLayout lay, uint tone, ColorF accent)
        {
            _lay.SetIfChanged(lay);
            _tone.SetIfChanged(tone);
            _accent.SetIfChanged(accent);
            _overlay = UseContext(Overlay.Service);
            _sync ??= () => _filterIx.SetIfChanged((int)_p.Filter.Value);
            UseEffect(_sync);
            _toneOf ??= () => _tone.Value;
            _measureColumn ??= r => { if (r.H > 1f && MathF.Abs(r.H - _colH.Peek()) > 0.5f) _colH.Value = r.H; };
            _verbs ??= new ClassicRowVerbs
            {
                Expanded = _p.Expanded, Playing = _p.Playing, Play = _p.Play, Open = _p.Open, ToggleSaved = _p.ToggleSaved, PlayNext = _p.PlayNext,
                Expand = _p.Expand, Menu = _p.RowMenu, Drag = _p.RowDrag,
            };
            _pinnedOf ??= PinnedGroup;
            _pinned = UseComputed(_pinnedOf);
        }

        /// <summary>The list column: the virtualised list and the pinned group label over it.</summary>
        Element ListColumn()
        {
            _items ??= BoundItems.Project(_p.View, static v => PrefixCount + v.Items.Length + 1, SlotAt, ShowSlot.None);
            _measured ??= new MeasuredStackVirtualLayout(P.RowEstimate, false, ExtentOf);
            _listLayout ??= RepeatLayout.Measured(_measured);
            _template ??= Template;
            _listOptions ??= new ListOptions<ShowSlot>
            {
                SelectionMode = ItemsSelectionMode.None,
                Selector = SelectorVisual.None,
                Controller = _ctl,
                Grow = 1f,
                ContentType = ContentTypeOf,
                PersistentPrefixCount = PrefixCount,
                Scroll = new ScrollOptions
                {
                    ScrollKey = "cp-scroll:" + _p.RouteKey, Handle = _scroll, AutoEdgeFade = false, EdgeCues = ScrollEdgeCues.None,
                    // The rows cut themselves at the pinned list bar's lower edge.
                    ItemClipTopInset = P.ListBarH,
                },
            };
            Element list = ItemsView.CreateBound<ShowSlot>(_items!, _template!, _listLayout!.Value, _listOptions) with { Key = "cp-list:" + _p.RouteKey };
            return new BoxEl
            {
                Key = "cp-list-z", ZStack = true, Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                Children =
                [
                    list,
                    new BoxEl
                    {
                        Key = "cp-pinned-layer", Direction = 1, HitTestPassThrough = true, MinWidth = 0f, IsolateLayout = true,
                        Padding = new Edges4(0f, P.ListBarH, 0f, 0f),
                        Children = [Embed.Comp(new SlotProps(this), static () => new PinnedHead()) with { Key = "cp-pinned-head" }],
                    },
                ],
            };
        }

        static ShowSlot SlotAt(PodcastShowView v, int i)
        {
            if (i < PrefixCount) return new ShowSlot(i, PodcastItemKind.Empty, null, null, false);
            int k = i - PrefixCount;
            if (k == v.Items.Length) return new ShowSlot(FootPart, PodcastItemKind.Empty, null, null, false);
            if ((uint)k >= (uint)v.Items.Length) return ShowSlot.None;
            var it = v.Items[k];
            var g = (uint)it.Group < (uint)v.Groups.Length ? v.Groups[it.Group] : null;
            var vm = it.Kind == PodcastItemKind.Row && (uint)it.Episode < (uint)v.Episodes.Length ? v.Episodes[it.Episode] : null;
            return new ShowSlot(-1, it.Kind, it.Kind == PodcastItemKind.Group ? g : null, vm, it.First);
        }

        /// <summary>-1: a page part (a prefix or the foot); else the list item's kind.</summary>
        int KindAt(int i)
        {
            if (i < PrefixCount) return -1;
            var v = _p.View.Peek();
            int k = i - PrefixCount;
            return (uint)k < (uint)v.Items.Length ? (int)v.Items[k].Kind : -1;
        }

        /// <summary>Recycle pools per kind: a row never rebinds as a label; each page part is its own.</summary>
        int ContentTypeOf(int i)
        {
            int kind = KindAt(i);
            if (kind >= 0) return kind;
            return i < PrefixCount ? P.PartType + i : P.PartType + FootPart;
        }

        /// <summary>An item's seeded extent (the measured one replaces it on realize).</summary>
        float ExtentOf(int i)
        {
            var lay = _lay.Peek();
            if (i == ColumnPart) return lay.Stacked ? (_colH.Peek() > 0f ? _colH.Peek() : P.ColumnEstimate) : 0f;
            if (i == BarPart) return P.ListBarH;
            return KindAt(i) switch
            {
                (int)PodcastItemKind.Group => MathF.Max(P.GroupH, lay.Target),
                (int)PodcastItemKind.Row => lay.RowDescription ? P.RowEstimateTall : P.RowEstimate,
                (int)PodcastItemKind.More or (int)PodcastItemKind.Empty => P.FootEstimate,
                _ => P.SimilarEstimate,
            };
        }

        Element Template(BoundItemScope<ShowSlot> scope)
        {
            int i = scope.Row.Index.Peek();
            var host = this;
            switch (i)
            {
                case ColumnPart: return Embed.Comp(new SlotProps(host), static () => new ColumnSlot());
                case BarPart:
                    // RAW: the sticky root must be the slot itself (a sticky root returned from a component never pins).
                    return ClassicPodcastKit.PinnedBar("cp-listbar", Embed.Comp(new SlotProps(host), static () => new ListBarSlot()), P.ListBarH, 0f, _barStuck, _toneOf!);
            }
            var item = scope.Item;
            if (item.Peek().Kind == PodcastItemKind.Group && item.Peek().Part < 0) return Embed.Comp(() => new HeadSlot(host, item));
            return Embed.Comp(() => new BodySlot(host, item));
        }

        /// <summary>A list item's gutters (the list column's side padding).</summary>
        static Edges4 Gutters(ClassicPodcastLayout lay) => new(lay.MainPad, 0f, lay.MainPad, 0f);

        /// <summary>The group the list is in at the line under the pinned list bar (-1: the bar is not pinned, or the line is above the first label). A
        /// memo over the offset: the pinned label re-renders only when the group changes.</summary>
        int PinnedGroup()
        {
            if (!_barStuck.Value || _measured is not { } m) return -1;
            double o = _scroll.Offset.Value;
            var v = _p.View.Value;
            int i = m.IndexAt((float)o + P.ListBarH, 0f) - PrefixCount;
            if (i < 0 || v.Items.Length == 0) return -1;
            if (i >= v.Items.Length) i = v.Items.Length - 1;
            for (; i >= 0; i--) if (v.Items[i].Group >= 0) return v.Items[i].Group;
            return -1;
        }

        sealed record SlotProps(PageHost Host);

        /// <summary>The pinned group label over the list: the group the list is in under the list bar, on the page's ground with the hairline.</summary>
        sealed class PinnedHead : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                int gi = host._pinned?.Value ?? -1;
                var v = host._p.View.Peek();
                if ((uint)gi >= (uint)v.Groups.Length) return new BoxEl { Height = 0f };
                var lay = host._lay.Value;
                uint tone = host._tone.Value;
                return new BoxEl
                {
                    Key = "cp-pinned-head:" + v.Groups[gi].Key, Direction = 1, MinWidth = 0f, Fill = ClassicPodcastKit.Backing(tone),
                    Children =
                    [
                        new BoxEl { Direction = 1, MinWidth = 0f, Padding = Gutters(lay), Children = [host.GroupHeadRow(v.Groups[gi], v.Years, lay)] },
                        new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                    ],
                };
            }
        }

        /// <summary>[0] the cover column at Compact (on the column's layer fill); nothing from Small up.</summary>
        sealed class ColumnSlot : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                var lay = host._lay.Value;
                if (!lay.Stacked) return new BoxEl { Grow = 1f, Basis = 0f, Height = 0f };
                var hv = host._head!.Value;
                return new BoxEl
                {
                    Key = "cp-column-stacked", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Fill = Tok.FillLayerDefault, Padding = Edges4.All(P.ColumnPadStacked),
                    Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade,
                    OnBoundsChanged = host._measureColumn,
                    Children = [host.ColumnRegion(hv, lay, host._accent.Value)],
                };
            }
        }

        /// <summary>[1] the list bar's content (the sticky band around it is the slot itself).</summary>
        sealed class ListBarSlot : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                var lay = host._lay.Value;
                return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = Gutters(lay), Children = [host.ListBar(lay)] };
            }
        }

        /// <summary>A group label: the lowercase group name with its count, the jump list, Mark all as played.</summary>
        sealed class HeadSlot(PageHost host, IReadSignal<ShowSlot> item) : Component
        {
            public override Element Render()
            {
                var slot = item.Value;
                var lay = host._lay.Value;
                if (slot.Group is not { } g) return new BoxEl { Height = MathF.Max(P.GroupH, lay.Target) };
                var years = host._p.View.Peek().Years;
                return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = Gutters(lay), Children = [host.GroupHeadRow(g, years, lay)] };
            }
        }

        /// <summary>An episode row, the "Load more" foot, the empty line, or the page's foot (More like this), between its gutters. It re-renders only when
        /// its slot's SHAPE changes (the part, the kind): a recycled slot that rebinds to another episode re-renders the row alone (<see cref="RowItem"/>),
        /// so a fast scroll realising many rows a frame pays for each row's content only.</summary>
        sealed class BodySlot(PageHost host, IReadSignal<ShowSlot> item) : Component
        {
            Func<(int Part, PodcastItemKind Kind)>? _shapeOf;
            RowItemProps? _row;

            public override Element Render()
            {
                _shapeOf ??= () => (item.Value.Part, item.Value.Kind);
                var shape = UseComputed(_shapeOf).Value;
                var lay = host._lay.Value;
                var p = host._p;
                if (shape.Part == FootPart)
                {
                    var hv = host._head!.Value;
                    var kids = new List<Element>(2);
                    if (!hv.Similar.IsDefaultOrEmpty)
                    {
                        kids.Add(ClassicPodcastKit.SectionHeader("cp-similar-head", Loc.Get(Strings.Podcast.Pages.MoreLikeThis)));
                        kids.Add(ClassicPodcastKit.MoreLikeThis(hv.Similar, p.OpenUri, lay.Stacked, p.CardMenu, p.PlayCard));
                    }
                    return new BoxEl
                    {
                        Key = "cp-foot", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Padding = Gutters(lay) with { Bottom = P.MainFoot + R.Space.DockReserve }, Children = kids.ToArray(),
                    };
                }
                Element body = shape.Kind switch
                {
                    PodcastItemKind.Row => Embed.Comp(_row ??= new RowItemProps(host, item), static () => new RowItem()),
                    PodcastItemKind.More => new BoxEl
                    {
                        Key = "cp-more", Direction = 0, Justify = FlexJustify.Center, Padding = new Edges4(0f, P.SectionBottom, 0f, P.SectionBottom),
                        Children = [Button.Create(Loc.Get(Strings.Podcast.Pages.LoadMore), p.LoadMore) with { Key = "load-more" }],
                    },
                    PodcastItemKind.Empty => new BoxEl
                    {
                        Key = "cp-empty", Direction = 0, Gap = P.SectionGap, AlignItems = FlexAlign.Center, Wrap = true, Padding = new Edges4(0f, P.EmptyPad, 0f, P.EmptyPad),
                        Children = [Body(Loc.Get(Strings.Podcast.Pages.NoMatch)) with { Color = Tok.TextSecondary },
                            HyperlinkButton.Create(Loc.Get(Strings.Podcast.Pages.ClearFilter), p.ClearFilter)],
                    },
                    _ => new BoxEl(),
                };
                return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = Gutters(lay), Children = [body] };
            }
        }

        sealed record RowItemProps(PageHost Host, IReadSignal<ShowSlot> Item);

        /// <summary>The episode row of a row slot: ONE row per slot (a constant key), so a recycled slot re-renders its row for the new episode instead of
        /// mounting a new one.</summary>
        sealed class RowItem : Component
        {
            public override Element Render()
            {
                var p = UseProps<RowItemProps>();
                var host = p.Host;
                var lay = host._lay.Value;
                return p.Item.Value.Vm is { } vm
                    ? ClassicPodcastKit.EpisodeRow(vm, lay, lay.RowExpands, lay.RowDescription, host._accent.Value, host._verbs!) with { Key = "ep" }
                    : new BoxEl();
            }
        }

        // ── the list bar ──

        Element ListBar(ClassicPodcastLayout lay)
        {
            var p = _p;
            if (_words is null)
            {
                var filters = s_placeholder.Filters;
                _words = new Controls.Words.Word[filters.Length];
                for (int i = 0; i < _words.Length; i++)
                {
                    int ix = i;
                    _words[i] = new Controls.Words.Word(
                        Prop.Of(() => _p.View.Value.Filters is { Length: > 0 } f && ix < f.Length ? f[ix].Label : s_placeholder.Filters[ix].Label),
                        Prop.Of(() => _p.View.Value.Filters is { Length: > 0 } f && ix < f.Length ? f[ix].CountText : ""));
                }
            }
            var accent = _accent;
            var kids = new List<Element>(4);
            bool searching = lay.Stacked && _searchOpen.Value;
            if (searching)
                kids.Add(Swap("tb:search-field", true, SearchBox(lay, fill: true)));
            else if (lay.FilterMenu)
                kids.Add(Swap("tb:filter-menu", true, Embed.Comp(_p, static () => new FilterMenu()) with { Key = "tb:filter-menu:host" }));
            else
                kids.Add(Swap("tb:filters", true, Controls.Words.Rail(_words, _filterIx, () => accent.Value, onSelect: i => _p.SetFilter((PodcastFilter)i), fit: true)));
            if (lay.Stacked)
                kids.Add(Controls.Named(Controls.IconAction(searching ? Icons.Cancel : Icons.Search, () => _searchOpen.Value = !_searchOpen.Peek(), size: lay.Target) with { Key = "tb:search" },
                    Loc.Get(Strings.Podcast.Pages.Search)));
            else kids.Add(Swap("tb:find-box", false, SearchBox(lay, fill: false)));
            string sortWord = Loc.Get(p.Oldest.Value ? Strings.Podcast.Pages.OldestFirst : Strings.Podcast.Pages.NewestFirst);
            kids.Add(Controls.Named(Controls.IconAction(Icons.Sort, p.ToggleSort, size: lay.Target) with { Key = "tb:sort" }, sortWord));
            return new BoxEl
            {
                Key = "cp-listbar:row", Direction = 0, Gap = P.ListBarGap, AlignItems = FlexAlign.Center, Height = P.ListBarH, MinWidth = 0f, Children = kids.ToArray(),
            };
        }

        static BoxEl Swap(string key, bool fill, Element body) => new()
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Grow = fill ? 1f : 0f, Basis = fill ? 0f : float.NaN, Shrink = fill ? 1f : 0f, MinWidth = 0f,
            Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Children = [body],
        };

        Element SearchBox(ClassicPodcastLayout lay, bool fill) => new BoxEl
        {
            Key = "tb:find", Shrink = 0f, Grow = fill ? 1f : 0f, MinWidth = 0f,
            Children = [AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Podcast.Pages.Search),
                width: fill ? P.SearchWSmall : lay.Tier == WidthTier.Small ? P.SearchWSmall : P.SearchW, text: _p.Query(), queryIcon: Icons.Search, grow: fill ? 1f : 0f)],
        };

        static readonly string[] s_noSuggest = [];

        // ── a group label ──

        Element GroupHeadRow(PodcastGroup g, ImmutableArray<PodcastYear> years, ClassicPodcastLayout lay)
        {
            var p = _p;
            bool jumps = years.Length > 1;
            string key = g.Key;
            var labelKids = new List<Element>(3)
            {
                new TextEl(g.Title) { Size = P.GroupLabelSize, LineHeight = P.GroupLabelLine, Weight = P.Strong, Color = Tok.TextSecondary, Case = TextCase.Lower, Wrap = TextWrap.NoWrap },
            };
            if (g.CountText.Length > 0) labelKids.Add(new TextEl(g.CountText) { Size = P.GroupLabelSize, LineHeight = P.GroupLabelLine, Color = Tok.TextTertiary });
            if (jumps) labelKids.Add(Icon(Icons.ChevronDown, P.SplitChevron, Tok.TextTertiary));
            var headKids = new List<Element>(3)
            {
                new BoxEl
                {
                    Key = "gh:jump:" + key, Direction = 0, Gap = P.GroupGap, AlignItems = FlexAlign.Center, Height = lay.Target, Shrink = 0f,
                    Padding = new Edges4(P.FilterPadX, 0f, P.FilterPadX, 0f), Margin = new Edges4(-P.FilterPadX, 0f, 0f, 0f), Corners = Radii.ControlAll,
                    Fill = Tok.FillSubtleTransparent, HoverFill = jumps ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
                    PressedFill = jumps ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent, Role = AutomationRole.Button, Focusable = jumps,
                    Cursor = jumps ? CursorId.Hand : CursorId.Arrow,
                    OnRealized = h => _groupNodes["head:" + key] = h, OnClick = jumps ? () => OpenJump(years, key) : null,
                    Children = labelKids.ToArray(),
                },
                new BoxEl { Grow = 1f },
            };
            if (g.Fresh) headKids.Add(HyperlinkButton.Create(Loc.Get(Strings.Podcast.Pages.MarkAllPlayed), p.MarkFreshPlayed) with { Key = "gh:mark" });
            return new BoxEl { Key = "gh", Direction = 0, Height = MathF.Max(P.GroupH, lay.Target), AlignItems = FlexAlign.Center, MinWidth = 0f, Children = headKids.ToArray() };
        }

        void OpenJump(ImmutableArray<PodcastYear> years, string groupKey)
        {
            if (_jump is { IsOpen: true } open) { open.Close(); return; }
            if (Controls.IsNullOverlay(_overlay) || !_groupNodes.TryGetValue("head:" + groupKey, out var anchor)) return;
            OverlayHandle? opened = null;
            opened = _overlay!.Open(() => anchor, () => JumpGrid(years, () => opened?.Close()), FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _jump = opened;
        }

        Element JumpGrid(ImmutableArray<PodcastYear> years, Action close)
        {
            var rows = new List<Element>();
            for (int i = 0; i < years.Length; i += P.JumpCols)
            {
                var cells = new Element[P.JumpCols];
                for (int c = 0; c < P.JumpCols; c++)
                {
                    if (i + c >= years.Length) { cells[c] = new BoxEl { Grow = 1f, Basis = 0f }; continue; }
                    var y = years[i + c];
                    cells[c] = new BoxEl
                    {
                        Key = "jump:" + y.Label, Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = P.JumpCellH, Justify = FlexJustify.Center,
                        Padding = new Edges4(P.JumpPad, 0f, P.JumpPad, 0f), Corners = Radii.ControlAll, Fill = Tok.FillControlDefault,
                        HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary, BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                        OnClick = () => { close(); JumpToGroup("y" + y.Label); },
                        Children = [Subtitle(y.Label), Caption(y.Count) with { Color = Tok.TextTertiary }],
                    };
                }
                rows.Add(new BoxEl { Direction = 0, Gap = P.JumpGap, Children = cells });
            }
            return new BoxEl
            {
                Key = "cp-jump", Direction = 1, Gap = P.JumpGap, Width = P.JumpW, Padding = Edges4.All(P.JumpPad),
                Children = [BodyStrong(Loc.Get(Strings.Podcast.Pages.JumpTo)), .. rows],
            };
        }

        /// <summary>Jump to a year: its label to the top of the list, under the pinned list bar.</summary>
        void JumpToGroup(string groupKey)
        {
            var v = _p.View.Peek();
            for (int k = 0; k < v.Items.Length; k++)
            {
                var it = v.Items[k];
                if (it.Kind != PodcastItemKind.Group || (uint)it.Group >= (uint)v.Groups.Length || v.Groups[it.Group].Key != groupKey) continue;
                _ctl.StartBringItemIntoView(PrefixCount + k, alignmentRatio: 0f, animate: false);
                _scroll.ScrollBy(-P.ListBarH, ScrollMove.Immediate);
                return;
            }
        }
    }
}
