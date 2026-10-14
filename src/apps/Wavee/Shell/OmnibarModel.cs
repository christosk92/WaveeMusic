// ── Shell/OmnibarModel.cs — the omnibar's suggestion model, its store and its verbs ────────────────────────────────
//
// Role: CORE (themes plan §5.1, PR C8; extraction X23). Two halves:
//   · `Shell.Omnibar` (moved verbatim from `Shell.cs` §14): the row kinds, the answer shapes, the request lifecycle
//     (`Query`, a generation-keyed state machine) and the pure row / height / cursor rules. The omnibar is owner I's; its
//     SUGGESTION SOURCE is owner P's (`Shell.Omnibar.Source`, Wave 5). Until P installs it the popup answers from the
//     navigation log the CORE already has, so the field is never a dead end. Nested under `Shell` so P's own `Search.cs`
//     types cannot collide with these names.
//   · `OmnibarModel` (below): the chrome row's ONE store over that lifecycle, plus what `Shell.Masthead.UI.cs` used to decide
//     inline — the keystroke and fetch edges, the in-flight request with its cancellation, the focus tickets, and the
//     choose / play / menu decisions. The field and the popup are views over it.

using FluentGpu.Controls;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Shell
{
    // ══ 14. THE OMNIBAR'S MODEL (ch 18 W15) — the seam owner P's `Search.cs` plugs into ══════════════════════════════
    //
    // The omnibar is owner I's; its SUGGESTION SOURCE is owner P's (Wave 5). Until P installs <see cref="Omnibar.Source"/>
    // the popup answers from the navigation log the CORE already has, so the field is never a dead end. Nested under
    // `Shell` so P's own `Search.cs` types (Wave 5) cannot collide with these names.

    public static class Omnibar
    {
        /// <summary>A rich row's kind — what it looks like and what choosing it does.</summary>
        public enum ItemKind : byte { Track, Artist, Album, Playlist, Genre, Episode, Podcast, Audiobook, User }

        /// <summary>One rich suggestion row.</summary>
        public sealed record Item(ItemKind Kind, EntityUri Uri, string Title, string? Subtitle = null, string? ImageUrl = null, AlbumKind? ReleaseKind = null)
        {
            /// <summary>The row's entity (kind + table slot), allocating the row if unseen; <see cref="EntityRef.IsNone"/>
            /// for a kind with no table (a genre, an audiobook). <see cref="Subtitle"/> is the DETAIL only (artists / owner /
            /// publisher / show) — the row composes its kind word and <see cref="TextJoin.Sep"/> (<c>Search.SubtitleText</c>).</summary>
            public EntityRef Ref => Entities.Ref(Uri.Id);
        }

        /// <summary>One answer: the query completions, then the rich rows.</summary>
        public sealed record Suggestions(IReadOnlyList<string> Queries, IReadOnlyList<Item> Items)
        {
            public static readonly Suggestions Empty = new(Array.Empty<string>(), Array.Empty<Item>());

            public bool IsEmpty => Queries.Count == 0 && Items.Count == 0;

            /// <summary>The inline ghost: the FIRST completion that starts with what was typed and is longer — not blindly
            /// <c>Queries[0]</c> (<c>loff</c> ghosts <c>loffler</c> even when <c>koffie</c> ranks first).</summary>
            public static string? GhostFor(string? typed, IReadOnlyList<string>? queries)
            {
                if (string.IsNullOrEmpty(typed) || queries is null) return null;
                for (int i = 0; i < queries.Count; i++)
                {
                    string q = queries[i];
                    if (q.Length > typed.Length && q.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return q;
                }
                return null;
            }
        }

        /// <summary>The seam: owner P's suggest request. Null ⇒ the popup answers from the navigation log
        /// (<see cref="FromHistory"/>). Called off the UI thread's await; the omnibar posts the answer back.</summary>
        public static Func<string, CancellationToken, Task<Suggestions>>? Source { get; set; }

        /// <summary>Row caps, and the same two caps bound the keyboard cursor and the invoke.</summary>
        public const int MaxQueryRows = 6, MaxRichRows = 10;

        public static int QueryRowCount(Suggestions s) => Math.Min(MaxQueryRows, s.Queries.Count);
        public static int RichRowCount(Suggestions s) => Math.Min(MaxRichRows, s.Items.Count);
        public static int SelectableCount(Suggestions s) => QueryRowCount(s) + RichRowCount(s);

        // ── the popup body's reserved height ─────────────────────────────────────────────────────────────────────────
        // The flyout never sizes itself by async data: while a request is Pending with nothing to show it reserves what the
        // PREVIOUS answer took (else a typical first answer), and the body is capped where the scroll viewport caps.

        /// <summary>The scroll viewport's cap, and so the most the popup body ever takes.</summary>
        public const float PopupBodyMaxHeight = 560f;

        /// <summary>One query row (the 40 floor plus its 2 + 2 margin), one rich row (the 58 floor plus its 2 + 2 margin),
        /// and the divider that separates the two groups (1 high, 4 + 4 margin).</summary>
        public const float QueryRowHeight = 44f, RichRowHeight = 62f, GroupDividerHeight = 9f;

        /// <summary>The notice slot ("No results found" / the failure + Retry): the stock list item floor.</summary>
        public const float NoticeHeight = 40f;

        /// <summary>The placeholder rows a Pending body is built from: the previous answer's own row counts when it had any,
        /// else <see cref="MaxQueryRows"/> query rows + 3 rich ones. The counts are not trimmed to the cap: the reserved
        /// height is <see cref="PopupBodyHeight"/> and the body's clip cuts the last row, like the real viewport.</summary>
        public static (int Query, int Rich) PendingSkeletonRows(int lastQueryRows, int lastRichRows)
        {
            int q = Math.Clamp(lastQueryRows, 0, MaxQueryRows);
            int r = Math.Clamp(lastRichRows, 0, MaxRichRows);
            return q + r == 0 ? (MaxQueryRows, 3) : (q, r);
        }

        /// <summary>The height a Pending body with nothing to show reserves: one notice slot when the last settled answer
        /// was a notice (Empty / Failed), else the previous answer's rows (or a typical first answer), capped at
        /// <see cref="PopupBodyMaxHeight"/>. Typing on past a no-match query therefore never pumps the card.</summary>
        public static float PendingBodyHeight(int lastQueryRows, int lastRichRows, bool lastWasNotice)
        {
            if (lastWasNotice) return NoticeHeight;
            var (q, r) = PendingSkeletonRows(lastQueryRows, lastRichRows);
            return PopupBodyHeight(q, r);
        }

        /// <summary>The body height of <paramref name="queryRows"/> + <paramref name="richRows"/> rows, never above
        /// <see cref="PopupBodyMaxHeight"/> (past it the viewport scrolls).</summary>
        public static float PopupBodyHeight(int queryRows, int richRows)
            => MathF.Min(PopupBodyMaxHeight, RawBodyHeight(Math.Max(0, queryRows), Math.Max(0, richRows)));

        static float RawBodyHeight(int q, int r)
            => q * QueryRowHeight + r * RichRowHeight + (q > 0 && r > 0 ? GroupDividerHeight : 0f);

        /// <summary>↑/↓ over the visible rows, wrapping through "none" (−1) at both ends.</summary>
        public static int MoveHighlight(int current, int delta, int count)
        {
            if (count <= 0) return -1;
            return delta > 0
                ? (current + 1 >= count ? -1 : current + 1)
                : (current < 0 ? count - 1 : current - 1);
        }

        /// <summary>▶ is offered for everything that can play (not a profile, not a genre).</summary>
        public static bool CanPlay(ItemKind kind) => kind is not (ItemKind.User or ItemKind.Genre);

        /// <summary>People are circles (radius 22); everything else a 5-DIP rounded square.</summary>
        public static bool IsCircular(ItemKind kind) => kind is ItemKind.Artist or ItemKind.User;

        /// <summary>Choosing a row PLAYS it (a track, an episode) rather than navigating. Deliberately UNCHANGED by the
        /// podcast rework's D-2 (an episode DEEP LINK opens its page): picking a rich row here is the user explicitly
        /// choosing something they just searched for — the same explicit-play action <c>Search.Hits.cs</c>'s hit rows and
        /// <c>Recents.UI.cs</c>'s rows are, not a passively-received shared link. <see cref="RouteFor"/> below has no
        /// Episode arm (it falls to <see cref="Route.None"/>) precisely because choosing one never navigates.</summary>
        public static bool ChoosePlays(ItemKind kind) => kind is ItemKind.Track or ItemKind.Episode;

        /// <summary>Where choosing a row navigates, or <see cref="Route.None"/> for a row that plays (or a profile row
        /// whose uri is not a <c>spotify:user:</c> one — a profile with a user uri opens its page, #161).</summary>
        public static Route RouteFor(Item item) => item.Kind switch
        {
            ItemKind.User when item.Uri.Kind == EntityKind.User => new Route(RouteKind.User, item.Uri, Intern(item.Title)),
            ItemKind.Artist => new Route(RouteKind.Artist, item.Uri, Intern(item.Title)),
            ItemKind.Album => new Route(RouteKind.Album, item.Uri, Intern(item.Title)),
            ItemKind.Playlist => new Route(RouteKind.Playlist, item.Uri, Intern(item.Title)),
            ItemKind.Podcast or ItemKind.Audiobook => new Route(RouteKind.Show, item.Uri, Intern(item.Title)),
            ItemKind.Genre => new Route(RouteKind.BrowseCategory, item.Uri, Intern(item.Title)),
            _ => Route.None,
        };

        /// <summary>A GENRE row carries its search-lookup origin, so its masthead reads <c>"&lt;query&gt;" › Genre</c> with
        /// no Browse rung (ch 18 parity 74). Port the origin write with the row, not just the navigation.</summary>
        public static NavOrigin? GenreOrigin(string? query)
        {
            string q = (query ?? "").Trim();
            return q.Length == 0 ? null : new NavOrigin(q, new Route(RouteKind.Search, default, Intern(q)));
        }

        /// <summary>The answer the popup gives with no source installed: the user's own recent searches that start or
        /// contain the typed text (newest first), then the recent ENTITY destinations whose title contains it. Distinct
        /// by route key; capped at the row caps. Empty text answers nothing.</summary>
        public static Suggestions FromHistory(IReadOnlyList<HistoryEntry> entries, ReadOnlySpan<char> typed)
        {
            var t = typed.Trim();
            if (t.IsEmpty || entries.Count == 0) return Suggestions.Empty;
            string needle = t.ToString();
            var queries = new List<string>(MaxQueryRows);
            var items = new List<Item>(MaxRichRows);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var r = entries[i].Route;
                if (r.Kind == RouteKind.Search)
                {
                    if (queries.Count >= MaxQueryRows || ArgOf(r) is not { Length: > 0 } q) continue;
                    if (!q.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Add("q" + q)) queries.Add(q);
                    continue;
                }
                if (items.Count >= MaxRichRows) continue;
                ItemKind? kind = r.Kind switch
                {
                    RouteKind.Album => ItemKind.Album,
                    RouteKind.Playlist => ItemKind.Playlist,
                    RouteKind.Artist => ItemKind.Artist,
                    RouteKind.Show => ItemKind.Podcast,
                    RouteKind.User => ItemKind.User,
                    _ => null,
                };
                if (kind is not { } k || !r.Subject.IsValid) continue;
                string title = Dest(r).Title;
                if (!title.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(NameOf(r))) items.Add(new Item(k, r.Subject, title));
            }
            return queries.Count == 0 && items.Count == 0 ? Suggestions.Empty : new Suggestions(queries, items);
        }

        /// <summary>Where the suggestion request stands. The popup renders BY this, and only <see cref="Empty"/> may say
        /// "No results found".</summary>
        public enum State : byte
        {
            /// <summary>No query — the field is blank; the popup is closed.</summary>
            Idle,
            /// <summary>A query exists and its answer has not landed — including the debounce window BEFORE the request is
            /// sent. The popup shows a progress row (plus the previous answer's rows) and no sentence.</summary>
            Pending,
            Results,
            Empty,
            /// <summary>The request did not produce an answer — a retry, never "nothing matched".</summary>
            Failed,
        }

        /// <summary>The suggestion request lifecycle — a GENERATION-keyed state machine (0.2.9 <c>OmnibarSuggestQuery</c>,
        /// ported verbatim). <see cref="Begin"/> runs on the UNDEBOUNCED keystroke and enters Pending at once, so "No
        /// results found" never flashes between a keystroke and its request; an answer for any other generation is
        /// dropped. Text equality is deliberately NOT the publish guard: a superseded response for a retyped query must
        /// lose to the newer request even when the texts match. UI thread only.</summary>
        public sealed class Query
        {
            int _generation;

            public int Generation => _generation;
            public string Text { get; private set; } = "";
            public State State { get; private set; } = State.Idle;

            /// <summary>While Pending these are the PREVIOUS answer's rows — the field does not blank on every keystroke.</summary>
            public Suggestions Suggestions { get; private set; } = Suggestions.Empty;

            public Exception? Failure { get; private set; }
            public bool IsPending => State == State.Pending;

            /// <summary>Raised synchronously after every state change.</summary>
            public event Action? Changed;

            /// <summary>The keystroke edge. Blank clears; the same trimmed text keeps the generation (an in-flight request
            /// stays valid); anything else starts a new generation in Pending. Returns the generation the answer must
            /// carry.</summary>
            public int Begin(string? query)
            {
                string q = (query ?? "").Trim();
                if (q.Length == 0) { Clear(); return _generation; }
                if (q == Text && State != State.Idle) return _generation;
                _generation++;
                Text = q;
                State = State.Pending;
                Failure = null;
                Changed?.Invoke();
                return _generation;
            }

            /// <summary>Re-arm a FAILED query as a new pending generation; a no-op in every other state.</summary>
            public int Retry()
            {
                if (State != State.Failed) return _generation;
                _generation++;
                State = State.Pending;
                Failure = null;
                Changed?.Invoke();
                return _generation;
            }

            /// <summary>Publish an answer for <paramref name="generation"/>; false (nothing changes) when superseded.</summary>
            public bool Complete(int generation, Suggestions suggestions)
            {
                if (generation != _generation) return false;
                Suggestions = suggestions;
                State = suggestions.IsEmpty ? State.Empty : State.Results;
                Failure = null;
                Changed?.Invoke();
                return true;
            }

            /// <summary>Publish a failure. False for a superseded generation AND for a cancellation of the current one —
            /// cancellation is not an answer (whoever cancelled moved on or is tearing the field down).</summary>
            public bool Fail(int generation, Exception failure)
            {
                if (generation != _generation || failure is OperationCanceledException) return false;
                Suggestions = Suggestions.Empty;
                State = State.Failed;
                Failure = failure;
                Changed?.Invoke();
                return true;
            }

            /// <summary>Back to Idle (NOT Empty: a blank field has no answer). Advances the generation so a late answer for
            /// the cleared query is dropped.</summary>
            public void Clear()
            {
                if (State == State.Idle) return;
                _generation++;
                Text = "";
                State = State.Idle;
                Suggestions = Suggestions.Empty;
                Failure = null;
                Changed?.Invoke();
            }
        }
    }
}

