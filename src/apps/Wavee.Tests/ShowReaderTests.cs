// ── Wavee.Tests/ShowReaderTests.cs — the show reader's page-level decisions (podcast plan §2 W1-W3, §4, §6.1, §10) ──
//
// `ShowReaderRules` is what the show page decides that is not a model fact of its own: which first screen (the head)
// the visitor gets — including the Returning-with-nothing-to-continue fallback and the "progress unavailable" arm —
// which primary pill the rail shows, the width arms, the in-show find, the view, the reader's item list with its row
// marks, and the per-show view prefs' seed and persist. All pure: spans in, answers out.

using Wavee;
using Xunit;
using Head = Wavee.ShowReaderRules.Head;
using Item = Wavee.ShowReaderShape.Item;
using Kind = Wavee.ShowReaderShape.ItemKind;
using Marks = Wavee.Episode.RowMarks;
using Primary = Wavee.ShowReaderRules.Primary;
using Status = Wavee.Episode.Rules.Status;

namespace Wavee.Tests;

public class ShowReaderTests
{
    // ── the head (§6.1: never flash New at a Returning listener; as-built P1-R: Resume -1 ⇒ a fallback) ──────────────

    [Fact]
    public void Head_IsPending_UntilTheMembershipAnswered_AndProgressSettled()
    {
        Assert.Equal(Head.Pending, ShowReaderRules.HeadOf(answered: false, settled: true, failed: false, ShowVisitKind.New, -1, 0));
        Assert.Equal(Head.Pending, ShowReaderRules.HeadOf(answered: true, settled: false, failed: false, ShowVisitKind.Returning, 3, 2));
        Assert.Equal(Head.Pending, ShowReaderRules.HeadOf(answered: false, settled: false, failed: false, ShowVisitKind.CaughtUp, -1, 0));
    }

    [Fact]
    public void Head_FollowsTheVisit_OnceSettled()
    {
        Assert.Equal(Head.New, ShowReaderRules.HeadOf(true, true, false, ShowVisitKind.New, -1, 3));
        Assert.Equal(Head.Returning, ShowReaderRules.HeadOf(true, true, false, ShowVisitKind.Returning, 2, 3));
        Assert.Equal(Head.CaughtUp, ShowReaderRules.HeadOf(true, true, false, ShowVisitKind.CaughtUp, -1, 0));
    }

    /// <summary>A Returning visit whose listen-next found nothing to continue (every unfinished episode is near its end)
    /// reads as caught up — never an empty "continue" section.</summary>
    [Fact]
    public void Head_Returning_WithNoResume_AndNoUpNext_FallsBackToCaughtUp()
    {
        Assert.Equal(Head.CaughtUp, ShowReaderRules.HeadOf(true, true, false, ShowVisitKind.Returning, -1, 0));
        // A resume alone, or up-next alone, is still something to continue.
        Assert.Equal(Head.Returning, ShowReaderRules.HeadOf(true, true, false, ShowVisitKind.Returning, -1, 2));
        Assert.Equal(Head.Returning, ShowReaderRules.HeadOf(true, true, false, ShowVisitKind.Returning, 4, 0));
    }

    /// <summary>The hydrate failed and nothing resident shows progress: a New head would be a false claim.</summary>
    [Fact]
    public void Head_NewAfterAFailedHydrate_IsUnavailable_ButResidentProgressStillCounts()
    {
        Assert.Equal(Head.Unavailable, ShowReaderRules.HeadOf(true, true, failed: true, ShowVisitKind.New, -1, 3));
        Assert.Equal(Head.Returning, ShowReaderRules.HeadOf(true, true, failed: true, ShowVisitKind.Returning, 1, 0));
        Assert.Equal(Head.CaughtUp, ShowReaderRules.HeadOf(true, true, failed: true, ShowVisitKind.CaughtUp, -1, 0));
    }

    // ── the rail's primary (§4; D-1: progress beats follow) ─────────────────────────────────────────────────────────

    [Fact]
    public void Primary_NewVisitor_NotFollowing_IsFollow()
    {
        Assert.Equal(Primary.Follow, ShowReaderRules.PrimaryOf(Head.New, followed: false, serial: true, firstKnown: true, resumable: false));
        Assert.Equal(Primary.Follow, ShowReaderRules.PrimaryOf(Head.New, followed: false, serial: false, firstKnown: false, resumable: false));
    }

