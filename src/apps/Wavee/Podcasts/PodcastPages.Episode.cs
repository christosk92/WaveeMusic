// ── Podcasts/PodcastPages.Episode.cs — the podcast episode page's core host (podcast pages v2; concept "Episode", winui-podcast-v2) ─────────────────────
//
// THE PAGE: a chapter map and a transcript that follows playback (concept note 6). The host owns:
//   · the demand: the episode (All), its show (Identity | Rating | Appearance | Facts), the show's membership and its rows (for "Next in this show"),
//     the chapters lease (`ChapterResources`), the transcript (raw, its headings kept), the comments' count and "More like this";
//   · ONE snapshot (`PodcastEpisodeView`), rebuilt when data lands, the saved set moves, the state changes (Play / Resume / Play again) or the position
//     crosses a chapter: NEVER per playback tick;
//   · the live leaves, each its own memo so a tick re-runs only what moves: the position (live `Playback.PositionMs` while this episode is on the deck,
//     else its resume point), the current chapter and line (change on a crossing), the map clock (per second), the time left (per minute);
//   · the cells (the section tab, follow playback, the find text) and every verb.
// It builds no element and reads no look (WVTH008): the WinUI picture is `Themes/WinUi/Structure/Views/Podcast/WinUiPodcastEpisode.cs`.
//
// Memo delegates are bound ONCE (the constructor): `UseComputed` keeps the first delegate it is given (`Show.Pane.cs` header). The page is keyed per
// route by the router, so the subject never changes under a mounted host; a scope change (sign-in, the fake catalogue) re-resolves through
// `Episode.ReaderEpisode`, which subscribes to the scope.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ChapterPage = Wavee.Spotify.Podcasts.Page<Wavee.Spotify.Podcasts.Chapter>;
using CommentPage = Wavee.Spotify.Podcasts.Page<Wavee.Spotify.Podcasts.Comment>;
using RecoPage = Wavee.Spotify.Podcasts.Page<Wavee.Spotify.Podcasts.Recommendation>;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>The episode page's host: the demand, the snapshot, the live leaves, the cells and the verbs.</summary>
[CoreHost]
public sealed class PodcastEpisodeScreen : Component
{
    /// <summary>How many rows "Next in this show" lists.</summary>
    const int NextRows = 2;
    const string MetaSep = " · ";

    readonly Signal<PodcastEpisodeTab> _tab = new(PodcastEpisodeTab.Chapters);
    readonly Signal<bool> _follow = new(true);
    readonly Signal<string> _find = new("");
    /// <summary>The chapters lease of the current scope: written by the layout effect that acquires it, read by the memos.</summary>
    readonly Signal<ChapterResources.Lease?> _chapters = new(null);

    PodcastPages.HostProps _host = new(default, "");
    Loadable<Spotify.Podcasts.Transcript>? _transcript;
    Loadable<CommentPage>? _comments;
    Loadable<RecoPage>? _similar;
    Memo<PodcastEpisodeView>? _view;
    // The coarse leaves the snapshot reads: each re-runs per tick while the episode plays but notifies only on a change, so the snapshot never does.
    Memo<PodcastRowState>? _stateMemo;
    Memo<int>? _chapterMemo;
    /// <summary>The episode's chapters (Spotify's chapter data, else the show notes' timestamp lines): re-runs on a data change, never per tick.</summary>
    Memo<Spotify.Podcasts.Chapter[]>? _itemsMemo;
    string? _notesHtml;
    Spotify.Podcasts.Chapter[] _notes = [];
    Memo<bool>? _playingMemo;
    PodcastEpisodePageProps? _props;
    PageChromeSink _sink = PageChromeSink.None;
    string _published = "";
    readonly Action _publishTitle;
    readonly Func<Action> _withdrawOnLeave;

    readonly Func<int> _positionOf, _chapterOf, _lineOf;
    readonly Func<Spotify.Podcasts.Chapter[]> _itemsOf;
    readonly Func<PodcastRowState> _stateOf;
    readonly Func<bool> _playingOf;
    readonly Func<string> _clockOf, _leftOf, _chapterLeftOf;
    readonly Func<PodcastEpisodeView> _viewOf;
    readonly Func<Element> _commentsView;
    readonly Func<ContextMenuModel?> _menu;
    readonly Action _play, _toggleSaved, _toggleCompleted, _openShow, _share, _demandShow;
    readonly Action<int> _seek;
    readonly Action<bool> _setFollow;
    readonly Action<PodcastEpisodeTab> _setTab;
    readonly Action<string> _open, _playOther, _playNext;
    readonly Func<Signal<string>> _findCell;
    readonly Func<string?> _onDeckOf = static () =>
    {
        var id = Playback.CurrentId.Value;
        return !id.IsEmpty && Playback.IsPlaying.Value ? id.Text : null;
    };
    readonly Action<string> _toggleSavedOther = static uri => { var x = Other(uri); if (x.IsValid) Spotify.Podcasts.ToggleSaved(x); };
    /// <summary>A similar show's card: its container menu (follow, share, ...) and play (or pause when it is the playing context).</summary>
    readonly Func<string, ContextMenuModel?> _cardMenu = static uri =>
    {
        if (!EntityId.TryParse(uri, out var id)) return null;
        var show = Entities.Show(id);
        string title = show.IsValid && show.Knows(ShowFields.Title) ? show.Title : "";
        string? art = show.IsValid && show.Knows(ShowFields.Image) ? Controls.ArtUrl(show.ImageId) : null;
        return Menus.Container(ActionTarget.ForShow(new EntityUri(id), title), art, null);
    };
    readonly Action<string> _playCard = static uri => { if (EntityId.TryParse(uri, out var id)) Playback.PlayOrToggleContext(id); };
    readonly Func<string, ContextMenuModel?> _rowMenu = static uri =>
    {
        var x = Other(uri);
        return x.IsValid ? Episode.Menu(x, new Episode.MenuOptions(ShowGoToShow: false)) : null;
    };