// ══ THE OMNIBAR MODEL ════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>What the omnibar needs from the shell, every effect a delegate: the suggestion source, the log fallback, the
/// search text cell, the focus request, navigation and the play seam. The live set reads the shell's statics LAZILY (inside
/// the delegates), so building the model never depends on static initialisation order; the tests pass fakes.</summary>
public sealed record OmnibarPorts(
    Func<Func<string, CancellationToken, Task<Shell.Omnibar.Suggestions>>?> Source,
    Func<string, Shell.Omnibar.Suggestions> FromHistory,
    Action<string> SetSearchText,
    Action RequestFocus,
    Action<Shell.Route, Shell.NavOrigin?> Go,
    Func<string, Shell.Route> SearchRoute,
    Func<Action<EntityUri>?> PlayContext)
{
    /// <summary>The shell's own.</summary>
    public static OmnibarPorts Live() => new(
        Source: static () => Shell.Omnibar.Source,
        FromHistory: static text => Shell.Omnibar.FromHistory(Shell.History.Store.Entries, text),
        SetSearchText: static text => Shell.SearchText.Value = text,
        RequestFocus: static () => Shell.SearchFocusRequest.Value = Shell.SearchFocusRequest.Peek() + 1,
        Go: static (route, origin) => Shell.GoTo(route, origin),
        SearchRoute: static arg => Shell.Parse("search", arg),
        PlayContext: static () => Shell.OnPlayContext);
}

