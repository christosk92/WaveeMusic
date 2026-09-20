// ── Wavee.Tests/ShowReaderShapeTests.cs — the reader's item list (podcast plan §3.1, §5.2, §10) ─────────────────────
//
// `ShowReaderShape.Build` lays out what the show reader's bound list realizes: the sticky rail (the persistent prefix),
// the visit head, the "episodes N" header, month groups interleaved with rows IN VIEW ORDER, then the load-more foot and
// the similar-shows shelf. Slots here are arbitrary ints — the rule copies them through and never interprets them.

using Wavee;
using Xunit;
using Item = Wavee.ShowReaderShape.Item;
using Kind = Wavee.ShowReaderShape.ItemKind;

namespace Wavee.Tests;

public class ShowReaderShapeTests
{
    static int At(int y, int m, int d)
        => (int)new DateTimeOffset(y, m, d, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>THE one month-key encoding (<see cref="Wavee.DateKeys"/>): year*100 + month.</summary>
    static int Key(int y, int m) => y * 100 + m;

    static Item[] Build(int[] slots, int[] dates, bool more = false, bool similar = false, int? room = null, int skip = -1)
    {
        var into = new Item[room ?? ShowReaderShape.MaxItems(slots.Length)];
        int n = ShowReaderShape.Build(slots, dates, more, similar, into, skip);
        return into[..n];
    }

    static Kind[] Kinds(Item[] items) => Array.ConvertAll(items, i => i.Kind);

    // newest first: two in September, one in August, one in July 2026
    static readonly int[] Slots = [41, 42, 43, 44];
    static readonly int[] Dates = [At(2026, 9, 15), At(2026, 9, 1), At(2026, 8, 25), At(2026, 7, 7)];

    [Fact]
    public void ThePrefixIsOne_TheStickyRailAtItemZero()
    {
        Assert.Equal(1, ShowReaderShape.Prefix);
        var items = Build(Slots, Dates);
        Assert.Equal(new Item(Kind.Rail, -1, -1), items[0]);
        Assert.Equal(new Item(Kind.Head, -1, -1), items[1]);
        Assert.Equal(new Item(Kind.Header, -1, -1), items[2]);
    }

    [Fact]
    public void Groups_OpenAtEveryMonthBoundary()
    {
        Item[] expected =
        [
            new(Kind.Rail, -1, -1), new(Kind.Head, -1, -1), new(Kind.Header, -1, -1),
            new(Kind.Group, 41, Key(2026, 9)), new(Kind.Row, 41, Key(2026, 9)), new(Kind.Row, 42, Key(2026, 9)),
            new(Kind.Group, 43, Key(2026, 8)), new(Kind.Row, 43, Key(2026, 8)),
            new(Kind.Group, 44, Key(2026, 7)), new(Kind.Row, 44, Key(2026, 7)),
        ];
        Assert.Equal(expected, Build(Slots, Dates));
    }

    [Fact]
    public void OldestOrder_ReversesTheGroups_TheyFollowTheView()
    {
        var items = Build([44, 43, 42, 41], [Dates[3], Dates[2], Dates[1], Dates[0]]);
        Assert.Equal(new[] { Key(2026, 7), Key(2026, 8), Key(2026, 9) },
            Array.ConvertAll(Array.FindAll(items, i => i.Kind == Kind.Group), i => i.GroupKey));
        Assert.Equal(new Item(Kind.Group, 42, Key(2026, 9)), items[7]);   // the group carries its FIRST row's slot
    }

    [Fact]
    public void TheSameMonthInAnotherYear_IsAnotherGroup()
    {
        var items = Build([1, 2], [At(2026, 9, 3), At(2025, 9, 20)]);
        Assert.Equal(new[] { Kind.Rail, Kind.Head, Kind.Header, Kind.Group, Kind.Row, Kind.Group, Kind.Row }, Kinds(items));
        Assert.Equal(Key(2025, 9), items[5].GroupKey);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FootAndSimilar_AppearOnlyWhenAsked_InThatOrder(bool more, bool similar)
    {
        var tail = Kinds(Build(Slots, Dates, more, similar))[10..];
        var expected = new List<Kind>();
        if (more) expected.Add(Kind.Foot);
        if (similar) expected.Add(Kind.Similar);
        Assert.Equal(expected.ToArray(), tail);
    }

    [Fact]
    public void AnEmptyView_IsTheFixedItemsAlone()
    {
        Assert.Equal(new[] { Kind.Rail, Kind.Head, Kind.Header }, Kinds(Build([], [])));
        Assert.Equal(new[] { Kind.Rail, Kind.Head, Kind.Header, Kind.Foot, Kind.Similar }, Kinds(Build([], [], true, true)));
    }

    [Fact]
    public void UndatedRows_JoinTheRunningGroup_AndNeverOpenOne()
    {
        var items = Build([1, 2, 3, 4], [0, At(2026, 9, 1), 0, At(2026, 8, 1)]);
        Item[] expected =
        [
            new(Kind.Rail, -1, -1), new(Kind.Head, -1, -1), new(Kind.Header, -1, -1),
            new(Kind.Row, 1, -1),                                           // before any dated row: no header
            new(Kind.Group, 2, Key(2026, 9)), new(Kind.Row, 2, Key(2026, 9)), new(Kind.Row, 3, Key(2026, 9)),
            new(Kind.Group, 4, Key(2026, 8)), new(Kind.Row, 4, Key(2026, 8)),
        ];
        Assert.Equal(expected, items);
    }

    [Fact]
    public void ShortDates_ReadAsUndated()
        => Assert.Equal(new[] { Kind.Rail, Kind.Head, Kind.Header, Kind.Group, Kind.Row, Kind.Row },
            Kinds(Build([1, 2], [At(2026, 9, 1)])));

    [Fact]
    public void MaxItems_CoversTheWorstCase_EveryRowItsOwnMonth()
    {
        int[] slots = [1, 2, 3], dates = [At(2026, 9, 1), At(2026, 8, 1), At(2026, 7, 1)];
        Assert.Equal(11, ShowReaderShape.MaxItems(3));
        Assert.Equal(11, Build(slots, dates, more: true, similar: true).Length);
        Assert.Equal(ShowReaderShape.Fixed, ShowReaderShape.MaxItems(0));
        Assert.Equal(ShowReaderShape.Fixed, ShowReaderShape.MaxItems(-4));
    }

    [Fact]
    public void ATooSmallSpan_GetsWhatFits_AndNeverAnOrphanGroupHeader()
    {
        // Room for the three fixed items and one more: the September group needs two, so it is not started.
        Assert.Equal(new[] { Kind.Rail, Kind.Head, Kind.Header }, Kinds(Build(Slots, Dates, room: 4)));
        Assert.Equal(new[] { Kind.Rail, Kind.Head, Kind.Header, Kind.Group, Kind.Row }, Kinds(Build(Slots, Dates, room: 5)));
        Assert.Equal(new[] { Kind.Rail, Kind.Head }, Kinds(Build(Slots, Dates, room: 2)));
        Assert.Empty(Build(Slots, Dates, room: 0));
    }

    [Theory]
    [InlineData(2026, 9)]
    [InlineData(2025, 1)]
    [InlineData(1999, 12)]
    public void GroupKey_IsTheOneDecimalMonthKey_AndRoundTripsThroughEveryDecoder(int year, int month)
    {
        int key = ShowReaderShape.MonthKeyOf(At(year, month, 28));
        Assert.Equal(year * 100 + month, key);
        Assert.Equal(key, DateKeys.MonthKey(year, month));
        Assert.Equal(year, DateKeys.YearOfMonth(key));
        Assert.Equal(month, DateKeys.MonthOfMonth(key));
        // the date rail reads the SAME key: its jump key is the identity and its year decoder agrees
        Assert.Equal(key, ShowDateIndex.KeyOf(key));
        Assert.Equal(year, ShowDateIndex.YearOf(key));
    }

    [Fact]
    public void GroupKey_IsUtc_AndUndatedIsMinusOne()
    {
        int lastSecondOfAugustUtc = (int)new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(Key(2026, 8), ShowReaderShape.MonthKeyOf(lastSecondOfAugustUtc));
        Assert.Equal(Key(2026, 9), ShowReaderShape.MonthKeyOf(lastSecondOfAugustUtc + 1));
        Assert.Equal(-1, ShowReaderShape.MonthKeyOf(0));
        Assert.Equal(-1, ShowReaderShape.MonthKeyOf(-86_400));
    }
    // ── the head's episode is not repeated as a body row (report 11a) ───────────────────────────────────────────────

    /// <summary>The visit head SHOWS the resume episode (the continue hero); the body must not print it again.</summary>
    [Fact]
    public void SkipSlot_DropsTheHeadsEpisode_AndOnlyIt()
    {
        var items = Build(Slots, Dates, skip: 42);
        Assert.DoesNotContain(items, i => i.Kind == Kind.Row && i.Slot == 42);
        Assert.Equal(new[] { 41, 43, 44 },
            Array.ConvertAll(Array.FindAll(items, i => i.Kind == Kind.Row), i => i.Slot));
        // its month still stands (41 is September too), and every other item is untouched
        Assert.Equal(new[] { Kind.Rail, Kind.Head, Kind.Header, Kind.Group, Kind.Row, Kind.Group, Kind.Row, Kind.Group, Kind.Row },
            Kinds(items));
    }

    /// <summary>A head episode ALONE in its month takes the month header with it — the group opens at the first row
    /// that survives, carrying that row's own slot.</summary>
    [Fact]
    public void SkipSlot_TakesAMonthHeaderWithIt_WhenItWasThatMonthsOnlyRow()
    {
        var items = Build(Slots, Dates, skip: 43);
        Assert.Equal(new[] { Key(2026, 9), Key(2026, 7) },
            Array.ConvertAll(Array.FindAll(items, i => i.Kind == Kind.Group), i => i.GroupKey));
        Assert.Equal(new[] { 41, 42, 44 },
            Array.ConvertAll(Array.FindAll(items, i => i.Kind == Kind.Row), i => i.Slot));
        // the surviving group carries the FIRST row that opened it
        Assert.Equal(new Item(Kind.Group, 41, Key(2026, 9)), items[3]);
    }

    /// <summary>-1 is "skip nothing" — the default, and the only value the reader passes when its head owns no
    /// episode (a New or CaughtUp visit).</summary>
    [Fact]
    public void SkipSlot_MinusOne_SkipsNothing()
        => Assert.Equal(Build(Slots, Dates), Build(Slots, Dates, skip: -1));
}