    [Fact]
    public void Primary_NewVisitor_Following_PlaysEpisodeOneOfASerial_ElseTheLatest()
    {
        Assert.Equal(Primary.PlayFirst, ShowReaderRules.PrimaryOf(Head.New, true, serial: true, firstKnown: true, resumable: false));
        // A serial whose first episode is not resident (a partial membership) cannot promise episode 1.
        Assert.Equal(Primary.PlayLatest, ShowReaderRules.PrimaryOf(Head.New, true, serial: true, firstKnown: false, resumable: false));
        Assert.Equal(Primary.PlayLatest, ShowReaderRules.PrimaryOf(Head.New, true, serial: false, firstKnown: true, resumable: false));
    }

    [Fact]
    public void Primary_Returning_Resumes_WhenListenNextHasAResume_EvenWithoutFollowing()
    {
        Assert.Equal(Primary.Resume, ShowReaderRules.PrimaryOf(Head.Returning, followed: false, serial: true, firstKnown: true, resumable: true));
        Assert.Equal(Primary.Resume, ShowReaderRules.PrimaryOf(Head.Returning, followed: true, serial: false, firstKnown: false, resumable: true));
        Assert.Equal(Primary.PlayLatest, ShowReaderRules.PrimaryOf(Head.Returning, followed: true, serial: false, firstKnown: false, resumable: false));
    }

    [Theory]
    [InlineData(Head.CaughtUp)]
    [InlineData(Head.Pending)]
    [InlineData(Head.Unavailable)]
    public void Primary_AnyOtherHead_PlaysTheLatest_NeverFollow(Head head)
        => Assert.Equal(Primary.PlayLatest, ShowReaderRules.PrimaryOf(head, followed: false, serial: true, firstKnown: true, resumable: true));

    [Fact]
    public void Ghost_BesideFollow_IsEpisodeOneOfAResidentSerial_ElseTheLatest()
    {
        Assert.Equal(Primary.PlayFirst, ShowReaderRules.GhostOf(serial: true, firstKnown: true));
        Assert.Equal(Primary.PlayLatest, ShowReaderRules.GhostOf(serial: true, firstKnown: false));
        Assert.Equal(Primary.PlayLatest, ShowReaderRules.GhostOf(serial: false, firstKnown: true));
    }

    [Theory]
    [InlineData(Head.Pending, false)]
    [InlineData(Head.New, false)]
    [InlineData(Head.Returning, true)]
    [InlineData(Head.CaughtUp, true)]
    [InlineData(Head.Unavailable, true)]
    public void Ledger_IsShown_OnceDecided_AndNotForANewVisitor(Head head, bool shown)
        => Assert.Equal(shown, ShowReaderRules.LedgerShown(head));

    // ── the width arms ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f, false, false)]      // not measured yet: the wide arm
    [InlineData(480f, true, true)]
    [InlineData(539f, true, true)]
    [InlineData(540f, false, true)]
    [InlineData(719f, false, true)]
    [InlineData(720f, false, false)]
    [InlineData(1200f, false, false)]
    public void WidthArms(float width, bool narrow, bool railScrolls)
    {
        Assert.Equal(narrow, ShowReaderRules.Narrow(width));
        Assert.Equal(railScrolls, ShowReaderRules.RailScrolls(width));
    }

    // ── the find (§4) ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Find_IsATrimmedOrdinalIgnoreCaseContains_OverTitleOrDescription()
    {
        Assert.True(ShowReaderRules.Matches("Dead air", "The tapes go to auction.", ""));
        Assert.True(ShowReaderRules.Matches("Dead air", "The tapes go to auction.", "   "));
        Assert.True(ShowReaderRules.Matches("Dead air", "", "DEAD"));
        Assert.True(ShowReaderRules.Matches("Dead air", "The tapes go to auction.", "  auction "));
        Assert.False(ShowReaderRules.Matches("Dead air", "The tapes go to auction.", "radio"));
        Assert.False(ShowReaderRules.Matches("", "", "x"));
    }

