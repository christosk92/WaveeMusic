// ── Entities/Show.Page.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the show arm of the shared detail frame: the page (identity, demand, frame spec) and the BOUND episode list
//
// Role: UI
// Owner: M
// Wave: 5
// Budget: 700 lines
// Spec: ch 09 §1.1, §1.4, W1-W9, §6.1, §7, §9.2, §9.3 items 1 and 3, §9.4 · ch 03 (the frame's show arm) · 0.2.9
//       `Features/Detail/{EpisodeList, DetailPage (MapShow)}.cs`
//
// ── WHAT THE PAGE HANDS THE FRAME ────────────────────────────────────────────────────────────────────────────────────
//
// `Detail.Frame` with `Config.Show` (TypeYear eyebrow "Podcast", Content = Episodes, Heart = Follow, Selection = None, no
// trailing): the identity (title, cover, eyebrow, the publisher · "N episodes" meta line — DRAWN, the §9.4 fix, which
// needs the frame's meta gate to admit a show: a shared-file patch — the description, the share link), the cover's drag
// payload, and ONE slot, `FrameSlots.Episodes`, which the frame calls with its VERTICAL flag (the toolbar is absent in
// the vertical arm, W4 — the slot's signature is a shared-file patch too).
//
// ── THE DEMAND (ch 09 §9.3 item 3: a show page needs THREE demands, not the plan's one) ─────────────────────────────
//
//   1. `ShowFields.All` — title, cover, publisher, description (keyed on the show slot + scope epoch);
//   2. the `ShowEpisodes` edge while it is unanswered — and the NEXT page only from the load-more pill (a 700-episode
//      back-catalogue is never paged off a scroll position);
//   3. `EpisodeFields.Row | About | Progress` over the resident episode slots, re-run as the membership lands. Progress
//      is per-USER state with its own authority column (ch 09 §7 DATA GAPS); an episode whose progress is unknown
//      renders UNPLAYED.
//
// ── THE LIST (ch 09 §9.2: zero allocation on a scroll frame, the row stays the W7 card) ─────────────────────────────
//
// `ItemsView.CreateBound` over ONE snapshot memo (the filtered, ordered view of `Edges.ShowEpisodes` as ORIGINAL indices,
// the resume pick over ALL episodes, the paging gate). The items are [head, card × view, foot]: the head (toolbar ·
// banner · "Episodes" · the empty arm) and the foot (the pill + the 96 reserve) are components that re-render only when
// their own small models change; a card slot re-renders only when its KIND flips, and everything per-episode is a bind
// over the slot's equality-gated `Episode.RowItem` (handle + row version). Recycle pools are split by content type so
// a scroll never rebuilds a card as a head. Play is the SHOW context at the episode (§6.1).

