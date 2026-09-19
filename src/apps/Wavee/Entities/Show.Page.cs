// ── Entities/Show.Page.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the show page: the frame spec (identity + the six podcast rail slots), the page's ONE model (filter · sort · find,
// seeded from and persisted to `ShowViewPrefs`; one memoized snapshot of everything the rules derive), the READER — a
// bound list with a sticky word rail as its persistent prefix — and `ShowReaderRules`, the page's pure decisions
//
// Role: UI (with a CORE section: ShowReaderRules)
// Owner: P
// Wave: P3 (the podcast rework)
// Budget: 900 lines (podcast-show-rework-implementation.md §11) — landed ~1200: the four rail-slot components (§4) are
//   the named split (a `Show.Rail.cs` partial) the P3 report asks for
// Spec: podcast-show-rework-implementation.md §2 W1-W3, §3.1 (the component tree), §4 (the interaction contract), §5.6
//       (the reader host), §6.1 (readiness), §7, §12 D-1/D-3/D-5/D-6 · as-built-20260919.md P1-R ("P3 notes"), P2-M (the
//       slots), P2-T (ProgressSettled / ProgressFailed) · the prototype podcast-show-episode-mica.html (the show scene)
//
// ── THE TREE ─────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//   Show.Page → PageHost                                       keyed "show-page:" + routeKey; owns the ReaderModel
//   └─ Detail.Frame(Config.Show, Identity, Slots by presence)
//      ├─ rail: Badges (BadgesSlot) · Attribution (the publisher) · Rating (RatingSlot → the rate flyout) · meta
//      │        (N episodes · cadence · since) · Ledger (LedgerSlot) · Primary (PrimarySlot: Follow + ghost │ Resume ·
//      │        N min left │ episode 1 │ latest) · Satellites (♥ · trailer · 🔔 disabled · share · ⋯) · blurb (not New)
//      └─ right: Slots.Episodes → ReaderHost
//          └─ SkelRegion(list pending) → ItemsView.CreateBound over the snapshot, PersistentPrefixCount = 1
//              ├─ [0] RAW sticky element (.Sticky(0)) → RailBody: filter words + counts · FindBox · sort words
//              ├─ [1] VisitHead   New: doors + about │ Returning: hero + up next + new since │ CaughtUp │ shimmer
//              ├─ [2] EpisodesHeader  "episodes N" (+ the empty arm)
//              ├─ [3..] month Group │ Episode.ReaderRow                       ShowReaderShape.Build + ShowReaderRules.Layout
//              └─ Foot  load more │ the dock reserve
//
// ── ONE MODEL, ONE SNAPSHOT ──────────────────────────────────────────────────────────────────────────────────────────
//
// §5.6 put the snapshot on the reader host; it lives on the PAGE instead, because the rail reads the same derived facts
// (the ledger, the visit that picks the primary, the tone) and two memos over the same inputs could disagree for a
// frame. `ReaderModel.Snap` (the page's `UseComputed`) is the ONE coherent read — slots, pcts (D-5: completed = 1),
// the view (status ∩ find, newest/oldest), listen-next, the visit, the ledger, the cadence, the item list — rebuilt when
// its inputs publish, never per frame; `Facts` is the page's small equality-gated projection of it (the frame spec
// re-renders only when a fact the rail shows moved), and every other consumer gates on a stamp of its own.
//
// ── THE STICKY RAIL (the Artist.Reader canon) ────────────────────────────────────────────────────────────────────────
//
// Slot 0 is the list's persistent prefix and is handed back as a RAW element carrying `.Sticky(0)`: a sticky root
// returned FROM a component never pins (its parent would be the component anchor, of its own height). Everything after
// the prefix is guillotined at the rail's lower edge by ONE shared clip (`ItemClipTopInset` + the 24-DIP fade band), so
// the rail needs no plate of its own — the page's tone ground shows through, as in the prototype.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Show
{
    /// <summary><see cref="Shell.RouteKind.Show"/>: the shared frame with the episode reader in the right column. The
    /// page is keyed by its route, so the subject is a constructor fact of its host (the persisted-view seed needs it
    /// before the first render).</summary>
    public static Element Page(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        var subject = route.Subject;
        return Embed.Comp(new PageProps(routeKey), () => new PageHost(subject)) with { Key = "show-page:" + routeKey };
    }

    sealed record PageProps(string RouteKey);

    // ══ 1. THE PAGE ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the rail shows, projected from the snapshot — the page re-renders (and re-pushes the frame spec)
    /// only when one of these moved.</summary>
    readonly record struct PageFacts(
        ShowReaderRules.Head Head, ShowReaderRules.Primary Primary, ShowReaderRules.Primary Ghost,
        int ResumeLeft, int FirstNumber, bool Publisher, ShowFlags Flags,
        int RatingX100, int RatingCount, int MyRating, bool HasTrailer, uint ToneArgb, string Meta, bool Topics)
    {
        public bool Badges => (Flags & (ShowFlags.Exclusive | ShowFlags.Explicit | ShowFlags.Video)) != 0;
        public bool Rating => RatingX100 > 0;
    }

    sealed class PageHost : Component
    {
        readonly ReaderModel _m;
        Scope? _scope;
        Show _show;
        string _routeKey = "";
        Element[]? _satellites;
        readonly Detail.FrameActions _actions;
        readonly Detail.FrameSlots?[] _slotSets = new Detail.FrameSlots?[32];
        readonly Func<bool, Element> _episodes;
        readonly Func<Func<ColorF>, Element> _primary;
        readonly Func<Element[]> _satellitesOf;
        readonly Func<float, Element> _attribution, _rating, _ledger, _topics;
        readonly Func<Element> _badges;
        readonly Func<PageFacts> _facts;
        readonly Func<ContextMenuModel?> _more;
        readonly Action _demandShow, _demandRows, _demandTrailer, _persist, _playTrailer, _share;
        readonly Prop<bool> _heartShown, _trailerShown;

        public PageHost(EntityUri subject)
        {
            _m = new ReaderModel(subject);
            Controls.Library ??= User.LibrarySeam;               // the Follow primary and the ♥ satellite write through it
            _facts = ComputeFacts;
            _demandShow = DemandShow;
            _demandRows = DemandRows;
            _demandTrailer = DemandTrailer;
            _persist = PersistView;
            _playTrailer = PlayTrailer;
            _share = () => Episode.CopyLink(_show.IsValid ? Actions.WebLinkOf(_show.Uri) : "");
            _more = MoreMenu;
            _actions = new Detail.FrameActions { CoverDrag = CoverPayload };
            _episodes = _ => Embed.Comp(new ReaderProps(_m, _routeKey), static () => new ReaderHost());
            _primary = accent => Embed.Comp(new SlotProps(_m, accent), static () => new PrimarySlot()) with { Key = "show:primary" };
            _satellitesOf = () => _satellites ??= BuildSatellites();
            _attribution = w => PublisherLine(_show, w);
            _badges = () => Embed.Comp(new SlotProps(_m, null), static () => new BadgesSlot());
            _rating = w => Embed.Comp(new SlotProps(_m, null, w), static () => new RatingSlot());
            _ledger = w => Embed.Comp(new SlotProps(_m, null, w), static () => new LedgerSlot());
            _topics = w => Embed.Comp(new SlotProps(_m, null, w), static () => new TopicsSlot());
            _heartShown = Prop.Of(() => _m.Facts is { } f && f.Value.Primary != ShowReaderRules.Primary.Follow);
            _trailerShown = Prop.Of(() => _m.Facts is { } f && f.Value.HasTrailer);
        }

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            uint epoch = Entities.ScopeEpoch.Value;              // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            _ = scope.Shows.Changed.Value;
            _ = scope.Edges.ShowEpisodes.Changed.Value;            // the meta line's loading bar
            var overlay = UseContext(Overlay.Service);
            if (!Controls.IsNullOverlay(overlay)) Track.DrawerOverlay = overlay;   // idempotent (contract §3)

            if (!ReferenceEquals(_scope, scope))
            {
                _scope = scope;
                // The factory allocates an empty row for an unseen uri: the page binds it at once and Ensure fills it.
                _show = _m.Subject.IsValid ? Entities.Show(_m.Subject) : default;
            }
            _routeKey = p.RouteKey;
            _m.Snap = UseComputed(_m.ComputeSnap);
            _m.Facts = UseComputed(_facts);
            var facts = _m.Facts.Value;
            var show = _show;
            UseEffect(_demandShow, DepKey.From(show.Slot, (int)epoch));
            UseActivation(onActivated: Reopen);
            UseEffect(_demandRows);
            UseEffect(_demandTrailer);
            UseEffect(_persist);

            if (!show.IsValid) return Controls.Vacancy(Controls.VacancyVoice.Error);
            return Detail.Frame(new Detail.FrameSpec
            {
                Identity = IdentityOf(show, in facts),
                Config = Detail.Config.Show,
                Actions = _actions,
                Slots = SlotsFor(MaskOf(in facts)),
                RouteKey = p.RouteKey,
            });
        }

        /// <summary>A slot is absent until its facts are known (§6.1, never reserved) and the frame's slot equality is
        /// PRESENCE, so each presence combination is ONE cached <see cref="Detail.FrameSlots"/> over the same builders.</summary>
        static int MaskOf(in PageFacts f)
            => (f.Publisher ? 1 : 0) | (f.Badges ? 2 : 0) | (f.Rating ? 4 : 0) | (ShowReaderRules.LedgerShown(f.Head) ? 8 : 0) | (f.Topics ? 16 : 0);

        Detail.FrameSlots SlotsFor(int mask) => _slotSets[mask] ??= new Detail.FrameSlots
        {
            Episodes = _episodes,
            Primary = _primary,
            Satellites = _satellitesOf,
            Attribution = (mask & 1) != 0 ? _attribution : null,
            Badges = (mask & 2) != 0 ? _badges : null,
            Rating = (mask & 4) != 0 ? _rating : null,
            Ledger = (mask & 8) != 0 ? _ledger : null,
            Topics = (mask & 16) != 0 ? _topics : null,
        };

        PageFacts ComputeFacts()
        {
            var s = _m.Read();
            var show = s.Show;
            bool known = show.IsValid;
            bool followed = Controls.Library is { } lib && _m.SubjectText.Length > 0 && lib.IsSaved(_m.SubjectText);
            bool serial = s.Order == ConsumptionOrder.Sequential;
            bool firstKnown = s.FirstSlot > 0;
            var resume = new Episode(s.ResumeSlot);
            bool resumable = s.ResumeSlot > 0 && resume.IsValid;
            var first = new Episode(s.FirstSlot);
            int firstNumber = firstKnown && first.IsValid && first.Knows(EpisodeFields.Title) && first.Number > 0 ? first.Number : 1;
            bool rating = known && show.Knows(ShowFields.Rating);
            return new PageFacts(
                Head: s.Head,
                Primary: ShowReaderRules.PrimaryOf(s.Head, followed, serial, firstKnown, resumable),
                Ghost: ShowReaderRules.GhostOf(serial, firstKnown),
                ResumeLeft: resumable ? Episode.LeftMinutes(resume.ProgressMs, resume.DurationMs) : 0,
                FirstNumber: firstNumber,
                Publisher: known && show.Knows(ShowFields.Publisher) && !show.PublisherId.IsEmpty,
                Flags: known ? show.Flags : ShowFlags.None,
                RatingX100: rating ? show.RatingX100 : 0,
                RatingCount: rating ? show.RatingCount : 0,
                MyRating: rating ? show.MyRating : 0,
                HasTrailer: known && show.Knows(ShowFields.Facts) && !show.TrailerId.IsEmpty,
                ToneArgb: known && show.Knows(ShowFields.Appearance) ? show.Tone : 0u,
                Meta: MetaOf(s.Total, s.Cadence, s.OldestYear),
                Topics: known && show.Knows(ShowFields.Topics) && !show.TopicsId.IsEmpty);
        }

        void DemandShow()
        {
            var show = _show;
            if (!show.IsValid) return;
            Entities.Ensure(show, ShowFields.All);
            Reopen();
        }

        void Reopen()
        {
            var show = _show;
            if (!show.IsValid || !ReferenceEquals(_scope, Entities.Current)) return;
            var state = Entities.Current.Edges.ShowEpisodes.State(show.Slot);
            string uri = show.Uri.Text;
            if (state == EdgeState.Unknown) { Entities.EnsureEdge(FetchEdge.ShowEpisodes, show.Slot); return; }
            var verdict = ListFreshness.Decide(true, ListStamps.IsDirty(uri), ListStamps.RevalidatedAtMs(uri), ListStamps.NowMs(), false);
            if (verdict != ListFreshness.Verdict.Paint)
                Entities.RefreshEdge(FetchEdge.ShowEpisodes, show.Slot,
                    verdict == ListFreshness.Verdict.PaintThenRevalidate ? FetchPriority.Prefetch : FetchPriority.Visible);
        }

        // Auto-tracked: as the membership lands (a page, a refresh), ask for what a row paints. The planner dedupes; the
        // page demands its WHOLE resident model, never a visible window.
        void DemandRows()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.ShowEpisodes.Changed.Value;
            var show = _show;
            if (!show.IsValid) return;
            var slots = scope.Edges.ShowEpisodes.Targets(show.Slot);
            if (slots.Length > 0)
                Entities.Ensure(System.Runtime.InteropServices.MemoryMarshal.Cast<int, Episode>(slots),
                                EpisodeFields.Row | EpisodeFields.About | EpisodeFields.Progress);
        }

        /// <summary>The trailer is its own episode row OUTSIDE the membership (a text uri on the show): once the show's
        /// facts name one, its identity is part of this page's model (the door, the satellite).</summary>
        void DemandTrailer()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Shows.Changed.Value;
            var show = _show;
            if (!show.IsValid || !show.Knows(ShowFields.Facts) || show.TrailerId.IsEmpty) return;
            var trailer = new Episode(scope.Episodes.Slot(show.TrailerId));
            if (trailer.IsValid) Entities.Ensure(trailer, EpisodeFields.Row | EpisodeFields.About);
        }

        /// <summary>D-6: a filter or sort change is written to the ONE capped blob (the find never is, §4). Auto-tracked
        /// on the two signals; the seed itself writes nothing.</summary>
        void PersistView()
        {
            int status = _m.Status.Value, order = _m.Order.Value;
            if (status == _m.SavedStatus && order == _m.SavedOrder) return;
            _m.SavedStatus = status;
            _m.SavedOrder = order;
            if (_m.PrefsId.Length == 0) return;
            string blob = Platform.Settings.Get(Platform.Keys.PodcastViews);
            string next = ShowReaderRules.Persist(blob, _m.PrefsId, status, order);
            if (!string.Equals(next, blob, StringComparison.Ordinal)) Platform.Settings.Set(Platform.Keys.PodcastViews, next);
        }

        /// <summary>The CTA's satellites (W1): ♥ (hidden while Follow is the primary) · trailer (when the show names one)
        /// · the bell, DISABLED with its reason (D-10) · share · ⋯. Built once — a FIXED array per slot set; what comes
        /// and goes binds its presence.</summary>
        Element[] BuildSatellites()
        {
            string name = _show.IsValid && _show.Knows(ShowFields.Title) ? _show.Title : "";
            return
            [
                new BoxEl { Key = "sat:heart", Visible = _heartShown, Children = [Detail.RailSatelliteSave(_m.SubjectText, name)] },
                new BoxEl
                {
                    Key = "sat:trailer", Visible = _trailerShown,
                    Children = [Detail.RailSatellite(Icons.TvMonitor, Loc.Get(Strings.Podcast.Trailer), _playTrailer)],
                },
                Detail.RailSatellite(Icons.Bell, Loc.Get(Strings.Podcast.Notify.Unavailable), null) with { Key = "sat:bell" },
                Detail.RailSatellite(Icons.Share, Loc.Get(Strings.Menu.Share), _share) with { Key = "sat:share" },
                Detail.RailSatelliteMore(_more),
            ];
        }

        /// <summary>The trailer plays ALONE (it is not a member of the show context).</summary>
        void PlayTrailer()
        {
            var show = _show;
            if (!show.IsValid || show.TrailerId.IsEmpty) return;
            var trailer = new Episode(Entities.Current.Episodes.Slot(show.TrailerId));
            if (trailer.IsValid) Episode.Invoke(trailer, () => Playback.PlayContext(trailer.Id));
        }

        /// <summary>The show's ⋯: play next · add to queue (the container verbs over the membership) · pin · share.</summary>
        ContextMenuModel? MoreMenu()
        {
            var show = _show;
            if (!show.IsValid) return null;
            var ctx = new ActionContext(ActionTarget.ForShow(show.Uri, show.Knows(ShowFields.Title) ? show.Title : ""), Actions.Services);
            var rows = new List<MenuFlyoutItem>(6);
            if (Actions.Menu.Row(ActionId.PlayContextNext, in ctx) is { } next) rows.Add(next);
            if (Actions.Menu.Row(ActionId.AddContextToQueue, in ctx) is { } queue) rows.Add(queue);
            if (Actions.Menu.Row(ActionId.PinToSidebar, in ctx) is { } pin) Actions.Menu.Group(rows, pin);
            Actions.Menu.Group(rows, Episode.ShareMenu(show.Uri, in ctx));
            return rows.Count == 0 ? null : new ContextMenuModel(rows);
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

    /// <summary>The show's identity snapshot. The CALLER subscribes (Shows, ShowEpisodes, the facts); this reads.</summary>
    static Detail.Identity IdentityOf(Show show, in PageFacts f)
    {
        var edge = Entities.Current.Edges.ShowEpisodes;
        var state = edge.State(show.Slot);
        return new Detail.Identity
        {
            Subject = show.Uri,
            Kind = DetailKind.Show,
            HeaderPending = !show.Knows(ShowFields.Title),
            Title = show.Knows(ShowFields.Title) ? show.Title : "",
            CoverUrl = show.Knows(ShowFields.Image) ? Controls.ArtUrl(show.ImageId) : null,
            // The provider's tone, as the payload accent the frame falls back to when the cover cannot be graded.
            CardAccent = f.ToneArgb,
            Eyebrow = Detail.Text.Eyebrow(DetailKind.Show, BadgeStyle.TypeYear, AlbumKind.Album, 0,
                                          collaborative: false, isPublic: true, visibilityKnown: true),
            // "N episodes · weekly · since 2023" — the publisher moved to the Attribution slot (P2-M). The count is the
            // edge's TOTAL, so the line waits for the membership; until then the frame holds it as a shimmer bar.
            Meta = state == EdgeState.Unknown ? null : f.Meta,
            MetaLoading = state == EdgeState.Unknown && !edge.IsFailed(show.Slot),
            // A NEW visitor reads the full about in the reader (W2), so the rail drops its clamp of it.
            DescriptionHtml = f.Head != ShowReaderRules.Head.New && show.Knows(ShowFields.About) && (!show.DescriptionId.IsEmpty || !show.HtmlDescriptionId.IsEmpty)
                ? Entities.Strings.Resolve(!show.HtmlDescriptionId.IsEmpty ? show.HtmlDescriptionId : show.DescriptionId) : null,
            ShareUrl = Actions.WebLinkOf(show.Uri),
        };
    }

    // ══ 2. THE MODEL ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One item of the reader's bound list: its kind, its month (a Group's; a Row's running group), and — for a
    /// Row — the equality-gated row item; a Group's marks carry NoRule when it sits right under the header.</summary>
    readonly record struct ReaderItem(ShowReaderShape.ItemKind Kind, int GroupKey, Episode.RowItem Row);

    /// <summary>ONE coherent read of the page (see the file header). Immutable once published.</summary>
    sealed class ReaderSnap
    {
        public static readonly ReaderSnap Empty = new() { Pending = true };

        public Show Show;
        public bool Pending, Failed, Complete;
        /// <summary>The resident episodes, NEWEST FIRST (the edge order).</summary>
        public int[] Slots = [];
        public ReaderItem[] Items = [];
        public int ItemCount, ViewCount, Total;
        public bool Filtered, IsEmpty, EmptyShow;
        public bool CanLoadMore, Paging;
        public int NextOffset, LocalAsked;
        public ShowReaderRules.Head Head;
        public ShowLedger Ledger;
        public bool ProgressFailed;
        public ShowCadence.Kind Cadence;
        public DayOfWeek? Day;
        public ConsumptionOrder Order;
        public int ResumeSlot, FirstSlot, LatestSlot, TrailerSlot, OldestYear;
        public int[] UpNext = [], Fresh = [];
        public string CountAll = "", CountUnplayed = "", CountProgress = "", CountPlayed = "";
        /// <summary>A fold of everything the visit head paints (its episodes and their versions, the counts, the day).</summary>
        public ulong HeadFold;
    }

    sealed class ReaderModel
    {
        public readonly EntityUri Subject;
        public readonly string SubjectText, PrefsId;
        /// <summary><c>Episode.Rules.Status</c> (0 all · 1 unplayed · 2 in progress · 3 played) and the order (0 newest ·
        /// 1 oldest): seeded from the per-show prefs at construction (the library idiom), persisted on change.</summary>
        public readonly Signal<int> Status, Order;
        /// <summary>The in-show find: session-only (§4).</summary>
        public readonly Signal<string> Find = new("");
        public int SavedStatus, SavedOrder;
        public Memo<ReaderSnap>? Snap;
        public Memo<PageFacts>? Facts;
        public readonly Func<ReaderSnap> ComputeSnap;
        public readonly Action LoadMore, ResetView, MarkFreshPlayed;

        // the page's own half of the paging cursor (ch 09 §9.1)
        readonly Signal<int> _localAsked = new(0);
        readonly Signal<int> _pagingFrom = new(-1);
        uint _pagingVersion;

        public ReaderModel(EntityUri subject)
        {
            Subject = subject;
            SubjectText = subject.IsValid ? subject.Text : "";
            PrefsId = ShowReaderRules.PrefsId(SubjectText);
            var (status, order) = ShowReaderRules.Seed(Platform.Settings.Get(Platform.Keys.PodcastViews), PrefsId);
            Status = new Signal<int>(status);
            Order = new Signal<int>(order);
            SavedStatus = status;
            SavedOrder = order;
            ComputeSnap = Compute;
            LoadMore = NextPage;
            ResetView = () => { Status.Value = 0; Find.Value = ""; };
            MarkFreshPlayed = MarkFresh;
        }

        /// <summary>The snapshot, subscribing.</summary>
        public ReaderSnap Read() => Snap?.Value ?? ReaderSnap.Empty;
        /// <summary>The snapshot, for a click or a builder that renders what its stamp was computed from.</summary>
        public ReaderSnap Peek() => Snap?.Peek() ?? ReaderSnap.Empty;

        /// <summary>Play the SHOW context at <paramref name="e"/> (§4) — the show's own order resolves the rest.</summary>
        public void PlayInShow(Episode e)
        {
            var show = Peek().Show;
            if (show.IsValid && e.IsValid) Playback.PlayEpisode(e.Id, show.Id, new Playback.EpisodeStart(Playback.EpisodeStartKind.Resume));
        }

        /// <summary>"mark all played" on the new-since head: one mark per fresh episode (the local stage + the herodotus
        /// revision through the 2 s write queue, <see cref="Entities.MarkEpisode"/>).</summary>
        void MarkFresh()
        {
            var fresh = Peek().Fresh;
            for (int i = 0; i < fresh.Length; i++) Entities.MarkEpisode(new Episode(fresh[i]), played: true);
        }

        /// <summary>The NEXT page of the membership. A tap while a page is out does nothing; an offset somebody already
        /// asked for is not re-asked — the local cursor steps past it instead.</summary>
        void NextPage()
        {
            var s = Peek();
            if (!s.Show.IsValid || !s.CanLoadMore || s.Paging) return;
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

        ReaderSnap Compute()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Shows.Changed.Value;
            _ = scope.Episodes.Changed.Value;                      // progress, rows landing
            _ = scope.Edges.ShowEpisodes.Changed.Value;
            int status = Status.Value, order = Order.Value, localAsked = _localAsked.Value, pagingFrom = _pagingFrom.Value;
            string query = Find.Value;
            bool settled = Spotify.Telemetry.ProgressSettled.Value, failed = Spotify.Telemetry.ProgressFailed.Value;
            EntityId playingId = Playback.CurrentId.Value;          // listen-next's "the playing episode wins resume"
            if (!Subject.IsValid) return new ReaderSnap { Failed = true };

            var show = new Show(scope.Shows.Slot(Subject.Id));
            int slot = show.Slot;
            var edge = scope.Edges.ShowEpisodes;
            int[] slots = edge.Targets(slot).ToArray();
            int n = slots.Length;
            var pcts = new float[n];
            var published = new int[n];
            var playedAt = new int[n];
            var q = query.AsSpan().Trim();
            bool[]? found = q.IsEmpty ? null : new bool[n];
            int playing = -1;
            for (int i = 0; i < n; i++)
            {
                var e = new Episode(slots[i]);
                if (!e.IsValid) continue;
                pcts[i] = Episode.ReaderPctOf(e);                  // D-5: completed reads 1
                published[i] = e.Knows(EpisodeFields.Published) ? e.PublishedAt : 0;
                playedAt[i] = e.Knows(EpisodeFields.Progress) ? e.PlayedAt : 0;
                if (playing < 0 && !playingId.IsEmpty && e.Id == playingId) playing = i;
                if (found is not null)
                    found[i] = ShowReaderRules.Matches(e.Knows(EpisodeFields.Title) ? e.Title : "",
                        e.Knows(EpisodeFields.About) && !e.DescriptionId.IsEmpty ? Entities.Strings.Resolve(e.DescriptionId) : "", q);
            }

            var readiness = edge.Readiness(slot);
            var state = edge.State(slot);
            bool answered = readiness is EdgeState.Partial or EdgeState.Complete;
            int edgeTotal = edge.Total(slot);
            int total = Math.Max(edgeTotal, n);
            var consumption = show.Knows(ShowFields.Facts) ? show.Order : ConsumptionOrder.Unknown;
            int lastPlayed = ShowLedger.LastPlayed(playedAt);
            var ledger = ShowLedger.Of(pcts, published, lastPlayed, total, consumption);
            var (cadence, day) = ShowCadence.Of(published);
            Span<int> up = stackalloc int[ListenNext.UpNextMax];
            var (resume, upCount) = ListenNext.Pick(pcts, consumption, playing, up);
            var visit = ShowVisit.Of(ledger.Played + ledger.InProgress > 0, ledger.ToGo, ledger.InProgress);
            var head = ShowReaderRules.HeadOf(answered, settled, failed, visit, resume, upCount);
            // "new" and the progress counts are claims about the listener's history: made only once the hydrate settled
            // and succeeded (§6.1 — rows show no state rather than a false one).
            bool trusted = settled && !failed;

            var statusRule = (Episode.Rules.Status)Math.Clamp(status, 0, 3);
            var view = new int[n];
            int viewCount = ShowReaderRules.View(pcts, statusRule, oldest: order == 1, found, view);
            int capacity = ShowReaderShape.MaxItems(viewCount);
            var shape = new ShowReaderShape.Item[capacity];
            var marks = new Episode.RowMarks[capacity];
            int count = ShowReaderRules.Layout(slots, pcts, published, view.AsSpan(0, viewCount), lastPlayed, trusted, shape, marks);
            var items = new ReaderItem[count];
            for (int j = 0; j < count; j++)
            {
                var it = shape[j];
                items[j] = new ReaderItem(it.Kind, it.GroupKey, it.Kind == ShowReaderShape.ItemKind.Row
                    ? Episode.RowItem.Of(new Episode(it.Slot), marks[j])
                    : new Episode.RowItem(default, 0u, marks[j]));
            }

            var upNext = new int[upCount];
            for (int k = 0; k < upCount; k++) upNext[k] = slots[up[k]];
            int freshCount = 0;
            if (trusted) for (int i = 0; i < n; i++) if (ShowLedger.IsFresh(pcts[i], published[i], lastPlayed)) freshCount++;
            var fresh = new int[freshCount];
            for (int i = 0, k = 0; k < freshCount && i < n; i++)
                if (ShowLedger.IsFresh(pcts[i], published[i], lastPlayed)) fresh[k++] = slots[i];
            int trailer = show.Knows(ShowFields.Facts) && !show.TrailerId.IsEmpty
                          && scope.Episodes.TryGetSlot(show.TrailerId, out int trailerSlot) ? trailerSlot : 0;
            bool complete = state == EdgeState.Complete;
            int firstSlot = complete && n > 0 ? slots[n - 1] : 0;
            int oldestYear = complete && n > 0 && published[n - 1] > 0
                ? DateTimeOffset.FromUnixTimeSeconds(published[n - 1]).UtcDateTime.Year : 0;

            bool failedNow = edge.IsFailed(slot);
            bool paging = Episode.Rules.Paging(pagingFrom, _pagingVersion, edge.Version(slot), failedNow);
            int local = Episode.Rules.LocalCursorAfter(localAsked, pagingFrom, paging, failedNow);
            int edgeAsked = show.EpisodesAsked;
            int resumeSlot = resume >= 0 ? slots[resume] : 0;

            ulong fold = 14695981039346656037UL;
            Mix(ref fold, (uint)head);
            Mix(ref fold, show.IsValid ? show.Version : 0u);
            Mix(ref fold, (uint)total);
            Mix(ref fold, (uint)consumption | ((uint)cadence << 8) | (day is { } d ? ((uint)d + 1) << 16 : 0u) | ((uint)state << 24));
            Mix(ref fold, (uint)ledger.Played);
            Mix(ref fold, (uint)ledger.InProgress);
            Mix(ref fold, (uint)ledger.ToGo);
            MixRow(ref fold, resumeSlot);
            MixRow(ref fold, firstSlot);
            MixRow(ref fold, n > 0 ? slots[0] : 0);
            MixRow(ref fold, trailer);
            for (int k = 0; k < upNext.Length; k++) MixRow(ref fold, upNext[k]);
            for (int k = 0; k < fresh.Length; k++) MixRow(ref fold, fresh[k]);

            return new ReaderSnap
            {
                Show = show,
                Pending = readiness == EdgeState.Unknown,
                Failed = readiness == EdgeState.Failed,
                Complete = complete,
                Slots = slots,
                Items = items,
                ItemCount = count,
                ViewCount = viewCount,
                Total = total,
                Filtered = statusRule != Episode.Rules.Status.All || !q.IsEmpty,
                IsEmpty = viewCount == 0 && answered,
                EmptyShow = q.IsEmpty && Episode.Rules.IsEmptyShow(edgeTotal, n, statusRule),
                // A COMPLETE list never shows the pill, whatever the cursor column says; a partial one gates on the cursor.
                CanLoadMore = Episode.Rules.CanLoadMore(state, edgeAsked, local, edgeTotal),
                Paging = paging,
                NextOffset = Episode.Rules.NextOffset(edgeAsked, local, n),
                LocalAsked = local,
                Head = head,
                Ledger = ledger,
                ProgressFailed = failed,
                Cadence = cadence,
                Day = day,
                Order = consumption,
                ResumeSlot = resumeSlot,
                FirstSlot = firstSlot,
                LatestSlot = n > 0 ? slots[0] : 0,
                TrailerSlot = trailer,
                OldestYear = oldestYear,
                UpNext = upNext,
                Fresh = fresh,
                CountAll = FormatCache.Int(total),
                CountUnplayed = trusted ? FormatCache.Int(ledger.ToGo) : "",
                CountProgress = trusted ? FormatCache.Int(ledger.InProgress) : "",
                CountPlayed = trusted ? FormatCache.Int(ledger.Played) : "",
                HeadFold = fold,
            };
        }

        static void Mix(ref ulong h, uint v)
        {
            h ^= v;
            h *= 1099511628211UL;
        }

        static void MixRow(ref ulong h, int slot)
        {
            var e = new Episode(slot);
            Mix(ref h, (uint)slot);
            Mix(ref h, slot > 0 && e.IsValid ? e.Version : 0u);
        }
    }

    // ══ 3. THE READER ════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record ReaderProps(ReaderModel Model, string RouteKey);

    /// <summary>The right column (§5.6): one bound list behind the membership's skeleton. Owns the width arms, the
    /// tone, the rows' context and the rail's words — everything built once.</summary>
    sealed class ReaderHost : Component, IPropsHost
    {
        ReaderProps? _latest;
        readonly Signal<ReaderProps?> _props = new(null);
        ReaderModel? _m;
        BoundItemsSource<ReaderItem>? _items;
        ListOptions<ReaderItem>? _options;
        string _optionsKey = "\0";
        Element? _rail;
        IReadSignal<float>? _width;
        IReadSignal<Design.PageAccent>? _accent;
        Memo<ColorF>? _tone;
        readonly RepeatLayout _layout = RepeatLayout.VariableList(ItemEstimate);

        internal Memo<bool>? Narrow, RailScrolls;
        internal Episode.RowContext? RowCtx;
        internal Controls.Words.Word[]? FilterWords, SortWords;
        /// <summary>The show's tone as a live read (a memo, so a bind re-fires only when the colour moves).</summary>
        internal readonly Func<ColorF> Tone;

        readonly Func<bool> _narrowOf, _railScrollsOf, _pending, _failed;
        readonly Func<ColorF> _toneOf;
        readonly Func<BoundItemScope<ReaderItem>, Element> _template;
        readonly Func<int, int> _contentType;
        readonly Func<Element> _content, _shimmer, _failedView;
        readonly Action _retry;
        readonly Action<Episode> _play;
        static readonly Func<Episode, ContextMenuModel?> s_menu = static e => Episode.Menu(e, new Episode.MenuOptions(ShowGoToShow: false));

        public ReaderHost()
        {
            _narrowOf = () => ShowReaderRules.Narrow(_width?.Value ?? 0f);
            _railScrollsOf = () => ShowReaderRules.RailScrolls(_width?.Value ?? 0f);
            _toneOf = () =>
            {
                ColorF fallback = _accent is { } a ? a.Value.Fill : Tok.AccentDefault;
                return ToneOf(_m?.Facts is { } f ? f.Value.ToneArgb : 0u, fallback);
            };
            Tone = () => _tone?.Value ?? Tok.AccentDefault;
            _pending = () => _m?.Read().Pending ?? true;
            _failed = () => _m?.Read().Failed ?? false;
            _contentType = i =>
            {
                var s = _m?.Peek();
                return s is null || (uint)i >= (uint)s.ItemCount ? 0 : (int)s.Items[i].Kind;
            };
            // Slot 0 — the persistent prefix — is the RAW sticky rail; every other slot a component (file header).
            _template = scope => scope.Row.Index.Peek() == 0 ? (_rail ??= RailElement()) : Embed.Comp(() => new ReaderSlot(this, scope));
            _content = () => ItemsView.CreateBound(_items!, _template, _layout, OptionsFor(_latest?.RouteKey ?? ""));
            _shimmer = () => SeedList(Narrow?.Peek() ?? false);
            _retry = () =>
            {
                if (_m?.Peek().Show is { IsValid: true } show) Entities.RefreshEdge(FetchEdge.ShowEpisodes, show.Slot);
            };
            _failedView = () => Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, onAction: _retry);
            _play = e => Episode.Invoke(e, () => Model.PlayInShow(e));
        }

        internal ReaderModel Model => _m!;

        public void ApplyProps(object props)
        {
            var p = (ReaderProps)props;
            _latest = p;
            _m = p.Model;
            _props.Value = p;                                      // equality-gated: a data-equal re-push is free
        }

        public override Element Render()
        {
            _ = _props.Value;
            var overlay = UseContext(Overlay.Service);
            _accent = UseContext(Design.AccentCtx.Slot);
            _width = UseMeasuredWidth(4f);
            Narrow = UseComputed(_narrowOf);
            RailScrolls = UseComputed(_railScrollsOf);
            _tone = UseComputed(_toneOf);
            var m = Model;
            RowCtx ??= new Episode.RowContext
            {
                Tone = Tone, Play = _play, Menu = s_menu, Overlay = Controls.IsNullOverlay(overlay) ? null : overlay,
            };
            // The words are frozen at the rail's mount (their positions ARE Episode.Rules.Status / the order); the counts
            // are live binds over the snapshot, empty until progress is trusted.
            FilterWords ??= new Controls.Words.Word[]
            {
                new(Loc.Bind(Strings.Podcast.Filter.All), Prop.Of(() => m.Read().CountAll)),
                new(Loc.Bind(Strings.Podcast.Filter.Unplayed), Prop.Of(() => m.Read().CountUnplayed)),
                new(Loc.Bind(Strings.Podcast.Filter.InProgress), Prop.Of(() => m.Read().CountProgress)),
                new(Loc.Bind(Strings.Podcast.Filter.Played), Prop.Of(() => m.Read().CountPlayed)),
            };
            SortWords ??= new Controls.Words.Word[] { new(Loc.Bind(Strings.Podcast.Sort.Newest)), new(Loc.Bind(Strings.Podcast.Sort.Oldest)) };
            _items ??= BoundItems.Project(m.Snap!, static s => s.ItemCount, static (s, i) => s.Items[i], default(ReaderItem));
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

        ListOptions<ReaderItem> OptionsFor(string routeKey)
        {
            if (_options is null || !string.Equals(_optionsKey, routeKey, StringComparison.Ordinal))
            {
                _optionsKey = routeKey;
                _options = new ListOptions<ReaderItem>
                {
                    SelectionMode = ItemsSelectionMode.None,           // no marquee, no Ctrl+A, no batch bar
                    Selector = SelectorVisual.None,
                    Grow = 1f,
                    ContentType = _contentType,                        // recycle pools per kind: a row never rebinds as a head
                    PersistentPrefixCount = ShowReaderShape.Prefix,
                    Scroll = new ScrollOptions
                    {
                        ScrollKey = "episodes:" + routeKey,             // the position survives a swap
                        AutoEdgeFade = false,                           // the rail's clip band owns the top feather
                        ItemClipTopInset = RailHeight,
                        ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
                    },
                };
            }
            return _options;
        }

        /// <summary>ITEM 0: the rail's sticky plane — a RAW element (never a component: see the file header) whose one
        /// child re-renders on the width arms; every word, count and the find box inside it binds the model's signals.</summary>
        Element RailElement() => new BoxEl
        {
            Direction = 1, Height = RailHeight, Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 0f, Justify = FlexJustify.Center,
            Children = [Embed.Comp(() => new RailBody(this))],
        }.Sticky(0f);
    }

    /// <summary>One persistent slot of the list. Re-renders only when its KIND flips or the width arm changes; a row's
    /// per-episode reads are binds over the slot's gated row item, so a recycle re-binds and rebuilds nothing. The root
    /// Grows in the list's row wrapper (the ItemsView-item rule) and carries the reader's side padding.</summary>
    sealed class ReaderSlot : Component
    {
        readonly ReaderHost _host;
        readonly RowScope _row;
        readonly IReadSignal<ReaderItem> _item;
        readonly Func<ShowReaderShape.ItemKind> _kindOf;
        readonly Func<Episode.RowItem> _rowOf;

        public ReaderSlot(ReaderHost host, BoundItemScope<ReaderItem> scope)
        {
            _host = host;
            _row = scope.Row;
            var item = scope.Item;
            _item = item;
            _kindOf = () => item.Value.Kind;
            _rowOf = () => item.Value.Row;
        }

        public override Element Render()
        {
            var kind = UseComputed(_kindOf).Value;
            var row = UseComputed(_rowOf);
            bool narrow = _host.Narrow?.Value ?? false;
            var host = _host;
            Element body = kind switch
            {
                ShowReaderShape.ItemKind.Head => Embed.Comp(() => new VisitHead(host)) with { Key = "rd:head" },
                ShowReaderShape.ItemKind.Header => Embed.Comp(() => new EpisodesHeader(host)) with { Key = "rd:header" },
                ShowReaderShape.ItemKind.Group => GroupLabel(new BoundItemScope<ReaderItem>(_row, _item)) with { Key = "rd:group" },
                ShowReaderShape.ItemKind.Row => Episode.ReaderRow(new BoundItemScope<Episode.RowItem>(_row, row), host.RowCtx!, narrow) with { Key = "rd:row" },
                ShowReaderShape.ItemKind.Foot => Embed.Comp(() => new ReaderFoot(host)) with { Key = "rd:foot" },
                _ => new BoxEl(),
            };
            float pad = narrow ? ReaderPadNarrow : ReaderPad;
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = new Edges4(pad, 0f, pad, 0f),
                Children = [body],
            };
        }
    }

    // ══ 4. THE RAIL'S SLOTS ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What a rail slot body is handed: the page's model, the frame's accent (Primary only) and the rail's
    /// measure (NaN in the vertical header: fill the column).</summary>
    sealed record SlotProps(ReaderModel Model, Func<ColorF>? Accent, float Width = float.NaN);

    /// <summary>The CTA's primary by visit (§4): New &amp; not following → Follow + a ghost ▶ (episode 1 for a serial,
    /// else the latest); Returning → Resume · N min left on listen-next's resume; otherwise ▶ the latest. Play is
    /// always the show context at the episode.</summary>
    sealed class PrimarySlot : Component
    {
        ReaderModel? _m;
        Func<ColorF>? _accent;
        Memo<ColorF>? _tone;
        readonly Func<ColorF> _toneOf, _toneRead;
        readonly Action _follow, _resume, _first, _latest;

        public PrimarySlot()
        {
            _toneOf = () =>
            {
                ColorF fallback = _accent?.Invoke() ?? Tok.AccentDefault;
                return ToneOf(_m?.Facts is { } f ? f.Value.ToneArgb : 0u, fallback);
            };
            _toneRead = () => _tone?.Value ?? Tok.AccentDefault;
            _follow = () =>
            {
                if (_m is not { } m || Controls.Library is not { } lib || m.SubjectText.Length == 0) return;
                var show = m.Peek().Show;
                lib.ToggleSaved(m.SubjectText, show.IsValid && show.Knows(ShowFields.Title) ? show.Title : null);
            };
            _resume = () => PlaySlot(_m?.Peek().ResumeSlot ?? 0);
            _first = () => PlaySlot(_m?.Peek().FirstSlot ?? 0);
            _latest = () => PlaySlot(_m?.Peek().LatestSlot ?? 0);
        }

        void PlaySlot(int slot)
        {
            if (_m is not { } m) return;
            var e = new Episode(slot);
            if (slot > 0 && e.IsValid) Episode.Invoke(e, () => m.PlayInShow(e));
            else if (m.Peek().Show is { IsValid: true } show) Playback.PlayContext(show.Id);
        }

        public override Element Render()
        {
            var p = UseProps<SlotProps>();
            _m = p.Model;
            _accent = p.Accent;
            _tone = UseComputed(_toneOf);
            var f = _m.Facts?.Value ?? default;
            Element pill = f.Primary switch
            {
                ShowReaderRules.Primary.Follow => Detail.PlayPill(_toneRead, _follow, Loc.Get(Strings.Artist.Follow), Icons.Add),
                ShowReaderRules.Primary.Resume => Detail.PlayPill(_toneRead, _resume,
                    Loc.Get(Strings.Podcast.Resume) + " · " + Strings.Podcast.Left(Episode.DurationWords(f.ResumeLeft))),
                ShowReaderRules.Primary.PlayFirst => Detail.PlayPill(_toneRead, _first, Strings.Podcast.Badge.Episode(FormatCache.Int(f.FirstNumber))),
                _ => Detail.PlayPill(_toneRead, _latest, Loc.Get(Strings.Podcast.Latest)),
            };
            if (f.Primary != ShowReaderRules.Primary.Follow) return pill;
            Element ghost = f.Ghost == ShowReaderRules.Primary.PlayFirst
                ? Controls.Pill(Strings.Podcast.Badge.Episode(FormatCache.Int(f.FirstNumber)), _first, ButtonAppearance.Standard, glyph: Icons.Play)
                : Controls.Pill(Loc.Get(Strings.Podcast.Latest), _latest, ButtonAppearance.Standard, glyph: Icons.Play);
            return new BoxEl
            {
                Direction = 0, Wrap = true, Gap = Detail.RailLayout.SatelliteGap, AlignItems = FlexAlign.Center, Children = [pill, ghost],
            };
        }
    }

    readonly record struct LedgerStamp(bool Failed, int Played, int InProgress, int ToGo, int Fresh);

    /// <summary>The played ledger (W1): the three-part bar and "84 played · 3 in progress · 41 to go · 5 new" — or, when
    /// the hydrate failed, "progress unavailable" (§6.1).</summary>
    sealed class LedgerSlot : Component
    {
        ReaderModel? _m;
        IReadSignal<Design.PageAccent>? _accent;
        Memo<ColorF>? _tone;
        readonly Func<LedgerStamp> _stamp;
        readonly Func<ColorF> _toneOf, _toneRead;

        public LedgerSlot()
        {
            _stamp = () =>
            {
                if (_m is not { } m) return default;
                var s = m.Read();
                var l = s.Ledger;
                return new LedgerStamp(s.ProgressFailed, l.Played, l.InProgress, l.ToGo, l.Fresh);
            };
            _toneOf = () =>
            {
                ColorF fallback = _accent is { } a ? a.Value.Fill : Tok.AccentDefault;
                return ToneOf(_m?.Facts is { } f ? f.Value.ToneArgb : 0u, fallback);
            };
            _toneRead = () => _tone?.Value ?? Tok.AccentDefault;
        }

        public override Element Render()
        {
            var p = UseProps<SlotProps>();
            _m = p.Model;
            _accent = UseContext(Design.AccentCtx.Slot);
            _tone = UseComputed(_toneOf);
            var st = UseComputed(_stamp).Value;
            float width = float.IsFinite(p.Width) ? p.Width : float.NaN;
            if (st.Failed)
                return new BoxEl
                {
                    Direction = 1, Width = width, MinWidth = 0f,
                    Children = [new TextEl(Loc.Get(Strings.Podcast.ProgressUnavailable)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f }],
                };
            int sum = st.Played + st.InProgress + st.ToGo;
            float played = sum > 0 ? st.Played / (float)sum : 0f, progress = sum > 0 ? st.InProgress / (float)sum : 0f;
            TextSpan[] line = st.Fresh > 0
                ? [new TextSpan(Strings.Podcast.Ledger(st.Played, st.InProgress, st.ToGo) + " · "),
                   new TextSpan(Strings.Podcast.LedgerNew(st.Fresh), Weight: 600, Color: _toneRead())]
                : [new TextSpan(Strings.Podcast.Ledger(st.Played, st.InProgress, st.ToGo))];
            return new BoxEl
            {
                Direction = 1, Gap = Detail.RailLayout.LedgerGap, Width = width, MinWidth = 0f,
                Children =
                [
                    Controls.LedgerBar(played, progress, _toneRead),
                    new SpanTextEl(line) { Size = 12f, LineHeight = Detail.RailLayout.LedgerLineHeight, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                ],
            };
        }
    }

    /// <summary>The rating row: ★ 4.8 · "12,431 ratings" (· "yours 5★") — a button that opens the rate flyout, whose
    /// stars are DISABLED with their reason until the write lands (wave P10, D-10).</summary>
    sealed class RatingSlot : Component
    {
        ReaderModel? _m;
        IReadSignal<Design.PageAccent>? _accent;
        IOverlayService? _overlay;
        Memo<ColorF>? _tone;
        NodeHandle _anchor;
        OverlayHandle? _handle;
        readonly Func<ColorF> _toneOf, _toneRead;
        readonly Action _toggle, _clearHandle;
        readonly Action<NodeHandle> _realized;
        readonly Func<NodeHandle> _anchorOf;
        readonly Func<Element> _flyout;

        public RatingSlot()
        {
            _toneOf = () =>
            {
                ColorF fallback = _accent is { } a ? a.Value.Fill : Tok.AccentDefault;
                return ToneOf(_m?.Facts is { } f ? f.Value.ToneArgb : 0u, fallback);
            };
            _toneRead = () => _tone?.Value ?? Tok.AccentDefault;
            _toggle = Toggle;
            _clearHandle = () => _handle = null;
            _realized = h => _anchor = h;
            _anchorOf = () => _anchor;
            _flyout = Flyout;
        }

        public override Element Render()
        {
            var p = UseProps<SlotProps>();
            _m = p.Model;
            _overlay = UseContext(Overlay.Service);
            _accent = UseContext(Design.AccentCtx.Slot);
            _tone = UseComputed(_toneOf);
            var f = _m.Facts?.Value ?? default;
            string average = (f.RatingX100 / 100f).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
            string count = Strings.Podcast.Ratings(f.RatingCount);
            if (f.MyRating > 0) count += " · " + Strings.Podcast.Yours(f.MyRating);
            var button = new BoxEl
            {
                Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f,
                Padding = new Edges4(4f, 2f, 4f, 2f), Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = _toggle, OnRealized = _realized,
                Children =
                [
                    Icon(Icons.FavoriteStarFill, 13f) with { Color = Prop.Of(_toneRead) },
                    new TextEl(average) { Size = 12.5f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 },
                    new TextEl(count) { Size = 12.5f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                ],
            };
            var show = _m.Peek().Show;
            string name = show.IsValid && show.Knows(ShowFields.Title) ? show.Title : "";
            return new BoxEl
            {
                Direction = 0, MinWidth = 0f, MaxWidth = float.IsFinite(p.Width) ? p.Width : float.NaN,
                Children = [Controls.Named(button, Strings.Podcast.Rate.Title(name))],
            };
        }

        void Toggle()
        {
            var overlay = _overlay;
            if (Controls.IsNullOverlay(overlay)) return;
            if (_handle is { IsOpen: true } open) { open.Close(); return; }
            var handle = overlay.Open(_anchorOf, _flyout, FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.ClosedAction = _clearHandle;
            _handle = handle;
        }

        /// <summary>The rate flyout (built at OPEN): "Rate {name}", the listener's stars — display-only — and the reason.</summary>
        Element Flyout()
        {
            var show = _m?.Peek().Show ?? default;
            return show.IsValid
                ? Embed.Comp(new PodcastReaderUI.RatingProps(show.Uri, _toneRead), static () => new PodcastReaderUI.RatingEditor())
                : new BoxEl();
        }

    }

    /// <summary>The badge chips on the eyebrow's line: exclusive (the tone) · E · video.</summary>
    sealed class BadgesSlot : Component
    {
        ReaderModel? _m;
        IReadSignal<Design.PageAccent>? _accent;
        Memo<ColorF>? _tone;
        readonly Func<ColorF> _toneOf, _toneRead;

        public BadgesSlot()
        {
            _toneOf = () =>
            {
                ColorF fallback = _accent is { } a ? a.Value.Fill : Tok.AccentDefault;
                return ToneOf(_m?.Facts is { } f ? f.Value.ToneArgb : 0u, fallback);
            };
            _toneRead = () => _tone?.Value ?? Tok.AccentDefault;
        }

        public override Element Render()
        {
            var p = UseProps<SlotProps>();
            _m = p.Model;
            _accent = UseContext(Design.AccentCtx.Slot);
            _tone = UseComputed(_toneOf);
            var flags = _m.Facts?.Value.Flags ?? ShowFlags.None;
            var chips = new List<Element>(3);
            if ((flags & ShowFlags.Exclusive) != 0) chips.Add(Controls.Chip(Loc.Get(Strings.Podcast.Badge.Exclusive), tone: true, toneOf: _toneRead));
            if ((flags & ShowFlags.Explicit) != 0) chips.Add(Controls.Chip("E"));
            if ((flags & ShowFlags.Video) != 0) chips.Add(Controls.Chip(Loc.Get(Strings.Podcast.Badge.Video), Icons.Movie));
            return new BoxEl
            {
                Direction = 0, Wrap = true, Gap = Detail.RailLayout.BadgeGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = chips.ToArray(),
            };
        }
    }
}

// ══ 5. THE PAGE'S PURE DECISIONS (CORE — pinned by ShowReaderTests) ══════════════════════════════════════════════════

/// <summary>The show reader's decisions that are not a model fact of their own: which head, which primary, the width
/// arms, the find predicate, the view, the item list with its row marks, and the per-show view prefs' seed and persist.
/// Engine-free; <see cref="Layout"/> (per snapshot), <see cref="Persist"/> (a click) and <see cref="PrefsId"/> (once per
/// page) allocate — nothing here runs per frame.</summary>
public static class ShowReaderRules
{
    /// <summary>The reader's first screen (W1/W2): <see cref="Pending"/> until the membership answered AND the login
    /// hydrate settled (§6.1 — never flash New at a Returning listener); <see cref="Unavailable"/> when the hydrate
    /// failed and nothing resident shows progress (a New head then would be a false claim).</summary>
    public enum Head : byte { Pending, New, Returning, CaughtUp, Unavailable }

    /// <summary>The rail's primary pill (§4).</summary>
    public enum Primary : byte { PlayLatest, PlayFirst, Follow, Resume }

    /// <summary>Below this reader width the rows narrow: no numeral, 48 art, the disc alone (W3).</summary>
    public const float NarrowBelow = 540f;
    /// <summary>Below this reader width the word rail scrolls sideways instead of pinning its sort words right (W1).</summary>
    public const float RailScrollsBelow = 720f;

    /// <summary>A width of 0 is "not measured yet": the wide arm.</summary>
    public static bool Narrow(float width) => width > 0f && width < NarrowBelow;

    public static bool RailScrolls(float width) => width > 0f && width < RailScrollsBelow;

    /// <summary>The head. A Returning visit whose listen-next found nothing to continue (every unfinished episode is
    /// past <see cref="ListenNext.NearComplete"/> — Resume -1 and no up-next, as-built P1-R) falls back to CaughtUp.</summary>
    public static Head HeadOf(bool answered, bool settled, bool failed, ShowVisitKind visit, int resume, int upCount)
    {
        if (!answered || !settled) return Head.Pending;
        return visit switch
        {
            ShowVisitKind.New => failed ? Head.Unavailable : Head.New,
            ShowVisitKind.CaughtUp => Head.CaughtUp,
            _ => resume < 0 && upCount <= 0 ? Head.CaughtUp : Head.Returning,
        };
    }

    /// <summary>§4: New &amp; not following → Follow; New &amp; following → episode 1 (a serial whose first is resident) or
    /// the latest; Returning → Resume when listen-next has one; anything else (caught up, pending, unavailable) → the
    /// latest. Progress beats follow (D-1): a Returning listener who never followed is offered Resume, not Follow.</summary>
    public static Primary PrimaryOf(Head head, bool followed, bool serial, bool firstKnown, bool resumable) => head switch
    {
        Head.New when !followed => Primary.Follow,
        Head.New => serial && firstKnown ? Primary.PlayFirst : Primary.PlayLatest,
        Head.Returning when resumable => Primary.Resume,
        _ => Primary.PlayLatest,
    };

    /// <summary>The ghost pill beside Follow: episode 1 for a serial whose first episode is resident, else the latest.</summary>
    public static Primary GhostOf(bool serial, bool firstKnown) => serial && firstKnown ? Primary.PlayFirst : Primary.PlayLatest;

    /// <summary>The ledger slot is present once the head is decided and is not a new visitor's.</summary>
    public static bool LedgerShown(Head head) => head is Head.Returning or Head.CaughtUp or Head.Unavailable;

    /// <summary>The find (§4): the trimmed query in the title or the description, ordinal-ignore-case; an empty query
    /// matches everything.</summary>
    public static bool Matches(ReadOnlySpan<char> title, ReadOnlySpan<char> description, ReadOnlySpan<char> query)
    {
        var q = query.Trim();
        return q.IsEmpty || title.Contains(q, StringComparison.OrdinalIgnoreCase) || description.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rows the reader shows, as ORIGINAL indices in view order: the status filter over the pcts
    /// (<c>Episode.Rules.Matches</c>) ∩ the find (<paramref name="found"/>, parallel to the pcts; EMPTY = no find),
    /// newest first as the edge orders them, reversed for oldest. Returns the count written.</summary>
    public static int View(ReadOnlySpan<float> pcts, Episode.Rules.Status status, bool oldest, ReadOnlySpan<bool> found, Span<int> into)
    {
        int n = 0;
        for (int i = 0; i < pcts.Length && n < into.Length; i++)
            if (Episode.Rules.Matches(status, pcts[i]) && (found.IsEmpty || (i < found.Length && found[i]))) into[n++] = i;
        if (oldest) into[..n].Reverse();
        return n;
    }

    /// <summary>The reader's item list: <see cref="ShowReaderShape.Build"/> over the view (its slots and their dates, the
    /// Foot always — the list's end is the pill or the dock reserve) plus each item's marks: a Row right under a month
    /// header or the "episodes" head drops its hairline (<c>NoRule</c>), a Group right under the head drops its top air,
    /// and a Row is <c>Fresh</c> when <paramref name="trusted"/> (progress settled and succeeded) and
    /// <see cref="ShowLedger.IsFresh"/> says so. <paramref name="view"/> holds ORIGINAL indices into the parallel spans.
    /// <paramref name="items"/> and <paramref name="marks"/> need <see cref="ShowReaderShape.MaxItems"/>(view.Length).</summary>
    public static int Layout(ReadOnlySpan<int> slots, ReadOnlySpan<float> pcts, ReadOnlySpan<int> publishedAt,
                             ReadOnlySpan<int> view, int lastPlayedAt, bool trusted,
                             Span<ShowReaderShape.Item> items, Span<Episode.RowMarks> marks)
    {
        int v = view.Length;
        var viewSlots = new int[v];
        var viewDates = new int[v];
        for (int k = 0; k < v; k++)
        {
            int i = view[k];
            viewSlots[k] = slots[i];
            viewDates[k] = i < publishedAt.Length ? publishedAt[i] : 0;
        }
        int count = ShowReaderShape.Build(viewSlots, viewDates, canLoadMore: true, hasSimilar: false, items);
        int row = 0;
        for (int j = 0; j < count && j < marks.Length; j++)
        {
            var kind = items[j].Kind;
            var prev = j > 0 ? items[j - 1].Kind : ShowReaderShape.ItemKind.Rail;
            var m = Episode.RowMarks.None;
            if (kind == ShowReaderShape.ItemKind.Row && row < v)
            {
                int i = view[row++];
                if (prev is ShowReaderShape.ItemKind.Group or ShowReaderShape.ItemKind.Header) m |= Episode.RowMarks.NoRule;
                if (trusted && ShowLedger.IsFresh(pcts[i], i < publishedAt.Length ? publishedAt[i] : 0, lastPlayedAt))
                    m |= Episode.RowMarks.Fresh;
            }
            else if (kind == ShowReaderShape.ItemKind.Group && prev == ShowReaderShape.ItemKind.Header)
                m |= Episode.RowMarks.NoRule;
            marks[j] = m;
        }
        return count;
    }

    /// <summary>The prefs key of a show: the base62 id after the uri's last ':' (<see cref="ShowViewPrefs"/> takes ids,
    /// never uris); "" when there is none.</summary>
    public static string PrefsId(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        int cut = uri.LastIndexOf(':');
        string id = cut < 0 ? uri : uri[(cut + 1)..];
        return ShowViewPrefs.IsValidId(id) ? id : "";
    }

    /// <summary>The page's seed (D-6): the stored (status, order), each clamped to the values the reader defines — a
    /// hand-edited blob can carry anything.</summary>
    public static (int Status, int Order) Seed(string? blob, string prefsId)
    {
        var (status, order) = ShowViewPrefs.Read(blob, prefsId);
        return (status is >= 0 and <= (int)Episode.Rules.Status.Played ? status : 0, order is 0 or 1 ? order : 0);
    }

    /// <summary>The blob with this show's view written (moved to the front, capped — <see cref="ShowViewPrefs.Write"/>).</summary>
    public static string Persist(string? blob, string prefsId, int status, int order)
        => ShowViewPrefs.Write(blob, prefsId, status, order);
}