/// <summary>The omnibar's verbs: what a keystroke, a click on a suggestion and the keyboard DO, for any theme that draws the
/// field and its popup.</summary>
public interface IOmnibarVerbs
{
    /// <summary>Put <paramref name="text"/> in the field (the search text IS the route's query).</summary>
    void SetQuery(string text);

    /// <summary>Enter on the field: open the search page for the trimmed text.</summary>
    void Submit(string query);

    /// <summary>Choose the suggestion at <paramref name="selection"/> (the visible rows, query rows first). False when there
    /// is nothing at that index.</summary>
    bool Choose(int selection);

    /// <summary>The row's ▶: a track or an episode plays through the context seam (a current track toggles pause); anything
    /// else that can play toggles its context.</summary>
    void Play(Shell.Omnibar.Item item);

    /// <summary>Whether the row offers a context menu at all.</summary>
    bool HasRowMenu(Shell.Omnibar.Item item);

    /// <summary>The row's context menu (built at open time).</summary>
    ContextMenuModel? RowMenu(Shell.Omnibar.Item item);

    /// <summary>Retry a failed suggestion request.</summary>
    void Retry();

    /// <summary>Ask for keyboard focus in the field (Ctrl+F, or the allocator handing the caret to a new form).</summary>
    void Focus();