    public PodcastEpisodeScreen()
    {
        _positionOf = PositionOf; _stateOf = StateOf; _chapterOf = ChapterOf; _itemsOf = ItemsOf; _lineOf = LineOf; _playingOf = PlayingOf;
        _clockOf = ClockOf; _leftOf = LeftOf; _chapterLeftOf = ChapterLeftOf; _viewOf = ViewOf;
        _publishTitle = PublishTitle;
        _withdrawOnLeave = () => WithdrawTitle;
        _commentsView = () => Embed.Comp(new PodcastReaderUI.DiscussionProps(_host.Subject.Text, false), static () => new PodcastReaderUI.Discussion())
            with { Key = "episode-comments:" + _host.Subject.Text };
        _menu = MoreMenu;
        _play = Play; _toggleSaved = ToggleSaved; _toggleCompleted = ToggleCompleted; _openShow = OpenShow; _share = Share; _demandShow = DemandShow;
        _seek = SeekTo;
        _setFollow = v => _follow.Value = v;
        _setTab = t => _tab.Value = t;
        _open = OpenOther; _playOther = PlayOther; _playNext = PlayNextOther;
        _findCell = () => _find;
    }

    Episode Current => Episode.ReaderEpisode(_host.Subject);

    public override Element Render()
    {
        _host = UseProps<PodcastPages.HostProps>();
        uint epoch = Entities.ScopeEpoch.Value;
        _ = Entities.Current.Episodes.Changed.Value;
        var e = Current;
        string uri = _host.Subject.Text;
        string scrollScope = UseContext(Shell.PageScrollScope);

        // ── demand ──
        var subject = _host.Subject;
        UseEffect(() =>
        {
            var ep = Entities.Episode(subject);
            PodcastPages.Open(ep);
        }, DepKey.From((int)epoch, 0));
        UseEffect(_demandShow);
        UseLayoutEffect(() =>
        {
            var ep = Episode.ReaderEpisode(subject);
            if (!ep.IsValid) return null;
            var lease = ChapterResources.Shared.Acquire(new ChapterResources.Identity(epoch, Entities.Current.Key.Account, ep.Id));
            lease.Start();
            _chapters.Value = lease;
            return () =>
            {
                if (ReferenceEquals(_chapters.Peek(), lease)) _chapters.Value = null;
                lease.Dispose();
            };
        }, DepKey.From((int)epoch, e.Slot));

        // The transcript: raw (its headings kept), asked once the episode says where it lives (`EpisodeFields.Transcript`).
        bool transcriptKnown = Platform.Args.Fake || (e.IsValid && e.Knows(EpisodeFields.Transcript));
        string transcriptUrl = e.IsValid ? e.TranscriptUrl : "";
        var key = new Spotify.Podcasts.TranscriptKey(e.IsValid ? e.Id : default, e.IsValid ? e.TranscriptLanguage : "", e.IsValid && e.TranscriptReadAlong);
        string account = Entities.Current.Key.Account;
        bool known = e.IsValid && e.Knows(EpisodeFields.Title);
        _transcript = UseResource(ct => TranscriptOf(key, transcriptUrl, account, transcriptKnown, ct), s_transcriptSeed,
            epoch + ":" + uri + ":" + known + ":" + transcriptKnown + ":" + transcriptUrl).Loadable;
        _comments = UseResource(ct => Platform.Args.Fake ? PodcastReaderFixtures.Answer(uri, PodcastReaderFixtures.Comments, ct)
                : Spotify.Podcasts.CommentsAsync(epoch, uri, "", false, ct),
            new CommentPage([], "", 0, "", 0), epoch + ":" + uri + ":" + known).Loadable;
        string showUri = e.IsValid && e.Show.IsValid ? e.Show.Uri.Text : "";
        _similar = UseResource(ct => showUri.Length == 0 ? Task.FromResult(new RecoPage([], "", 0, "", 200))
                : Platform.Args.Fake ? Task.FromResult(PodcastReaderFixtures.SimilarShows(showUri))
                : PodcastReaderUI.RecommendationsOf(showUri, show: true, ct),
            new RecoPage([], "", 0, "", 0), epoch + ":" + showUri).Loadable;

        _stateMemo = UseComputed(_stateOf);
        _playingMemo = UseComputed(_playingOf);
        _itemsMemo = UseComputed(_itemsOf);
        _chapterMemo = UseComputed(_chapterOf);
        _view = UseComputed(_viewOf);
        var position = UseComputed(_positionOf);
        var chapter = _chapterMemo;
        var line = UseComputed(_lineOf);
        var clock = UseComputed(_clockOf);
        var left = UseComputed(_leftOf);
        var chapterLeft = UseComputed(_chapterLeftOf);
        var playing = _playingMemo;
        var onDeck = UseComputed(_onDeckOf);
        // The window tab and the history rows read the page's own title (`PageChromeChannel.PublishedTitle`), not the route's "Episode".
        _sink = UseContext(PageChromeSink.Context);
        UseEffect(_publishTitle);
        UseEffect(_withdrawOnLeave, DepKey.Empty);

        _props ??= new PodcastEpisodePageProps
        {
            RouteKey = _host.RouteKey,
            ScrollKey = scrollScope + "podcast-episode:" + _host.RouteKey,
            View = _view, Tab = _tab, SetTab = _setTab,
            PositionMs = position, Playing = playing, CurrentChapter = chapter, CurrentLine = line,
            MapClock = clock, Left = left, ChapterLeft = chapterLeft,
            Follow = _follow, SetFollow = _setFollow, Find = _findCell,
            Play = _play, SeekTo = _seek, ToggleSaved = _toggleSaved, ToggleCompleted = _toggleCompleted, OpenShow = _openShow,
            Open = _open, PlayEpisode = _playOther, PlayNext = _playNext, OpenUri = DetailVerbs.NavigateKey,
            Menu = _menu, Share = _share, Comments = _commentsView,
            State = _stateMemo, OnDeck = onDeck, ToggleSavedEpisode = _toggleSavedOther, RowMenu = _rowMenu, CardMenu = _cardMenu, PlayCard = _playCard,
            RowDrag = PodcastPages.RowDrag,
        };
        return Slots.View(ViewKeys.PodcastEpisodePage, _props);
    }

