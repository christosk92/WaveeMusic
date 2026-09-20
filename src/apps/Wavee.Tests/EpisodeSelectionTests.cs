using FluentGpu.Signals;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// `EpisodeSelection` is the show READER's bulk selection (owner report 5) over the engine's `SelectionModel`: the
// reader's item space interleaves month headers with rows, so what is pinned here is the one thing that can silently
// go wrong — a header being selectable, or being counted. `Episode.ReaderLoadState` stays too: unrelated to selection,
// and still exactly the row-hydration contract `Episode.ReaderRow`'s `SkelRegionEl` reads.
[Collection(EntitiesCollection.Name)]
public class EpisodeSelectionTests : IDisposable
{
    public EpisodeSelectionTests() { Fetch.Reset(); Store.Shutdown(); Store.Use(null); TestScope.Fresh(); }
    public void Dispose() { Fetch.Reset(); Store.Shutdown(); Store.Use(null); }

    static Episode Reserve(int number) => Entities.Episode(EntityId.Parse("spotify:episode:" + number.ToString().PadLeft(22, '0')));

    [Fact]
    public void Row_hydration_transitions_without_reopening_the_list()
    {
        var episode = Reserve(1);
        Assert.Equal(LoadState.Pending, Episode.ReaderLoadState(episode));
        var staging = Staging.Rent();
        ref var row = ref staging.Episodes.RowFor(episode.Id, Authority.Full, (uint)EpisodeFields.Title);
        row.Title = staging.Text("A resolved episode");
        TestScope.CommitAndPublish(staging);
        Assert.Equal(LoadState.Ready, Episode.ReaderLoadState(episode));
        Assert.False(episode.Knows(EpisodeFields.Duration));
        Assert.Empty(Episode.DurationLabel(episode.DurationMs));
    }

    [Fact]
    public void Terminal_row_failure_exposes_retry_instead_of_an_endless_shimmer()
    {
        var episode = Reserve(1);
        Entities.Current.Episodes.Failed[episode.Slot] = (uint)EpisodeFields.Title;
        Assert.Equal(LoadState.Failed, Episode.ReaderLoadState(episode));
        Entities.Current.Episodes.Failed[episode.Slot] = 0;
        Assert.Equal(LoadState.Pending, Episode.ReaderLoadState(episode));
    }
    // ── the reader's item space: rows are selectable, month headers are not (report 5) ──────────────────────────────

    /// <summary>A stand-in for the reader's flat item list: index → is it a row, and which episode.</summary>
    static EpisodeSelection Over(bool[] isRow, Episode[] episodes)
        => new(i => (uint)i < (uint)episodes.Length ? episodes[i] : default, () => isRow.Length, i => (uint)i < (uint)isRow.Length && isRow[i]);

    [Fact]
    public void Select_all_takes_every_row_and_no_month_header()
    {
        // rail, head, header, GROUP, row, row, GROUP, row
        bool[] isRow = [false, false, false, false, true, true, false, true];
        var episodes = new[] { default(Episode), default, default, default, Reserve(1), Reserve(2), default, Reserve(3) };
        var selection = Over(isRow, episodes);

        selection.SelectAll();
        Assert.True(selection.Selecting.Peek());
        Assert.Equal(3, selection.SelectedCount);
        Assert.Equal(new[] { 4, 5, 7 }, Enumerable.Range(0, isRow.Length).Where(selection.Model.IsSelected).ToArray());
        Assert.Equal(3, selection.Selected().Count);
    }

    /// <summary>The bar's count is the count of ROWS, not of selected indices: a marquee or a Ctrl+A that swept a
    /// month header in must not report it.</summary>
    [Fact]
    public void A_selected_header_is_never_counted()
    {
        bool[] isRow = [false, false, false, false, true, true];
        var selection = Over(isRow, [default, default, default, default, Reserve(1), Reserve(2)]);
        selection.Sync();
        selection.Model.SelectRange(0, 5);
        Assert.Equal(2, selection.SelectedCount);
        Assert.Equal(2, selection.Selected().Count);
    }

    /// <summary>Disarming drops the selection with it — a check lane that vanishes while rows stay selected leaves a
    /// bar with no visible cause; and the bar itself is gone at zero (`Controls.SelectionBar` minCount 1).</summary>
    [Fact]
    public void Disarming_and_exit_both_clear_the_selection()
    {
        bool[] isRow = [false, true, true];
        var selection = Over(isRow, [default, Reserve(1), Reserve(2)]);
        selection.Sync();
        selection.Arm(true);
        selection.Model.Select(1);
        Assert.Equal(1, selection.SelectedCount);

        selection.Arm(false);
        Assert.Equal(0, selection.SelectedCount);
        Assert.False(selection.Selecting.Peek());

        selection.Arm(true);
        selection.Model.Select(2);
        selection.Exit();
        Assert.Equal(0, selection.SelectedCount);
        Assert.False(selection.Selecting.Peek());
    }

    [Fact]
    public void An_empty_reader_selects_nothing()
    {
        var selection = Over([], []);
        selection.SelectAll();
        Assert.Equal(0, selection.SelectedCount);
        Assert.Empty(selection.Selected());
    }
}