    /// <summary>Escape: the icon-mode flyout is closed.</summary>
    void Escape();
}

/// <summary>The chrome row's ONE suggestion store and its verbs: the lifecycle (<see cref="Query"/>), a version bumped on
/// every change (the render subscription), the keyboard cursor over the visible rows, the focus tickets, and the two
/// mounts' shared facts. The field mode and the icon-mode flyout are two mounts of ONE omnibar over ONE model, so a
/// search-mode flip keeps the rows, the pending generation and the cursor.</summary>
public sealed class OmnibarModel : IOmnibarVerbs
{
    readonly OmnibarPorts _ports;
    int _focusTicketHandled;

    public OmnibarModel(OmnibarPorts ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        _ports = ports;
        Query.Changed += () => Version.Value = Version.Peek() + 1;
    }

    /// <summary>The suggestion request lifecycle (a generation-keyed state machine).</summary>
    public Shell.Omnibar.Query Query { get; } = new();

    /// <summary>Bumped on every <see cref="Query"/> change.</summary>
    public Signal<int> Version { get; } = new(0);

    /// <summary>The keyboard cursor over the visible rows; -1 = none.</summary>
    public Signal<int> Highlight { get; } = new(-1);

    /// <summary>The search field has focus — the chrome allocator reads it to hand the caret to the new form across a mode flip.</summary>
    public Signal<bool> SearchFocused { get; } = new(false);