    /// <summary>Auto-tracked on the snapshot: publishes the episode's title once it is known and whenever it changes.</summary>
    void PublishTitle()
    {
        string title = _view?.Value.Episode.Title ?? "";
        if (title.Length == 0 || title == _published) return;
        _published = title;
        _sink.Publish(new PageChromeRow([], [], Title: title));
    }

    void WithdrawTitle() => _sink.Publish(null);

    /// <summary>The show's identity, its membership and the rows "Next in this show" paints. Auto-tracked: re-asks as the episode's show and the
    /// membership land (the planner dedupes).</summary>
    void DemandShow()
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        _ = scope.Episodes.Changed.Value;
        _ = scope.Edges.ShowEpisodes.Changed.Value;
        var e = Current;
        if (!e.IsValid || !e.Show.IsValid) return;
        var show = e.Show;
        Entities.Ensure(show, ShowFields.Identity | ShowFields.Rating | ShowFields.Appearance | ShowFields.Facts);
        if (scope.Edges.ShowEpisodes.State(show.Slot) == EdgeState.Unknown) { Entities.EnsureEdge(FetchEdge.ShowEpisodes, show.Slot); return; }
        var slots = scope.Edges.ShowEpisodes.Targets(show.Slot);
        if (slots.Length > 0) Entities.Ensure(scope.Episodes, slots, (uint)ShowReaderRules.RowDemand);
    }

    static readonly Spotify.Podcasts.Transcript s_transcriptSeed = new("", [], 0);

    static Task<Spotify.Podcasts.Transcript> TranscriptOf(Spotify.Podcasts.TranscriptKey key, string url, string account, bool known, CancellationToken ct)
    {
        if (key.Episode.IsEmpty) return Never(ct);
        if (Platform.Args.Fake) return PodcastReaderFixtures.Answer(key.Episode.Text, PodcastReaderFixtures.TranscriptFor, ct);
        if (!known) return Never(ct);
        if (url.Length == 0) return Task.FromResult(new Spotify.Podcasts.Transcript(key.Language, [], 404));
        return Spotify.Podcasts.TranscriptAsync(key, url, account, ct);

        static async Task<Spotify.Podcasts.Transcript> Never(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }
    }

    // ══ THE LIVE LEAVES ═════════════════════════════════════════════════════════════════════════════════════════════════════

    ChapterPage? ChapterData()
    {
        var lease = _chapters.Value;
        if (lease is null) return null;
        return lease.Data.State.Value == (byte)LoadState.Ready ? lease.Data.Value.Value : null;
    }

    /// <summary>The episode's chapters: Spotify's chapter data when it lists any; once it has answered with none (or failed), the show notes'
    /// timestamp lines ("(00:12:30) Title", <see cref="RichTextBlocks.Split"/>), which the About's prose no longer shows (<c>Episode.AboutProse</c>
    /// splits them off), so a show that only writes its chapters into its notes still gets the chapter list and its seeks. Empty while the data is on
    /// its way (the tab keeps its count's room). The notes' chapters are parsed once per description.</summary>
    Spotify.Podcasts.Chapter[] ItemsOf()
    {
        var data = ChapterData();
        if (data is { Items.Length: > 0 }) return data.Items;
        if (data is null && _chapters.Value is { } lease && lease.Data.State.Value == (byte)LoadState.Pending) return [];
        _ = Entities.Current.Episodes.Changed.Value;
        var e = Current;
        if (!e.IsValid) return [];
        string html = e.HtmlDescription.Length > 0 ? e.HtmlDescription : Episode.DescriptionOf(e);
        if (string.Equals(html, _notesHtml, StringComparison.Ordinal)) return _notes;
        var links = RichTextBlocks.Split(html).Chapters;
        // The splitter reads one timestamp per LINE: an HTML body of paragraphs on one line hides them, the plain description keeps its lines.
        if (links.Length == 0 && e.HtmlDescription.Length > 0) links = RichTextBlocks.Split(Episode.DescriptionOf(e)).Chapters;
        var notes = new Spotify.Podcasts.Chapter[links.Length];
        for (int i = 0; i < links.Length; i++) notes[i] = new Spotify.Podcasts.Chapter("", links[i].Title, links[i].StartMs, 0);
        _notesHtml = html;
        return _notes = notes;
    }

    Spotify.Podcasts.Chapter[] Items() => _itemsMemo?.Value ?? ItemsOf();

    /// <summary>Is this episode the deck's, with the deck not idle (its clock is the position)?</summary>
    static bool Live(Episode e) => Episode.IsNowPlaying(e) && Playback.PhaseSignal.Value != Playback.Phase.Idle;

    int PositionOf()
    {
        var e = Current;
        if (!e.IsValid) return 0;
        var p = Episode.PlaybackProgressOf(e);
        int duration = Math.Max(0, p.DurationMs > 0 ? p.DurationMs : e.DurationMs);
        if (!p.Known) return 0;
        if (p.Completed && !Live(e)) return duration;
        return Math.Clamp(p.PositionMs, 0, duration);
    }

    PodcastRowState StateOf()
    {
        var e = Current;
        if (!e.IsValid) return PodcastRowState.Unplayed;
        float pct = Episode.PlaybackProgressOf(e).Pct;
        if (Episode.Rules.Played(pct)) return PodcastRowState.Played;
        if (Episode.Rules.InProgress(pct)) return PodcastRowState.InProgress;
        return PodcastRowState.Unplayed;
    }

    bool PlayingOf()
    {
        var e = Current;
        return e.IsValid && Episode.IsNowPlaying(e) && Playback.IsPlaying.Value;
    }

    /// <summary>Is the episode under way (on the deck, or in progress at rest)? Only then is there a current chapter or line.</summary>
    bool UnderWay(Episode e) => Live(e) || StateOf() == PodcastRowState.InProgress;

    /// <summary>The chapter under way (-1 on a played or untouched episode: a thumb there would claim a place nobody is at).</summary>
    int ChapterOf()
    {
        var items = Items();
        if (items.Length == 0) return -1;
        var e = Current;
        if (!e.IsValid || !UnderWay(e)) return -1;
        return PodcastReaderRules.CurrentChapter(items, PositionOf());
    }

    /// <summary>The transcript line the position is in (the snapshot's line index), -1 before the first or when not under way.</summary>
    int LineOf()
    {
        if (_view is null) return -1;
        var lines = _view.Value.Transcript;
        if (lines.IsDefaultOrEmpty) return -1;
        var e = Current;
        if (!e.IsValid || !UnderWay(e)) return -1;
        int pos = PositionOf();
        int lo = 0, hi = lines.Length - 1, at = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (lines[mid].StartMs <= pos) { at = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return at;
    }

    string ClockOf()
    {
        var e = Current;
        if (!e.IsValid || !e.Knows(EpisodeFields.Duration) || e.DurationMs <= 0) return "";
        string total = Playback.TimeFormat.Clock(e.DurationMs);
        if (!UnderWay(e)) return total;
        return Playback.TimeFormat.Clock(PositionOf()) + " / " + total;
    }

    string LeftOf()
    {
        var e = Current;
        if (!e.IsValid || !UnderWay(e)) return "";
        return Episode.LeftLabel(PositionOf(), e.DurationMs);
    }

    string ChapterLeftOf()
    {
        int at = ChapterOf();
        var items = Items();
        if (at < 0 || at >= items.Length) return "";
        var c = items[at];
        int end = c.EndMs > c.StartMs ? c.EndMs : at + 1 < items.Length ? items[at + 1].StartMs : Current.DurationMs;
        if (end <= c.StartMs) return "";
        return MetaSep + Episode.LeftLabel(PositionOf() - c.StartMs, end - c.StartMs);
    }

    // ══ THE SNAPSHOT ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The last snapshot handed out: a recompute that lands on the same content returns THIS instance, so the memo does not notify and no view
    /// re-renders (the tables publish for any episode's write, the deck's pause stores a resume point the page already shows, ...).</summary>
    PodcastEpisodeView? _last;

    PodcastEpisodeView ViewOf()
    {
        var next = BuildView();
        if (_last is not null)
        {
            if (SameView(_last, next)) return _last;
            next = Reuse(next, _last);
        }
        return _last = next;
    }

    /// <summary>A new snapshot keeps the previous one's lists where their content did not change, so a view that reads only a list (the chapters, the
    /// transcript, "Next in this show") sees the same instance and does not re-render. The chapters compare without their live parts (fill, current,
    /// played: the WinUI rows and map derive those from <c>CurrentChapter</c> and <c>State</c>).</summary>
    static PodcastEpisodeView Reuse(PodcastEpisodeView next, PodcastEpisodeView last)
    {
        if (SameChapters(next.Chapters, last.Chapters)) next = next with { Chapters = last.Chapters };
        if (SameLines(next.Transcript, last.Transcript)) next = next with { Transcript = last.Transcript };
        if (SameItems(next.More, last.More)) next = next with { More = last.More };
        if (SameItems(next.Similar, last.Similar)) next = next with { Similar = last.Similar };
        if (SameItems(next.Meta, last.Meta)) next = next with { Meta = last.Meta };
        return next;
    }

    static bool SameChapters(ImmutableArray<PodcastChapterVm> x, ImmutableArray<PodcastChapterVm> y)
    {
        if (x.IsDefault || y.IsDefault) return x.IsDefault == y.IsDefault;
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++)
            if (x[i] with { Fill = 0f, Current = false, Played = false } != y[i] with { Fill = 0f, Current = false, Played = false }) return false;
        return true;
    }

    static bool SameLines(ImmutableArray<PodcastLineVm> x, ImmutableArray<PodcastLineVm> y)
        => x.IsDefault || y.IsDefault ? x.IsDefault == y.IsDefault
            : x.Length == y.Length && (x.Length == 0 || (x[0] == y[0] && x[^1] == y[^1] && x[x.Length / 2] == y[y.Length / 2]));

    static bool SameItems<T>(ImmutableArray<T> x, ImmutableArray<T> y)
    {
        if (x.IsDefault || y.IsDefault) return x.IsDefault == y.IsDefault;
        if (x.Length != y.Length) return false;
        var c = EqualityComparer<T>.Default;
        for (int i = 0; i < x.Length; i++) if (!c.Equals(x[i], y[i])) return false;
        return true;
    }

    /// <summary>Content equality of two snapshots: records by value, the lists by their items, the menu by what it shows (its verbs are fresh closures).</summary>
    static bool SameView(PodcastEpisodeView a, PodcastEpisodeView b)
    {
        // The episode's own resting progress and time left are not drawn here (the page binds the live position): a stored resume point is no change.
        if (a.Load != b.Load || a.Episode with { Progress = 0f, Left = "" } != b.Episode with { Progress = 0f, Left = "" } || a.ShowUri != b.ShowUri || a.ShowTitle != b.ShowTitle || a.ShowArtUrl != b.ShowArtUrl
            || a.Tone != b.Tone || a.Crumb != b.Crumb || a.PrimaryLabel != b.PrimaryLabel || a.PrimaryAccent != b.PrimaryAccent
            || a.MapHead != b.MapHead || a.MapSub != b.MapSub || a.MapTime != b.MapTime || a.DurationMs != b.DurationMs
            || a.TranscriptLoad != b.TranscriptLoad || a.TranscriptSub != b.TranscriptSub || a.About != b.About || a.CommentCount != b.CommentCount
            || a.Next != b.Next || a.MoreSub != b.MoreSub || a.MapQuietFirst != b.MapQuietFirst || a.StillTime != b.StillTime
            || a.VideoLabel != b.VideoLabel || a.NoChapters != b.NoChapters || a.NoTranscript != b.NoTranscript
            || a.ChaptersPending != b.ChaptersPending || a.CommentsPending != b.CommentsPending || a.Blocked != b.Blocked)
            return false;
        // A chapter's resting fill moves with the position (the current one draws the live position; the others are 0 or 1 by their flags).
        if (a.Chapters.Length != b.Chapters.Length) return false;
        for (int i = 0; i < a.Chapters.Length; i++) if (a.Chapters[i] with { Fill = 0f } != b.Chapters[i] with { Fill = 0f }) return false;
        if (!Same(a.Meta, b.Meta) || !Same(a.More, b.More) || !Same(a.Similar, b.Similar)) return false;
        // The transcript's lines are rebuilt from the same document: the document's identity is in its first/last line and its length.
        if (a.Transcript.Length != b.Transcript.Length) return false;
        if (a.Transcript.Length > 0 && (a.Transcript[0] != b.Transcript[0] || a.Transcript[^1] != b.Transcript[^1])) return false;
        if (a.Menu.Length != b.Menu.Length) return false;
        for (int i = 0; i < a.Menu.Length; i++)
            if (a.Menu[i].Label != b.Menu[i].Label || a.Menu[i].Sub != b.Menu[i].Sub || a.Menu[i].Glyph != b.Menu[i].Glyph || a.Menu[i].Checked != b.Menu[i].Checked)
                return false;
        return true;

        static bool Same<T>(ImmutableArray<T> x, ImmutableArray<T> y)
        {
            if (x.IsDefault || y.IsDefault) return x.IsDefault == y.IsDefault;
            if (x.Length != y.Length) return false;
            var c = EqualityComparer<T>.Default;
            for (int i = 0; i < x.Length; i++) if (!c.Equals(x[i], y[i])) return false;
            return true;
        }
    }

    PodcastEpisodeView BuildView()
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        _ = scope.Episodes.Changed.Value;
        _ = scope.Shows.Changed.Value;
        _ = scope.Edges.ShowEpisodes.Changed.Value;
        _ = Spotify.Podcasts.SavedChanged.Value;
        var e = Current;
        if (!e.IsValid) return PodcastEpisodeView.Empty;
        var page = PodcastPages.EpisodeOf(e);
        if (!page.Loaded || !e.Knows(EpisodeFields.Title))
            return scope.Episodes.IsFailed(e.Slot, (uint)EpisodeFields.All) ? PodcastEpisodeView.Empty with { Load = PodcastLoad.Failed } : PodcastEpisodeView.Empty;

        var state = _stateMemo?.Value ?? StateOf();
        int chapterAt = _chapterMemo?.Value ?? -1;
        var show = e.Show;
        bool showKnown = show.IsValid && show.Knows(ShowFields.Title);
        bool serial = show.IsValid && show.Order == ConsumptionOrder.Sequential;
        var vm = Vm(e, serial, state);
        int duration = e.Knows(EpisodeFields.Duration) ? e.DurationMs : 0;

        // ── chapters (the fill is the resting one; the view binds the live position for the current chapter) ──
        var data = ChapterData();
        var items = Items();
        var chapters = ImmutableArray<PodcastChapterVm>.Empty;
        if (items.Length > 0)
        {
            var b = ImmutableArray.CreateBuilder<PodcastChapterVm>(items.Length);
            // A peek: the resting fill at build time (the current chapter's live fill is the view's bind on the position).
            int position = chapterAt < 0 ? 0 : Episode.IsNowPlaying(e) ? Playback.PositionMs.Peek() : e.ProgressMs;
            for (int i = 0; i < items.Length; i++)
            {
                var c = items[i];
                int end = c.EndMs > c.StartMs ? c.EndMs : i + 1 < items.Length ? items[i + 1].StartMs : duration;
                if (end <= c.StartMs) end = c.StartMs + 1;
                bool played = state == PodcastRowState.Played || (chapterAt >= 0 && i < chapterAt);
                float fill = played ? 1f : i == chapterAt ? Math.Clamp((position - c.StartMs) / (float)(end - c.StartMs), 0f, 1f) : 0f;
                string title = c.Title.Length > 0 ? c.Title : Strings.Podcast.Pages.ChapterN(i + 1);
                b.Add(new PodcastChapterVm(i, title, c.StartMs, end, Playback.TimeFormat.Clock(c.StartMs), Episode.DurationLabel(end - c.StartMs),
                    fill, i == chapterAt, played)
                {
                    Span = Playback.TimeFormat.Clock(c.StartMs) + MetaSep + Episode.DurationLabel(end - c.StartMs),
                    Number = FluentGpu.Foundation.FormatCache.Int(i + 1),
                });
            }
            chapters = b.MoveToImmutable();
            vm = vm with { ChapterCount = chapters.Length };
        }

        // ── the map head ──
        string mapHead = "", mapSub = "";
        bool quietFirst = false;
        if (chapterAt >= 0 && chapterAt < chapters.Length)
        {
            mapSub = Strings.Podcast.Pages.ChapterOf(chapterAt + 1, chapters.Length) + MetaSep;
            mapHead = chapters[chapterAt].Title;
            quietFirst = true;
        }
        else if (state == PodcastRowState.Played)
        {
            mapHead = Loc.Get(Strings.Podcast.Pages.Played);
            mapSub = e.PlayedAt > 0 ? MetaSep + Strings.Podcast.Pages.FinishedOn(Episode.DateLabel(e.PlayedAt)) : "";
        }
        else if (chapters.Length > 0)
        {
            mapHead = Strings.Podcast.Pages.ChaptersCount(chapters.Length);
            mapSub = MetaSep + Loc.Get(Strings.Podcast.Pages.SelectToStart);
        }

        // ── the transcript, the chapter titles its headings ──
        var lines = ImmutableArray<PodcastLineVm>.Empty;
        var tload = PodcastLoad.Pending;
        string tsub = "";
        if (_transcript is { } tl)
        {
            var ts = (LoadState)tl.State.Value;
            var doc = tl.Value.Value;
            if (ts == LoadState.Failed) tload = PodcastLoad.Failed;
            else if (ts == LoadState.Ready)
            {
                tload = PodcastLoad.Ready;
                lines = Lines(doc, chapters);
                string language = LanguageName(doc.Language.Length > 0 ? doc.Language : e.TranscriptLanguage);
                tsub = language.Length > 0 ? Strings.Podcast.Pages.TranscriptAuto(language) : "";
            }
        }

        // ── meta ──
        var meta = ImmutableArray.CreateBuilder<string>(4);
        if (e.PublishedAt > 0) meta.Add(DateWithYear(e.PublishedAt));
        if (duration > 0) meta.Add(Episode.DurationLabel(duration));
        if (chapters.Length > 0) meta.Add(Strings.Podcast.Pages.ChaptersCount(chapters.Length));
        if (state == PodcastRowState.Played) meta.Add(Loc.Get(Strings.Podcast.Pages.Played));

        // ── the primary ──
        // The resting word: Pause is the view's leaf (it binds Playing), so a press never rebuilds the snapshot.
        string primary = state == PodcastRowState.InProgress ? Loc.Get(Strings.Podcast.Pages.Resume)
            : state == PodcastRowState.Played ? Loc.Get(Strings.Podcast.Pages.PlayAgain)
            : Loc.Get(Strings.Podcast.Pages.Play);

        // ── the show's other episodes ──
        PodcastEpisodeVm? next = null;
        var more = ImmutableArray<PodcastEpisodeVm>.Empty;
        string moreSub = "";
        if (show.IsValid)
        {
            // The show's rows (the data layer's `ShowOf`, its own list order), split around this episode by publish date: the newer unplayed ones
            // nearest first, the older (the archive) newest first.
            var rowsOf = PodcastPages.ShowOf(show).Episodes;
            long at = page.PublishedUnixMs;
            var newer = new List<Episode>(NextRows + 1);
            var older = new List<Episode>(NextRows + 1);
            var byDate = new List<EpisodeRow>(rowsOf.Length);
            foreach (var r in rowsOf)
                if (r.Episode.Slot != e.Slot && r.Kind != EpisodeKind.Trailer && r.State != PlayState.Played && r.Episode.IsValid) byDate.Add(r);
            byDate.Sort(static (a, b) => a.PublishedUnixMs.CompareTo(b.PublishedUnixMs));
            foreach (var r in byDate) if (r.PublishedUnixMs > at && newer.Count < NextRows + 1) newer.Add(r.Episode);
            for (int i = byDate.Count - 1; i >= 0 && older.Count < NextRows + 1; i--) if (byDate[i].PublishedUnixMs <= at) older.Add(byDate[i].Episode);
            List<Episode> rows;
            bool archive;
            if (state == PodcastRowState.Played)
            {
                var up = newer.Count > 0 ? newer[0] : older.Count > 0 ? older[0] : default;
                if (up.IsValid)
                {
                    next = Vm(up, serial, RestState(up));
                    newer.Remove(up); older.Remove(up);
                }
                archive = older.Count > 0;
            }
            else archive = newer.Count == 0;
            rows = archive ? older : newer;
            if (rows.Count > NextRows) rows.RemoveRange(NextRows, rows.Count - NextRows);
            var mb = ImmutableArray.CreateBuilder<PodcastEpisodeVm>(rows.Count);
            foreach (var r in rows) mb.Add(Vm(r, serial, RestState(r)));
            more = mb.MoveToImmutable();
            moreSub = more.Length == 0 ? "" : Loc.Get(archive ? Strings.Podcast.Pages.ArchiveUnplayed : Strings.Podcast.Pages.NewerUnplayed);
        }

        // ── comments, similar ──
        int commentCount = _comments is { } cl && cl.State.Value == (byte)LoadState.Ready && cl.Value.Value.Ok ? cl.Value.Value.Total : 0;
        var similar = ImmutableArray<PodcastCardVm>.Empty;
        if (_similar is { } sl && sl.State.Value == (byte)LoadState.Ready && sl.Value.Value.Ok)
        {
            var recos = sl.Value.Value.Items;
            var sb = ImmutableArray.CreateBuilder<PodcastCardVm>(recos.Length);
            foreach (var r in recos) sb.Add(new PodcastCardVm(r.Uri, r.Title, r.Subtitle, r.Image.Length > 0 ? r.Image : null));
            similar = sb.MoveToImmutable();
        }

        var (about, _) = Episode.AboutProse(e);
        string? showArt = showKnown && show.Knows(ShowFields.Image) ? Controls.ArtUrl(show.ImageId) : null;
        return new PodcastEpisodeView(PodcastLoad.Ready, vm, show.IsValid ? show.Uri.Text : "", showKnown ? show.Title : "", showArt,
            show.IsValid ? show.Tone : 0u,
            e.Number > 0 ? Strings.Podcast.Pages.EpisodeN(e.Number) : "", meta.ToImmutable(), primary, state != PodcastRowState.Played,
            PrimaryMenu(e, state, serial), chapters, mapHead, mapSub, Clock(duration), duration,
            lines, tload, tsub, about, commentCount, next, moreSub, more, similar)
        {
            MapQuietFirst = quietFirst,
            StillTime = Clock(duration),
            VideoLabel = (e.Flags & EpisodeFlags.Video) != 0 ? Loc.Get(Strings.Podcast.Pages.Video) : "",
            NoChapters = Loc.Get(Strings.Podcast.Pages.NoChapters),
            ChaptersPending = data is null && _chapters.Value is { } lease && lease.Data.State.Value == (byte)LoadState.Pending,
            CommentsPending = _comments is { } cp && cp.State.Value == (byte)LoadState.Pending,
            NoTranscript = Loc.Get(Strings.Podcast.Pages.TranscriptNone),
            Blocked = PodcastPages.BlockedOf(e.Flags),
        };
    }

    static string Clock(int ms) => ms > 0 ? Playback.TimeFormat.Clock(ms) : "";

    static PodcastRowState RestState(Episode x)
    {
        float pct = Episode.ReaderPctOf(x);
        return Episode.Rules.Played(pct) ? PodcastRowState.Played : Episode.Rules.InProgress(pct) ? PodcastRowState.InProgress : PodcastRowState.Unplayed;
    }

    /// <summary>One episode as the page's rows and cards read it.</summary>
    static PodcastEpisodeVm Vm(Episode x, bool serial, PodcastRowState state)
    {
        int number = x.Number;
        string title = Episode.TitleSansNumber(x.Title, number);
        int duration = x.Knows(EpisodeFields.Duration) ? x.DurationMs : 0;
        float pct = Episode.ReaderPctOf(x);
        var flags = x.Flags;
        return PodcastPages.WithAccess(new PodcastEpisodeVm(x.Uri.Text, title, Episode.DescriptionOf(x), number, serial,
            x.PublishedAt > 0 ? Episode.DateLabel(x.PublishedAt) : "", x.PublishedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(x.PublishedAt).Year : 0,
            Episode.DurationLabel(duration), state == PodcastRowState.InProgress ? Episode.LeftLabel(x.ProgressMs, duration) : "", pct, state,
            Spotify.Podcasts.IsSaved(x), (flags & EpisodeFlags.Video) != 0, x.Kind == EpisodeKind.Trailer, (flags & EpisodeFlags.Unplayable) == 0,
            0, (flags & EpisodeFlags.HasTranscript) != 0, Episode.ArtOf(x))
        {
            Explicit = (flags & EpisodeFlags.Explicit) != 0, Bonus = x.Kind == EpisodeKind.Bonus,
        }, flags);
    }

    /// <summary>"Mar 14 2023": the day label and the year.</summary>
    static string DateWithYear(int unixSeconds)
    {
        string day = Episode.DateLabel(unixSeconds);
        int year = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).Year;
        return day.Length == 0 ? "" : day + " " + year.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The transcript's spoken lines; a chapter's number and title head the first line at or after its start (the wire's own heading lines
    /// when the episode has no chapters).</summary>
    static ImmutableArray<PodcastLineVm> Lines(Spotify.Podcasts.Transcript doc, ImmutableArray<PodcastChapterVm> chapters)
    {
        if (doc.Lines.Length == 0) return [];
        var b = ImmutableArray.CreateBuilder<PodcastLineVm>(doc.Lines.Length);
        int nextChapter = 0;
        string? pending = null;
        foreach (var l in doc.Lines)
        {
            if (l.Heading)
            {
                if (chapters.IsEmpty && l.Text.Length > 0) pending = l.Text;
                continue;
            }
            if (string.IsNullOrWhiteSpace(l.Text)) continue;
            string? heading = pending;
            pending = null;
            while (nextChapter < chapters.Length && chapters[nextChapter].StartMs <= l.StartMs)
            {
                var c = chapters[nextChapter++];
                heading = (c.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + MetaSep + c.Title;
            }
            b.Add(new PodcastLineVm(l.StartMs, Playback.TimeFormat.Clock(l.StartMs), l.Text, heading));
        }
        return b.ToImmutable();
    }

    /// <summary>The transcript's language by name: the app runs with invariant globalization (no culture table), so the common ones are named here and
    /// any other shows its tag.</summary>
    static string LanguageName(string? tag)
    {
        string code = LanguageCode(tag);
        return code switch
        {
            "EN" => "English", "DE" => "Deutsch", "NL" => "Nederlands", "FR" => "Français", "ES" => "Español", "IT" => "Italiano",
            "PT" => "Português", "SV" => "Svenska", "JA" => "日本語", "EL" => "Ελληνικά",
            _ => code,
        };
    }

    /// <summary>A BCP-47 tag's primary subtag, upper-cased ("en-US" is "EN"); "" when there is none or it is not a language subtag.</summary>
    static string LanguageCode(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var span = tag.AsSpan().Trim();
        int cut = span.IndexOfAny('-', '_');
        if (cut > 0) span = span[..cut];
        return span.Length is 0 or > 8 ? "" : span.ToString().ToUpperInvariant();
    }

    /// <summary>The smart action's menu: what the button plays, starting over, play next, the show's newest and a serial's trailer.</summary>
    ImmutableArray<PodcastMenuItem> PrimaryMenu(Episode e, PodcastRowState state, bool serial)
    {
        var b = ImmutableArray.CreateBuilder<PodcastMenuItem>(5);
        string label = state == PodcastRowState.InProgress ? Loc.Get(Strings.Podcast.Pages.Resume)
            : state == PodcastRowState.Played ? Loc.Get(Strings.Podcast.Pages.PlayAgain) : Loc.Get(Strings.Podcast.Pages.Play);
        b.Add(new PodcastMenuItem(label, Episode.TitleSansNumber(e.Title, e.Number), Icons.Play, true, _play));
        if (state != PodcastRowState.Unplayed)
            b.Add(new PodcastMenuItem(Loc.Get(Strings.Podcast.Pages.StartOver), null, Icons.Undo, false,
                () => Episode.StartAt(Current, Playback.EpisodeStartKind.Beginning)));
        var show = e.Show;
        if (show.IsValid)
        {
            var slots = Entities.Current.Edges.ShowEpisodes.Targets(show.Slot);
            if (slots.Length > 0 && slots[0] != e.Slot)
            {
                var newest = new Episode(slots[0]);
                if (newest.IsValid && newest.Knows(EpisodeFields.Title))
                {
                    string id = newest.Uri.Text;
                    b.Add(new PodcastMenuItem(Loc.Get(Strings.Podcast.Pages.PlayNewest), Episode.TitleSansNumber(newest.Title, newest.Number), Icons.Play, false,
                        () => PlayOther(id)));
                }
            }
            if (serial && show.Knows(ShowFields.Facts) && !show.TrailerId.IsEmpty)
            {
                var trailer = new Episode(Entities.Current.Episodes.Slot(show.TrailerId));
                if (trailer.IsValid && trailer.Slot != e.Slot)
                {
                    string id = trailer.Uri.Text;
                    b.Add(new PodcastMenuItem(Loc.Get(Strings.Podcast.Pages.PlayTrailer), Episode.DurationLabel(trailer.DurationMs), Icons.Play, false,
                        () => PlayOther(id)));
                }
            }
        }
        return b.ToImmutable();
    }

    // ══ THE VERBS ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    void Play()
    {
        var e = Current;
        if (!e.IsValid) return;
        bool played = Episode.Rules.Played(Episode.ReaderPctOf(e));
        Episode.Invoke(e, () => Episode.StartAt(e, played ? Playback.EpisodeStartKind.Beginning : Playback.EpisodeStartKind.Resume));
    }

    /// <summary>Seek to a chapter or a transcript line: starts playback there (the deck episode seeks and resumes; any other starts at the position) and
    /// turns following back on, so the transcript centres the line the listener chose.</summary>
    void SeekTo(int ms)
    {
        var e = Current;
        if (!e.IsValid) return;
        ms = Math.Max(0, ms);
        if (Episode.IsNowPlaying(e) && Playback.PhaseSignal.Peek() != Playback.Phase.Idle)
        {
            Playback.SeekTo(ms);
            if (!Playback.IsPlaying.Peek()) Playback.TogglePlay("episode.seek");
        }
        else Episode.StartAt(e, Playback.EpisodeStartKind.Position, ms);
        _follow.Value = true;
    }

    void ToggleSaved() { var e = Current; if (e.IsValid) Spotify.Podcasts.ToggleSaved(e); }

    void ToggleCompleted()
    {
        var e = Current;
        if (e.IsValid) Entities.MarkEpisode(e, !Episode.Rules.Played(Episode.ReaderPctOf(e)));
    }

    void OpenShow()
    {
        var e = Current;
        if (!e.IsValid || !e.Show.IsValid) return;
        Shell.GoTo(Shell.For(e.Show.Uri, e.Show.Title));
    }

    void Share() { var e = Current; if (e.IsValid) Episode.CopyLink(Actions.WebLinkOf(e.Uri)); }

    ContextMenuModel? MoreMenu()
    {
        var e = Current;
        return e.IsValid ? Episode.Menu(e, new Episode.MenuOptions(ShowGoToShow: true)) : null;
    }

    static Episode Other(string uri) => EntityId.TryParse(uri, out var id) ? Episode.ReaderEpisode(new EntityUri(id)) : default;

    static void OpenOther(string uri) { var x = Other(uri); if (x.IsValid) Episode.OpenPage(x); }

    static void PlayOther(string uri)
    {
        var x = Other(uri);
        if (!x.IsValid) return;
        bool played = Episode.Rules.Played(Episode.ReaderPctOf(x));
        Episode.Invoke(x, () => Episode.StartAt(x, played ? Playback.EpisodeStartKind.Beginning : Playback.EpisodeStartKind.Resume));
    }

    static void PlayNextOther(string uri) { var x = Other(uri); if (x.IsValid) Episode.Enqueue(x, true); }
}