using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public readonly partial struct Show
{
    /// <summary><see cref="Shell.RouteKind.Show"/>: the shared frame with episodes in the right column.</summary>
    public static Element Page(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route.Subject, routeKey), static () => new PageHost()) with { Key = "show-page:" + routeKey };
    }

    sealed record PageProps(EntityUri Subject, string RouteKey);

    sealed class PageHost : Component
    {
        Scope? _scope;
        EntityUri _subject;
        Show _show;
        string _routeKey = "";
        readonly Detail.FrameActions _actions;
        readonly Detail.FrameSlots _slots;
        readonly Action _demandShow, _demandRows;

        public PageHost()
        {
            _demandShow = DemandShow;
            _demandRows = DemandRows;
            _actions = new Detail.FrameActions { CoverDrag = CoverPayload };
            _slots = new Detail.FrameSlots { Episodes = vertical => EpisodeList(_show, _routeKey, vertical) };
        }

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            uint epoch = Entities.ScopeEpoch.Value;              // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            _ = scope.Shows.Changed.Value;
            _ = scope.Edges.ShowEpisodes.Changed.Value;            // the meta line's count and its loading bar
            var overlay = UseContext(Overlay.Service);
            if (!Controls.IsNullOverlay(overlay)) Track.DrawerOverlay = overlay;   // idempotent (contract §3)

            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(p.Subject))
            {
                _scope = scope;
                _subject = p.Subject;
                // The factory allocates an empty row for an unseen uri: the page binds it at once and Ensure fills it.
                _show = p.Subject.IsValid ? Entities.Show(p.Subject) : default;
            }
            _routeKey = p.RouteKey;
            var show = _show;
            UseEffect(_demandShow, DepKey.From(show.Slot, (int)epoch));
            UseEffect(_demandRows);

            if (!show.IsValid) return Controls.Vacancy(Controls.VacancyVoice.Error);
            return Detail.Frame(new Detail.FrameSpec
            {
                Identity = IdentityOf(show),
                Config = Detail.Config.Show,
                Actions = _actions,
                Slots = _slots,
                RouteKey = p.RouteKey,
            });
        }

        void DemandShow()
        {
            var show = _show;
            if (!show.IsValid) return;
            Entities.Ensure(show, ShowFields.All);
            // Only an unanswered list is asked here: later pages are the pill's, and a failed ask must not re-arm itself.
            if (Entities.Current.Edges.ShowEpisodes.State(show.Slot) == EdgeState.Unknown)
                Entities.EnsureEdge(FetchEdge.ShowEpisodes, show.Slot);
        }

        // Auto-tracked: as the membership lands (a page, a refresh), ask for what a card paints. The planner dedupes.
        void DemandRows()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.ShowEpisodes.Changed.Value;
            var show = _show;
            if (!show.IsValid) return;
            var slots = scope.Edges.ShowEpisodes.Targets(show.Slot);
            if (slots.Length > 0)
                Entities.Ensure(MemoryMarshal.Cast<int, Episode>(slots), EpisodeFields.Row | EpisodeFields.About | EpisodeFields.Progress);
        }

        object? CoverPayload()
        {
            var show = _show;
            if (!show.IsValid) return null;
            string uri = show.Uri.Text;
            return new DragPayload(DragKind.Show, uri, uri, show.Knows(ShowFields.Title) ? show.Title : "",
                                   new EntityRef(EntityKind.Show, show.Slot), ArtUrl: Controls.ArtUrl(show.ImageId));
        }
    }

    /// <summary>The show's identity snapshot. The CALLER subscribes (Shows, ShowEpisodes); this reads.</summary>
    static Detail.Identity IdentityOf(Show show)
    {
        var edge = Entities.Current.Edges.ShowEpisodes;
        var state = edge.State(show.Slot);
        string? publisher = show.Knows(ShowFields.Publisher) && !show.PublisherId.IsEmpty
            ? Entities.Strings.Resolve(show.PublisherId) : null;
        return new Detail.Identity
        {
            Subject = show.Uri,
            Kind = DetailKind.Show,
            HeaderPending = !show.Knows(ShowFields.Title),
            Title = show.Knows(ShowFields.Title) ? show.Title : "",
            CoverUrl = show.Knows(ShowFields.Image) ? Controls.ArtUrl(show.ImageId) : null,
            Eyebrow = Detail.Text.Eyebrow(DetailKind.Show, BadgeStyle.TypeYear, AlbumKind.Album, 0,
                                          collaborative: false, isPublic: true, visibilityKnown: true),
            // "<Publisher> · N episodes" — the line 0.2.9 built and never drew (§9.4). The count is the edge's TOTAL, so
            // it is stated only once the membership answered; until then the frame holds the row as a shimmer bar.
            Meta = state == EdgeState.Unknown ? null : Detail.Text.ShowMeta(publisher, show.TotalEpisodes),
            MetaLoading = state == EdgeState.Unknown && !edge.IsFailed(show.Slot),
            DescriptionHtml = show.Knows(ShowFields.About) && !show.DescriptionId.IsEmpty
                ? Entities.Strings.Resolve(show.DescriptionId) : null,
            // The show's OWN web link — never 0.2.9's playlist-url fallback (§9.4).
            ShareUrl = Actions.WebLinkOf(show.Uri),
        };
    }

    // ══ THE EPISODE LIST ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The <c>FrameSlots.Episodes</c> body: the bound episode column for <paramref name="show"/>.</summary>
    static Element EpisodeList(Show show, string routeKey, bool vertical)
        => Embed.Comp(new EpisodeListProps(show, routeKey, vertical), static () => new EpisodeListHost());

    sealed record EpisodeListProps(Show Show, string RouteKey, bool Vertical);

    /// <summary>A list item's kind. Card is 0 so the source's fallback item (a slot transiently past the end) paints an
    /// empty card, never a second head.</summary>
    const byte CardItem = 0, HeadItem = 1, FootItem = 2;

    readonly record struct ListItem(byte Kind, Episode.RowItem Row);

    /// <summary>ONE coherent read of the column, rebuilt when its inputs publish (never per frame).</summary>
    sealed class ListSnap
    {
        public Show Show;
        public int[] Slots = [];
        public int[] View = [];
        public int ViewCount;
        public int ResumeSlot;
        public bool Vertical, Pending, Failed, Empty, EmptyShow, CanLoadMore, Paging;
        public int NextOffset, LocalAsked;
    }

    readonly record struct HeadModel(bool Vertical, int ResumeSlot, uint ResumeVersion, bool Empty, bool EmptyShow);

    readonly record struct FootModel(bool CanLoadMore, bool Paging);

    sealed class EpisodeListHost : Component, IPropsHost
    {
        EpisodeListProps? _latest;
        readonly Signal<EpisodeListProps?> _props = new(null);

        // ── view state: the two selectors and the page's own half of the paging cursor ──
        internal readonly Signal<int> Status = new(0), Order = new(0);
        readonly Signal<int> _localAsked = new(0);
        readonly Signal<int> _pagingFrom = new(-1);
        uint _pagingVersion;

        Memo<ListSnap>? _snap;
        BoundItemsSource<ListItem>? _items;
        ListOptions<ListItem>? _options;
        string _optionsKey = "\0";
        readonly RepeatLayout _layout = RepeatLayout.VariableList(124f);   // a 112 card + its 12 gap; measured on realize

        // ── cached delegates (nothing here allocates per render or per scroll) ──
        readonly Func<ListSnap> _compute;
        readonly Func<BoundItemScope<ListItem>, Element> _template;
        readonly Func<int, int> _contentType;
        readonly Func<bool> _pending, _failed;
        readonly Func<Element> _content, _shimmer, _failedView;
        internal readonly Action<Episode> PlayAction;
        internal readonly Action ResumeAction, LoadMoreAction;
        readonly Action _retry;

        public EpisodeListHost()
        {
            _compute = Compute;
            _template = scope => Embed.Comp(() => new EpisodeSlot(this, scope));
            _contentType = i =>
            {
                var s = _snap?.Peek();
                return s is null ? CardItem : i == 0 ? HeadItem : i > s.ViewCount ? FootItem : CardItem;
            };
            _pending = () => _snap?.Value.Pending ?? true;
            _failed = () => _snap?.Value.Failed ?? false;
            _content = () => ItemsView.CreateBound(_items!, _template, _layout, OptionsFor(_latest?.RouteKey ?? ""));
            _shimmer = () => SeedList(_latest?.Vertical ?? false);
            _retry = () => { if (_latest is { Show.IsValid: true } p) Entities.RefreshEdge(FetchEdge.ShowEpisodes, p.Show.Slot); };
            _failedView = () => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: _retry);
            PlayAction = Play;
            ResumeAction = Resume;
            LoadMoreAction = LoadMore;
        }

        public void ApplyProps(object props)
        {
            var p = (EpisodeListProps)props;
            _latest = p;
            _props.Value = p;                                      // equality-gated: a data-equal re-push is free
        }

        internal ListSnap? Snapshot => _snap?.Value;

        public override Element Render()
        {
            _ = _props.Value;
            _snap = UseComputed(_compute);
            _items ??= BoundItems.Project(_snap, static s => s.ViewCount + 2, static (s, i) => ItemAt(s, i), default(ListItem));
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                Children =
                [
                    new SkelRegionEl(
                        Pending: _pending, Failed: _failed, Content: _content, ShimmerSource: _shimmer, OnFailed: _failedView,
                        Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false),
                ],
            };
        }

        ListOptions<ListItem> OptionsFor(string routeKey)
        {
            if (_options is null || !string.Equals(_optionsKey, routeKey, StringComparison.Ordinal))
            {
                _optionsKey = routeKey;
                _options = new ListOptions<ListItem>
                {
                    SelectionMode = ItemsSelectionMode.None,          // no marquee, no Ctrl+A, no batch bar (item 65)
                    Grow = 1f,
                    ContentType = _contentType,
                    Scroll = new ScrollOptions { ScrollKey = "episodes:" + routeKey },   // §9.2: the position survives a swap
                };
            }
            return _options;
        }

        static ListItem ItemAt(ListSnap s, int i)
        {
            if (i == 0) return new ListItem(HeadItem, default);
            int v = i - 1;
            if (v >= s.ViewCount) return new ListItem(FootItem, default);
            return new ListItem(CardItem, Episode.RowItem.Of(new Episode(s.Slots[s.View[v]])));
        }

        ListSnap Compute()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Shows.Changed.Value;
            _ = scope.Episodes.Changed.Value;                      // progress, rows landing
            _ = scope.Edges.ShowEpisodes.Changed.Value;
            var p = _props.Value;
            int status = Status.Value, order = Order.Value, localAsked = _localAsked.Value, pagingFrom = _pagingFrom.Value;
            if (p is null || !p.Show.IsValid) return new ListSnap { Vertical = p?.Vertical ?? false, Pending = p is null, Failed = p is not null };

            var show = p.Show;
            var edge = scope.Edges.ShowEpisodes;
            int slot = show.Slot;
            var slots = edge.Targets(slot).ToArray();
            int n = slots.Length;
            var pcts = new float[n];
            for (int i = 0; i < n; i++)
            {
                var e = new Episode(slots[i]);
                pcts[i] = e.IsValid ? Episode.Rules.PctOf(e) : 0f;
            }
            var view = new int[n];
            var statusRule = (Episode.Rules.Status)Math.Clamp(status, 0, 3);
            int viewCount = Episode.Rules.View(pcts, statusRule, oldest: order == 1, view);
            int resume = Episode.Rules.ResumePick(pcts);              // over ALL episodes, whatever the filter shows

            var readiness = edge.Readiness(slot);
            int total = edge.Total(slot);
            // The paging cursor (§7, §9.1): how far anybody has ASKED — the source's `EpisodesAsked`, and the page's own
            // half, which advances past a page that ANSWERED (with rows or without) and stays put on a failure so the
            // next tap retries. An ask is in flight until the relation's version moves or it fails.
            bool failedNow = edge.IsFailed(slot);
            bool paging = Episode.Rules.Paging(pagingFrom, _pagingVersion, edge.Version(slot), failedNow);
            int local = Episode.Rules.LocalCursorAfter(localAsked, pagingFrom, paging, failedNow);
            int edgeAsked = show.EpisodesAsked;

            return new ListSnap
            {
                Show = show,
                Slots = slots,
                View = view,
                ViewCount = viewCount,
                ResumeSlot = resume >= 0 ? slots[resume] : 0,
                Vertical = p.Vertical,
                Pending = readiness == EdgeState.Unknown,
                Failed = readiness == EdgeState.Failed,
                Empty = viewCount == 0 && readiness is EdgeState.Partial or EdgeState.Complete,
                EmptyShow = Episode.Rules.IsEmptyShow(total, n, statusRule),
                // A COMPLETE list never shows the pill, whatever the cursor column says; a partial one gates on the cursor.
                CanLoadMore = Episode.Rules.CanLoadMore(edge.State(slot), edgeAsked, local, total),
                Paging = paging,
                NextOffset = Episode.Rules.NextOffset(edgeAsked, local, n),
                LocalAsked = local,
            };
        }

        /// <summary>Play the SHOW context at the clicked episode — the show's own order resolves it, whatever the filter
        /// or sort shows (§6.1).</summary>
        void Play(Episode e)
        {
            if (_snap?.Peek() is not { } s || !s.Show.IsValid || !e.IsValid) return;
            Playback.PlayContext(s.Show.Id, e.Id);
        }

        void Resume()
        {
            if (_snap?.Peek() is not { } s || !s.Show.IsValid || s.ResumeSlot <= 0) return;
            Playback.PlayContext(s.Show.Id, new Episode(s.ResumeSlot).Id);
        }

        /// <summary>The NEXT page of the membership. A tap while a page is out does nothing; an offset somebody already
        /// asked for is not re-asked (the planner would dedupe it and the pill would say "Loading…" forever) — the local
        /// cursor steps past it instead.</summary>
        void LoadMore()
        {
            if (_snap?.Peek() is not { } s || !s.Show.IsValid || !s.CanLoadMore || s.Paging) return;
            var edge = Entities.Current.Edges.ShowEpisodes;
            int slot = s.Show.Slot, from = s.NextOffset;
            _localAsked.Value = s.LocalAsked;                      // fold the previous answered page into the signal
            if (edge.WasAsked(slot, from))
            {
                _localAsked.Value = Episode.Rules.LocalCursorAfter(s.LocalAsked, from, paging: false, failed: false);
                return;
            }
            _pagingVersion = edge.Version(slot);
            Entities.EnsureEdge(FetchEdge.ShowEpisodes, slot, from);
            _pagingFrom.Value = from;
        }
    }

    /// <summary>One persistent slot of the column. Re-renders only when its KIND flips (a data change moving the foot);
    /// a card's per-episode reads are binds over the slot's row memo, so a recycle re-binds and rebuilds nothing.</summary>
    sealed class EpisodeSlot : Component
    {
        readonly EpisodeListHost _host;
        readonly RowScope _row;
        readonly Func<byte> _kindOf;
        readonly Func<Episode.RowItem> _rowOf;

        public EpisodeSlot(EpisodeListHost host, BoundItemScope<ListItem> scope)
        {
            _host = host;
            _row = scope.Row;
            var item = scope.Item;
            _kindOf = () => item.Value.Kind;
            _rowOf = () => item.Value.Row;
        }

        public override Element Render()
        {
            var kind = UseComputed(_kindOf);
            var row = UseComputed(_rowOf);
            var host = _host;
            switch (kind.Value)
            {
                case HeadItem:
                    return Embed.Comp(() => new EpisodeHead(host)) with { Key = "eps:head" };
                case FootItem:
                    return Embed.Comp(() => new EpisodeFoot(host)) with { Key = "eps:foot" };
                default:
                    return new BoxEl
                    {
                        Key = "eps:card", Direction = 1, MinWidth = 0f,
                        Padding = new Edges4(Spacing.L, 0f, Spacing.L, Spacing.M),
                        Children = [Episode.BoundRow(new BoundItemScope<Episode.RowItem>(_row, row), host.PlayAction)],
                    };
            }
        }
    }

    /// <summary>The column's head: the toolbar (two-column only), the Listen-next banner (picked from ALL episodes, so it
    /// survives any filter and any order — items 25-26), the "Episodes" header, and the empty arm.</summary>
    sealed class EpisodeHead : Component
    {
        readonly EpisodeListHost _host;
        readonly Func<HeadModel> _model;

        public EpisodeHead(EpisodeListHost host)
        {
            _host = host;
            _model = () =>
            {
                if (host.Snapshot is not { } s) return default;
                var resume = new Episode(s.ResumeSlot);
                return new HeadModel(s.Vertical, s.ResumeSlot, s.ResumeSlot > 0 && resume.IsValid ? resume.Version : 0u,
                                     s.Empty, s.EmptyShow);
            };
        }

        public override Element Render()
        {
            var m = UseComputed(_model).Value;
            var kids = new List<Element>(4);
            if (!m.Vertical) kids.Add(EpisodeToolbar(_host.Status, _host.Order));
            if (m.ResumeSlot > 0) kids.Add(ResumeBanner(new Episode(m.ResumeSlot), _host.ResumeAction));
            kids.Add(EpisodesHeader());
            if (m.Empty) kids.Add(EmptyArm(m.EmptyShow));
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.M, MinWidth = 0f,
                Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
                Children = kids.ToArray(),
            };
        }
    }

    /// <summary>The column's foot: the load-more pill above the 96 reserve, or the reserve alone (the last card already
    /// carries its 12).</summary>
    sealed class EpisodeFoot : Component
    {
        readonly EpisodeListHost _host;
        readonly Func<FootModel> _model;

        public EpisodeFoot(EpisodeListHost host)
        {
            _host = host;
            _model = () => host.Snapshot is { } s ? new FootModel(s.CanLoadMore, s.Paging) : default;
        }

        public override Element Render()
        {
            var m = UseComputed(_model).Value;
            return m.CanLoadMore
                ? new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Padding = new Edges4(Spacing.L, 0f, Spacing.L, BottomReserve),
                    Children = [LoadMorePill(m.Paging, _host.LoadMoreAction)],
                }
                : new BoxEl { Height = BottomReserve - Spacing.M };
        }
    }
}