    // ── the view: status ∩ find, newest first, reversed for oldest ──────────────────────────────────────────────────

    static readonly float[] Pcts = [0f, 0.5f, 1f, 0f];

    static int[] View(Status status, bool oldest, bool[]? found = null)
    {
        var into = new int[Pcts.Length];
        int n = ShowReaderRules.View(Pcts, status, oldest, found, into);
        return into[..n];
    }

    [Fact]
    public void View_FiltersByStatus_AndReversesForOldest()
    {
        Assert.Equal([0, 1, 2, 3], View(Status.All, oldest: false));
        Assert.Equal([3, 2, 1, 0], View(Status.All, oldest: true));
        Assert.Equal([0, 3], View(Status.Unplayed, oldest: false));
        Assert.Equal([1], View(Status.InProgress, oldest: false));
        Assert.Equal([2], View(Status.Played, oldest: true));
    }

    [Fact]
    public void View_IntersectsTheFind_AndAnEmptyFindMeansEverything()
    {
        Assert.Equal([1, 3], View(Status.All, oldest: false, [false, true, false, true]));
        Assert.Equal([3], View(Status.Unplayed, oldest: false, [false, true, false, true]));
        Assert.Equal([0, 1, 2, 3], View(Status.All, oldest: false, []));
    }

    // ── the item list (the snapshot's layout over ShowReaderShape) ─────────────────────────────────────────────────

