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
//      └─ right: Slots.Episodes → ReaderHost                      DEMANDS the rows' facts (ShowReaderRules.RowDemand) itself
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

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
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
        readonly Action _demandShow, _demandTrailer, _persist, _playTrailer, _share, _publishBand;
        IReadSignal<bool>? _isActive;
        /// <summary>The show's title row has no tabs, so its band publication carries a never-lit active signal.</summary>
        static readonly Signal<int> s_noTab = new(Detail.BandLayout.NoSection);
        static readonly Action<int> s_noPivot = static _ => { };
        readonly Prop<bool> _heartShown, _trailerShown;

        public PageHost(EntityUri subject)
        {
            _m = new ReaderModel(subject);
            Controls.Library ??= User.LibrarySeam;               // the Follow primary and the ♥ satellite write through it
            _facts = ComputeFacts;
            _demandShow = DemandShow;
            _demandTrailer = DemandTrailer;
            _persist = PersistView;
            _publishBand = PublishBand;
            _playTrailer = PlayTrailer;
            _share = () => Episode.CopyLink(_show.IsValid ? Actions.WebLinkOf(_show.Uri) : "");
            _more = MoreMenu;
            _actions = new Detail.FrameActions { CoverDrag = CoverPayload };
            // THE VERTICAL SEAM. `FrameSlots.Episodes` is `Func<bool, Element>` and the bool is Detail.UI.cs's
            // `vertical` arm (mode 3 / the Hero page layout): the show page deliberately ignores it today and hands the
            // SAME reader to both arms — Detail.UI.cs's vertical branch already wraps it in `ShowHeaderCore`. A future
            // Hero/vertical show layout plugs in HERE (`v => v ? VerticalReader(...) : Reader(...)`) and nowhere else;
            // nothing below hard-codes a two-column shape, and the rail/grip/splitter are Detail.UI.cs's, not this
            // page's, so the two-column arm needs no change to make room for it.
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
            // The ROWS' facts are the reader's own demand (ReaderHost.DemandRows), not this page's: the library pane
            // mounts the same reader without a page around it.
            UseEffect(_demandTrailer);
            UseEffect(_persist);
            // The Zune band's row 2 shows the show's name (title only: no tabs, no actions) once it is known; the route title
            // prefills the first frame.
            _isActive = UseIsActive();
            UseActivation(onActivated: _publishBand);
            UseEffect(_publishBand, DepKey.From(HashCode.Combine(show.IsValid ? show.Title : "", _routeKey)));

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

        /// <summary>Hands the show's name to the Zune band's row 2 under the route name, only while this page is the active one.</summary>
        void PublishBand()
        {
            if (_isActive is { } act && !act.Peek()) return;
            if (!_show.IsValid || _routeKey.Length == 0) return;
            string title = _show.Title;
            if (title.Length == 0) return;
            PageHead.PublishBand(_routeKey, title, Array.Empty<string>(), s_noTab, s_noPivot);
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
            var target = ActionTarget.ForShow(show.Uri, show.Knows(ShowFields.Title) ? show.Title : "");
            return Menus.Container(in target, show.Knows(ShowFields.Image) ? Controls.ArtUrl(show.ImageId) : null, null,
                new ContainerExtras { OnPage = true });
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

    /// <summary>The date rail's projection over the reader's items — the same two lambdas
    /// <see cref="ShowDateIndex.Project"/> hands the shared <see cref="JumpIndex"/> kernel over the shape's own item
    /// type, restated here for the page's ReaderItem twin (both go through <see cref="ShowDateIndex.KeyOf"/>).</summary>
    static readonly Func<ReaderItem, bool> s_isGroup = static it => it.Kind == ShowReaderShape.ItemKind.Group;
    static readonly Func<ReaderItem, int> s_groupJumpKey = static it => ShowDateIndex.KeyOf(it.GroupKey);

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
        /// <summary><see cref="Filtered"/> IS results mode (<see cref="ShowReaderRules.ResultsMode"/>): a status filter or a
        /// find is on, so the list is the answer to a question and the head stays out of its way.</summary>
        public bool Filtered, IsEmpty, EmptyShow;
        public bool CanLoadMore, Paging;
        public int NextOffset, LocalAsked;
        public ShowReaderRules.Head Head;
        /// <summary>Is the visit head painted? False in results mode (it is hidden — never filtered); sort alone leaves it
        /// shown. Part of the head's fold: it is a fact of what the head paints.</summary>
        public bool HeadShown = true;
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
            // An AUDIOBOOK's membership arrives in the provider's native playlist order (chapter 1 first) and carries
            // its sample among the chapters. `AudiobookOrder` is the ONE place that is turned into the reader's order —
            // everything below (the pcts, the ledger, listen-next, the view, the item list) then reads the same span it
            // always did. Playback still plays the context Spotify sent; this rewrites nothing but the reader's view.
            bool audiobook = show.IsValid && show.IsAudiobook;
            int chapterTrailer = 0;
            if (audiobook && slots.Length > 0)
            {
                var kinds = new EpisodeKind[slots.Length];
                for (int i = 0; i < slots.Length; i++)
                {
                    var e = new Episode(slots[i]);
                    kinds[i] = e.IsValid && e.Knows(EpisodeFields.Title) ? e.Kind : EpisodeKind.Full;
                }
                var chapters = new int[slots.Length];
                int written = AudiobookOrder.Normalize(slots, kinds, chapters, out chapterTrailer, out _);
                slots = written == chapters.Length ? chapters : chapters[..written];
            }
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
            // "new" and the progress counts are claims about the listener's history: made only once the hydrate settled
            // and succeeded (§6.1 — rows show no state rather than a false one).
            bool trusted = settled && !failed;
            // One episode, one place: the "new since you were here" block shows every fresh episode, so up next never
            // offers one (it takes the next candidates instead).
            var freshAt = new bool[n];
            int freshCount = ShowReaderRules.FreshMask(pcts, published, lastPlayed, trusted, freshAt);
            Span<int> up = stackalloc int[ListenNext.UpNextMax];
            var (resume, upCount) = ListenNext.Pick(pcts, consumption, playing, up, freshAt);
            var visit = ShowVisit.Of(ledger.Played + ledger.InProgress > 0, ledger.ToGo, ledger.InProgress);
            // The new-since rows are something to come back to too: a Returning listener whose only unfinished episodes
            // are the fresh ones still gets the Returning head (hero-less, with its new-since block), never "all caught up".
            var head = ShowReaderRules.HeadOf(answered, settled, failed, visit, resume, upCount + freshCount);

            var statusRule = (Episode.Rules.Status)Math.Clamp(status, 0, 3);
            var view = new int[n];
            int viewCount = ShowReaderRules.View(pcts, statusRule, oldest: order == 1, found, view);
            int capacity = ShowReaderShape.MaxItems(viewCount);
            var shape = new ShowReaderShape.Item[capacity];
            var marks = new Episode.RowMarks[capacity];
            int resumeAt = resume >= 0 ? slots[resume] : 0;
            // RESULTS MODE: while a status filter or a find is on, the head is HIDDEN (never filtered — it is still built
            // from every episode, parity #25) and the list is exactly the answer. Sort alone leaves it shown (parity #26).
            bool results = ShowReaderRules.ResultsMode(statusRule, q);
            // Report 11a: the visit head already SHOWS its episode (the continue hero), so the body must not repeat it
            // as its first row — but only while the head is shown: a find that matches nothing but the hero's episode
            // must list it. Only the Returning head owns one — New's doors and CaughtUp show none.
            int headSlot = ShowReaderRules.HeadSlot(head, resumeAt, headShown: !results);
            // An audiobook has no months (its chapters are a story, not a release calendar): the dates the ITEM LIST
            // sees are empty, so no Group ever opens. Every other rule still reads the real dates.
            var layoutDates = audiobook ? ReadOnlySpan<int>.Empty : published;
            int count = ShowReaderRules.Layout(slots, pcts, layoutDates, view.AsSpan(0, viewCount), lastPlayed, trusted,
                                               shape, marks, headSlot);
            var items = new ReaderItem[count];
            for (int j = 0, row = 0; j < count; j++)
            {
                var it = shape[j];
                if (it.Kind != ShowReaderShape.ItemKind.Row)
                {
                    items[j] = new ReaderItem(it.Kind, it.GroupKey, new Episode.RowItem(default, 0u, marks[j]));
                    continue;
                }
                while (headSlot != -1 && row < viewCount && slots[view[row]] == headSlot) row++;
                int orig = row < viewCount ? view[row++] : -1;
                items[j] = new ReaderItem(it.Kind, it.GroupKey, Episode.RowItem.Of(new Episode(it.Slot), marks[j],
                    audiobook && orig >= 0 ? AudiobookOrder.ChapterNumber(orig, n) : 0));
            }

            var upNext = new int[upCount];
            for (int k = 0; k < upCount; k++) upNext[k] = slots[up[k]];
            var fresh = new int[freshCount];
            for (int i = 0, k = 0; k < freshCount && i < n; i++)
                if (freshAt[i]) fresh[k++] = slots[i];
            int trailer = show.Knows(ShowFields.Facts) && !show.TrailerId.IsEmpty
                          && scope.Episodes.TryGetSlot(show.TrailerId, out int trailerSlot) ? trailerSlot : 0;
            // An audiobook names no trailer on the show: its SAMPLE is a member of the membership, lifted out above.
            if (trailer == 0) trailer = chapterTrailer;
            bool complete = state == EdgeState.Complete;
            int firstSlot = complete && n > 0 ? slots[n - 1] : 0;
            int oldestYear = complete && n > 0 && published[n - 1] > 0
                ? DateTimeOffset.FromUnixTimeSeconds(published[n - 1]).UtcDateTime.Year : 0;

            bool failedNow = edge.IsFailed(slot);
            bool paging = Episode.Rules.Paging(pagingFrom, _pagingVersion, edge.Version(slot), failedNow);
            int local = Episode.Rules.LocalCursorAfter(localAsked, pagingFrom, paging, failedNow);
            int edgeAsked = show.EpisodesAsked;
            int resumeSlot = resumeAt;

            ulong fold = 14695981039346656037UL;
            Mix(ref fold, (uint)head | (results ? 0x100u : 0u));      // results mode hides the head: a fact of what it paints
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
                Filtered = results,
                IsEmpty = viewCount == 0 && answered,
                EmptyShow = q.IsEmpty && Episode.Rules.IsEmptyShow(edgeTotal, n, statusRule),
                // A COMPLETE list never shows the pill, whatever the cursor column says; a partial one gates on the cursor.
                CanLoadMore = Episode.Rules.CanLoadMore(state, edgeAsked, local, edgeTotal),
                Paging = paging,
                NextOffset = Episode.Rules.NextOffset(edgeAsked, local, n),
                LocalAsked = local,
                Head = head,
                HeadShown = !results,
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
    /// tone, the rows' context and the rail's words — everything built once.
    /// <para>MODEL SWAPS. Every `??=`-cached thing here resolves <c>_m</c> (the CURRENT model, re-pointed by
    /// <see cref="ApplyProps"/>) at call time, and <c>_items</c> projects the model's <c>Snap</c> memo — which its owner
    /// keeps stable for its own lifetime (Show.Pane.cs's header). So the derived state here does NOT freeze on the first
    /// model. The host is nonetheless KEYED per show by both its owners (<c>"showpane:reader:"+slot</c> / the route key)
    /// and stays that way ON PURPOSE: the scroll offset, the <see cref="ItemsViewController"/>, the bulk
    /// <see cref="EpisodeSelection"/>, the sticky month, the year strip and the persistent rail slot are per-show
    /// IDENTITY, not derived state — a different show must start at the top with nothing selected.</para></summary>
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
        /// <summary>The list's extents are ANALYTIC (<see cref="ExtentOf"/>): an unmeasured item seeds at its own kind's
        /// height instead of one global estimate (the head is ~500, the foot ~400, a month header 42), and corrects to its
        /// measured extent on realize — so a restored scroll position and the scroll anchor start nearly right.</summary>
        readonly RepeatLayout _layout;
        readonly Func<int, float> _extentOf;

        internal Memo<bool>? Narrow;
        /// <summary>Which rung of the toolbar's collapse ladder the toolbar's OWN measured width is on
        /// (<see cref="ShowToolbarLayout.For"/>) — the rail is ONE row at every one of them.</summary>
        internal Memo<ToolbarStage>? Toolbar;
        /// <summary>The toolbar's arranged width (its root's bounds, gutters included) and what its two rails measured
        /// (<see cref="Controls.Words.Measured"/>) — the inputs of <see cref="ShowToolbarLayout.For"/>. Plain signals the
        /// stage memo reads: a measure re-runs the memo, and the toolbar re-renders only when the STAGE moves.</summary>
        internal readonly Signal<float> ToolbarWidth = new(0f);
        readonly Signal<ToolbarNeeds> _needs = new(default);
        float[] _filterWordW = [];
        ToolbarStage _stageShown = ToolbarStage.Full;
        internal readonly Controls.Words.Measured OnFiltersMeasured, OnSortMeasured;
        internal readonly Action<RectF> OnToolbarBounds;
        /// <summary>"More like this": ONE request per show visit, owned by the reader (which lives as long as the show's
        /// page does) — never by the foot, which the list remounts whenever its items move (a keystroke, a filter).</summary>
        internal Resource<Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>> Similar;
        /// <summary>The list rows' context (it carries <see cref="EpisodeSelection"/>) and the HEAD's — the visit head's
        /// "new since" rows render over a fixed scope that owns no selection state, so they never wear a check lane.</summary>
        internal Episode.RowContext? RowCtx, HeadRowCtx;
        internal Controls.Words.Word[]? FilterWords, SortWords;
        /// <summary>The show's tone as a live read (a memo, so a bind re-fires only when the colour moves).</summary>
        internal readonly Func<ColorF> Tone;
        /// <summary>The narrow toolbar's collapsed find box (report 4a) — session-only, like the query itself.</summary>
        internal readonly Signal<bool> FindOpen = new(false);
        /// <summary>The "nothing matches" reset: the model's reset (filter back to all, find cleared) AND the narrow find
        /// field closed — a reset that leaves an empty field open is a dangling affordance.</summary>
        internal readonly Action OpenFind, CloseFind, ToggleSelecting, ResetView;
        internal readonly Func<bool> IsSelecting;
        /// <summary>Bulk selection over the reader's rows (report 5). Armed by the toolbar's select toggle; the bar
        /// appears only above zero.</summary>
        internal readonly EpisodeSelection Selection;

        readonly Func<bool> _narrowOf, _pending, _failed;
        readonly Func<ToolbarStage> _toolbarStageOf;
        readonly Action _syncSelectedWord;
        readonly Func<int, Element> _selectionCommands;
        readonly Func<IReadOnlyList<Episode>> _selectedEpisodes;
        readonly Func<int> _selectedOf;
        Memo<int>? _selected;
        readonly ItemsViewController _ctl = new();
        /// <summary>The episode list's scroll handle: the pinned month is re-read off every offset it publishes.</summary>
        readonly ScrollHandle _scroll = new();
        readonly Action _watchSticky;
        string _scrollScope = "";   // the tab (Shell.PageScrollScope), composed onto the restore key
        // the date rail (report 4b): the month groups projected out of the snapshot's items, their distinct years, and
        // the month pinned at the list's top
        JumpGroup[] _groups = new JumpGroup[ShowDateIndex.MaxGroups];
        int _groupCount;
        int[] _years = [];
        readonly Signal<int> _stickyMonth = new(-1);
        readonly Action<int> _jumpYear;
        readonly Func<int[]> _yearsOf;
        Memo<int[]>? _yearsMemo;
        readonly Func<ColorF> _toneOf;
        readonly Func<BoundItemScope<ReaderItem>, Element> _template;
        readonly Func<int, int> _contentType;
        readonly Func<Element> _content, _shimmer, _failedView;
        readonly Action _retry, _demandRows;
        readonly Action<Episode> _play;
        static readonly Func<Episode, ContextMenuModel?> s_menu = static e => Episode.Menu(e, new Episode.MenuOptions(ShowGoToShow: false));

        public ReaderHost()
        {
            _narrowOf = () => ShowReaderRules.Narrow(_width?.Value ?? 0f);
            _toolbarStageOf = ToolbarStageOf;
            _syncSelectedWord = SyncSelectedWord;
            OnFiltersMeasured = FiltersMeasured;
            OnSortMeasured = SortMeasured;
            OnToolbarBounds = r => { if (MathF.Abs(r.W - ToolbarWidth.Peek()) > 0.5f) ToolbarWidth.Value = r.W; };
            _extentOf = ExtentOf;
            _layout = RepeatLayout.Extents(_extentOf, ItemEstimate);
            Selection = new EpisodeSelection(EpisodeAt, () => _m?.Peek().ItemCount ?? 0, IsRowAt);
            IsSelecting = () => Selection.Selecting.Value;
            ToggleSelecting = () => Selection.Arm(!Selection.Selecting.Peek());
            OpenFind = () => FindOpen.Value = true;
            CloseFind = () => { FindOpen.Value = false; if (_m is { } m) m.Find.Value = ""; };
            ResetView = () => { _m?.ResetView(); FindOpen.Value = false; };
            _selectedEpisodes = Selection.Selected;
            _selectionCommands = fit => Track.SelectionLane(fit, SelectionLaneArgs.ForEpisodes(
                Selection.SelectedCount, _selectedEpisodes, Selection.Exit, Selection.SelectAll));
            _selectedOf = () => Selection.SelectedCount;
            _jumpYear = JumpToYear;
            _watchSticky = () =>
            {
                _ = _scroll.Offset.Value;   // re-runs on every published offset; the write is value-gated
                _stickyMonth.SetIfChanged((int)MonthAtTop());
            };
            _yearsOf = YearsOf;
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
            _demandRows = DemandRows;
        }

        internal ReaderModel Model => _m!;

        /// <summary>The reader's measured width (the head lays its columns out from it); <paramref name="subscribe"/> false
        /// is for a builder that renders what its stamp was computed from.</summary>
        internal float MeasuredWidth(bool subscribe) => _width is null ? 0f : subscribe ? _width.Value : _width.Peek();

        // ── the measured toolbar (ShowToolbarLayout) ─────────────────────────────────────────────────────────────────

        /// <summary>The stage memo's body: the toolbar's own width against what its rails measured, with the stage on
        /// screen as the hysteresis' "previous". The memo is equality-gated, so the toolbar re-renders only when the
        /// stage actually moves — never per resize frame.</summary>
        ToolbarStage ToolbarStageOf()
        {
            var stage = ShowToolbarLayout.For(ToolbarWidth.Value, _needs.Value, ShowReaderRules.Narrow(_width?.Value ?? 0f), _stageShown);
            _stageShown = stage;
            return stage;
        }

        /// <summary>The filter rail's measure: its natural width, and each chip's width — the selected one is what the
        /// last stage's menu button is sized from.</summary>
        void FiltersMeasured(float total, float[] words)
        {
            if (_filterWordW.Length != words.Length) _filterWordW = new float[words.Length];
            Array.Copy(words, _filterWordW, words.Length);
            _needs.Value = _needs.Peek() with { Filters = total, SelectedWord = WordWidth(_m?.Status.Peek() ?? 0) };
        }

        void SortMeasured(float total, float[] words) => _needs.Value = _needs.Peek() with { Sort = total };

        float WordWidth(int word) => (uint)word < (uint)_filterWordW.Length ? _filterWordW[word] : 0f;

        /// <summary>Another chip became the selected one: the menu button's width follows it.</summary>
        void SyncSelectedWord()
        {
            _ = _props.Value;                                      // a model swap re-runs this for the new model
            float w = WordWidth(_m?.Status.Value ?? 0);
            var needs = _needs.Peek();
            if (needs.SelectedWord != w) _needs.Value = needs with { SelectedWord = w };
        }

        // ── the list's extents ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One item's SEEDED extent (<see cref="ShowReaderRules.SeedExtent"/> for its kind). A peek: the layout
        /// calls this on a seed, a resize or a splice — never per frame, never inside a tracked scope.</summary>
        float ExtentOf(int index)
        {
            var s = _m?.Peek();
            if (s is null || (uint)index >= (uint)s.ItemCount) return ItemEstimate;
            var it = s.Items[index];
            return ShowReaderRules.SeedExtent(it.Kind, s.Head, s.HeadShown, (it.Row.Marks & Episode.RowMarks.NoRule) != 0, s.Fresh.Length);
        }

        /// <summary>The always-on truthfulness line (renderer == estimator — the Artist reader's <c>CheckChrome</c>
        /// idiom). An item whose height is ARITHMETIC (<see cref="ShowReaderRules.ExactExtent"/>: the month header, the
        /// "episodes" header) reports its ARRANGED height here, and when that differs by more than half a DIP from the
        /// number the extent seed reads for the same item, one <c>show.reader.extent</c> line says by exactly how much; no
        /// line at all IS the proof. Deduped per slot on (declared, measured); an unarranged item (H 0) is not a
        /// measurement. Nothing is allocated unless it logs, and it logs only on a defect.</summary>
        internal void CheckExtent(ShowReaderShape.ItemKind kind, bool noRule, float measured, ref float loggedDeclared, ref float loggedMeasured)
        {
            var s = _m?.Peek();
            float declared = s is null ? float.NaN : ShowReaderRules.ExactExtent(kind, noRule, s.IsEmpty);
            if (!ShowReaderRules.ExtentDisagrees(declared, measured)) return;
            if (declared == loggedDeclared && measured == loggedMeasured) return;
            loggedDeclared = declared;
            loggedMeasured = measured;
            Log.Event(WaveeLogLevel.Warning, "ui", "show.reader.extent", "Show reader item height disagrees with its extent",
                null, -1, null,
                WaveeLogField.Of("item", kind.ToString()), WaveeLogField.Of("declared", declared),
                WaveeLogField.Of("measured", measured), WaveeLogField.Of("delta", measured - declared));
        }

        /// <summary>THE ROWS' DEMAND lives on the reader, not on whoever mounts it. Auto-tracked: as the membership lands
        /// (a page, a refresh, a model swap), ask for what a row paints — <see cref="ShowReaderRules.RowDemand"/> over the
        /// WHOLE resident membership, never a visible window; the planner dedupes. It sat on the route page's host until
        /// 2026-09-20, and the library pane (Show.Pane.cs), which mounts this same reader, never asked: a show whose rows
        /// nothing else had made resident shimmered forever there — nothing was coming, and nothing could fail either.</summary>
        void DemandRows()
        {
            _ = _props.Value;                                      // a model swap re-runs this for the new show
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.ShowEpisodes.Changed.Value;
            if (_m is not { } m || !m.Subject.IsValid || !scope.Shows.TryGetSlot(m.Subject.Id, out int show)) return;
            var slots = scope.Edges.ShowEpisodes.Targets(show);
            if (slots.Length > 0) Entities.Ensure(scope.Episodes, slots, (uint)ShowReaderRules.RowDemand);
        }

        // ── the selection's item space (report 5): the LIST's index space is the snapshot's, prefix included ──
        bool IsRowAt(int index)
        {
            var s = _m?.Peek();
            return s is not null && (uint)index < (uint)s.ItemCount && s.Items[index].Kind == ShowReaderShape.ItemKind.Row;
        }

        Episode EpisodeAt(int index)
        {
            var s = _m?.Peek();
            if (s is null || (uint)index >= (uint)s.ItemCount) return default;
            var it = s.Items[index];
            return it.Kind == ShowReaderShape.ItemKind.Row ? it.Row.Episode : default;
        }

        // ── the date rail (report 4b) ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The month groups of the CURRENT item list, projected through the shared
        /// <see cref="JumpIndex"/> kernel (the A–Z strip's kernel), then folded to their distinct years.</summary>
        int[] YearsOf()
        {
            var s = _m?.Read() ?? ReaderSnap.Empty;
            _groupCount = JumpIndex.Project<ReaderItem>(s.Items.AsSpan(0, s.ItemCount), s_isGroup, s_groupJumpKey, _groups);
            var years = new int[_groupCount];
            int n = ShowDateIndex.Years(_groups.AsSpan(0, _groupCount), years);
            return n == years.Length ? years : years[..n];
        }

        void JumpToYear(int year)
        {
            int flat = ShowDateIndex.ResolveYear(_groups.AsSpan(0, _groupCount), year);
            if (flat >= 0) _ctl.StartBringItemIntoView(flat, alignmentRatio: 0f, animate: true);
        }

        /// <summary>The month pinned at the list's top: the item under the viewport's top edge, read through the
        /// controller (the list's extents are variable, so there is no offset table to index).</summary>
        long MonthAtTop()
        {
            var s = _m?.Peek();
            if (s is null || s.ItemCount == 0 || !_ctl.TryGetItemIndex(0f, 0f, out int i)) return -1;
            return (uint)i < (uint)s.ItemCount ? s.Items[i].GroupKey : -1;
        }

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
            _scrollScope = UseContext(Shell.PageScrollScope);
            _width = UseMeasuredWidth(4f);
            Narrow = UseComputed(_narrowOf);
            Toolbar = UseComputed(_toolbarStageOf);
            _tone = UseComputed(_toneOf);
            _selected = UseComputed(_selectedOf);
            _yearsMemo = UseComputed(_yearsOf);
            UseEffect(_demandRows);
            UseSignalEffect(_watchSticky);
            UseSignalEffect(_syncSelectedWord);
            var m = Model;
            // "More like this" is the READER's request, not the foot's: the list remounts the foot whenever its items move
            // (every keystroke of the find, every filter), and a request owned by the foot went out again each time — eleven
            // identical POSTs in seven seconds of typing. Keyed by the show and the scope, so it is asked once per visit.
            string subjectText = m.SubjectText;
            Similar = UseResource(ct => PodcastReaderUI.RecommendationsOf(subjectText, show: true, ct),
                PodcastReaderUI.RecommendationsSeed, (subjectText, (int)Entities.ScopeEpoch.Value));
            HeadRowCtx ??= new Episode.RowContext
            {
                Tone = Tone, Play = _play, Menu = s_menu, Overlay = Controls.IsNullOverlay(overlay) ? null : overlay,
            };
            RowCtx ??= new Episode.RowContext
            {
                Tone = Tone, Play = _play, Menu = s_menu, Overlay = Controls.IsNullOverlay(overlay) ? null : overlay,
                Selection = Selection.Rows,
            };
            // The words are frozen at the rail's mount (their positions ARE Episode.Rules.Status / the order); the counts
            // are live binds over the snapshot, empty until progress is trusted. The thunks read `_m` — the CURRENT
            // model — and never the `m` local: a `??=` cache that closed over the local would freeze the counts on the
            // first model this host ever saw, which is exactly the trap Show.Pane.cs's header describes.
            FilterWords ??= new Controls.Words.Word[]
            {
                new(Loc.Bind(Strings.Podcast.Filter.All), Prop.Of(() => _m?.Read().CountAll ?? "")),
                new(Loc.Bind(Strings.Podcast.Filter.Unplayed), Prop.Of(() => _m?.Read().CountUnplayed ?? "")),
                new(Loc.Bind(Strings.Podcast.Filter.InProgress), Prop.Of(() => _m?.Read().CountProgress ?? "")),
                new(Loc.Bind(Strings.Podcast.Filter.Played), Prop.Of(() => _m?.Read().CountPlayed ?? "")),
            };
            SortWords ??= new Controls.Words.Word[] { new(Loc.Bind(Strings.Podcast.Sort.Newest)), new(Loc.Bind(Strings.Podcast.Sort.Oldest)) };
            _items ??= BoundItems.Project(m.Snap!, static s => s.ItemCount, static (s, i) => s.Items[i], default(ReaderItem));
            int selected = _selected!.Value;
            // owner decision D3: 2+ selected rows always show the bar; ONE only while multi-select is armed
            int barCount = SelectionBarRules.Visible(selected, Selection.Selecting.Value) ? selected : 0;
            var years = _yearsMemo!.Value;
            Element list = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ZStack = true,
                Children =
                [
                    new SkelRegionEl(
                        Pending: _pending, Failed: _failed, Content: _content, ShimmerSource: _shimmer, OnFailed: _failedView,
                        Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false),
                    // the month pinned under the rail, and the selection bar docked at the list's foot
                    StickyMonth(_stickyMonth, Narrow.Value ? ReaderPadNarrow : ReaderPad),
                    Controls.SelectionBar(barCount, _selectionCommands, standalone: true, bottomPadding: BottomReserve, minCount: 1),
                ],
            };
            // The strip's 30 DIP are ALWAYS reserved: a filter that leaves one year takes the strip's rows away, and a list
            // that widened by 30 for it (then narrowed again on the way back) reflowed every row sideways — and every
            // width-derived thing in the head with it.
            return new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
                Children = [list, years.Length > 1 ? YearStrip(years, _stickyMonth, _jumpYear) : YearStripReserve()],
            };
        }

        ListOptions<ReaderItem> OptionsFor(string routeKey)
        {
            routeKey = _scrollScope + routeKey;
            if (_options is null || !string.Equals(_optionsKey, routeKey, StringComparison.Ordinal))
            {
                _optionsKey = routeKey;
                _options = new ListOptions<ReaderItem>
                {
                    // Report 5: Extended, so a row can be selected — the ROWS own the check lane and the selected fill
                    // (Episode.ReaderRowContent), which is why the selector visual stays None.
                    SelectionMode = ItemsSelectionMode.Extended,
                    Selection = Selection.Model,
                    Selector = SelectorVisual.None,
                    Controller = _ctl,
                    Grow = 1f,
                    ContentType = _contentType,                        // recycle pools per kind: a row never rebinds as a head
                    PersistentPrefixCount = ShowReaderShape.Prefix,
                    Scroll = new ScrollOptions
                    {
                        ScrollKey = "episodes:" + routeKey,             // the position survives a swap
                        AutoEdgeFade = false,                           // the rail's clip band owns the top feather
                        ItemClipTopInset = RailHeight,
                        ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
                        Handle = _scroll,
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
        readonly Action<RectF> _onBounds;
        float _loggedDeclared, _loggedMeasured;

        public ReaderSlot(ReaderHost host, BoundItemScope<ReaderItem> scope)
        {
            _host = host;
            _row = scope.Row;
            var item = scope.Item;
            _item = item;
            _kindOf = () => item.Value.Kind;
            _rowOf = () => item.Value.Row;
            _onBounds = OnBounds;
        }

        /// <summary>This slot's ARRANGED height, checked against its extent (<see cref="ReaderHost.CheckExtent"/>). The
        /// kind and the marks are read at the moment of the report — a recycled slot reports for the item it holds now.</summary>
        void OnBounds(RectF r)
        {
            var it = _item.Peek();
            _host.CheckExtent(it.Kind, (it.Row.Marks & Episode.RowMarks.NoRule) != 0, r.H, ref _loggedDeclared, ref _loggedMeasured);
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
                OnBoundsChanged = _onBounds,
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
        readonly Action _follow, _resume, _first, _latest;

        public PrimarySlot()
        {
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
            else if (m.Peek().Show is { IsValid: true } show) Playback.PlayOrToggleContext(show.Id);
        }

        public override Element Render()
        {
            var p = UseProps<SlotProps>();
            _m = p.Model;
            var f = _m.Facts?.Value ?? default;
            // Reports 11c/11d: the APP accent (never the show tone) and a label that WRAPS — Workstream B's
            // Controls.PrimaryButton(wrap: true), not a bespoke pill (was Show.PrimaryPill).
            Element pill = f.Primary switch
            {
                ShowReaderRules.Primary.Follow => Controls.PrimaryButton(Loc.Get(Strings.Artist.Follow), _follow, Tok.AccentDefault, Icons.Add, wrap: true),
                ShowReaderRules.Primary.Resume => Controls.PrimaryButton(
                    Loc.Get(Strings.Podcast.Resume) + " · " + Strings.Podcast.Left(Episode.DurationWords(f.ResumeLeft)), _resume, Tok.AccentDefault, wrap: true),
                ShowReaderRules.Primary.PlayFirst => Controls.PrimaryButton(Strings.Podcast.Badge.Episode(FormatCache.Int(f.FirstNumber)), _first, Tok.AccentDefault, wrap: true),
                _ => Controls.PrimaryButton(Loc.Get(Strings.Podcast.Latest), _latest, Tok.AccentDefault, wrap: true),
            };
            if (f.Primary != ShowReaderRules.Primary.Follow) return pill;
            Element ghost = f.Ghost == ShowReaderRules.Primary.PlayFirst
                ? Button.Create(Strings.Podcast.Badge.Episode(FormatCache.Int(f.FirstNumber)), _first, ButtonAppearance.Standard, glyph: Icons.Play)
                : Button.Create(Loc.Get(Strings.Podcast.Latest), _latest, ButtonAppearance.Standard, glyph: Icons.Play);
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
                    Children = [Ui.Caption(Loc.Get(Strings.Podcast.ProgressUnavailable)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f }],
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
                    new SpanTextEl(line) { Size = Ui.Caption("").Size, LineHeight = Detail.RailLayout.LedgerLineHeight, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
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
                    Design.Type.DenseTitle(average) with { Color = Tok.TextPrimary, MaxLines = 1 },
                    Design.Type.DenseMeta(count) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
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

    /// <summary>What the reader asks for every resident member, from whichever host mounts it (the route page, the
    /// library pane): what a row PAINTS (<see cref="EpisodeFields.Row"/>), its two-line blurb
    /// (<see cref="EpisodeFields.About"/>) and its progress (<see cref="EpisodeFields.Progress"/> — no route serves it
    /// per episode, so the plan seals it and the login hydrate fills it; asking is what keeps a later hydrate from being
    /// asked again). One constant, so the demand and the reveal (<c>Episode.RevealState</c>) cannot drift apart.</summary>
    public const EpisodeFields RowDemand = EpisodeFields.Row | EpisodeFields.About | EpisodeFields.Progress;

    /// <summary>A width of 0 is "not measured yet": the wide arm.</summary>
    public static bool Narrow(float width) => width > 0f && width < NarrowBelow;

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

    // ── results mode ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>RESULTS MODE: a status filter or a find is on, so the list is the answer to a question and the visit head
    /// (hero, up next, new since) is HIDDEN — not filtered: it is still built from every episode (parity #25), it just is
    /// not painted, because a ~500-DIP head above one result reads as part of the results and pushes them below the fold.
    /// Sort alone never counts (parity #26): newest/oldest reorders the same list. <paramref name="find"/> is trimmed here,
    /// like the find itself (<see cref="Matches"/>).</summary>
    public static bool ResultsMode(Episode.Rules.Status status, ReadOnlySpan<char> find)
        => status != Episode.Rules.Status.All || !find.Trim().IsEmpty;

    /// <summary>The ONE episode the body must not repeat as a row: the continue hero's, and only while the head is SHOWN
    /// (<paramref name="headShown"/> = not <see cref="ResultsMode"/>) and is the Returning head that owns one. -1 = none.
    /// A find that matches nothing but the hero's episode lists it as a row; the head is not there to show it.</summary>
    public static int HeadSlot(Head head, int resumeSlot, bool headShown)
        => headShown && head == Head.Returning && resumeSlot > 0 ? resumeSlot : -1;

    /// <summary>The episodes the "new since you were here" block shows, as a mask PARALLEL to the pcts (and the
    /// <paramref name="publishedAt"/> dates): <see cref="ShowLedger.IsFresh"/>, made only once progress is
    /// <paramref name="trusted"/> (settled and succeeded — "new" is a claim about the listener's history). Returns how
    /// many are set. Up next (<see cref="ListenNext.Pick"/>'s <c>skip</c>) never offers one of them.</summary>
    public static int FreshMask(ReadOnlySpan<float> pcts, ReadOnlySpan<int> publishedAt, int lastPlayedAt, bool trusted, Span<bool> into)
    {
        int n = 0;
        for (int i = 0; i < pcts.Length && i < into.Length; i++)
        {
            bool fresh = trusted && ShowLedger.IsFresh(pcts[i], i < publishedAt.Length ? publishedAt[i] : 0, lastPlayedAt);
            into[i] = fresh;
            if (fresh) n++;
        }
        return n;
    }

    // ── the up-next card's lead ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What leads an up-next card: the episode's numeral, its 40 × 40 cover, or nothing at all.</summary>
    public enum MiniLeadKind : byte { Numeral, Art, None }

    /// <summary>The lead for ONE row of up-next cards, decided from the row as a whole so a row NEVER mixes a numeral on
    /// one card with a cover (or a blank) on the next: every card numbered → numerals (the design, a serial's); otherwise
    /// every card has a cover → covers (the number, if any, moves into the meta line, like a list row); otherwise no lead
    /// column at all — never a blank reserved numeral gutter (the "missing image" on a show whose newest episodes are
    /// unnumbered). An episode that has not loaded yet is neutral: the caller passes what it knows.</summary>
    public static MiniLeadKind MiniLead(bool allNumbered, bool allHaveArt)
        => allNumbered ? MiniLeadKind.Numeral : allHaveArt ? MiniLeadKind.Art : MiniLeadKind.None;

    // ── the head's columns ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The reader's gutters (24, 16 under the narrow arm) and the date strip's reserved column.</summary>
    public const float Pad = 24f, PadNarrow = 16f, YearStripWidth = 30f;
    /// <summary>The head's cards: the door's and the up-next card's minimum width, the gap between them, and how many
    /// columns at most (the up-next block holds three, the doors three).</summary>
    public const float DoorMin = Controls.DoorMinWidth, MiniMin = 220f, TileGap = 12f;
    public const int TileColumns = 3;

    /// <summary>How many EQUAL columns fit <paramref name="width"/> DIP: the prototype's <c>repeat(auto-fit, minmax(min,
    /// 1fr))</c> as arithmetic — floor((width + gap) / (min + gap)), at least 1, at most <paramref name="max"/>. The head
    /// lays its cards out in this many columns instead of letting a wrapping row decide (a wrap row that narrowed with
    /// clean contents overflowed into a line nobody had counted and painted over the next section). A width of 0 is "not
    /// measured yet" and reads the widest arm.</summary>
    public static int Columns(float width, float min, float gap, int max)
    {
        max = Math.Max(1, max);
        if (!(width > 0f)) return max;
        return Math.Clamp((int)MathF.Floor((width + gap) / (min + gap)), 1, max);
    }

    /// <summary>The head's CONTENT width inside a reader <paramref name="readerWidth"/> wide: both gutters and the date
    /// strip's reserve (always there, see the strip) taken off. 0 = not measured.</summary>
    public static float HeadWidth(float readerWidth)
        => readerWidth > 0f ? MathF.Max(0f, readerWidth - YearStripWidth - 2f * (Narrow(readerWidth) ? PadNarrow : Pad)) : 0f;

    /// <summary>The door and up-next column counts for a reader <paramref name="readerWidth"/> wide.</summary>
    public static (int Doors, int Minis) HeadColumns(float readerWidth)
    {
        float w = HeadWidth(readerWidth);
        return (Columns(w, DoorMin, TileGap, TileColumns), Columns(w, MiniMin, TileGap, TileColumns));
    }

    // ── the list's extents ───────────────────────────────────────────────────────────────────────────────────────────
    //
    // What the list ASSUMES an item is before it is measured. The seeds are estimates (a head's height is text, the foot's
    // is a shelf); the chrome whose height is ARITHMETIC — the rail, the month header, the "episodes" header — is exact,
    // and `Show.UI.cs` states those heights on the elements themselves so the number here and the number there cannot drift
    // (ReaderHost.CheckExtent logs the day they do).

    /// <summary>The sticky toolbar plane (<c>Show.RailHeight</c>).</summary>
    public const float RailExtent = 48f;
    /// <summary>The "episodes N" header at rest: the 30-DIP pivot line and 12 under it.</summary>
    public const float HeaderLine = 30f, HeaderFoot = 12f, HeaderExtent = HeaderLine + HeaderFoot;
    /// <summary>A month header: 14 of air above (none right under the "episodes" head), a 24-DIP line, 4 under it.</summary>
    public const float GroupAir = 14f, GroupLine = 24f, GroupFoot = 4f;
    /// <summary>A row before it is measured (title + blurb + meta + the padding of a 64-DIP art tile).</summary>
    public const float RowSeed = 96f;
    /// <summary>The head's seeds by arm. Pending: 12 + the 30-DIP pivot + 12 + the 124-DIP hero seed + 26. New: the doors
    /// (a 132-DIP card under a pivot) and the full about. Returning: the pivot, the hero, one row of up-next cards and the
    /// 26-DIP section gap; new since adds its header and one <see cref="RowSeed"/> per fresh episode. CaughtUp: a pivot and
    /// a line. Unavailable: one quiet line.</summary>
    public const float PendingSeed = 204f, NewSeed = 420f, ReturningSeed = 278f, NewSinceChrome = 68f,
                       CaughtUpSeed = 90f, UnavailableSeed = 58f;
    /// <summary>The foot: the load-more pill and the "More like this" shelf over the dock reserve.</summary>
    public const float FootSeed = 400f;

    /// <summary>A month header's height: its air only when it does not sit right under the "episodes" head.</summary>
    public static float GroupExtent(bool noRule) => (noRule ? 0f : GroupAir) + GroupLine + GroupFoot;

    /// <summary>What an UNMEASURED item of <paramref name="kind"/> is assumed to be (DIP) — the list's per-kind seed.
    /// A hidden head (results mode) is 0: it paints nothing.</summary>
    public static float SeedExtent(ShowReaderShape.ItemKind kind, Head head, bool headShown, bool noRule, int fresh) => kind switch
    {
        ShowReaderShape.ItemKind.Rail => RailExtent,
        ShowReaderShape.ItemKind.Head => !headShown ? 0f : head switch
        {
            Head.New => NewSeed,
            Head.Returning => ReturningSeed + (fresh > 0 ? NewSinceChrome + fresh * RowSeed : 0f),
            Head.CaughtUp => CaughtUpSeed,
            Head.Unavailable => UnavailableSeed,
            _ => PendingSeed,
        },
        ShowReaderShape.ItemKind.Header => HeaderExtent,
        ShowReaderShape.ItemKind.Group => GroupExtent(noRule),
        ShowReaderShape.ItemKind.Foot => FootSeed,
        _ => RowSeed,
    };

    /// <summary>The kinds whose arranged height is ARITHMETIC, and that height; <see cref="float.NaN"/> for a kind whose
    /// height is its content's (a row, the head, the foot — and the "episodes" header while it carries the empty arm).</summary>
    public static float ExactExtent(ShowReaderShape.ItemKind kind, bool noRule, bool headerEmpty) => kind switch
    {
        ShowReaderShape.ItemKind.Rail => RailExtent,
        ShowReaderShape.ItemKind.Group => GroupExtent(noRule),
        ShowReaderShape.ItemKind.Header when !headerEmpty => HeaderExtent,
        _ => float.NaN,
    };

    /// <summary>Does an ARRANGED height disagree with the declared one by more than half a DIP? An unarranged item
    /// (<paramref name="measured"/> 0) is not a measurement, and an undeclared extent (NaN) has nothing to disagree with.</summary>
    public static bool ExtentDisagrees(float declared, float measured)
        => measured > 0f && !float.IsNaN(declared) && MathF.Abs(measured - declared) > 0.5f;

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
                             Span<ShowReaderShape.Item> items, Span<Episode.RowMarks> marks, int skipSlot = -1)
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
        int count = ShowReaderShape.Build(viewSlots, viewDates, canLoadMore: true, hasSimilar: false, items, skipSlot);
        int row = 0;
        for (int j = 0; j < count && j < marks.Length; j++)
        {
            var kind = items[j].Kind;
            var prev = j > 0 ? items[j - 1].Kind : ShowReaderShape.ItemKind.Rail;
            var m = Episode.RowMarks.None;
            // step over the head's episode in LOCKSTEP with Build, so every mark stays on the row it describes
            while (skipSlot != -1 && row < v && slots[view[row]] == skipSlot) row++;
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
