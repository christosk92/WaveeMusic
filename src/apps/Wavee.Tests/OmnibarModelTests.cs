// ── Wavee.Tests/OmnibarModelTests.cs — the omnibar's store, fetch edges, focus tickets and verbs (PR C8) ───────────
//
// `Shell.Masthead.UI.cs` used to hold the suggestion store, the in-flight request with its cancellation, the focus-ticket
// latch and the choose decisions inside its components. They are `OmnibarModel` now, behind `OmnibarPorts` (every effect a
// delegate), so these tests drive the real model with a recorder: no window, no engine host, no static shell state.
//
//   A SUPERSEDED ANSWER LOSES. A keystroke moves the generation and cancels the request; a late answer for the old
//   generation is dropped even if the texts match.
//
//   A FOCUS TICKET IS ACTED ON AT MOST ONCE, and only if it is NEWER than the last one a form consumed: a form mounting
//   later (a search-mode flip) must never pop the flyout open for a Ctrl+F from minutes ago.

using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Wavee.Tests;

// Shell.Parse interns route args into the process-wide Entities.Strings table, which other suites reset: serial with them.
[Collection(EntitiesCollection.Name)]
public class OmnibarModelTests
{
    sealed class Recorder
    {
        public readonly List<string> SearchText = new();
        public readonly List<(Shell.Route Route, Shell.NavOrigin? Origin)> Went = new();
        public readonly List<EntityUri> Played = new();
        public int FocusRequests;
        public Func<string, CancellationToken, Task<Shell.Omnibar.Suggestions>>? Source;
        public Func<string, Shell.Omnibar.Suggestions> History = static _ => Shell.Omnibar.Suggestions.Empty;
        public bool HasPlayContext = true;

        public OmnibarModel Model() => new(new OmnibarPorts(
            Source: () => Source,
            FromHistory: text => History(text),
            SetSearchText: SearchText.Add,
            RequestFocus: () => FocusRequests++,
            Go: (route, origin) => Went.Add((route, origin)),
            SearchRoute: arg => Shell.Parse("search", arg),
            PlayContext: () => HasPlayContext ? Played.Add : null));
    }

    static Shell.Omnibar.Suggestions Answer(string[] queries, params Shell.Omnibar.Item[] items) => new(queries, items);

    static Shell.Omnibar.Item Artist(string name = "Radiohead")
        => new(Shell.Omnibar.ItemKind.Artist, EntityUri.Parse("spotify:artist:4Z8W4fKeB5YxbusRsdQVPb"), name);

    static void Settle(OmnibarModel model, string typed, Shell.Omnibar.Suggestions answer)
        => Assert.True(model.Query.Complete(model.Query.Begin(typed), answer));

    // ── the keystroke and fetch edges ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_keystroke_moves_the_generation_and_the_same_text_does_not()
    {
        var model = new Recorder().Model();
        Assert.True(model.Begin("rad"));
        Assert.True(model.Query.IsPending);
        Assert.False(model.Begin("  rad "));   // trimmed to the same text: the in-flight request stays valid
        Assert.True(model.Begin("radi"));
    }

    [Fact]
    public void Clearing_the_field_moves_the_generation_so_the_caller_cancels()
    {
        var model = new Recorder().Model();
        Assert.False(model.Begin(""));          // already idle: nothing to cancel
        model.Begin("rad");
        Assert.True(model.Begin("   "));        // blank clears: a late answer for "rad" is dropped
        Assert.Equal(Shell.Omnibar.State.Idle, model.Query.State);
    }

    [Fact]
    public void A_fetch_is_needed_only_for_the_settled_generation_while_it_is_pending()
    {
        var model = new Recorder().Model();
        model.Begin("a");
        int first = model.Query.Generation;
        Assert.True(model.NeedsFetch(first));

        model.Begin("ab");
        Assert.False(model.NeedsFetch(first));                    // the debounce settled on a superseded generation
        Assert.True(model.NeedsFetch(model.Query.Generation));

        Settle(model, "ab", Answer(["abba"]));
        Assert.False(model.NeedsFetch(model.Query.Generation));   // answered: nothing to re-issue
    }

    [Fact]
    public void Every_store_change_bumps_the_version()
    {
        var model = new Recorder().Model();
        int v0 = model.Version.Peek();
        model.Begin("a");
        int v1 = model.Version.Peek();
        Assert.True(v1 > v0);
        model.Query.Complete(model.Query.Generation, Answer(["abba"]));
        Assert.True(model.Version.Peek() > v1);
    }

