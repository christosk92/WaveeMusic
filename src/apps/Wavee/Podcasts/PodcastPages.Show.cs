// ── Podcasts/PodcastPages.Show.cs — the podcast show page's core host (podcast pages v2, concept winui-podcast-v2 "Show") ─────────────────────────
//
// The host owns everything the show page decides; a theme draws `ViewKeys.PodcastShowPage` from ONE snapshot (`PodcastShowView`) and the page's cells.
//
//   PodcastShowScreen (keyed per route by the router)
//   ├─ demand   Entities.Ensure(show, All) · the episode edge (first page, then Load more) · the rows' facts · the trailer row · "More like this"
//   ├─ cells    filter · query · oldest-first · selected · expanded · pane tab        (signals; written only by the verbs)
//   ├─ snapshot ShowModel.Compute (ONE UseComputed, a stable delegate): the head, the smart action and its menu, the rows (state at rest), the
//   │           counts, the groups ("New since your last visit", then years; a serial's one "In order"), the jump years, HasMore, the similar cards
//   ├─ pane     the selected episode's chapters (a ChapterResources lease, up to five around the current one)
//   └─ Slots.View(ViewKeys.PodcastShowPage, props built ONCE)
//
// THE SMART ACTION ("The smart action, state by state"): an episode in progress → Resume (the playing one, else the most recently played); a serial →
// Play part N (the first unplayed part in order; "Start at the beginning" for part 1, "Next in order" after it); an episodic show with unplayed
// episodes → Play newest (the newest unplayed; "New" when it came out after the last play); all played → Play latest again (no accent) with the
// cadence's next likely date.
//
// THE VISIT: Wavee stores no last visit yet (the data lane's `PodcastPages.Open/Leave`), so "new since your last visit" is "published after the show's
// last play" (`ShowLedger.IsFresh`): a first visit marks nothing new.

using System.Collections.Immutable;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>The show page's host: the demand, the snapshot, the cells and the verbs.</summary>
[CoreHost]
public sealed class PodcastShowScreen : Component
{
    ShowModel? _m;
    PodcastShowPageProps? _props;
    ChapterResources.Lease? _lease;
    ChapterResources.Identity _leaseKey;
    readonly Signal<Loadable<Spotify.Podcasts.Page<Spotify.Podcasts.Chapter>>?> _chapters = new(null);
    Func<PodcastPaneChapters>? _computeChapters;
    Action? _syncLease, _dispose;
    Func<Action>? _cleanup;

    public override Element Render()
    {
        var hp = UseProps<PodcastPages.HostProps>();
        var m = _m ??= new ShowModel(hp.Subject);
        uint epoch = Entities.ScopeEpoch.Value;
        Controls.Library ??= User.LibrarySeam;
        _computeChapters ??= ComputeChapters;
        _syncLease ??= SyncChapterLease;
        _dispose ??= () =>
        {
            _lease?.Dispose();
            _lease = null;
            if (_m is { Subject.IsValid: true } model) PodcastPages.Leave(Entities.Show(model.Subject));
        };
        _cleanup ??= () => _dispose;
        string subject = m.SubjectText;
        var similar = UseResource(ct => PodcastReaderUI.RecommendationsOf(subject, show: true, ct), s_similarSeed, DepKey.From((int)epoch));
        m.Similar = similar.Loadable;
        m.View = UseComputed(m.ComputeView);
        m.Playing = UseComputed(m.ComputePlaying);
        m.PaneChapters = UseComputed(_computeChapters);
        UseEffect(m.Demand, DepKey.From((int)epoch));
        UseEffect(m.DemandRows);
        UseEffect(m.DemandTrailer);
        UseEffect(m.SeedSelection);
        UseEffect(_syncLease);
        UseEffect(_cleanup, DepKey.Empty);
        UseActivation(onActivated: m.Reopen);

        _props ??= new PodcastShowPageProps
        {
            RouteKey = hp.RouteKey,
            View = m.View,
            Filter = m.Filter,
            SetFilter = m.SetFilter,
            Query = m.QueryCell,
            Oldest = m.Oldest,
            ToggleSort = m.ToggleSort,
            Selected = m.Selected,
            Select = m.Select,
            Expanded = m.Expanded,
            Expand = m.Expand,
            PaneTab = m.PaneTab,
            SetPaneTab = m.SetPaneTab,
            Playing = m.Playing,
            Play = m.Play,
            PlaySmart = m.PlaySmart,
            PaneChapters = m.PaneChapters,
            Rate = null,
            Open = m.Open,
            ToggleSaved = m.ToggleSaved,
            PlayNext = m.PlayNext,
            MarkFreshPlayed = m.MarkFreshPlayed,
            ClearFilter = m.ClearFilter,
            LoadMore = m.LoadMore,
            ToggleFollow = m.ToggleFollow,
            PlayTrailer = m.PlayTrailer,
            OpenUri = m.OpenUri,
            RowMenu = m.RowMenu,
            ShowMenu = m.ShowMenu,
            Share = m.Share,
            OpenKind = m.OpenKind,
            CardMenu = m.CardMenu,
            PlayCard = m.PlayCard,
        };
        return Slots.View(ViewKeys.PodcastShowPage, _props);
    }

    static readonly Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation> s_similarSeed = new([], "", 0, "", 0);

    /// <summary>The pane's chapter lease follows the selected episode (auto-tracked on the selection and the scope).</summary>
    void SyncChapterLease()
    {
        var m = _m;
        if (m is null) return;
        string? id = m.Selected.Value;
        uint epoch = Entities.ScopeEpoch.Value;
        var uri = id is null ? default : EntityUri.Parse(id);
        if (!uri.IsValid)
        {
            _lease?.Dispose();
            _lease = null;
            _chapters.Value = null;
            return;
        }
        var key = new ChapterResources.Identity(epoch, Entities.Current.Key.Account, uri.Id);
        if (_lease is not null && key == _leaseKey) return;
        _lease?.Dispose();
        _leaseKey = key;
        _lease = ChapterResources.Shared.Acquire(key);
        _lease.Start();
        _chapters.Value = _lease.Data;
    }

