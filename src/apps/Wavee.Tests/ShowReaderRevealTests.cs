// ── Wavee.Tests/ShowReaderRevealTests.cs — the show reader's rows resolve, or fail with a Retry; never shimmer forever ─
//
// THE BUG THIS FILE GATES (2026-09-20, "some podcasts are infinite shimmering"). The show reader (Show.Page.cs's
// `ReaderHost`) is mounted by TWO hosts — the route page and the library pane (Show.Pane.cs) — and the demand for the
// rows' facts (`Ensure(Row | About | Progress)` over the resident membership) lived on the PAGE's host only. In the
// pane nothing ever asked, so a show whose rows nothing else had made resident (the queue, saved episodes, a page
// visit earlier in the session) painted 324 skeletons and three empty up-next chips, indefinitely: nothing was coming,
// and — since nothing was asked — nothing could fail either. The wire never saw those rows at all.
//
// Two pure decisions moved out of the components, both pinned here without a table:
//   · `ShowReaderRules.RowDemand` — WHAT the reader asks for every resident member, wherever it is mounted;
//   · `Episode.RevealState` — WHAT a reader surface (a row, an up-next chip, the continue hero) paints for four column
//     facts: Ready, a shimmer, or a definite failure with a Retry.
// The sequence a real membership produces (asked → the batch's shape → answered → Ready; omitted twice → Failed →
// Retry re-asks) goes through the same public doors every Fetch test uses — `Entities.Ensure` / `Fetch.Drain` /
// `Fetch.Answer` / `Fetch.Pump` over real `EpisodeTable` rows, with `RecordingProvider` (FetchTests.cs) standing in
// for the transport. Nothing here reads production source text.

using FluentGpu.Signals;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class EpisodeRevealStateTests
{
    [Theory]
    [InlineData(false, false, false, false, LoadState.Pending)]   // no row (a recycled slot's default item): shimmer
    [InlineData(false, true, true, true, LoadState.Pending)]      // …whatever the other flags claim
    [InlineData(true, false, false, false, LoadState.Pending)]    // title not known, nothing failed: coming
    [InlineData(true, false, false, true, LoadState.Failed)]      // title not known, the ask failed (or was sealed): Retry
    [InlineData(true, true, true, false, LoadState.Ready)]        // the title is there
    [InlineData(true, true, true, true, LoadState.Ready)]         // a stale failure mark never hides a title that landed
    [InlineData(true, true, false, false, LoadState.Failed)]      // answered with an EMPTY title: a definite failure
    public void RevealState_is_a_table_of_the_four_facts(bool valid, bool knowsTitle, bool hasTitle, bool failed, LoadState expected)
        => Assert.Equal(expected, Episode.RevealState(valid, knowsTitle, hasTitle, failed));

    [Fact]
    public void RowDemand_asks_what_a_row_paints_its_blurb_and_its_progress()
    {
        const EpisodeFields d = ShowReaderRules.RowDemand;
        Assert.Equal(EpisodeFields.Row, d & EpisodeFields.Row);
        Assert.Equal(EpisodeFields.About, d & EpisodeFields.About);
        Assert.Equal(EpisodeFields.Progress, d & EpisodeFields.Progress);
        // …and nothing a row does not paint: the detail page's own groups are its own ask.
        Assert.Equal(EpisodeFields.None, d & (EpisodeFields.Detail | EpisodeFields.Transcript | EpisodeFields.Media | EpisodeFields.Completion));
    }
}

[Collection(EntitiesCollection.Name)]
public class ShowReaderRevealTests : IDisposable
{
    const uint Demand = (uint)ShowReaderRules.RowDemand;
    const uint Paint = (uint)(EpisodeFields.Identity | EpisodeFields.About);
    const uint Progress = (uint)EpisodeFields.Progress;

