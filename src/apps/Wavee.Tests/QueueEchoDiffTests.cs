// ── Wavee.Tests/QueueEchoDiffTests.cs — `QueueEchoDiff.Compare` (§2.5, §6.4) ────────────────────────────────────────

using Xunit;

namespace Wavee.Tests;

public class QueueEchoDiffTests
{
    [Fact]
    public void Exact_when_the_echoed_row_sits_at_the_recorded_position()
    {
        long[] echoed = [10, 20, 30];
        Assert.Equal(QueueEchoVerdict.Exact, QueueEchoDiff.Compare(ourRowId: 30, ourPosition: 2, echoed));
    }

    [Fact]
    public void Row_missing_when_our_row_id_is_absent_from_the_echo()
    {
        long[] echoed = [10, 20, 30];
        Assert.Equal(QueueEchoVerdict.RowMissing, QueueEchoDiff.Compare(ourRowId: 99, ourPosition: 1, echoed));
    }

    [Fact]
    public void Position_shifted_when_the_row_lands_earlier_than_we_placed_it()
    {
        long[] echoed = [30, 10, 20];
        Assert.Equal(QueueEchoVerdict.PositionShifted, QueueEchoDiff.Compare(ourRowId: 30, ourPosition: 2, echoed));
    }

    [Fact]
    public void Rows_added_when_the_row_lands_later_than_we_placed_it()
    {
        long[] echoed = [1, 2, 3, 30];
        Assert.Equal(QueueEchoVerdict.RowsAdded, QueueEchoDiff.Compare(ourRowId: 30, ourPosition: 0, echoed));
    }
}