    static int At(int y, int m, int d) => (int)new DateTimeOffset(y, m, d, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    static int Key(int y, int m) => y * 12 + m - 1;

    // newest first: two in September (one fresh, one in progress), two in August (one played, one old and unplayed)
    static readonly int[] Slots = [50, 40, 30, 20];
    static readonly int[] Dates = [At(2026, 9, 10), At(2026, 9, 3), At(2026, 8, 25), At(2026, 8, 5)];
    static readonly int LastPlayed = At(2026, 8, 20);

    static (Item[] Items, Marks[] Marks) Layout(int[] view, bool trusted = true)
    {
        int capacity = ShowReaderShape.MaxItems(view.Length);
        var items = new Item[capacity];
        var marks = new Marks[capacity];
        int n = ShowReaderRules.Layout(Slots, Pcts, Dates, view, LastPlayed, trusted, items, marks);
        return (items[..n], marks[..n]);
    }

    [Fact]
    public void Layout_IsRailHeadHeader_ThenMonthsAndRowsInViewOrder_ThenTheFoot()
    {
        var (items, _) = Layout([0, 1, 2, 3]);
        Assert.Equal(
        [
            new Item(Kind.Rail, -1, -1), new Item(Kind.Head, -1, -1), new Item(Kind.Header, -1, -1),
            new Item(Kind.Group, 50, Key(2026, 9)), new Item(Kind.Row, 50, Key(2026, 9)), new Item(Kind.Row, 40, Key(2026, 9)),
            new Item(Kind.Group, 30, Key(2026, 8)), new Item(Kind.Row, 30, Key(2026, 8)), new Item(Kind.Row, 20, Key(2026, 8)),
            new Item(Kind.Foot, -1, -1),
        ], items);
    }

    [Fact]
    public void Layout_RowsUnderAMonthHeader_DropTheirHairline_AndTheFirstMonthItsTopAir()
    {
        var (_, marks) = Layout([0, 1, 2, 3]);
        Assert.Equal(Marks.NoRule, marks[3]);                     // September, right under "episodes"
        Assert.Equal(Marks.NoRule, marks[4] & Marks.NoRule);      // row 50, right under September
        Assert.Equal(Marks.None, marks[5]);                       // row 40
        Assert.Equal(Marks.None, marks[6]);                       // August, after a row: keeps its air
        Assert.Equal(Marks.NoRule, marks[7]);                     // row 30, right under August
        Assert.Equal(Marks.None, marks[9]);                       // the foot
    }

    /// <summary>NEW = unplayed and published after the show's last play (ShowLedger.IsFresh) — and only once progress is
    /// trusted (settled, not failed): the mark is a claim about the listener's history.</summary>
    [Fact]
    public void Layout_MarksFreshRows_OnlyWhenTrusted()
    {
        var (_, marks) = Layout([0, 1, 2, 3]);
        Assert.Equal(Marks.NoRule | Marks.Fresh, marks[4]);       // 50: unplayed, after the last play
        Assert.Equal(Marks.None, marks[5] & Marks.Fresh);         // 40: after it, but started
        Assert.Equal(Marks.None, marks[8] & Marks.Fresh);         // 20: unplayed, but before it

        var (_, untrusted) = Layout([0, 1, 2, 3], trusted: false);
        Assert.Equal(Marks.NoRule, untrusted[4]);
    }

    [Fact]
    public void Layout_OldestFirst_ReadsItsMonthsAscending()
    {
        var (items, _) = Layout([3, 2, 1, 0]);
        Assert.Equal(new Item(Kind.Group, 20, Key(2026, 8)), items[3]);
        Assert.Equal(new Item(Kind.Row, 20, Key(2026, 8)), items[4]);
        Assert.Equal(new Item(Kind.Row, 30, Key(2026, 8)), items[5]);
        Assert.Equal(new Item(Kind.Group, 40, Key(2026, 9)), items[6]);
        Assert.Equal(new Item(Kind.Row, 50, Key(2026, 9)), items[8]);
    }

    [Fact]
    public void Layout_AFilteredView_KeepsOnlyItsRows_AndAnEmptyViewIsTheFrameAlone()
    {
        var (unplayed, _) = Layout([0, 3]);
        Assert.Equal([Kind.Rail, Kind.Head, Kind.Header, Kind.Group, Kind.Row, Kind.Group, Kind.Row, Kind.Foot],
                     Array.ConvertAll(unplayed, i => i.Kind));
        Assert.Equal(20, unplayed[6].Slot);

        var (empty, _) = Layout([]);
        Assert.Equal([Kind.Rail, Kind.Head, Kind.Header, Kind.Foot], Array.ConvertAll(empty, i => i.Kind));
    }

    // ── the view prefs (D-6): the page's seed and persist over ShowViewPrefs ────────────────────────────────────────

    [Fact]
    public void PrefsId_IsTheBase62IdAfterTheUrisLastColon()
    {
        Assert.Equal("4rOoJ6Egrf8K2IrywzwOMk", ShowReaderRules.PrefsId("spotify:show:4rOoJ6Egrf8K2IrywzwOMk"));
        Assert.Equal("sh0", ShowReaderRules.PrefsId("spotify:show:sh0"));
        Assert.Equal("", ShowReaderRules.PrefsId("spotify:show:"));
        Assert.Equal("", ShowReaderRules.PrefsId(""));
        Assert.Equal("", ShowReaderRules.PrefsId(null));
    }

    [Fact]
    public void Prefs_RoundTripThroughThePagesSeed_PerShow()
    {
        string a = ShowReaderRules.PrefsId("spotify:show:aaa111"), b = ShowReaderRules.PrefsId("spotify:show:bbb222");
        string blob = ShowReaderRules.Persist("", a, (int)Status.InProgress, 1);
        blob = ShowReaderRules.Persist(blob, b, (int)Status.Unplayed, 0);
        Assert.Equal(((int)Status.InProgress, 1), ShowReaderRules.Seed(blob, a));
        Assert.Equal(((int)Status.Unplayed, 0), ShowReaderRules.Seed(blob, b));
        Assert.Equal((0, 0), ShowReaderRules.Seed(blob, "ccc333"));

        // A later write of the same show replaces its entry.
        blob = ShowReaderRules.Persist(blob, a, (int)Status.Played, 0);
        Assert.Equal(((int)Status.Played, 0), ShowReaderRules.Seed(blob, a));
    }

    [Fact]
    public void Seed_ClampsWhatTheReaderDoesNotDefine_AndAnEmptyStoreIsTheDefault()
    {
        Assert.Equal((0, 0), ShowReaderRules.Seed("aaa:9:7", "aaa"));
        Assert.Equal((3, 0), ShowReaderRules.Seed("aaa:3:2", "aaa"));
        Assert.Equal((0, 1), ShowReaderRules.Seed("aaa:4:1", "aaa"));
        Assert.Equal((0, 0), ShowReaderRules.Seed(null, "aaa"));
        Assert.Equal((0, 0), ShowReaderRules.Seed("", ""));
    }
}