    public ShowReaderRevealTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
    }

    static Scope Boot()
    {
        Entities.Boot(CatalogScope.Fake());
        return Entities.Current;
    }

    /// <summary>A membership's rows, exactly as the edge commit leaves them: allocated by uri, nothing known.</summary>
    static Episode[] Members(int count)
    {
        var rows = new Episode[count];
        for (int i = 0; i < count; i++)
            rows[i] = Entities.Episode(EntityId.Parse("spotify:episode:" + (i + 1).ToString().PadLeft(22, '0')));
        return rows;
    }

    static int[] SlotsOf(Episode[] rows) => Array.ConvertAll(rows, static e => e.Slot);

    /// <summary>Answer a ticket with the paint groups (Identity + About) for exactly <paramref name="named"/> — the
    /// EpisodeV4 body a real batch lands, and NOT Progress, which no per-episode route serves.</summary>
    static void AnswerPaint(uint ticket, params Episode[] named)
    {
        var s = Staging.Rent();
        foreach (var e in named)
        {
            ref var row = ref s.Episodes.RowFor(e.Id, Authority.Full, Paint);
            row.Title = s.Text("Episode " + e.Slot);
            row.Image = s.Text("https://i.scdn.co/image/" + e.Slot);
            row.Description = s.Text("about " + e.Slot);
            row.DurationMs = 1_800_000;
            row.PublishedAt = 1_700_000_000 + e.Slot;
        }
        Fetch.Answer(ticket, s);
    }

    [Fact]
    public void A_never_asked_member_is_pending_and_the_reader_demand_asks_the_whole_membership_in_one_shape()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        var rows = Members(5);
        foreach (var e in rows) Assert.Equal(LoadState.Pending, Episode.ReaderLoadState(e));

        // What ReaderHost.DemandRows does for whichever host mounts it — the route page or the library pane.
        Entities.Ensure(scope.Episodes, SlotsOf(rows), Demand);
        Fetch.Drain();

        var sent = Assert.Single(provider.Seen);
        Assert.Equal(rows.Length, sent.Count);
        Assert.Equal(Demand, sent.Wanted);
        Assert.Equal(FetchPriority.Visible, sent.Priority);
    }

    [Fact]
    public void Rows_the_answer_names_reveal_and_the_routeless_progress_group_is_sealed_not_missed()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        var rows = Members(3);
        Entities.Ensure(scope.Episodes, SlotsOf(rows), Demand);
        Fetch.Drain();

        AnswerPaint(provider.Seen[0].Ticket, rows);

        var t = scope.Episodes;
        foreach (var e in rows)
        {
            Assert.Equal(LoadState.Ready, Episode.ReaderLoadState(e));
            Assert.True(e.Knows(EpisodeFields.Row | EpisodeFields.About));
            // Progress has no per-episode route: the plan sealed it (Asked, never filled) and the answer's review must
            // not count it as an omission — no failure mark, no un-ask, no retry of a request that cannot answer.
            Assert.Equal(Progress, t.Asked[e.Slot] & Progress);
            Assert.False(t.IsFailed(e.Slot, Progress));
        }
        Assert.Equal(0, Fetch.Pending);
        Entities.Now = 100;
        Fetch.Pump();
        Assert.Single(provider.Seen);                                  // nothing rode a second request
    }

    [Fact]
    public void A_member_the_wire_omits_twice_fails_with_a_retry_and_the_retry_asks_again()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        var rows = Members(2);
        Episode kept = rows[0], omitted = rows[1];
        Entities.Ensure(scope.Episodes, SlotsOf(rows), Demand);
        Fetch.Drain();

        AnswerPaint(provider.Seen[0].Ticket, kept);                   // miss #1 for `omitted` → re-asked, backed off
        Assert.Equal(LoadState.Ready, Episode.ReaderLoadState(kept));
        Assert.Equal(LoadState.Pending, Episode.ReaderLoadState(omitted));   // still coming: a shimmer, not a failure
        Assert.Equal(1, Fetch.Pending);

        Entities.Now = 1;                                              // Backoff(0) = 1 s
        Fetch.Pump();
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(1, provider.Seen[1].Count);                       // only the omitted row rides the retry
        Assert.Equal(Paint, provider.Seen[1].Wanted);                  // …for the groups a route serves, never Progress
        Fetch.Answer(provider.Seen[1].Ticket, null);                   // miss #2 → one more retry owed
        Assert.Equal(LoadState.Pending, Episode.ReaderLoadState(omitted));

        Entities.Now = 4;                                              // Backoff(1) = 2 s → ready at 3
        Fetch.Pump();
        Assert.Equal(3, provider.Seen.Count);
        Fetch.Answer(provider.Seen[2].Ticket, null);                   // miss #3 → sealed AND marked failed

        // The seal reaches the row: a definite failure arm with a Retry, not the seed shimmer.
        Assert.Equal(LoadState.Failed, Episode.ReaderLoadState(omitted));
        Assert.Equal(0, Fetch.Pending);

        // The row's Retry (Episode.UI.cs's failure arm, the head's chip and hero share it) re-asks what was sealed.
        Episode.RetryRow(omitted);
        Assert.Equal(LoadState.Pending, Episode.ReaderLoadState(omitted));   // Plan retires the failure mark: coming again
        Fetch.Drain();
        Assert.Equal(4, provider.Seen.Count);
        Assert.Equal(1, provider.Seen[3].Count);
        Assert.Equal(Paint, provider.Seen[3].Wanted & Paint);

        AnswerPaint(provider.Seen[3].Ticket, omitted);
        Assert.Equal(LoadState.Ready, Episode.ReaderLoadState(omitted));
    }

    [Fact]
    public void An_identity_that_lands_empty_is_a_failure_not_a_shimmer()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        var rows = Members(1);
        Entities.Ensure(scope.Episodes, SlotsOf(rows), Demand);
        Fetch.Drain();

        var s = Staging.Rent();
        _ = s.Episodes.RowFor(rows[0].Id, Authority.Full, (uint)EpisodeFields.Title);   // a title group with no text
        Fetch.Answer(provider.Seen[0].Ticket, s);

        Assert.True(rows[0].Knows(EpisodeFields.Title));
        Assert.Equal(LoadState.Failed, Episode.ReaderLoadState(rows[0]));
    }
}