    /// <summary>Up to <see cref="PaneChapterMax"/> chapters around the current one (the episode's resume point, or its live position while it plays).</summary>
    PodcastPaneChapters ComputeChapters()
    {
        var data = _chapters.Value;
        if (data is null) return PodcastPaneChapters.None;
        byte state = data.State.Value;
        var page = data.Value.Value;
        if (state == (byte)LoadState.Pending) return PodcastPaneChapters.Loading;
        if (state != (byte)LoadState.Ready || !page.Ok || page.Items.Length == 0) return PodcastPaneChapters.None;
        string? id = _m?.Selected.Peek();
        var uri = id is null ? default : EntityUri.Parse(id);
        var e = uri.IsValid ? Entities.Episode(uri) : default;
        int position = -1;
        if (e.IsValid)
        {
            if (Episode.IsNowPlaying(e)) position = Playback.PositionMs.Value / ChapterPositionStep * ChapterPositionStep;
            else if (e.Knows(EpisodeFields.Progress) && Episode.Rules.InProgress(Episode.ReaderPctOf(e))) position = e.ProgressMs;
        }
        var items = page.Items;
        int current = position >= 0 ? PodcastReaderRules.CurrentChapter(items, position) : -1;
        int from = Math.Max(0, Math.Min(current - 2, items.Length - PaneChapterMax));
        int to = Math.Min(items.Length, from + PaneChapterMax);
        var shown = ImmutableArray.CreateBuilder<PodcastChapterVm>(to - from);
        int dur = e.IsValid && e.Knows(EpisodeFields.Duration) ? e.DurationMs : 0;
        for (int i = from; i < to; i++)
        {
            var c = items[i];
            int end = c.EndMs > 0 ? c.EndMs : i + 1 < items.Length ? items[i + 1].StartMs : dur;
            shown.Add(new PodcastChapterVm(i, c.Title, c.StartMs, end, ShowModel.Clock(c.StartMs), Episode.DurationLabel(Math.Max(0, end - c.StartMs)),
                0f, i == current, current > i));
        }
        return new PodcastPaneChapters(items.Length, shown.MoveToImmutable(), false);
    }

    /// <summary>The pane shows five chapters; a playing episode's current chapter is re-read every few seconds, never per frame.</summary>
    const int PaneChapterMax = 5, ChapterPositionStep = 5_000;
}

/// <summary>The show page's model: the cells, the snapshot and the verbs. Everything is built once per page; the delegates are stable.</summary>
sealed class ShowModel
{
    public readonly EntityUri Subject;
    public readonly string SubjectText;
    public Memo<PodcastShowView>? View;
    public Memo<string?>? Playing;
    public Memo<PodcastPaneChapters>? PaneChapters;
    public Loadable<Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>>? Similar;

    readonly Signal<PodcastFilter> _filter = new(PodcastFilter.All);
    readonly Signal<string> _query = new("");
    readonly Signal<bool> _oldest = new(false);
    readonly Signal<string?> _selected = new(null), _expanded = new(null);
    readonly Signal<PodcastPaneTab> _paneTab = new(PodcastPaneTab.Episode);
    readonly Signal<int> _askedFrom = new(-1);
    /// <summary>Whether the reader chose the order (else it follows the show: a serial reads oldest first).</summary>
    bool _orderChosen, _seeded;
    /// <summary>The smart menu's last video choice (Play as video / Audio only), for its check.</summary>
    bool _audioOnly;

    public IReadSignal<PodcastFilter> Filter => _filter;
    public IReadSignal<bool> Oldest => _oldest;
    public IReadSignal<string?> Selected => _selected;
    public IReadSignal<string?> Expanded => _expanded;
    public IReadSignal<PodcastPaneTab> PaneTab => _paneTab;
    public readonly Func<Signal<string>> QueryCell;
    public readonly Func<PodcastShowView> ComputeView;
    public readonly Func<string?> ComputePlaying;
    public readonly Action<PodcastFilter> SetFilter;
    public readonly Action ToggleSort, MarkFreshPlayed, ClearFilter, LoadMore, ToggleFollow, PlayTrailer, PlaySmart, Demand, DemandRows, DemandTrailer,
        SeedSelection, Reopen;
    public readonly Action? Share;
    public readonly Action<string?> Select, Expand;
    public readonly Action<PodcastPaneTab> SetPaneTab;
    public readonly Action<string> Play, Open, ToggleSaved, PlayNext, OpenUri;
    public readonly Func<string, ContextMenuModel?> RowMenu;
    public readonly Func<ContextMenuModel?> ShowMenu;
    public readonly Action OpenKind;
    public readonly Func<string, ContextMenuModel?> CardMenu;
    public readonly Action<string> PlayCard;

    PodcastShowHead? _lastHead;
    PodcastSmart? _lastSmart;
    ImmutableArray<PodcastCardVm> _lastSimilar = [];

    static bool SameHead(PodcastShowHead a, PodcastShowHead b)
        => a with { Topics = [], Facts = [] } == b with { Topics = [], Facts = [] }
           && a.Topics.AsSpan().SequenceEqual(b.Topics.AsSpan()) && a.Facts.AsSpan().SequenceEqual(b.Facts.AsSpan());