    /// <summary>The icon-mode flyout is open — read with <see cref="SearchFocused"/> by the allocator.</summary>
    public Signal<bool> FlyoutOpen { get; } = new(false);

    // ── what a theme's view reads (T4b): the members below name no `Shell` type, so a view in Themes\** can use them ─────

    /// <summary>The generation the current query is on (<c>Shell.Omnibar.Query.Generation</c>): the fetch edge settles on it.</summary>
    public int QueryGeneration => Query.Generation;

    /// <summary>The trimmed text the current query was started with (<c>Shell.Omnibar.Query.Text</c>).</summary>
    public string QueryText => Query.Text;

    /// <summary>The inline ghost for <paramref name="typed"/>: the first completion that starts with it and is longer, or "" when
    /// there is none (<c>Shell.Omnibar.Suggestions.GhostFor</c> over the current answer's completions).</summary>
    public string GhostFor(string typed) => Shell.Omnibar.Suggestions.GhostFor(typed, Query.Suggestions.Queries) ?? "";

    // ── the keystroke and fetch edges ────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE KEYSTROKE EDGE, undebounced: Pending starts NOW, so "No results found" never flashes between a keystroke
    /// and its request. True when the generation moved, so the caller cancels its in-flight request.</summary>
    public bool Begin(string typed)
    {
        int before = Query.Generation;
        return Query.Begin(typed) != before;
    }