    [Fact]
    public async Task A_source_answer_is_posted_and_settles_the_generation()
    {
        var rec = new Recorder { Source = (text, _) => Task.FromResult(Answer([text + "!"])) };
        var model = rec.Model();
        var fetch = model.NewFetch();
        var posted = new List<Action>();

        model.Begin("rad");
        var task = fetch.Start(posted.Add, model.Query.Generation, "rad");
        await task;

        Assert.Equal(Shell.Omnibar.State.Pending, model.Query.State);   // posted, not applied inline
        Assert.Single(posted);
        posted[0]();
        Assert.Equal(Shell.Omnibar.State.Results, model.Query.State);
        Assert.Equal("rad!", model.Query.Suggestions.Queries[0]);
    }

    [Fact]
    public async Task A_superseded_request_is_cancelled_and_its_late_answer_is_dropped()
    {
        var gates = new List<TaskCompletionSource<Shell.Omnibar.Suggestions>>();
        var tokens = new List<CancellationToken>();
        var rec = new Recorder
        {
            Source = (_, ct) =>
            {
                var gate = new TaskCompletionSource<Shell.Omnibar.Suggestions>();
                gates.Add(gate);
                tokens.Add(ct);
                return gate.Task;
            },
        };
        var model = rec.Model();
        var fetch = model.NewFetch();
        var posted = new List<Action>();

        model.Begin("a");
        int first = model.Query.Generation;
        var firstTask = fetch.Start(posted.Add, first, "a");
        Assert.True(fetch.InFlight);

        // The next keystroke: the generation moves, the caller cancels, and the second request starts.
        Assert.True(model.Begin("ab"));
        fetch.Cancel();
        Assert.True(tokens[0].IsCancellationRequested);
        Assert.False(fetch.InFlight);
        var secondTask = fetch.Start(posted.Add, model.Query.Generation, "ab");

        // The first request answers anyway (it ignored the token): the store drops it.
        gates[0].SetResult(Answer(["stale"]));
        await firstTask;
        foreach (var action in posted.ToArray()) action();
        Assert.Equal(Shell.Omnibar.State.Pending, model.Query.State);

        gates[1].SetResult(Answer(["fresh"]));
        await secondTask;
        foreach (var action in posted.ToArray()) action();
        Assert.Equal(Shell.Omnibar.State.Results, model.Query.State);
        Assert.Equal("fresh", model.Query.Suggestions.Queries[0]);
    }

    [Fact]
    public async Task A_cancellation_is_not_a_failure()
    {
        var rec = new Recorder { Source = (_, ct) => Task.FromCanceled<Shell.Omnibar.Suggestions>(new CancellationToken(true)) };
        var model = rec.Model();
        var posted = new List<Action>();
        model.Begin("x");

        await model.NewFetch().Start(posted.Add, model.Query.Generation, "x");

        Assert.Empty(posted);                                            // nothing to say: whoever cancelled moved on
        Assert.Equal(Shell.Omnibar.State.Pending, model.Query.State);
    }

    [Fact]
    public async Task A_failure_is_posted_as_a_retry_offer_and_retry_starts_a_new_generation()
    {
        var rec = new Recorder { Source = (_, _) => Task.FromException<Shell.Omnibar.Suggestions>(new InvalidOperationException("down")) };
        var model = rec.Model();
        var posted = new List<Action>();
        model.Begin("x");
        int gen = model.Query.Generation;

        await model.NewFetch().Start(posted.Add, gen, "x");
        Assert.Single(posted);
        posted[0]();
        Assert.Equal(Shell.Omnibar.State.Failed, model.Query.State);

        model.Retry();
        Assert.Equal(Shell.Omnibar.State.Pending, model.Query.State);
        Assert.NotEqual(gen, model.Query.Generation);
    }

    [Fact]
    public async Task With_no_source_the_navigation_log_answers_synchronously()
    {
        var rec = new Recorder { History = text => Answer([text + " (history)"]) };
        var model = rec.Model();
        var posted = new List<Action>();
        model.Begin("jazz");

        var task = model.NewFetch().Start(posted.Add, model.Query.Generation, "jazz");

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Empty(posted);                                           // no hop: the answer is already in
        Assert.Equal(Shell.Omnibar.State.Results, model.Query.State);
        Assert.Equal("jazz (history)", model.Query.Suggestions.Queries[0]);
        await task;
    }

    // ── the focus tickets ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_focus_ticket_is_acted_on_once_and_an_older_one_never()
    {
        var model = new Recorder().Model();
        Assert.False(model.ConsumeFocusTicket(0));   // the initial value of the request cell is not a request
        Assert.True(model.ConsumeFocusTicket(1));
        Assert.False(model.ConsumeFocusTicket(1));   // the other mount of the same omnibar sees the same ticket
        Assert.True(model.ConsumeFocusTicket(3));
        Assert.False(model.ConsumeFocusTicket(2));   // a form mounting late never replays an older Ctrl+F
    }

