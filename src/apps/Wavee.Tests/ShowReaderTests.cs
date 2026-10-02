// ── Wavee.Tests/ShowReaderTests.cs — the show reader's page-level decisions (podcast plan §2 W1-W3, §4, §6.1, §10) ──
//
// `ShowReaderRules` is what the show page decides that is not a model fact of its own: which first screen (the head)
// the visitor gets — including the Returning-with-nothing-to-continue fallback and the "progress unavailable" arm —
// which primary pill the rail shows, the width arms, the in-show find, the view, the reader's item list with its row
// marks, and the per-show view prefs' seed and persist. All pure: spans in, answers out.

using System.Linq;
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

    /// <summary>ONE width arm is left here: the rows' narrow arm. The toolbar's own stages are
    /// <see cref="ShowToolbarLayout"/>'s measured ladder (PodcastShowToolbarTests), and the sideways-scrolling
    /// rail — the scrollbar the owner reported — is gone with its rule.</summary>
    [Theory]
    [InlineData(0f, false)]             // not measured yet: the wide arm
    [InlineData(480f, true)]
    [InlineData(539f, true)]
    [InlineData(540f, false)]
    [InlineData(1200f, false)]
    public void WidthArms(float width, bool narrow)
        => Assert.Equal(narrow, ShowReaderRules.Narrow(width));

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
    /// <summary>THE one month-key encoding (<see cref="Wavee.DateKeys"/>): year*100 + month.</summary>
    static int Key(int y, int m) => y * 100 + m;

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

    // ── results mode: a filter or a find hides the head (never filters it); sort alone never does ───────────────────────

    [Fact]
    public void ResultsMode_IsAStatusFilterOrAFind_NeverSort()
    {
        Assert.False(ShowReaderRules.ResultsMode(Status.All, ""));
        Assert.False(ShowReaderRules.ResultsMode(Status.All, "   "));      // a blank find is no find (trimmed, like Matches)
        Assert.True(ShowReaderRules.ResultsMode(Status.Unplayed, ""));
        Assert.True(ShowReaderRules.ResultsMode(Status.InProgress, ""));
        Assert.True(ShowReaderRules.ResultsMode(Status.Played, "  "));
        Assert.True(ShowReaderRules.ResultsMode(Status.All, "why ger"));
        Assert.True(ShowReaderRules.ResultsMode(Status.Played, "why ger"));
        // sort is not an input at all: oldest-first over the same (All, no find) view is not results mode
        Assert.False(ShowReaderRules.ResultsMode(Status.All, string.Empty));
    }

    [Fact]
    public void HeadSlot_IsTheResumeEpisode_OnlyWhileTheReturningHeadIsShown()
    {
        Assert.Equal(40, ShowReaderRules.HeadSlot(Head.Returning, 40, headShown: true));
        Assert.Equal(-1, ShowReaderRules.HeadSlot(Head.Returning, 40, headShown: false));   // results mode: the head is not there
        Assert.Equal(-1, ShowReaderRules.HeadSlot(Head.Returning, 0, headShown: true));     // no resume episode
        foreach (var head in new[] { Head.New, Head.CaughtUp, Head.Pending, Head.Unavailable })
            Assert.Equal(-1, ShowReaderRules.HeadSlot(head, 40, headShown: true));          // only Returning owns a hero
    }

    static int RowsOf(int[] view, int skipSlot)
    {
        var items = new Item[ShowReaderShape.MaxItems(view.Length)];
        var marks = new Marks[items.Length];
        int n = ShowReaderRules.Layout(Slots, Pcts, Dates, view, LastPlayed, trusted: true, items, marks, skipSlot);
        return items[..n].Count(i => i.Kind == Kind.Row);
    }

    /// <summary>"why ger" matches only the hero's episode (slot 40): the body must list it — the head that showed it is
    /// hidden — instead of "Episodes 1 of 326" over an empty list.</summary>
    [Fact]
    public void AFindThatMatchesOnlyTheHerosEpisode_ListsExactlyOneRow()
    {
        var view = View(Status.All, oldest: false, [false, true, false, false]);
        Assert.Equal([1], view);
        bool results = ShowReaderRules.ResultsMode(Status.All, "why ger");
        int headSlot = ShowReaderRules.HeadSlot(Head.Returning, resumeSlot: 40, headShown: !results);
        Assert.Equal(1, RowsOf(view, headSlot));

        // the old rule (skip the hero whatever the mode) leaves nothing: the bug
        Assert.Equal(0, RowsOf(view, skipSlot: 40));
    }

    /// <summary>The count over the list ("N of Total") is the view's size, and in results mode that IS the rows shown —
    /// for every filter, with or without a find; with the head shown (sort alone) the hero's row moves into the head.</summary>
    [Theory]
    [InlineData(Status.Unplayed, false)]
    [InlineData(Status.InProgress, false)]
    [InlineData(Status.Played, false)]
    [InlineData(Status.All, true)]
    [InlineData(Status.Unplayed, true)]
    public void InResultsMode_TheCountIsTheRowsShown(Status status, bool withFind)
    {
        bool[]? found = withFind ? [true, true, false, true] : null;
        var view = View(status, oldest: false, found);
        bool results = ShowReaderRules.ResultsMode(status, withFind ? "x" : "");
        Assert.True(results);
        int headSlot = ShowReaderRules.HeadSlot(Head.Returning, resumeSlot: 40, headShown: !results);
        Assert.Equal(view.Length, RowsOf(view, headSlot));
    }

    [Fact]
    public void WithTheHeadShown_SortAlone_TheHerosEpisodeLeavesTheList()
    {
        var view = View(Status.All, oldest: true);
        Assert.False(ShowReaderRules.ResultsMode(Status.All, ""));
        int headSlot = ShowReaderRules.HeadSlot(Head.Returning, resumeSlot: 40, headShown: true);
        Assert.Equal(40, headSlot);
        Assert.Equal(view.Length - 1, RowsOf(view, headSlot));
    }

    // ── one episode, one place: up next never offers what "new since you were here" shows ──────────────────────────────

    // newest first: three unplayed after the last play (0, 1, 3), one in progress (2: the resume), one played, one old
    static readonly float[] UpPcts = [0f, 0f, 0.5f, 0f, 1f, 0f];
    static readonly int[] UpDates = [At(2026, 9, 20), At(2026, 9, 10), At(2026, 9, 1), At(2026, 8, 20), At(2026, 8, 10), At(2026, 8, 1)];
    static readonly int UpLastPlayed = At(2026, 8, 15);

    [Fact]
    public void FreshMask_IsTheNewSinceBlocksOwnPredicate_AndNeedsTrustedProgress()
    {
        var mask = new bool[UpPcts.Length];
        Assert.Equal(3, ShowReaderRules.FreshMask(UpPcts, UpDates, UpLastPlayed, trusted: true, mask));
        Assert.Equal([true, true, false, true, false, false], mask);

        Assert.Equal(0, ShowReaderRules.FreshMask(UpPcts, UpDates, UpLastPlayed, trusted: false, mask));
        Assert.All(mask, m => Assert.False(m));
        Assert.Equal(0, ShowReaderRules.FreshMask(UpPcts, UpDates, lastPlayedAt: 0, trusted: true, mask));   // never played: no "since"
    }

    [Fact]
    public void UpNext_AndNewSince_NeverShareAnEpisode()
    {
        var fresh = new bool[UpPcts.Length];
        ShowReaderRules.FreshMask(UpPcts, UpDates, UpLastPlayed, trusted: true, fresh);

        // without the rule the two blocks show the same three episodes ...
        Span<int> overlapping = stackalloc int[ListenNext.UpNextMax];
        var (resume0, count0) = ListenNext.Pick(UpPcts, ConsumptionOrder.Episodic, playing: -1, overlapping);
        Assert.Equal(2, resume0);
        Assert.Equal([0, 1, 3], overlapping[..count0].ToArray());

        // ... with it, up next takes the next candidates instead
        Span<int> up = stackalloc int[ListenNext.UpNextMax];
        var (resume, count) = ListenNext.Pick(UpPcts, ConsumptionOrder.Episodic, playing: -1, up, fresh);
        Assert.Equal(2, resume);                                    // the resume is never skipped
        Assert.Equal([5], up[..count].ToArray());
        for (int i = 0; i < count; i++) Assert.False(fresh[up[i]]);
    }

    [Fact]
    public void UpNext_SkipsFreshEpisodes_InASerialsStoryOrderToo()
    {
        // newest first = highest number first; nothing listened to yet but the fresh mask covers the two newest
        float[] pcts = [0f, 0f, 0f, 0f];
        bool[] fresh = [true, true, false, false];
        Span<int> up = stackalloc int[ListenNext.UpNextMax];
        var (resume, count) = ListenNext.Pick(pcts, ConsumptionOrder.Sequential, playing: -1, up, fresh);
        Assert.Equal(-1, resume);
        Assert.Equal([3, 2], up[..count].ToArray());                // the story's start, then forward — never a fresh one
    }

    // ── the up-next card's lead ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MiniLead_EveryCardNumbered_IsTheNumeral()
    {
        Assert.Equal(ShowReaderRules.MiniLeadKind.Numeral, ShowReaderRules.MiniLead(allNumbered: true, allHaveArt: true));
        Assert.Equal(ShowReaderRules.MiniLeadKind.Numeral, ShowReaderRules.MiniLead(allNumbered: true, allHaveArt: false));
    }

    [Fact]
    public void MiniLead_OneUnnumbered_WithArt_IsTheCover()
        => Assert.Equal(ShowReaderRules.MiniLeadKind.Art, ShowReaderRules.MiniLead(allNumbered: false, allHaveArt: true));

    [Fact]
    public void MiniLead_UnnumberedAndNoArt_IsNoLeadColumnAtAll()
        => Assert.Equal(ShowReaderRules.MiniLeadKind.None, ShowReaderRules.MiniLead(allNumbered: false, allHaveArt: false));

    // ── the head's columns: computed equal columns, never a wrapping row ─────────────────────────────────────────────

    [Theory]
    [InlineData(700f, 3)]
    [InlineData(684f, 3)]       // (684 + 12) / 232 = 3 exactly
    [InlineData(683f, 2)]
    [InlineData(452f, 2)]       // (452 + 12) / 232 = 2 exactly
    [InlineData(451f, 1)]
    [InlineData(100f, 1)]
    [InlineData(5000f, 3)]      // capped
    [InlineData(0f, 3)]         // not measured yet: the widest arm
    public void Columns_AreFloorOfWidthPlusGapOverMinPlusGap_AtLeastOne_AtMostMax(float width, int columns)
        => Assert.Equal(columns, ShowReaderRules.Columns(width, 220f, 12f, 3));

    [Fact]
    public void HeadColumns_TakeTheGuttersAndTheYearStripOffTheReadersWidth()
    {
        Assert.Equal(0f, ShowReaderRules.HeadWidth(0f));
        Assert.Equal(1000f - 30f - 48f, ShowReaderRules.HeadWidth(1000f));
        Assert.Equal(500f - 30f - 32f, ShowReaderRules.HeadWidth(500f));                 // the narrow arm's 16-DIP gutters
        Assert.Equal((3, 3), ShowReaderRules.HeadColumns(1000f));
        Assert.Equal((2, 2), ShowReaderRules.HeadColumns(600f));
        Assert.Equal((1, 1), ShowReaderRules.HeadColumns(500f));
        Assert.Equal((3, 3), ShowReaderRules.HeadColumns(0f));
    }

    // ── the list's extents ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SeedExtents_ArePerKind_AndAHiddenHeadIsZero()
    {
        float Seed(Kind k, Head h = Head.Pending, bool shown = true, bool noRule = false, int fresh = 0)
            => ShowReaderRules.SeedExtent(k, h, shown, noRule, fresh);

        Assert.Equal(48f, Seed(Kind.Rail));
        Assert.Equal(96f, Seed(Kind.Row));
        Assert.Equal(400f, Seed(Kind.Foot));
        Assert.Equal(42f, Seed(Kind.Header));
        Assert.Equal(42f, Seed(Kind.Group));
        Assert.Equal(28f, Seed(Kind.Group, noRule: true));             // right under the "episodes" head: no air above
        Assert.Equal(0f, Seed(Kind.Head, Head.Returning, shown: false, fresh: 2));
        Assert.Equal(204f, Seed(Kind.Head, Head.Pending));
        Assert.Equal(420f, Seed(Kind.Head, Head.New));
        Assert.Equal(278f, Seed(Kind.Head, Head.Returning));
        Assert.Equal(278f + 68f + 2 * 96f, Seed(Kind.Head, Head.Returning, fresh: 2));    // ≈ the ~500-DIP head the owner saw
        Assert.True(Seed(Kind.Head, Head.CaughtUp) < Seed(Kind.Head, Head.Returning));
    }

    [Fact]
    public void ExactExtents_AreTheArithmeticKinds_AndTheRestHaveNoDeclaration()
    {
        Assert.Equal(48f, ShowReaderRules.ExactExtent(Kind.Rail, false, false));
        Assert.Equal(42f, ShowReaderRules.ExactExtent(Kind.Group, noRule: false, headerEmpty: false));
        Assert.Equal(28f, ShowReaderRules.ExactExtent(Kind.Group, noRule: true, headerEmpty: false));
        Assert.Equal(42f, ShowReaderRules.ExactExtent(Kind.Header, false, headerEmpty: false));
        Assert.True(float.IsNaN(ShowReaderRules.ExactExtent(Kind.Header, false, headerEmpty: true)));   // the empty arm is content
        Assert.True(float.IsNaN(ShowReaderRules.ExactExtent(Kind.Row, false, false)));
        Assert.True(float.IsNaN(ShowReaderRules.ExactExtent(Kind.Head, false, false)));
        Assert.True(float.IsNaN(ShowReaderRules.ExactExtent(Kind.Foot, false, false)));
    }

    [Fact]
    public void ExtentDisagrees_IsMoreThanHalfADip_ForAnArrangedItemWithADeclaration()
    {
        Assert.False(ShowReaderRules.ExtentDisagrees(42f, 42f));
        Assert.False(ShowReaderRules.ExtentDisagrees(42f, 42.5f));
        Assert.True(ShowReaderRules.ExtentDisagrees(42f, 42.6f));
        Assert.True(ShowReaderRules.ExtentDisagrees(42f, 28f));
        Assert.False(ShowReaderRules.ExtentDisagrees(42f, 0f));              // an unarranged item is not a measurement
        Assert.False(ShowReaderRules.ExtentDisagrees(float.NaN, 300f));      // nothing declared, nothing to disagree with
    }

    // ── the date rail over the reader's own items (report 4b) ───────────────────────────────────────────────────────

    static (Item[] Items, JumpGroup[] Groups) Reader(int[] slots, int[] dates)
    {
        var items = new Item[ShowReaderShape.MaxItems(slots.Length)];
        int n = ShowReaderShape.Build(slots, dates, canLoadMore: false, hasSimilar: false, items);
        var groups = new JumpGroup[ShowDateIndex.MaxGroups];
        int g = ShowDateIndex.Project(items.AsSpan(0, n), groups);
        return (items[..n], groups[..g]);
    }

    [Fact]
    public void DateRail_ProjectsTheMonthHeadersOfTheReadersItems_AndJumpsByYear()
    {
        // newest first: two in September 2026, one in August 2026, one in July 2025
        var (items, groups) = Reader([41, 42, 43, 44],
            [At(2026, 9, 15), At(2026, 9, 1), At(2026, 8, 25), At(2025, 7, 7)]);

        // one group per month boundary, each at its HEADER's flat index in the same item space the list realizes
        Assert.Equal(new[] { 202609, 202608, 202507 }, Array.ConvertAll(groups, g => g.Key));
        foreach (var g in groups) Assert.Equal(Kind.Group, items[g.Index].Kind);

        var years = new int[groups.Length];
        int y = ShowDateIndex.Years(groups, years);
        Assert.Equal(new[] { 2026, 2025 }, years[..y]);            // distinct, in the view's own order

        // a year resolves to its FIRST month's header; a year the view does not hold no-ops (-1)
        Assert.Equal(groups[0].Index, ShowDateIndex.ResolveYear(groups, 2026));
        Assert.Equal(groups[2].Index, ShowDateIndex.ResolveYear(groups, 2025));
        Assert.Equal(-1, ShowDateIndex.ResolveYear(groups, 2024));
    }

    [Fact]
    public void DateRail_IsEmptyForAnUndatedReader_AndAKeyDecodesToItsYear()
    {
        var (_, groups) = Reader([1, 2], [0, 0]);
        Assert.Empty(groups);
        Assert.Equal(-1, ShowDateIndex.KeyOf(-1));
        Assert.Equal(-1, ShowDateIndex.YearOf(-1));
        Assert.Equal(2026, ShowDateIndex.YearOf(ShowDateIndex.KeyOf(ShowReaderShape.MonthKeyOf(At(2026, 3, 2)))));
        Assert.Equal(202603, ShowDateIndex.KeyOf(ShowReaderShape.MonthKeyOf(At(2026, 3, 2))));
    }

    /// <summary>The rail and the shape share ONE key space: the jump key IS the shape's group key, so a strip tap
    /// resolves the year the sticky header is showing. (They disagreed while two month encodings were alive: the strip
    /// divided a <c>year*12 + month-1</c> key by 100 and no year ever lit.)</summary>
    [Fact]
    public void DateRail_JumpKeyIsTheShapesOwnMonthKey_SoTheStripAgreesWithTheStickyHeader()
    {
        var (items, groups) = Reader([41, 42], [At(2026, 9, 15), At(2025, 7, 7)]);

        foreach (var g in groups)
        {
            int sticky = items[g.Index].GroupKey;                 // what the sticky-month signal carries
            Assert.Equal(g.Key, sticky);                          // ... is the jump key itself
            Assert.Equal(DateKeys.YearOfMonth(sticky), ShowDateIndex.YearOf(g.Key));
            Assert.Equal(g.Index, ShowDateIndex.ResolveYear(groups, DateKeys.YearOfMonth(sticky)));
        }
    }

    /// <summary>The rail's decoders are TOTAL: a key from no space at all answers -1 instead of throwing.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    [InlineData(11)]              // the old packed space's january of year 0
    [InlineData(24_296)]          // the old packed space's 2026-09
    [InlineData(202_613)]         // month 13
    [InlineData(202_600)]         // month 0
    [InlineData(20_260_915)]      // a DAY key, handed to a month decoder
    public void DateRail_RejectsAKeyItDidNotMint(int key)
    {
        Assert.Equal(-1, ShowDateIndex.KeyOf(key));
        Assert.Equal(-1, ShowDateIndex.YearOf(key));
    }
}