    /// <summary>The fetch edge: the GENERATION settled and is still waiting for its answer (a retype to the same text is a
    /// new generation and must be answered; a re-mount finds a Pending generation whose request died and re-issues it).</summary>
    public bool NeedsFetch(int settledGeneration) => settledGeneration == Query.Generation && Query.IsPending;

    /// <summary>One request slot for one mounted form: it cancels its own in-flight request.</summary>
    public Fetch NewFetch() => new(this);

    // ── the focus tickets ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Act on a focus ticket (<c>Shell.SearchFocusRequest</c>) at most once, and only if it is NEWER than the last one
    /// a form consumed: a form mounting later (a mode flip) never acts on a Ctrl+F from minutes ago.</summary>
    public bool ConsumeFocusTicket(int ticket)
    {
        if (ticket <= _focusTicketHandled) return false;
        _focusTicketHandled = ticket;
        return true;
    }

    // ── the keyboard cursor ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>↑/↓ over the visible rows, wrapping through "none" at both ends.</summary>
    public void MoveHighlight(int delta)
        => Highlight.Value = Shell.Omnibar.MoveHighlight(Highlight.Peek(), delta, Shell.Omnibar.SelectableCount(Query.Suggestions));

    /// <summary>The popup closed: no row is selected.</summary>
    public void ResetHighlight() => Highlight.Value = -1;

    /// <summary>The root that mounted the omnibar's forms is going away (a root remount, <c>RootScope</c>): put back the facts only
    /// a MOUNTED form can truth-tell about: the caret is nowhere, the icon-mode flyout is closed, no row is selected, and a focus
    /// ticket asked before the remount (<paramref name="currentTicket"/> is <c>Shell.SearchFocusRequest</c> now) is never acted on by
    /// the new forms. The typed text (<c>Shell.SearchText</c>) and the suggestion store (<see cref="Query"/>) are PROCESS state and
    /// stay: a re-mount finds a Pending generation whose request died and re-issues it (<see cref="NeedsFetch"/>).</summary>
    public void ResetMountedFacts(int currentTicket)
    {
        SearchFocused.SetIfChanged(false);
        FlyoutOpen.SetIfChanged(false);
        Highlight.SetIfChanged(-1);
        if (currentTicket > _focusTicketHandled) _focusTicketHandled = currentTicket;
    }

    // ── IOmnibarVerbs ────────────────────────────────────────────────────────────────────────────────────────────────

    public void SetQuery(string text) => _ports.SetSearchText(text);

    public void Submit(string query) => _ports.Go(_ports.SearchRoute(query.Trim()), null);

    public bool Choose(int selection)
    {
        var s = Query.Suggestions;
        int queryRows = Shell.Omnibar.QueryRowCount(s);
        if (selection < 0 || selection >= Shell.Omnibar.SelectableCount(s)) return false;
        if (selection < queryRows)
        {
            string chosen = s.Queries[selection];
            SetQuery(chosen);
            _ports.Go(_ports.SearchRoute(chosen), null);
        }
        else
        {
            ChooseItem(s.Items[selection - queryRows], Query.Text);
        }
        return true;
    }

    /// <summary>A rich row's choice: a track or an episode PLAYS (through the context seam); everything else navigates, and a
    /// genre carries its search-lookup origin so its masthead reads <c>"query" › Genre</c>.</summary>
    public void ChooseItem(Shell.Omnibar.Item item, string? query)
    {
        if (Shell.Omnibar.ChoosePlays(item.Kind))
        {
            _ports.PlayContext()?.Invoke(item.Uri);
            return;
        }
        var route = Shell.Omnibar.RouteFor(item);
        if (route.IsNone) return;
        _ports.Go(route, item.Kind == Shell.Omnibar.ItemKind.Genre ? Shell.Omnibar.GenreOrigin(query) : null);
    }