    [Fact]
    public void Focus_asks_the_shell_for_a_new_ticket()
    {
        var rec = new Recorder();
        var model = rec.Model();
        model.Focus();
        model.Focus();
        Assert.Equal(2, rec.FocusRequests);
    }

    // ── the keyboard cursor ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_cursor_walks_the_visible_rows_and_wraps_through_none()
    {
        var model = new Recorder().Model();
        Settle(model, "r", Answer(["radio", "radiohead"], Artist()));    // three selectable rows
        Assert.Equal(-1, model.Highlight.Peek());

        model.MoveHighlight(+1); Assert.Equal(0, model.Highlight.Peek());
        model.MoveHighlight(+1); Assert.Equal(1, model.Highlight.Peek());
        model.MoveHighlight(+1); Assert.Equal(2, model.Highlight.Peek());
        model.MoveHighlight(+1); Assert.Equal(-1, model.Highlight.Peek());   // past the end: back to none
        model.MoveHighlight(-1); Assert.Equal(2, model.Highlight.Peek());    // up from none: the last row
        model.ResetHighlight(); Assert.Equal(-1, model.Highlight.Peek());
    }

    // ── choose, submit, play ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Choosing_a_query_row_sets_the_field_and_opens_that_search()
    {
        var rec = new Recorder();
        var model = rec.Model();
        Settle(model, "rad", Answer(["radio", "radiohead"], Artist()));

        Assert.True(model.Choose(1));

        Assert.Equal(new[] { "radiohead" }, rec.SearchText);
        var (route, origin) = Assert.Single(rec.Went);
        Assert.Equal(Shell.Parse("search", "radiohead"), route);
        Assert.Null(origin);
    }

    [Fact]
    public void Choosing_a_rich_row_navigates_to_its_destination()
    {
        var rec = new Recorder();
        var model = rec.Model();
        var artist = Artist();
        Settle(model, "rad", Answer(["radio"], artist));

        Assert.True(model.Choose(1));   // row 0 is the query row; row 1 is the artist

        Assert.Equal(Shell.Omnibar.RouteFor(artist), Assert.Single(rec.Went).Route);
        Assert.Empty(rec.SearchText);
        Assert.Empty(rec.Played);
    }

    [Fact]
    public void Choosing_a_track_plays_it_through_the_context_seam_and_does_not_navigate()
    {
        var rec = new Recorder();
        var model = rec.Model();
        var uri = EntityUri.Parse("spotify:track:4uLU6hMCjMI75M1A2tKUQC");
        Settle(model, "never", Answer([], new Shell.Omnibar.Item(Shell.Omnibar.ItemKind.Track, uri, "Never Gonna Give You Up")));

        Assert.True(model.Choose(0));

        Assert.Equal(new[] { uri }, rec.Played);
        Assert.Empty(rec.Went);
    }

    [Fact]
    public void Choosing_a_genre_carries_the_search_it_came_from_as_its_origin()
    {
        var rec = new Recorder();
        var model = rec.Model();
        var genre = new Shell.Omnibar.Item(Shell.Omnibar.ItemKind.Genre, EntityUri.Parse("spotify:genre:0JQ5DAqbMKFFzDl7qN9Apr"), "Pop");
        Settle(model, "pop", Answer([], genre));

        Assert.True(model.Choose(0));

        var (_, origin) = Assert.Single(rec.Went);
        Assert.NotNull(origin);
        Assert.Equal(Shell.Omnibar.GenreOrigin("pop"), origin);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(99)]
    public void Choosing_a_row_that_is_not_there_does_nothing(int selection)
    {
        var rec = new Recorder();
        var model = rec.Model();
        Settle(model, "a", Answer(["abba"]));    // one selectable row

        Assert.False(model.Choose(selection));

        Assert.Empty(rec.Went);
        Assert.Empty(rec.SearchText);
    }

    [Fact]
    public void Submit_opens_the_search_for_the_trimmed_text()
    {
        var rec = new Recorder();
        var model = rec.Model();
        model.Submit("  radiohead ");
        Assert.Equal(Shell.Parse("search", "radiohead"), Assert.Single(rec.Went).Route);
    }

    [Fact]
    public void Set_query_writes_the_shells_search_text()
    {
        var rec = new Recorder();
        var model = rec.Model();
        model.SetQuery("abba");
        Assert.Equal(new[] { "abba" }, rec.SearchText);
    }

    // ── the flyout and focus facts ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Escape_closes_the_flyout_fact()
    {
        var model = new Recorder().Model();
        model.FlyoutOpen.Value = true;
        model.Escape();
        Assert.False(model.FlyoutOpen.Peek());
        Assert.False(model.SearchFocused.Peek());
    }
}