    /// <summary>The same smart action: its words, episode and menu rows (labels, subs, checks). The menu's verbs act on the same episodes then.</summary>
    static bool SameSmart(PodcastSmart a, PodcastSmart b)
    {
        if (a with { Menu = [] } != b with { Menu = [] } || a.Menu.Length != b.Menu.Length) return false;
        for (int i = 0; i < a.Menu.Length; i++)
            if (a.Menu[i].Label != b.Menu[i].Label || a.Menu[i].Sub != b.Menu[i].Sub || a.Menu[i].Checked != b.Menu[i].Checked) return false;
        return true;
    }

    /// <summary>The last snapshot's facts the verbs act on (a click acts on what the page showed).</summary>
    Facts _facts = Facts.None;

    public ShowModel(EntityUri subject)
    {
        Subject = subject;
        SubjectText = subject.IsValid ? subject.Text : "";
        QueryCell = () => _query;
        ComputeView = Compute;
        ComputePlaying = static () =>
        {
            var id = Playback.CurrentId.Value;
            return Playback.IsPlaying.Value && !id.IsEmpty ? new EntityUri(id).Text : null;
        };
        SetFilter = f => { _filter.Value = f; _expanded.Value = null; };
        ToggleSort = () => { _orderChosen = true; _oldest.Value = !_oldest.Peek(); };
        Select = id => { _selected.Value = id; if (id is not null) _paneTab.Value = PodcastPaneTab.Episode; };
        Expand = id => _expanded.Value = _expanded.Peek() == id ? null : id;
        SetPaneTab = t => _paneTab.Value = t;
        ClearFilter = () => { _filter.Value = PodcastFilter.All; _query.Value = ""; };
        MarkFreshPlayed = () =>
        {
            foreach (int slot in _facts.Fresh) Entities.MarkEpisode(new Episode(slot), played: true);
        };
        LoadMore = NextPage;
        ToggleFollow = () =>
        {
            if (Controls.Library is { } lib && SubjectText.Length > 0) lib.ToggleSaved(SubjectText, _facts.Title);
        };
        PlayTrailer = () =>
        {
            var t = EpisodeOf(_facts.TrailerId);
            if (t.IsValid) Episode.Invoke(t, () => Playback.PlayContext(t.Id));
        };
        PlaySmart = () =>
        {
            var e = EpisodeOf(_facts.SmartId);
            if (!e.IsValid) return;
            var kind = _facts.SmartKind == PodcastSmartKind.CaughtUp ? Playback.EpisodeStartKind.Beginning : Playback.EpisodeStartKind.Resume;
            Episode.Invoke(e, () => Start(e, kind));
        };
        Play = id =>
        {
            var e = EpisodeOf(id);
            if (!e.IsValid) return;
            if (e.Knows(EpisodeFields.Title) && e.Kind == EpisodeKind.Trailer) { Episode.Invoke(e, () => Playback.PlayContext(e.Id)); return; }
            // A played episode plays again from its start; anything else resumes where it stopped.
            var kind = Episode.Rules.Played(Episode.ReaderPctOf(e)) ? Playback.EpisodeStartKind.Beginning : Playback.EpisodeStartKind.Resume;
            Episode.Invoke(e, () => Start(e, kind));
        };
        Open = id => { var e = EpisodeOf(id); if (e.IsValid) Episode.OpenPage(e); };
        ToggleSaved = id => { var e = EpisodeOf(id); if (e.IsValid) Spotify.Podcasts.ToggleSaved(e); };
        PlayNext = id => { var e = EpisodeOf(id); if (e.IsValid) Episode.Enqueue(e, next: true); };
        OpenUri = uri =>
        {
            var u = EntityUri.Parse(uri);
            if (u.IsValid) Shell.GoTo(Shell.For(u));
        };
        RowMenu = id => { var e = EpisodeOf(id); return e.IsValid ? Episode.Menu(e, new Episode.MenuOptions(ShowGoToShow: false)) : null; };
        ShowMenu = () =>
        {
            var show = ShowOf();
            if (!show.IsValid) return null;
            var target = ActionTarget.ForShow(show.Uri, _facts.Title);
            return Menus.Container(in target, show.Knows(ShowFields.Image) ? Controls.ArtUrl(show.ImageId) : null, null,
                new ContainerExtras { OnPage = true });
        };
        Share = () => Episode.CopyLink(Subject.IsValid ? Actions.WebLinkOf(Subject) : "");
        // The eyebrow's kind word: the podcasts hub Wavee has (Your Library > Podcasts).
        OpenKind = static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.LibraryPodcasts));
        CardMenu = static uri =>
        {
            var u = EntityUri.Parse(uri);
            if (!u.IsValid) return null;
            var other = Entities.Show(u);
            if (!other.IsValid) return null;
            string title = other.Knows(ShowFields.Title) ? other.Title : "";
            var target = ActionTarget.ForShow(other.Uri, title);
            return Menus.Container(in target, other.Knows(ShowFields.Image) ? Controls.ArtUrl(other.ImageId) : null, null);
        };
        PlayCard = static uri =>
        {
            var u = EntityUri.Parse(uri);
            if (u.IsValid) Playback.PlayContext(u.Id);
        };
        Reopen = () =>
        {
            var show = ShowOf();
            if (!show.IsValid) return;
            var edge = Entities.Current.Edges.ShowEpisodes;
            if (edge.State(show.Slot) == EdgeState.Unknown) { Entities.EnsureEdge(FetchEdge.ShowEpisodes, show.Slot); return; }
            string uri = show.Uri.Text;
            var verdict = ListFreshness.Decide(true, ListStamps.IsDirty(uri), ListStamps.RevalidatedAtMs(uri), ListStamps.NowMs(), false);
            if (verdict != ListFreshness.Verdict.Paint)
                Entities.RefreshEdge(FetchEdge.ShowEpisodes, show.Slot,
                    verdict == ListFreshness.Verdict.PaintThenRevalidate ? FetchPriority.Prefetch : FetchPriority.Visible);
        };
        Demand = () =>
        {
            var show = ShowOf();
            if (!show.IsValid) return;
            PodcastPages.Open(show);
            Entities.Ensure(show, ShowFields.All);
            Reopen();
        };
        DemandRows = () =>
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.ShowEpisodes.Changed.Value;
            if (!Subject.IsValid || !scope.Shows.TryGetSlot(Subject.Id, out int show)) return;
            var slots = scope.Edges.ShowEpisodes.Targets(show);
            if (slots.Length > 0) Entities.Ensure(scope.Episodes, slots, (uint)ShowReaderRules.RowDemand);
        };
        DemandTrailer = () =>
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Shows.Changed.Value;
            var show = ShowOf();
            if (!show.IsValid || !show.Knows(ShowFields.Facts) || show.TrailerId.IsEmpty) return;
            var trailer = new Episode(scope.Episodes.Slot(show.TrailerId));
            if (trailer.IsValid) Entities.Ensure(trailer, EpisodeFields.Row | EpisodeFields.About);
        };
        // The selection seeds ONCE, when the page first knows its smart action: the up-next episode; none when caught up or on a serial's first visit
        // (nothing played, nothing in progress): the pane then shows About the show.
        SeedSelection = () =>
        {
            var v = View?.Value;
            if (_seeded || v is null || v.Load != PodcastLoad.Ready || v.Smart is null) return;
            _seeded = true;
            var s = v.Smart;
            bool none = s.Kind == PodcastSmartKind.CaughtUp || (s.Kind == PodcastSmartKind.PlayPart && !_facts.AnyProgress);
            if (none) { _selected.Value = null; _paneTab.Value = PodcastPaneTab.About; }
            else { _selected.Value = s.EpisodeId; _paneTab.Value = PodcastPaneTab.Episode; }
        };
    }

    Show ShowOf() => Subject.IsValid ? Entities.Show(Subject) : default;

    static Episode EpisodeOf(string? id)
    {
        if (string.IsNullOrEmpty(id)) return default;
        var uri = EntityUri.Parse(id);
        return uri.IsValid && Entities.Current.Episodes.TryGetSlot(uri.Id, out int slot) ? new Episode(slot) : default;
    }

    void Start(Episode e, Playback.EpisodeStartKind kind)
    {
        var show = ShowOf();
        if (show.IsValid) Playback.PlayEpisode(e.Id, show.Id, new Playback.EpisodeStart(kind));
    }

    /// <summary>The next page of the membership (the list's Load more): one ask per offset, nothing while one is out.</summary>
    void NextPage()
    {
        var show = ShowOf();
        if (!show.IsValid) return;
        _askedFrom.Value = _askedFrom.Peek() + 1;
        PodcastPages.LoadMore(show);
    }

    /// <summary>What the verbs read from the last snapshot.</summary>
    sealed record Facts(string Title, string? TrailerId, string? SmartId, PodcastSmartKind SmartKind, int[] Fresh, bool AnyProgress)
    {
        public static readonly Facts None = new("", null, null, PodcastSmartKind.CaughtUp, [], false);
    }

    // ══ THE SNAPSHOT ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    PodcastShowView Compute()
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        _ = scope.Shows.Changed.Value;
        _ = scope.Episodes.Changed.Value;
        _ = scope.Edges.ShowEpisodes.Changed.Value;
        _ = Spotify.Podcasts.SavedChanged.Value;
        var filter = _filter.Value;
        string query = _query.Value;
        bool oldestCell = _oldest.Value;
        _ = _askedFrom.Value;
        EntityId playingId = Playback.CurrentId.Value;
                var similarState = Similar?.State.Value ?? (byte)LoadState.Pending;
        var similar = similarState == (byte)LoadState.Ready ? Similar?.Value.Value : null;
        if (!Subject.IsValid) return PodcastShowView.Empty with { Load = PodcastLoad.Failed };

        var show = new Show(scope.Shows.Slot(Subject.Id));
        var edge = scope.Edges.ShowEpisodes;
        int slot = show.Slot;
        var readiness = edge.Readiness(slot);
        var lib = Controls.Library;
        bool following = lib is not null && lib.IsSaved(SubjectText);
        bool known = show.IsValid && show.Knows(ShowFields.Title);

        // ── the rows: the data door's page (state at rest, "new since your last visit"), NEWEST FIRST (the membership's order) ──
        var page = PodcastPages.ShowOf(show);
        var rows = page.Episodes;
        int n = rows.Length;
        var slots = new int[n];
        int total = Math.Max(page.TotalEpisodes, n);
        var pcts = new float[n];
        var published = new int[n];
        var playedAt = new int[n];
        for (int i = 0; i < n; i++)
        {
            var e = rows[i].Episode;
            slots[i] = e.Slot;
            pcts[i] = rows[i].State == PlayState.Played ? 1f : rows[i].Pct;
            published[i] = (int)(rows[i].PublishedUnixMs / 1000);
            playedAt[i] = e.IsValid && e.Knows(EpisodeFields.Progress) ? e.PlayedAt : 0;
        }
        bool serial = page.Order == ShowOrder.Serial;
        // The order follows the show until the reader picks one: a serial reads oldest first.
        if (!_orderChosen) oldestCell = serial;
        var (cadence, _) = ShowCadence.Of(published);
        following = page.Saved == SaveState.Saved || following;

        var vms = new PodcastEpisodeVm[n];
        int unplayed = 0, inProgress = 0, saved = 0;
        bool anyProgress = false;
        for (int i = 0; i < n; i++)
        {
            var e = rows[i].Episode;
            var state = rows[i].State switch
            {
                PlayState.Played => PodcastRowState.Played,
                PlayState.InProgress => PodcastRowState.InProgress,
                PlayState.New => PodcastRowState.New,
                _ => rows[i].IsNew ? PodcastRowState.New : PodcastRowState.Unplayed,
            };
            if (state is PodcastRowState.InProgress) inProgress++;
            else if (state is not PodcastRowState.Played) unplayed++;
            if (state is PodcastRowState.Played or PodcastRowState.InProgress) anyProgress = true;
            bool isSaved = e.IsValid && Spotify.Podcasts.IsSaved(e);
            if (isSaved) saved++;
            vms[i] = RowOf(e, i, total, serial, state, pcts[i], isSaved);
        }

        // ── the smart action ──
        int resume = -1, playing = -1;
        for (int i = 0; i < n; i++)
            if (!playingId.IsEmpty && new Episode(slots[i]).Id == playingId) { playing = i; break; }
        if (playing >= 0 && Episode.Rules.InProgress(pcts[playing])) resume = playing;
        else
            for (int i = 0, best = -1; i < n; i++)
                if (Episode.Rules.InProgress(pcts[i]) && playedAt[i] > best) { best = playedAt[i]; resume = i; }
        int oldestUnplayed = -1, newest = -1, freshCount = 0;
        for (int i = n - 1; i >= 0; i--)
            if (!Episode.Rules.Played(pcts[i]) && !vms[i].Trailer && !Episode.Rules.InProgress(pcts[i])) { oldestUnplayed = i; break; }
        for (int i = 0; i < n; i++)
        {
            if (vms[i].State == PodcastRowState.New) freshCount++;
            if (newest < 0 && !Episode.Rules.Played(pcts[i]) && !vms[i].Trailer) newest = i;
        }
        string? trailerId = page.TrailerUri.Length > 0 ? page.TrailerUri : null;
        var trailer = page.Trailer;
        PodcastSmart? smart = null;
        if (n > 0 && readiness is EdgeState.Partial or EdgeState.Complete)
        {
            PodcastSmartKind kind;
            int at;
            string label, why, sub;
            float progress = -1f;
            bool accent = true;
            if (resume >= 0)
            {
                kind = PodcastSmartKind.Resume; at = resume;
                label = Loc.Get(Strings.Podcast.Pages.Resume);
                why = Loc.Get(Strings.Podcast.Pages.UpNext);
                progress = pcts[at];
                sub = vms[at].Left;
            }
            else if (serial && oldestUnplayed >= 0)
            {
                kind = PodcastSmartKind.PlayPart; at = oldestUnplayed;
                int part = total - at;
                label = Strings.Podcast.Pages.PlayPart(part);
                why = Loc.Get(part == 1 ? Strings.Podcast.Pages.StartAtBeginning : Strings.Podcast.Pages.NextInOrder);
                sub = Joined(vms[at].Length, Strings.Podcast.Pages.PartsInOrder(total));
            }
            else if (newest >= 0)
            {
                kind = PodcastSmartKind.PlayNewest; at = newest;
                label = Loc.Get(Strings.Podcast.Pages.PlayNewest);
                bool isNew = vms[at].State == PodcastRowState.New;
                why = Loc.Get(isNew ? Strings.Podcast.Pages.Fresh : Strings.Podcast.Pages.Latest);
                sub = Joined(vms[at].Date, vms[at].Length);
                if (isNew && freshCount > 1) sub = Joined(sub, Strings.Podcast.Pages.MoreFresh(freshCount - 1));
            }
            else
            {
                kind = PodcastSmartKind.CaughtUp; at = 0;
                label = Loc.Get(Strings.Podcast.Pages.PlayLatestAgain);
                why = "";
                accent = false;
                sub = NextLikely(published, cadence);
            }
            var menu = MenuOf(kind, at, newest, oldestUnplayed, slots, vms, serial, trailer, label);
            smart = new PodcastSmart(kind, vms[at].Id, label, why,
                kind == PodcastSmartKind.CaughtUp ? Loc.Get(Strings.Podcast.Pages.CaughtUp) : vms[at].Title, sub, progress, accent, menu);
        }

        // ── the filters, the view and the groups ──
        var q = query.AsSpan().Trim();
        var view = new List<int>(n);
        var shown = new bool[n];
        int shownCount = 0;
        for (int i = 0; i < n; i++)
        {
            var vm = vms[i];
            bool keep = filter switch
            {
                PodcastFilter.Unplayed => vm.State is PodcastRowState.New or PodcastRowState.Unplayed,
                PodcastFilter.InProgress => vm.State == PodcastRowState.InProgress,
                PodcastFilter.Saved => vm.Saved,
                _ => true,
            };
            if (keep && !q.IsEmpty) keep = ShowReaderRules.Matches(vm.Title, vm.Description, q);
            shown[i] = keep;
            if (keep) shownCount++;
            view.Add(i);
        }
        if (oldestCell) view.Reverse();
        var episodes = ImmutableArray.CreateBuilder<PodcastEpisodeVm>(view.Count);
        foreach (int i in view) episodes.Add(shown[i] ? vms[i] : vms[i] with { Hidden = true });
        var years = YearsOf(vms, serial);
        var groups = GroupsOf(vms, view, shown, serial, oldestCell, !q.IsEmpty, filter == PodcastFilter.All && q.IsEmpty, years);

        var filters = ImmutableArray.Create(
            new PodcastFilterVm(PodcastFilter.All, Loc.Get(Strings.Podcast.Pages.FilterAll), total) { CountText = total > 0 ? FormatCache.Int(total) : "" },
            new PodcastFilterVm(PodcastFilter.Unplayed, Loc.Get(Strings.Podcast.Pages.FilterUnplayed), unplayed) { CountText = unplayed > 0 ? FormatCache.Int(unplayed) : "" },
            new PodcastFilterVm(PodcastFilter.InProgress, Loc.Get(Strings.Podcast.Pages.FilterInProgress), inProgress) { CountText = inProgress > 0 ? FormatCache.Int(inProgress) : "" },
            new PodcastFilterVm(PodcastFilter.Saved, Loc.Get(Strings.Podcast.Pages.FilterSaved), saved) { CountText = saved > 0 ? FormatCache.Int(saved) : "" });

        var head = HeadOf(show, page, known, following, serial, total, cadence, published, trailer, trailerId);
        bool hasMore = page.CanLoadMore && filter == PodcastFilter.All && q.IsEmpty;
        var freshSlots = new List<int>();
        for (int i = 0; i < n; i++) if (vms[i].State == PodcastRowState.New) freshSlots.Add(slots[i]);
        _facts = new Facts(head.Title, trailer.IsValid ? trailerId : null, smart?.EpisodeId, smart?.Kind ?? PodcastSmartKind.CaughtUp, freshSlots.ToArray(),
            anyProgress);

        var load = readiness == EdgeState.Failed && n == 0 ? PodcastLoad.Failed
            : !known || !page.Loaded || readiness == EdgeState.Unknown ? PodcastLoad.Pending
            : PodcastLoad.Ready;
        // The parts a filter or a search cannot change are REUSED while their content holds: the page's projection compares them by reference, so a
        // filter switch re-renders the list alone (the hero, the bar and the pane keep their render).
        head = _lastHead is { } lh && SameHead(lh, head) ? lh : head;
        smart = _lastSmart is { } ls && smart is not null && SameSmart(ls, smart) ? ls : smart;
        var similarCards = SimilarOf(similar);
        if (_lastSimilar.Length == similarCards.Length && _lastSimilar.AsSpan().SequenceEqual(similarCards.AsSpan())) similarCards = _lastSimilar;
        _lastHead = head;
        _lastSmart = smart;
        _lastSimilar = similarCards;
        return new PodcastShowView(load, head, smart, episodes.MoveToImmutable(), groups, filters, years, hasMore,
            filter != PodcastFilter.All || !q.IsEmpty, similarCards) { ShownCount = shownCount };
    }

    static PodcastEpisodeVm RowOf(Episode e, int i, int total, bool serial, PodcastRowState state, float pct, bool saved)
    {
        if (!e.IsValid) return new PodcastEpisodeVm("", "", "", 0, serial, "", 0, "", "", 0f, state, false, false, false, false, 0, false, null);
        string id = e.Uri.Text;
        bool head = e.Knows(EpisodeFields.Title);
        var kind = head ? e.Kind : EpisodeKind.Full;
        int number = serial ? total - i : head && e.Number > 0 ? e.Number : 0;
        string raw = head ? e.Title : "";
        // A serial is numbered by position (concept note 5): "3. Title", the title's own "#3 ·" taken off.
        string title = serial && kind != EpisodeKind.Trailer && raw.Length > 0
            ? Strings.Podcast.Pages.NumberedTitle(number, Episode.TitleSansNumber(raw, e.Number)) : raw;
        int at = e.Knows(EpisodeFields.Published) ? e.PublishedAt : 0;
        var local = at > 0 ? DateTimeOffset.FromUnixTimeSeconds(at).LocalDateTime : default;
        int year = at > 0 ? local.Year : 0;
        string date = at > 0 ? Episode.DateLabel(at) : "";
        string longDate = at > 0 ? local.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "";
        int dur = e.Knows(EpisodeFields.Duration) ? e.DurationMs : 0;
        string length = dur > 0 ? Episode.DurationLabel(dur) : "";
        string left = state == PodcastRowState.InProgress && dur > 0 ? Episode.LeftLabel(e.ProgressMs, dur) : "";
        var flags = head ? e.Flags : EpisodeFlags.None;
        bool playable = (flags & EpisodeFlags.Unplayable) == 0;
        string eyebrow = number > 0
            ? Joined(serial ? Strings.Podcast.Pages.PartN(number) : Strings.Podcast.Pages.EpisodeN(number), longDate)
            : longDate;
        return new PodcastEpisodeVm(id, title, Episode.DescriptionOf(e), number, serial, date, year, length, left,
            state == PodcastRowState.Played ? 1f : state == PodcastRowState.InProgress ? pct : 0f, state, saved,
            (flags & EpisodeFlags.Video) != 0, kind == EpisodeKind.Trailer, playable, 0, (flags & EpisodeFlags.HasTranscript) != 0, Episode.ArtOf(e))
        {
            Eyebrow = eyebrow, LongDate = longDate, DescriptionHtml = e.Knows(EpisodeFields.About) ? e.HtmlDescription : "",
        };
    }

    /// <summary>The smart action's menu ("Other ways to play"): the default with a check, Play newest, Start this episode over (in progress), Play
    /// trailer (a serial) or Play oldest unplayed (catch up in order), and Play as video / Audio only for a video episode.</summary>
    ImmutableArray<PodcastMenuItem> MenuOf(PodcastSmartKind kind, int at, int newest, int oldestUnplayed, int[] slots, PodcastEpisodeVm[] vms,
        bool serial, Episode trailer, string label)
    {
        var items = ImmutableArray.CreateBuilder<PodcastMenuItem>(6);
        items.Add(new PodcastMenuItem(label, null, Icons.Play, true, PlaySmart));
        int latest = serial ? 0 : newest;
        if (latest >= 0 && latest != at && kind != PodcastSmartKind.CaughtUp)
        {
            string id = vms[latest].Id;
            items.Add(new PodcastMenuItem(Loc.Get(Strings.Podcast.Pages.PlayNewest), null, Icons.Play, false, () => Play(id)));
        }
        if (kind == PodcastSmartKind.Resume)
        {
            var e = new Episode(slots[at]);
            items.Add(new PodcastMenuItem(Loc.Get(Strings.Podcast.Pages.StartOver), null, Icons.Undo, false,
                () => Episode.Invoke(e, () => Start(e, Playback.EpisodeStartKind.Beginning))));
        }
        if (serial)
        {
            if (trailer.IsValid)
            {
                string sub = trailer.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(trailer.DurationMs) : "";
                items.Add(new PodcastMenuItem(Loc.Get(Strings.Podcast.Pages.PlayTrailer), sub, Icons.Play, false, PlayTrailer));
            }
        }
        else if (oldestUnplayed >= 0 && oldestUnplayed != at)
        {
            var e = new Episode(slots[oldestUnplayed]);
            items.Add(new PodcastMenuItem(Loc.Get(Strings.Podcast.Pages.PlayOldestUnplayed), Loc.Get(Strings.Podcast.Pages.CatchUp), Icons.Play, false,
                () => Episode.Invoke(e, () => Start(e, Playback.EpisodeStartKind.Resume))));
        }
        if (vms[at].Video)
        {
            items.Add(new PodcastMenuItem(Loc.Get(Strings.Podcast.Pages.PlayAsVideo), null, Icons.Video, !_audioOnly, PlayVideo));
            items.Add(new PodcastMenuItem(Loc.Get(Strings.Podcast.Pages.AudioOnly), null, Icons.Volume, _audioOnly, PlayAudio));
        }
        return items.ToImmutable();
    }

    void PlayVideo() { _audioOnly = false; PlaySmart(); Playback.SwitchVideo(true); }
    void PlayAudio() { _audioOnly = true; PlaySmart(); Playback.SwitchVideo(false); }

    static ImmutableArray<PodcastYear> YearsOf(PodcastEpisodeVm[] vms, bool serial)
    {
        if (serial) return [];
        var counts = new SortedDictionary<int, int>(Comparer<int>.Create(static (a, b) => b.CompareTo(a)));
        foreach (var vm in vms)
            if (vm.Year > 0) counts[vm.Year] = counts.TryGetValue(vm.Year, out int c) ? c + 1 : 1;
        var b = ImmutableArray.CreateBuilder<PodcastYear>(counts.Count);
        foreach (var (year, count) in counts)
            b.Add(new PodcastYear(year, year.ToString(CultureInfo.InvariantCulture), Strings.Podcast.Pages.YearEpisodes(count)) { Episodes = count });
        return b.MoveToImmutable();
    }

    /// <summary>"New since your last visit" (episodic, newest first, no search), then a group per year; a serial lists one "In order".
    /// <see cref="PodcastGroup.Rows"/> index the view (the snapshot's <c>Episodes</c>).</summary>
    static ImmutableArray<PodcastGroup> GroupsOf(PodcastEpisodeVm[] vms, List<int> view, bool[] shown, bool serial, bool oldest, bool searching, bool whole,
        ImmutableArray<PodcastYear> years)
    {
        var groups = ImmutableArray.CreateBuilder<PodcastGroup>();
        if (view.Count == 0) return groups.ToImmutable();
        if (serial)
        {
            var all = ImmutableArray.CreateBuilder<int>(view.Count);
            int visible = 0;
            for (int k = 0; k < view.Count; k++) { all.Add(k); if (shown[view[k]]) visible++; }
            groups.Add(new PodcastGroup("order", Loc.Get(Strings.Podcast.Pages.InOrder), visible, false, all.MoveToImmutable())
                { CountText = FormatCache.Int(visible), Hidden = visible == 0 });
            return groups.ToImmutable();
        }
        var inFresh = new bool[view.Count];
        var freshRows = ImmutableArray.CreateBuilder<int>();
        if (!searching && !oldest)
            for (int k = 0; k < view.Count; k++)
                if (vms[view[k]].State == PodcastRowState.New) { freshRows.Add(k); inFresh[k] = true; }
        if (freshRows.Count > 0)
        {
            int visible = 0;
            foreach (int k in freshRows) if (shown[view[k]]) visible++;
            groups.Add(new PodcastGroup("new", Loc.Get(Strings.Podcast.Pages.NewSince), visible, true, freshRows.ToImmutable())
                { CountText = FormatCache.Int(visible), Hidden = visible == 0 });
        }
        int currentYear = int.MinValue;
        ImmutableArray<int>.Builder? rows = null;
        for (int k = 0; k < view.Count; k++)
        {
            if (inFresh[k]) continue;
            int year = vms[view[k]].Year;
            if (rows is null || year != currentYear)
            {
                Flush(groups, rows, currentYear, whole, years, view, shown);
                currentYear = year;
                rows = ImmutableArray.CreateBuilder<int>();
            }
            rows.Add(k);
        }
        Flush(groups, rows, currentYear, whole, years, view, shown);
        return groups.ToImmutable();

        static void Flush(ImmutableArray<PodcastGroup>.Builder groups, ImmutableArray<int>.Builder? rows, int year, bool whole, ImmutableArray<PodcastYear> years,
            List<int> view, bool[] shown)
        {
            if (rows is null || rows.Count == 0) return;
            int count = 0;
            foreach (int k in rows) if (shown[view[k]]) count++;
            int visible = count;
            if (whole)
                foreach (var y in years)
                    if (y.Year == year) { count = Math.Max(count, y.Episodes); break; }
            string title = year > 0 ? year.ToString(CultureInfo.InvariantCulture) : "";
            groups.Add(new PodcastGroup("y" + title, title, count, false, rows.ToImmutable()) { CountText = FormatCache.Int(count), Hidden = visible == 0 });
        }
    }

    PodcastShowHead HeadOf(Show show, ShowPage page, bool known, bool following, bool serial, int total, ShowCadence.Kind cadence, int[] published, Episode trailer,
        string? trailerId)
    {
        if (!known) return PodcastShowView.Empty.Head with { Uri = SubjectText, Following = following };
        string title = show.Title;
        string? art = show.Knows(ShowFields.Image) ? Controls.ArtUrl(show.ImageId) : null;
        uint tone = show.Knows(ShowFields.Appearance) ? show.Tone : 0u;
        ImmutableArray<string> topics = [.. page.Categories.Take(TopicMax)];
        bool video = page.Video;
        string publisher = page.Publisher;
        var r = page.Rating;
        bool rated = r is { Average: > 0f };
        int ratingX100 = rated ? (int)MathF.Round(r!.Value.Average * 100f) : 0;
        int ratingCount = rated ? r!.Value.Count : 0;
        string rating = rated ? r!.Value.Average.ToString("0.0", CultureInfo.CurrentCulture) : "";
        string count = rated ? ratingCount.ToString("N0", CultureInfo.CurrentCulture) : "";
        string cadenceWord = Sentence(Show.CadenceWord(cadence));
        string countLine = serial ? Strings.Podcast.Pages.PartsCount(total) : Strings.Podcast.Pages.EpisodesCount(total);
        string about = page.About, aboutHtml = page.AboutHtml;
        int oldestYear = 0;
        for (int i = published.Length - 1; i >= 0; i--)
            if (published[i] > 0) { oldestYear = DateTimeOffset.FromUnixTimeSeconds(published[i]).LocalDateTime.Year; break; }
        var facts = ImmutableArray.CreateBuilder<PodcastFact>(6);
        if (publisher.Length > 0) facts.Add(new PodcastFact(Loc.Get(Strings.Podcast.Pages.Publisher), publisher));
        facts.Add(new PodcastFact(Loc.Get(Strings.Podcast.Pages.Episodes), oldestYear > 0
            ? Strings.Podcast.Pages.EpisodesSince(FormatCache.Int(total), oldestYear.ToString(CultureInfo.InvariantCulture)) : FormatCache.Int(total)));
        if (cadenceWord.Length > 0) facts.Add(new PodcastFact(Loc.Get(Strings.Podcast.Pages.Schedule), cadenceWord));
        facts.Add(new PodcastFact(Loc.Get(Strings.Podcast.Pages.Order), Loc.Get(serial ? Strings.Podcast.Pages.OrderSerial : Strings.Podcast.Pages.OrderNewest)));
        if (topics.Length > 0) facts.Add(new PodcastFact(Loc.Get(Strings.Podcast.Pages.Topics), string.Join(", ", topics)));
        if (video) facts.Add(new PodcastFact(Loc.Get(Strings.Podcast.Pages.Format), Loc.Get(Strings.Podcast.Pages.FormatVideo)));
        string trailerSub = trailer.IsValid && trailer.Knows(EpisodeFields.Duration)
            ? Strings.Podcast.Pages.TrailerSub(Episode.DurationLabel(trailer.DurationMs)) : "";
        return new PodcastShowHead(SubjectText, title, art, tone, Loc.Get(Strings.Podcast.Pages.KindPodcast), topics, video, serial, publisher,
            ratingX100, rating, rated ? "(" + count + ")" : "", rated ? (ratingCount == 1 ? Strings.Podcast.Pages.RatingsCount(1) : Strings.Podcast.Pages.RatingsMany(count)) : "",
            r is { CanRate: true }, cadenceWord, countLine, about, following, trailer.IsValid ? trailerId : null, trailerSub, facts.ToImmutable()) { AboutHtml = aboutHtml };
    }

    ImmutableArray<PodcastCardVm> SimilarOf(Spotify.Podcasts.Page<Spotify.Podcasts.Recommendation>? page)
    {
        if (page is null || !page.Ok || page.Items.Length == 0) return [];
        var b = ImmutableArray.CreateBuilder<PodcastCardVm>(Math.Min(page.Items.Length, SimilarMax));
        foreach (var r in page.Items)
        {
            if (b.Count == SimilarMax) break;
            if (string.Equals(r.Uri, SubjectText, StringComparison.Ordinal)) continue;
            b.Add(new PodcastCardVm(r.Uri, r.Title, r.Subtitle, string.IsNullOrEmpty(r.Image) ? null : r.Image));
        }
        return b.ToImmutable();
    }

    /// <summary>The caught-up line: when the next episode is likely (one cadence step after the newest, past today), or the quiet line.</summary>
    static string NextLikely(int[] published, ShowCadence.Kind cadence)
    {
        int days = cadence switch
        {
            ShowCadence.Kind.Daily => 1, ShowCadence.Kind.Weekly => 7, ShowCadence.Kind.Fortnightly => 14, ShowCadence.Kind.Monthly => 30, _ => 0,
        };
        int newest = published.Length > 0 ? published[0] : 0;
        if (days == 0 || newest <= 0) return Loc.Get(Strings.Podcast.Pages.CaughtUpQuiet);
        var next = DateTimeOffset.FromUnixTimeSeconds(newest).AddDays(days).LocalDateTime;
        var now = DateTime.Now;
        while (next < now) next = next.AddDays(days);
        return Joined(Sentence(Show.CadenceWord(cadence)), Strings.Podcast.Pages.CaughtUpNext(next.ToString("d MMM", CultureInfo.CurrentCulture)));
    }

    /// <summary>"1:02:03" / "4:05": a chapter's start.</summary>
    internal static string Clock(int ms)
    {
        int s = Math.Max(0, ms / 1000);
        int h = s / 3600, m = s / 60 % 60, sec = s % 60;
        return h > 0 ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{sec:00}") : string.Create(CultureInfo.InvariantCulture, $"{m}:{sec:00}");
    }

    /// <summary>The cadence word as a sentence ("Weekly"): the shared word is lower case for the old meta line.</summary>
    static string Sentence(string word) => word.Length == 0 ? word : CultureInfo.CurrentCulture.TextInfo.ToUpper(word[0]) + word[1..];

    static string Joined(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " · " + b;

    const int TopicMax = 3, SimilarMax = 6;
}
