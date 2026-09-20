// ── Wavee.Tests/ShellOmnibarTests.cs — the omnibar's suggestion lifecycle and row rules ─────────────────────────────
//
// Wave 4 stage B's gate for `Shell.Omnibar` (Shell/Shell.cs §14) — the model behind `+Shell.Masthead.UI.cs`'s field and
// popup, and the seam owner P's `Search.cs` plugs into in Wave 5. Ported from 0.2.9's `OmnibarSuggestQuery` contract:
//
//   ONLY A CONFIRMED EMPTY ANSWER MAY SAY "NO RESULTS". Pending starts on the UNDEBOUNCED keystroke, so the sentence
//   never flashes between a keystroke and its request; a failure is a retry, never "nothing matched".
//
//   THE GENERATION IS THE PUBLISH GUARD, NOT THE TEXT. A superseded answer for a retyped query must lose even when the
//   texts match.

using Xunit;

namespace Wavee.Tests;

public class OmnibarQueryLifecycleTests
{
    static Shell.Omnibar.Suggestions One(string query)
        => new([query], Array.Empty<Shell.Omnibar.Item>());

    [Fact]
    public void A_keystroke_enters_pending_at_once_and_an_answer_settles_it()
    {
        var q = new Shell.Omnibar.Query();
        int gen = q.Begin("radio");
        Assert.Equal(Shell.Omnibar.State.Pending, q.State);
        Assert.True(q.Complete(gen, One("radiohead")));
        Assert.Equal(Shell.Omnibar.State.Results, q.State);
    }

    [Fact]
    public void Only_a_confirmed_empty_answer_is_empty()
    {
        var q = new Shell.Omnibar.Query();
        int gen = q.Begin("zzzz");
        Assert.True(q.Complete(gen, Shell.Omnibar.Suggestions.Empty));
        Assert.Equal(Shell.Omnibar.State.Empty, q.State);
    }

    [Fact]
    public void A_superseded_answer_is_dropped()
    {
        var q = new Shell.Omnibar.Query();
        int first = q.Begin("a");
        q.Begin("ab");
        Assert.False(q.Complete(first, One("abba")));
        Assert.Equal(Shell.Omnibar.State.Pending, q.State);
    }

    [Fact]
    public void The_same_text_keeps_its_generation()
    {
        var q = new Shell.Omnibar.Query();
        int gen = q.Begin("abc");
        Assert.Equal(gen, q.Begin("  abc "));
    }

    [Fact]
    public void Pending_keeps_the_previous_rows_on_screen()
    {
        var q = new Shell.Omnibar.Query();
        var answer = One("radiohead");
        q.Complete(q.Begin("radio"), answer);
        q.Begin("radioh");
        Assert.Equal(Shell.Omnibar.State.Pending, q.State);
        Assert.Same(answer, q.Suggestions);
    }

    [Fact]
    public void A_cancellation_is_not_an_answer_but_a_failure_is_a_retry()
    {
        var q = new Shell.Omnibar.Query();
        int gen = q.Begin("x");
        Assert.False(q.Fail(gen, new OperationCanceledException()));
        Assert.Equal(Shell.Omnibar.State.Pending, q.State);

        Assert.True(q.Fail(gen, new InvalidOperationException("down")));
        Assert.Equal(Shell.Omnibar.State.Failed, q.State);
        int retry = q.Retry();
        Assert.NotEqual(gen, retry);
        Assert.Equal(Shell.Omnibar.State.Pending, q.State);
        Assert.Equal(retry, q.Retry());   // a no-op outside Failed
    }

    [Fact]
    public void Clearing_the_field_is_idle_and_drops_a_late_answer()
    {
        var q = new Shell.Omnibar.Query();
        int gen = q.Begin("x");
        q.Begin("   ");
        Assert.Equal(Shell.Omnibar.State.Idle, q.State);
        Assert.False(q.Complete(gen, One("xx")));
        Assert.Equal(Shell.Omnibar.State.Idle, q.State);
    }

    [Fact]
    public void Every_change_is_announced_once()
    {
        var q = new Shell.Omnibar.Query();
        int changes = 0;
        q.Changed += () => changes++;
        int gen = q.Begin("a");
        q.Complete(gen, One("ab"));
        q.Begin("a");            // same text: nothing moved
        q.Clear();
        Assert.Equal(3, changes);
    }
}

public class OmnibarRowRulesTests
{
    [Fact]
    public void The_ghost_is_the_first_completion_that_extends_what_was_typed()
    {
        Assert.Equal("loffler", Shell.Omnibar.Suggestions.GhostFor("loff", ["koffie", "loffler", "loffie"]));
        Assert.Null(Shell.Omnibar.Suggestions.GhostFor("loffler", ["loffler"]));
        Assert.Null(Shell.Omnibar.Suggestions.GhostFor(null, ["a"]));
        Assert.Null(Shell.Omnibar.Suggestions.GhostFor("a", null));
    }