    public void Play(Shell.Omnibar.Item item)
    {
        var kind = item.Kind;
        if (!Shell.Omnibar.CanPlay(kind)) return;
        if (!Shell.Omnibar.ChoosePlays(kind))
        {
            Playback.PlayOrToggleContext(item.Uri.Text);
            return;
        }
        var rf = item.Ref;   // allocates the table row if unseen (UI thread)
        var playContext = _ports.PlayContext();
        // A track/episode that is the current item toggles pause (the row verb every list row uses); else the host's play.
        // Without a table slot, the uri toggle compares playables with the item.
        if (kind == Shell.Omnibar.ItemKind.Track && rf.Kind == EntityKind.Track && rf.Slot > 0)
            Track.Invoke(new Track(rf.Slot), () => playContext?.Invoke(item.Uri));
        else if (playContext is { } start
                 && ContextPlayRules.For(item.Uri.Id, Playback.ContextUri.Peek(), Playback.CurrentId.Peek(), Playback.Error.Peek()) == ContextPlayAction.Start)
            start(item.Uri);
        else
            Playback.PlayOrToggleContext(item.Uri.Id);
    }

    public bool HasRowMenu(Shell.Omnibar.Item item)
    {
        var rf = item.Ref;
        return OmnibarRowRules.MenuOf(item.Kind) switch
        {
            OmnibarRowRules.MenuKind.Track => !rf.IsNone,
            OmnibarRowRules.MenuKind.Container => true,
            _ => false,
        };
    }

    public ContextMenuModel? RowMenu(Shell.Omnibar.Item item)
    {
        var rf = item.Ref;
        switch (OmnibarRowRules.MenuOf(item.Kind))
        {
            case OmnibarRowRules.MenuKind.Track when !rf.IsNone:
                Track.EnsureActions();
                return Track.Menu([new Track(rf.Slot)], new Track.MenuOptions(ShowGoToAlbum: true));
            case OmnibarRowRules.MenuKind.Container:
                return Menus.Container(ContainerTargetOf(item), item.ImageUrl, item.Subtitle);
            default:
                return null;
        }
    }

    static ActionTarget ContainerTargetOf(Shell.Omnibar.Item item) => item.Kind switch
    {
        Shell.Omnibar.ItemKind.Album => ActionTarget.ForAlbum(item.Uri, item.Title),
        Shell.Omnibar.ItemKind.Artist => ActionTarget.ForArtist(item.Uri, item.Title),
        Shell.Omnibar.ItemKind.Playlist => ActionTarget.ForPlaylist(item.Uri, item.Title),
        _ => ActionTarget.ForShow(item.Uri, item.Title),
    };

    public void Retry() => Query.Retry();

    public void Focus() => _ports.RequestFocus();

    public void Escape() => FlyoutOpen.SetIfChanged(false);

    /// <summary>One mounted form's request slot. The request in flight for the current generation: a superseding keystroke, a
    /// clear and an unmount cancel it; the store drops whatever a cancelled request would have said, so cancelling is only
    /// ever a saving.</summary>
    public sealed class Fetch
    {
        readonly OmnibarModel _model;
        CancellationTokenSource? _inflight;

        internal Fetch(OmnibarModel model) => _model = model;

        /// <summary>Whether a request is in flight.</summary>
        public bool InFlight => _inflight is not null;

        public void Cancel()
        {
            _inflight?.Cancel();
            _inflight = null;
        }

        /// <summary>Issue the request for <paramref name="generation"/>, cancelling any earlier one. No source installed (a
        /// Wave-4 build, <c>--fake</c>): the navigation log answers, synchronously. The returned task completes when the answer
        /// (or the failure) has been handed to <paramref name="post"/>.</summary>
        public Task Start(Action<Action> post, int generation, string text)
        {
            Cancel();
            if (_model._ports.Source() is not { } source)
            {
                _model.Query.Complete(generation, _model._ports.FromHistory(text));
                return Task.CompletedTask;
            }
            var cts = new CancellationTokenSource();
            _inflight = cts;
            return RunAsync(source, post, generation, text, cts.Token);
        }

        async Task RunAsync(Func<string, CancellationToken, Task<Shell.Omnibar.Suggestions>> source, Action<Action> post,
            int generation, string text, CancellationToken ct)
        {
            try
            {
                var answer = await source(text, ct).ConfigureAwait(false);
                post(() => _model.Query.Complete(generation, answer));
            }
            catch (OperationCanceledException) { }   // superseded or torn down: the store has already moved on
            catch (Exception ex) { post(() => _model.Query.Fail(generation, ex)); }
        }
    }
}