    [Fact]
    public void The_cursor_wraps_through_none_at_both_ends()
    {
        Assert.Equal(-1, Shell.Omnibar.MoveHighlight(0, +1, count: 0));
        Assert.Equal(0, Shell.Omnibar.MoveHighlight(-1, +1, count: 3));
        Assert.Equal(-1, Shell.Omnibar.MoveHighlight(2, +1, count: 3));
        Assert.Equal(2, Shell.Omnibar.MoveHighlight(-1, -1, count: 3));
        Assert.Equal(-1, Shell.Omnibar.MoveHighlight(0, -1, count: 3));
    }

    [Fact]
    public void The_row_caps_bound_the_cursor_too()
    {
        var queries = Enumerable.Range(0, 9).Select(i => "q" + i).ToArray();
        var items = Enumerable.Range(0, 14)
            .Select(i => new Shell.Omnibar.Item(Shell.Omnibar.ItemKind.Album, default, "a" + i)).ToArray();
        var s = new Shell.Omnibar.Suggestions(queries, items);
        Assert.Equal(Shell.Omnibar.MaxQueryRows, Shell.Omnibar.QueryRowCount(s));
        Assert.Equal(Shell.Omnibar.MaxRichRows, Shell.Omnibar.RichRowCount(s));
        Assert.Equal(16, Shell.Omnibar.SelectableCount(s));
    }

    [Theory]
    [InlineData(Shell.Omnibar.ItemKind.Track, true, true, false, true)]
    [InlineData(Shell.Omnibar.ItemKind.Episode, true, false, false, true)]
    [InlineData(Shell.Omnibar.ItemKind.Artist, true, false, true, false)]
    [InlineData(Shell.Omnibar.ItemKind.Album, true, false, false, false)]
    [InlineData(Shell.Omnibar.ItemKind.Genre, false, false, false, false)]
    [InlineData(Shell.Omnibar.ItemKind.User, false, false, true, false)]
    public void What_a_row_offers(Shell.Omnibar.ItemKind kind, bool play, bool heart, bool circle, bool choosePlays)
    {
        Assert.Equal(play, Shell.Omnibar.CanPlay(kind));
        Assert.Equal(heart, Shell.Omnibar.ShowsHeart(kind));
        Assert.Equal(circle, Shell.Omnibar.IsCircular(kind));
        Assert.Equal(choosePlays, Shell.Omnibar.ChoosePlays(kind));
    }

    [Fact]
    public void Choosing_a_row_navigates_to_its_own_kind_of_page()
    {
        var artist = EntityUri.Parse("spotify:artist:4Z8W4fKeB5YxbusRsdQVPb");
        Assert.Equal(Shell.RouteKind.Artist,
            Shell.Omnibar.RouteFor(new(Shell.Omnibar.ItemKind.Artist, artist, "Radiohead")).Kind);
        var show = EntityUri.Parse("spotify:show:5CfCWKI5pZ28U0uOzXkDHe");
        Assert.Equal(Shell.RouteKind.Show, Shell.Omnibar.RouteFor(new(Shell.Omnibar.ItemKind.Podcast, show, "A show")).Kind);
        Assert.Equal(Shell.RouteKind.Show, Shell.Omnibar.RouteFor(new(Shell.Omnibar.ItemKind.Audiobook, show, "A book")).Kind);
        Assert.True(Shell.Omnibar.RouteFor(new(Shell.Omnibar.ItemKind.Track, default, "t")).IsNone);
        Assert.True(Shell.Omnibar.RouteFor(new(Shell.Omnibar.ItemKind.User, default, "u")).IsNone);
    }

    [Fact]
    public void A_genre_carries_its_search_lookup_origin()
    {
        var maybe = Shell.Omnibar.GenreOrigin("  jazz ");
        Assert.True(maybe.HasValue);
        var origin = maybe.GetValueOrDefault();
        Assert.Equal("jazz", origin.Label);
        Assert.Equal(Shell.RouteKind.Search, origin.Route.Kind);
        Assert.Equal("jazz", Shell.ArgOf(origin.Route));
        Assert.Null(Shell.Omnibar.GenreOrigin("   "));
        Assert.Null(Shell.Omnibar.GenreOrigin(null));
    }

    [Fact]
    public void With_no_source_the_navigation_log_answers_newest_first()
    {
        var at = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var entries = new List<Shell.HistoryEntry>
        {
            new(Shell.Parse("search", "radio edits"), at),
            new(Shell.Parse("album:spotify:album:6dVIqQ8qmQ5GBnJ9shOYGE", "OK Computer"), at),
            new(Shell.Parse("pl:spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "Radio Hits"), at),
            new(new Shell.Route(Shell.RouteKind.Settings), at),
            new(Shell.Parse("search", "radiohead"), at),
            new(Shell.Parse("search", "radiohead"), at),   // a repeat is one row
        };
        var s = Shell.Omnibar.FromHistory(entries, " radio ");
        Assert.Equal(["radiohead", "radio edits"], s.Queries);
        var item = Assert.Single(s.Items);
        Assert.Equal(Shell.Omnibar.ItemKind.Playlist, item.Kind);
        Assert.Equal("Radio Hits", item.Title);
    }

    [Fact]
    public void A_blank_field_asks_the_log_nothing()
    {
        var entries = new List<Shell.HistoryEntry> { new(Shell.Parse("search", "anything"), DateTime.UtcNow) };
        Assert.True(Shell.Omnibar.FromHistory(entries, "  ").IsEmpty);
    }
}
