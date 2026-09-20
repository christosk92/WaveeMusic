// ── Entities/Show.Rules.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the show page's derived facts: the visit, listen-next, the played ledger, the cadence, the reader's item list, the
// episode doors and the per-show view prefs
//
// Role: CORE
// Owner: R
// Wave: P1 (the podcast rework)
// Budget: 420 lines (podcast-show-rework-implementation.md §11 → ch 09 §9.5)
// Spec: podcast-show-rework-implementation.md §5.2 (the code) · §4 (the interaction contract) · §6.1 (readiness) · §10
//   (the tests) · §12 D-1, D-5, D-6 · 0d0429a0 `ShowViewModel.cs:929-1036` (listen-next) · the approved prototype
//   `podcast-show-episode-mica.html` (`listenNext`, the ledger's P/U, the doors' `chrono[k ± 1]`)
//
// DERIVED FACTS LIVE ON THE MODEL. Every decision the show reader and the episode page make about a listener's place in
// a show is here, as a pure function of numbers the model already holds: pcts (`Episode.Rules.Pct`, one per RESIDENT
// episode, in the edge's NEWEST-FIRST order), unix-second dates (0 = unknown) and the show's `ConsumptionOrder`. Nothing
// reads a signal, a table or a clock — "the show's last play" comes in as an argument — so every rule is tested directly
// (§10) and the page renders answers instead of recomputing them. Nothing allocates except `ShowViewPrefs.Write`, which
// runs on a click.
//
// ONE COMPLETION RULE (D-5). `Episode.Rules.Completed` (≥ .98 or ≤ 30 s left) is THE verdict; these rules read pcts, so
// the caller passes a completed episode as 1f and `Episode.Rules.Played` then agrees with it everywhere below.

using System.Globalization;
using System.Text;

namespace Wavee;

// ══ 1. THE VISIT ═════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The show page's three first screens (plan W1/W2): New → start-here doors + the full about · Returning →
/// the continue hero, up next, "new since you were here" · CaughtUp → one line and the cadence.</summary>
public enum ShowVisitKind : byte { New, Returning, CaughtUp }

public static class ShowVisit
{
    /// <summary>Which first screen the visitor needs. PROGRESS BEATS FOLLOW STATE (D-1): someone 40 episodes in who
    /// never followed is Returning — follow is not even an input. CaughtUp NEEDS progress (an empty show nobody has
    /// played is New) and then means nothing RESIDENT is unplayed or in progress.
    /// <para>Feed it the ledger's own resident counts — <c>anyProgress = Played + InProgress &gt; 0</c>, <c>unplayed =
    /// ToGo</c> — so the head and the ledger line can never disagree. Meaningful only once progress has SETTLED (§6.1):
    /// until then the caller shows the head's skeleton, because a New head flashed at a Returning listener is a lie.</para></summary>
    public static ShowVisitKind Of(bool anyProgress, int unplayed, int inProgress)
        => !anyProgress ? ShowVisitKind.New
         : unplayed + inProgress <= 0 ? ShowVisitKind.CaughtUp
         : ShowVisitKind.Returning;
}

// ══ 2. LISTEN-NEXT ═══════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The continue hero (<c>Resume</c>) and up to <see cref="UpNextMax"/> mini cards, ported from 0d0429a0
/// <c>ShowViewModel.UpdateListenNextEpisodes</c> and made ORDER-AWARE — the WinUI app fetched <c>consumptionOrderV2</c>
/// and never read it.
/// <para>FINISHED = played or at/after <see cref="NearComplete"/> (0d0429a0's threshold — the last tenth is mostly
/// outro). A finished episode is never the resume and never up next.</para>
/// <para>RESUME = the now-playing episode unless it is played; else the NEWEST in-progress, unfinished one.</para>
/// <para>SERIAL (<see cref="ConsumptionOrder.Sequential"/>): the story continues from the ANCHOR — the OLDER of the
/// resume and the newest finished episode, i.e. the earliest point the listener stands at. (The WinUI anchor was the
/// newest finished, the resume only its fallback; the older of the two keeps "playing episode 3 after peeking at the
/// finale" on 4, 5, 6, and still offers 9 and 10 to someone who skipped ahead to 11.) Up next walks FORWARD from it
/// (toward index 0) past the resume and finished episodes; room left is filled in story order from the oldest
/// unfinished episode BEHIND the anchor — what the listener skipped. No anchor at all (never listened, nothing playing)
/// starts at episode 1, the highest index.</para>
/// <para>EVERY OTHER ORDER (episodic, recent, unknown): the newest unfinished episodes, the resume excluded.</para></summary>
public static class ListenNext
{
    /// <summary>At or above this an episode is finished for listen-next (0d0429a0 <c>nearCompleteProgressThreshold</c>).</summary>
    public const float NearComplete = 0.90f;
    /// <summary>The mini cards under the hero (W1's 3-up).</summary>
    public const int UpNextMax = 3;

    /// <summary>Played, or close enough that "resume" would be 3 minutes of credits.</summary>
    public static bool Finished(float pct) => Episode.Rules.Played(pct) || pct >= NearComplete;

    /// <param name="pcts">resident episodes, NEWEST FIRST (the edge order); a completed episode as 1f (D-5).</param>
    /// <param name="order">the show's consumption order; only <see cref="ConsumptionOrder.Sequential"/> walks the story.</param>
    /// <param name="playing">index into <paramref name="pcts"/> of THIS show's now-playing episode, or -1; an index
    /// outside the span reads as -1.</param>
    /// <param name="upNext">receives up to min(its length, <see cref="UpNextMax"/>) indices, in listening order.</param>
    /// <returns>the resume index (or -1) and how many indices were written to <paramref name="upNext"/>.</returns>
    public static (int Resume, int Count) Pick(ReadOnlySpan<float> pcts, ConsumptionOrder order, int playing, Span<int> upNext)
    {
        int cap = Math.Min(upNext.Length, UpNextMax), n = 0;
        int resume = (uint)playing < (uint)pcts.Length && !Episode.Rules.Played(pcts[playing]) ? playing : -1;
        for (int i = 0; resume < 0 && i < pcts.Length; i++)
            if (Episode.Rules.InProgress(pcts[i]) && !Finished(pcts[i])) resume = i;

        if (order != ConsumptionOrder.Sequential)
        {
            for (int i = 0; i < pcts.Length && n < cap; i++)
                if (i != resume && !Finished(pcts[i])) upNext[n++] = i;
            return (resume, n);
        }

        int finished = -1;                                            // the newest finished episode
        for (int i = 0; i < pcts.Length; i++)
            if (Finished(pcts[i])) { finished = i; break; }
        int anchor = Math.Max(resume, finished);                      // the OLDER of the two is the HIGHER index
        if (anchor < 0) anchor = pcts.Length;                         // never listened: just before episode 1
        for (int i = anchor - 1; i >= 0 && n < cap; i--)              // forward in the story = toward index 0
            if (i != resume && !Finished(pcts[i])) upNext[n++] = i;
        for (int i = pcts.Length - 1; i > anchor && n < cap; i--)     // then what was skipped, from episode 1 on
            if (i != resume && !Finished(pcts[i])) upNext[n++] = i;
        return (resume, n);
    }
}

// ══ 3. THE PLAYED LEDGER ═════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The rail's ledger (W1: the three-part bar and "84 played · 3 in progress · 41 to go · 5 new").
/// <para>COUNTS OVER THE RESIDENT EPISODES with the status filter's own three predicates — Played ≥ .98, InProgress in
/// (.01, .98), ToGo ≤ .01 — so the word rail's counts and this line never disagree; Played + InProgress + ToGo =
/// resident. <see cref="Fresh"/> ⊂ ToGo (<see cref="IsFresh"/>).</para>
/// <para>ONE EXTRAPOLATION — the tail rule (<see cref="TailAssumedPlayed"/>): an EPISODIC or RECENT show whose unloaded
/// members are older than everything resident, and whose OLDEST resident episode — the one adjacent to that tail — is
/// played, counts the tail (total − resident) as Played. Someone who has played up to the paging boundary of a show of
/// stand-alone episodes has, in practice, played what lies beyond it (the prototype's <c>total − unplayed −
/// inProgress</c>, guarded; 0d0429a0 counted the resident list only). A SERIAL never extrapolates — its tail is the
/// START of the story, and claiming it played would claim episode 1 — and neither does an Unknown order.</para></summary>
public readonly record struct ShowLedger(int Played, int InProgress, int ToGo, int Fresh)
{
    /// <param name="pcts">resident episodes, NEWEST FIRST (the edge order — paging loads newest first, so the unloaded
    /// tail is older); a completed episode as 1f (D-5).</param>
    /// <param name="publishedAt">unix seconds, PARALLEL to <paramref name="pcts"/>; 0 = unknown (never fresh).</param>
    /// <param name="lastPlayedAt">the show's last play, unix seconds (<see cref="LastPlayed"/>); 0 = never ⇒ nothing is fresh.</param>
    /// <param name="total">the show's episode count (the edge's Total); only the tail rule reads it.</param>
    public static ShowLedger Of(ReadOnlySpan<float> pcts, ReadOnlySpan<int> publishedAt, int lastPlayedAt, int total, ConsumptionOrder order)
    {
        int played = 0, progress = 0, toGo = 0, fresh = 0;
        for (int i = 0; i < pcts.Length; i++)
        {
            float p = pcts[i];
            if (Episode.Rules.Played(p)) played++;
            else if (Episode.Rules.InProgress(p)) progress++;
            else
            {
                toGo++;
                if (IsFresh(p, i < publishedAt.Length ? publishedAt[i] : 0, lastPlayedAt)) fresh++;
            }
        }
        if (TailAssumedPlayed(pcts, publishedAt, total, order)) played += total - pcts.Length;
        return new ShowLedger(played, progress, toGo, fresh);
    }

    /// <summary>"New since you were here" — the ledger's count, the head's section and the row's NEW mark are this ONE
    /// predicate: UNPLAYED (≤ .01 — a started episode is not news) and published STRICTLY after the show's last play.
    /// A show never played has no "since" (<paramref name="lastPlayedAt"/> 0 ⇒ false), nor has an undated episode.</summary>
    public static bool IsFresh(float pct, int publishedAt, int lastPlayedAt)
        => lastPlayedAt > 0 && publishedAt > lastPlayedAt && pct <= Episode.Rules.InProgressFloor;

    /// <summary>The show's last play: the newest <c>PlayedAt</c> (unix s) over its episodes; 0 when none was ever played.</summary>
    public static int LastPlayed(ReadOnlySpan<int> playedAt)
    {
        int last = 0;
        for (int i = 0; i < playedAt.Length; i++)
            if (playedAt[i] > last) last = playedAt[i];
        return last;
    }

    /// <summary>The tail rule (type doc), exactly: order is Episodic or Recent · total &gt; resident &gt; 0 · the dates are
    /// parallel to the pcts · the oldest resident (the LAST) is dated, dated no later than any other resident (so the
    /// unloaded tail beyond it is older than everything resident), and played.</summary>
    public static bool TailAssumedPlayed(ReadOnlySpan<float> pcts, ReadOnlySpan<int> publishedAt, int total, ConsumptionOrder order)
    {
        int last = pcts.Length - 1;
        if (order is not (ConsumptionOrder.Episodic or ConsumptionOrder.Recent) || last < 0 || total <= pcts.Length
            || publishedAt.Length != pcts.Length || !Episode.Rules.Played(pcts[last]) || publishedAt[last] <= 0)
            return false;
        for (int i = 0; i < last; i++)
            if (publishedAt[i] < publishedAt[last]) return false;
        return true;
    }
}

// ══ 4. THE CADENCE ═══════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>"weekly" in the rail's meta line, "New episodes usually land on Tuesdays." under caught-up.
/// <para>The SAMPLE is the newest ≤ <see cref="MaxSample"/> DATED episodes (0 = unknown is skipped), sorted, so an edge
/// that pins a trailer out of date order does not skew it; under <see cref="MinSample"/> dates everything is unknown.
/// The kind is the MEDIAN gap (a skipped week or a bonus drop moves a mean, not a median) against bands centred on the
/// four words the copy has: ≤ 1.5 d Daily · 5-10 d Weekly · 10-20 d Fortnightly · 20-45 d Monthly · anything else
/// Unknown. The 1.5-5 d hole is deliberate: twice or thrice a week has no word, and "weekly" would be a false fact.</para>
/// <para>The DAY is the modal UTC weekday of the same sample, only when it is a REAL mode — a single weekday holding at
/// least half the sample; a tie or a spread is null. UTC because the wire's dates are UTC and a rule takes no clock or
/// zone: a show released late on a US evening reads as the next UTC day.</para></summary>
public static class ShowCadence
{
    public enum Kind : byte { Unknown, Daily, Weekly, Fortnightly, Monthly }

    public const int MinSample = 4, MaxSample = 8;
    const int DaySeconds = 86_400;
    /// <summary>The bands, in seconds of median gap (type doc).</summary>
    public const int DailyMax = DaySeconds * 3 / 2, WeeklyMin = 5 * DaySeconds, WeeklyMax = 10 * DaySeconds,
        FortnightlyMax = 20 * DaySeconds, MonthlyMax = 45 * DaySeconds;

    public static (Kind Cadence, DayOfWeek? Day) Of(ReadOnlySpan<int> publishedAtNewestFirst)
    {
        Span<int> dates = stackalloc int[MaxSample];
        int n = 0;
        for (int i = 0; i < publishedAtNewestFirst.Length && n < MaxSample; i++)
            if (publishedAtNewestFirst[i] > 0) dates[n++] = publishedAtNewestFirst[i];
        if (n < MinSample) return (Kind.Unknown, null);
        var sample = dates[..n];
        sample.Sort();                                                // oldest first

        Span<int> gaps = stackalloc int[MaxSample - 1];
        for (int i = 1; i < n; i++) gaps[i - 1] = sample[i] - sample[i - 1];
        var g = gaps[..(n - 1)];
        g.Sort();
        long median = g.Length % 2 == 1 ? g[g.Length / 2] : ((long)g[g.Length / 2 - 1] + g[g.Length / 2]) / 2;

        Span<int> perDay = stackalloc int[7];
        for (int i = 0; i < n; i++) perDay[(int)DateTimeOffset.FromUnixTimeSeconds(sample[i]).UtcDateTime.DayOfWeek]++;
        int top = 0, holders = 0;
        for (int d = 1; d < 7; d++) if (perDay[d] > perDay[top]) top = d;
        for (int d = 0; d < 7; d++) if (perDay[d] == perDay[top]) holders++;
        DayOfWeek? day = holders == 1 && perDay[top] * 2 >= n ? (DayOfWeek)top : null;
        return (KindOf(median), day);
    }

    /// <summary>The band a median gap (seconds) falls in (type doc).</summary>
    public static Kind KindOf(long medianGapSeconds)
        => medianGapSeconds <= DailyMax ? Kind.Daily
         : medianGapSeconds < WeeklyMin ? Kind.Unknown
         : medianGapSeconds <= WeeklyMax ? Kind.Weekly
         : medianGapSeconds <= FortnightlyMax ? Kind.Fortnightly
         : medianGapSeconds <= MonthlyMax ? Kind.Monthly
         : Kind.Unknown;
}

// ══ 4b. THE ONE DATE-KEY FAMILY ══════════════════════════════════════════════════════════════════════════════════════

/// <summary>THE date keys the show reader and the episode row mint, and the ONLY decoders allowed to turn one back into
/// a date. Two keys, one family, both DECIMAL so a key reads as the date it is — in a log, a debugger, a crash dump —
/// and both sorting in calendar order:
/// <list type="bullet">
/// <item><b>month key</b> = <c>year*100 + month</c> (202609 is September 2026) — <see cref="MonthKeyOfUtc"/>, the show
/// reader's month groups and its date rail.</item>
/// <item><b>day key</b> = <c>year*10000 + month*100 + day</c> (20260915) — <see cref="DayKeyOfLocal"/>, the episode
/// row's date.</item>
/// </list>
/// <para>ONE ENCODING, ON PURPOSE. A packed <c>year*12 + month-1</c> month ordinal was alive here too (the reader's old
/// group key) beside <c>year*100 + month</c> (its date rail's), and a key minted in one space and decoded by the other
/// is an impossible date: 202409 read as year 16867, and anything below 12 read as year 0 — both of which threw
/// <c>ArgumentOutOfRangeException</c> out of a <c>DateTime</c> constructor on the RENDER path, which is a crashed app
/// loop, not a red squiggle. The packed space is deleted. Do not mint a third: mint through this type or not at all.</para>
/// <para>EVERY DECODER IS TOTAL. A key this type did not mint — the other space's, a truncated one, a zero month, a
/// February 30th — answers <see cref="None"/>, <c>false</c> or "" and never throws. That is the whole contract: a
/// render path must not be able to throw on data.</para></summary>
public static class DateKeys
{
    /// <summary>"no date": what an undated row, an unpinned sticky header and EVERY rejected key read as.</summary>
    public const int None = -1;

    /// <summary>The year range a <see cref="DateTime"/> can actually hold. A key outside it is not a date.</summary>
    const int MinYear = 1, MaxYear = 9999;

    // ── minting ──

    /// <summary>The month key of a year and month, or <see cref="None"/> when that is not a month.</summary>
    public static int MonthKey(int year, int month)
        => (uint)(year - MinYear) <= MaxYear - MinYear && (uint)(month - 1) < 12u ? year * 100 + month : None;

    /// <summary>The month key of a unix-seconds stamp in UTC (the reader groups by UTC month); <see cref="None"/> for
    /// an unknown date (0 or negative) — never a "january 1970".</summary>
    public static int MonthKeyOfUtc(int unixSeconds)
    {
        if (unixSeconds <= 0) return None;
        var d = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return MonthKey(d.Year, d.Month);
    }

    /// <summary>The day key of a year, month and day, or <see cref="None"/> when that is not a day (a zero month, a
    /// zero day, a February 30th).</summary>
    public static int DayKey(int year, int month, int day)
        => MonthKey(year, month) != None && (uint)(day - 1) < (uint)DateTime.DaysInMonth(year, month)
            ? year * 10000 + month * 100 + day
            : None;

    /// <summary>The day key of a unix-seconds stamp in the LISTENER'S clock (a row's date is the date they saw it
    /// published); <see cref="None"/> for an unknown date.</summary>
    public static int DayKeyOfLocal(int unixSeconds)
    {
        if (unixSeconds <= 0) return None;
        var d = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        return DayKey(d.Year, d.Month, d.Day);
    }

    // ── decoding (total) ──

    /// <summary>Splits a month key. False — with <paramref name="year"/> <see cref="None"/> and
    /// <paramref name="month"/> 0 — for anything that is not one.</summary>
    public static bool TryMonth(int monthKey, out int year, out int month)
    {
        if (monthKey > 0)
        {
            int y = monthKey / 100, m = monthKey % 100;
            if (MonthKey(y, m) == monthKey)
            {
                (year, month) = (y, m);
                return true;
            }
        }
        (year, month) = (None, 0);
        return false;
    }

    /// <summary>Splits a day key; false for anything that is not one.</summary>
    public static bool TryDay(int dayKey, out int year, out int month, out int day)
    {
        if (dayKey > 0)
        {
            int y = dayKey / 10000, m = dayKey / 100 % 100, d = dayKey % 100;
            if (DayKey(y, m, d) == dayKey)
            {
                (year, month, day) = (y, m, d);
                return true;
            }
        }
        (year, month, day) = (None, 0, 0);
        return false;
    }

    /// <summary>Is this a key this type could have minted?</summary>
    public static bool IsMonthKey(int monthKey) => TryMonth(monthKey, out _, out _);
    public static bool IsDayKey(int dayKey) => TryDay(dayKey, out _, out _, out _);

    /// <summary>The year of a month key; <see cref="None"/> for anything that is not one.</summary>
    public static int YearOfMonth(int monthKey) => TryMonth(monthKey, out int year, out _) ? year : None;

    /// <summary>The month (1-12) of a month key; 0 for anything that is not one.</summary>
    public static int MonthOfMonth(int monthKey) => TryMonth(monthKey, out _, out int month) ? month : 0;

    /// <summary>The first of the month a month key names. False leaves <paramref name="date"/> at
    /// <c>default</c> — the one place a <see cref="DateTime"/> is built from a key, and it is built only from
    /// components this type has already validated.</summary>
    public static bool TryMonthStart(int monthKey, out DateTime date)
    {
        if (TryMonth(monthKey, out int year, out int month))
        {
            date = new DateTime(year, month, 1);
            return true;
        }
        date = default;
        return false;
    }

    /// <summary>The day a day key names; false leaves <paramref name="date"/> at <c>default</c>.</summary>
    public static bool TryDayDate(int dayKey, out DateTime date)
    {
        if (TryDay(dayKey, out int year, out int month, out int day))
        {
            date = new DateTime(year, month, day);
            return true;
        }
        date = default;
        return false;
    }

    // ── labels (the render path's entry points: a label, never an exception) ──

    /// <summary>"MMMM yyyy" of a month key in <paramref name="culture"/>; "" for a key that is not one. A month name is
    /// culture text, so the caller lowers it with the CURRENT culture when its design asks for lowercase.</summary>
    public static string MonthLabel(int monthKey, CultureInfo culture)
        => TryMonthStart(monthKey, out var date) ? date.ToString("MMMM yyyy", culture) : "";

    /// <summary>"MMM d" of a day key in <paramref name="culture"/>; "" for a key that is not one.</summary>
    public static string DayLabel(int dayKey, CultureInfo culture)
        => TryDayDate(dayKey, out var date) ? date.ToString("MMM d", culture) : "";
}

// ══ 5. THE READER'S ITEM LIST ════════════════════════════════════════════════════════════════════════════════════════

/// <summary>What the reader's bound list realizes (§3.1): item 0 the sticky word rail (<see cref="Prefix"/> — the
/// list's <c>PersistentPrefixCount</c>), 1 the visit head, 2 the "episodes N" header, then the rows in VIEW order with a
/// month Group before each run of one UTC month, then the load-more Foot and the "more like this" shelf. Groups FOLLOW
/// the view, so an oldest-first view reads its months ascending. Rebuilt when its inputs publish, never per frame.
/// <para>ITEM FIELDS. Row: <c>Slot</c> = the caller's value from <c>viewSlots</c>, copied through uninterpreted (entity
/// slot or resident index — the rule never looks); <c>GroupKey</c> = the group it sits under. Group: <c>GroupKey</c> =
/// a <see cref="DateKeys"/> MONTH KEY, <c>year*100 + month</c> (<see cref="MonthKeyOf"/>), and nothing else ever — it is
/// the same key the date rail jumps by; <c>Slot</c> = its FIRST row's slot, so (Group, Slot) is a unique
/// list key even when a view that is not date-ordered revisits a month. Rail/Head/Header/Foot/Similar: -1, -1.</para>
/// <para>UNDATED rows (0) never open a group: they join the running one, and rows before any dated row sit under no
/// header (GroupKey -1) — never a "january 1970".</para></summary>
public static class ShowReaderShape
{
    public enum ItemKind : byte { Rail, Head, Header, Group, Row, Foot, Similar }
    public readonly record struct Item(ItemKind Kind, int Slot, int GroupKey);

    /// <summary>The sticky rail: <c>ListOptions.PersistentPrefixCount</c>.</summary>
    public const int Prefix = 1;
    /// <summary>Rail + Head + Header + Foot + Similar.</summary>
    public const int Fixed = 5;

    /// <summary>The capacity <see cref="Build"/> needs so nothing is dropped: every row opening its own month is the
    /// worst case.</summary>
    public static int MaxItems(int viewCount) => Fixed + 2 * Math.Max(0, viewCount);

    /// <summary>The <see cref="DateKeys"/> month key (<c>year*100 + month</c>) of a unix-seconds date in UTC;
    /// <see cref="DateKeys.None"/> when undated. There is no second month encoding: decode it with
    /// <see cref="DateKeys.YearOfMonth"/> / <see cref="DateKeys.MonthOfMonth"/> / <see cref="DateKeys.MonthLabel"/>,
    /// all of which are total.</summary>
    public static int MonthKeyOf(int unixSeconds) => DateKeys.MonthKeyOfUtc(unixSeconds);

    /// <param name="viewSlots">the rows, in VIEW order (filtered, found, sorted).</param>
    /// <param name="publishedAt">unix seconds PARALLEL to <paramref name="viewSlots"/> (entry k dates row k); 0 = undated.</param>
    /// <param name="into">at least <see cref="MaxItems"/>(viewSlots.Length) long; a shorter span gets what fits, in
    /// order, and never a group header without its first row.</param>
    /// <param name="skipSlot">ONE slot the body must not repeat (report 11a): the episode the visit head already shows
    /// as its hero. -1 (the default) skips nothing. The skipped row opens no month either — the next row of that month
    /// opens it, carrying its own slot — so a head episode alone in its month takes its header with it.</param>
    /// <returns>the number of items written.</returns>
    public static int Build(ReadOnlySpan<int> viewSlots, ReadOnlySpan<int> publishedAt, bool canLoadMore, bool hasSimilar,
                            Span<Item> into, int skipSlot = -1)
    {
        int n = 0;
        if (!Put(into, ref n, new Item(ItemKind.Rail, -1, -1)) || !Put(into, ref n, new Item(ItemKind.Head, -1, -1))
            || !Put(into, ref n, new Item(ItemKind.Header, -1, -1)))
            return n;

        int open = -1;
        for (int k = 0; k < viewSlots.Length; k++)
        {
            if (skipSlot != -1 && viewSlots[k] == skipSlot) continue;
            int key = MonthKeyOf(k < publishedAt.Length ? publishedAt[k] : 0);
            if (key >= 0 && key != open)
            {
                if (into.Length - n < 2) return n;
                into[n++] = new Item(ItemKind.Group, viewSlots[k], key);
                open = key;
            }
            if (!Put(into, ref n, new Item(ItemKind.Row, viewSlots[k], open))) return n;
        }
        if (canLoadMore && !Put(into, ref n, new Item(ItemKind.Foot, -1, -1))) return n;
        if (hasSimilar) _ = Put(into, ref n, new Item(ItemKind.Similar, -1, -1));
        return n;
    }

    static bool Put(Span<Item> into, ref int n, Item item)
    {
        if (n >= into.Length) return false;
        into[n++] = item;
        return true;
    }
}

// ══ 5b. THE READER'S TOOLBAR AND ITS DATE RAIL ═══════════════════════════════════════════════════════════════════════

/// <summary>How much of the show reader's toolbar still fits. ONE row at ONE height at every stage — the plane the row
/// sits on is the list's mount-time clip inset, so nothing here may add a second line.</summary>
public enum ToolbarStage : byte
{
    /// <summary>The resting shape: filter words with counts · spacer · find field · divider · sort words · select.</summary>
    Full,
    /// <summary>The find FIELD becomes a search glyph in the same row (opening it swaps the row's middle for the field
    /// plus a close affordance — never a second row).</summary>
    FindIcon,
    /// <summary>And the two sort WORDS become one glyph menu, so the filter words keep their counts.</summary>
    CompactSort,
}

/// <summary>The show reader's toolbar (report 4a, revised after the three-row build was rejected). There is no
/// measure-and-classify pass and no pressure classifier: the toolbar is ONE row whose pieces collapse at two
/// DETERMINISTIC breakpoints on the measured rail width, and a horizontal scroller is never an answer.
/// <para>THE ARITHMETIC behind the breakpoints, at the rail's own type (<c>Controls.Words</c> 13.5/18, gap 14) inside
/// the reader's narrow 16-DIP gutters: the four counted filter words run ≈280 DIP, the two sort words ≈90, the search
/// and select glyphs 30 each, the divider 1, and the row's four 16-DIP gaps ≈64. <see cref="FindCollapsesBelow"/>
/// (640) is where the 180-DIP find field stops fitting beside all of that; <see cref="SortCollapsesBelow"/> (480)
/// is where what is LEFT (≈450 DIP) meets the 448 DIP of content 480 leaves, so the sort words go next and the filter
/// counts — the thing the owner reads the toolbar for — survive to the narrowest window.</para>
/// <para>A width of 0 is "not measured yet" and reads WIDE, exactly as <see cref="ShowReaderRules.Narrow"/> does: the
/// first frame must not flash a collapsed arm at a window that turns out to be 1200 DIP.</para></summary>
public static class ShowToolbarLayout
{
    /// <summary>Below this the find field collapses behind a search icon (<see cref="ToolbarStage.FindIcon"/>).</summary>
    public const float FindCollapsesBelow = 640f;
    /// <summary>Below this the sort words additionally fold into one compact control
    /// (<see cref="ToolbarStage.CompactSort"/>). Necessarily below <see cref="FindCollapsesBelow"/>.</summary>
    public const float SortCollapsesBelow = 480f;

    /// <summary>The stage a measured rail width is in (type doc).</summary>
    public static ToolbarStage Of(float width)
        => width <= 0f ? ToolbarStage.Full
         : width < SortCollapsesBelow ? ToolbarStage.CompactSort
         : width < FindCollapsesBelow ? ToolbarStage.FindIcon
         : ToolbarStage.Full;

    /// <summary>The one bit the find box itself needs: is it collapsed behind the search glyph? True for BOTH collapsed
    /// stages — <see cref="ToolbarStage.CompactSort"/> folds the sort on top of an already-collapsed find.</summary>
    public static bool Narrow(float width) => Of(width) != ToolbarStage.Full;
}

/// <summary>The reader's date rail (report 4b) — the Albums list's A–Z jump strip, for dates. It is the SAME kernel
/// (<see cref="JumpIndex"/>) over the same shape: the month <see cref="ShowReaderShape.ItemKind.Group"/> items are the
/// headers, keyed by the shape's OWN <see cref="DateKeys"/> month key — there is no translation here any more, because
/// there is no second encoding to translate from; the strip itself shows YEARS, because a decade of months is a
/// scrollbar, not a strip.</summary>
public static class ShowDateIndex
{
    /// <summary>The jump table's cap — a show with more months than this stops collecting further groups (the kernel's
    /// documented fail-closed behaviour), which is ~42 years of monthly releases.</summary>
    public const int MaxGroups = 512;

    /// <summary>The jump key of a <see cref="ShowReaderShape"/> group key. The two ARE the same
    /// <see cref="DateKeys"/> month key now, so this is the identity — kept as the one named seam both projections (the
    /// shape's own, below, and the page's over its <c>ReaderItem</c> twin) go through, and it NORMALIZES: anything that
    /// is not a month key, including the undated run's -1, reads <see cref="DateKeys.None"/>.</summary>
    public static int KeyOf(int groupKey) => DateKeys.IsMonthKey(groupKey) ? groupKey : DateKeys.None;

    /// <summary>The year of a month key; <see cref="DateKeys.None"/> for anything that is not one.</summary>
    public static int YearOf(int monthKey) => DateKeys.YearOfMonth(monthKey);

    /// <summary>The month groups of a reader item list, as <see cref="JumpGroup"/>s over the FLAT item space (the page
    /// runs the same two lambdas over its own item record — both go through <see cref="KeyOf"/>).</summary>
    public static int Project(ReadOnlySpan<ShowReaderShape.Item> items, Span<JumpGroup> into)
        => JumpIndex.Project(items, s_isGroup, s_key, into);

    static readonly Func<ShowReaderShape.Item, bool> s_isGroup = static it => it.Kind == ShowReaderShape.ItemKind.Group;
    static readonly Func<ShowReaderShape.Item, int> s_key = static it => KeyOf(it.GroupKey);

    /// <summary>The DISTINCT years of a projected group table, in the order the groups were encountered (so an
    /// oldest-first view reads its years ascending and a newest-first one descending). Returns the count written.</summary>
    public static int Years(ReadOnlySpan<JumpGroup> groups, Span<int> into)
    {
        int n = 0;
        for (int i = 0; i < groups.Length && n < into.Length; i++)
        {
            int year = YearOf(groups[i].Key);
            if (year < 0) continue;
            bool seen = false;
            for (int j = 0; j < n && !seen; j++) seen = into[j] == year;
            if (!seen) into[n++] = year;
        }
        return n;
    }

    /// <summary>The flat index of the FIRST month group of <paramref name="year"/>, or -1 when the view holds none —
    /// the one failure mode a strip tap no-ops on (<see cref="JumpIndex.Resolve"/>'s own contract).</summary>
    public static int ResolveYear(ReadOnlySpan<JumpGroup> groups, int year)
    {
        for (int i = 0; i < groups.Length; i++)
            if (YearOf(groups[i].Key) == year) return groups[i].Index;
        return -1;
    }
}

// ══ 5c. THE AUDIOBOOK'S CHAPTER ORDER ════════════════════════════════════════════════════════════════════════════════

/// <summary>An audiobook's chapters, normalized onto the reader's ONE order. The provider hands an audiobook's
/// membership in NATIVE PLAYLIST ORDER — chapter 1 first — while every other show (and therefore every rule here: the
/// ledger, listen-next, the view, the month groups) reads NEWEST FIRST. Normalization is that one reversal, plus the
/// trailers lifted out.
/// <para>TRAILERS ARE NOT CHAPTERS. A sample (<see cref="EpisodeKind.Trailer"/>) is excluded from the reader's list and
/// from every count, and reported separately so the page can offer it as its own action — exactly as a podcast's
/// trailer door does. Hydration that has not identified the kinds yet (a SHORT or empty <paramref name="kinds"/> span)
/// treats every item as a chapter: an unknown chapter is kept and never reordered, because dropping it would be worse
/// than showing a sample among the chapters for one frame.</para>
/// <para>NORMALIZATION NEVER REWRITES PLAYBACK'S MEMBERSHIP: it only writes the caller's OUTPUT span, never
/// <paramref name="provider"/> — the context Spotify plays is still the native order it sent.</para></summary>
public static class AudiobookOrder
{
    /// <param name="provider">the membership in the provider's native playlist order (chapter 1 first). Untouched.</param>
    /// <param name="kinds">parallel to <paramref name="provider"/>; a shorter (or empty) span reads as all-chapters.</param>
    /// <param name="reader">receives the chapters in READER order (last chapter first); needs <paramref name="provider"/>'s length.</param>
    /// <param name="trailer">the FIRST trailer in provider order, or 0 when there is none.</param>
    /// <param name="trailerCount">how many trailers were excluded.</param>
    /// <returns>the number of chapters written to <paramref name="reader"/>.</returns>
    public static int Normalize(ReadOnlySpan<int> provider, ReadOnlySpan<EpisodeKind> kinds, Span<int> reader,
                                out int trailer, out int trailerCount)
    {
        trailer = 0;
        trailerCount = 0;
        int n = 0;
        for (int i = provider.Length - 1; i >= 0; i--)
        {
            if (i < kinds.Length && kinds[i] == EpisodeKind.Trailer) continue;
            if (n < reader.Length) reader[n] = provider[i];
            n++;
        }
        for (int i = 0; i < provider.Length; i++)
        {
            if (i >= kinds.Length || kinds[i] != EpisodeKind.Trailer) continue;
            if (trailerCount == 0) trailer = provider[i];
            trailerCount++;
        }
        return Math.Min(n, reader.Length);
    }

    /// <summary>The chapter number of reader position <paramref name="index"/> over <paramref name="count"/> chapters:
    /// the reader runs last-chapter-first, so position 0 is the last chapter and the final position is chapter 1. The
    /// provider states no <c>number</c> on an audiobook item, which is why the reader numbers by POSITION.</summary>
    public static int ChapterNumber(int index, int count) => (uint)index < (uint)count ? count - index : 0;
}

// ══ 6. THE EPISODE PAGE'S DOORS ══════════════════════════════════════════════════════════════════════════════════════

public static class EpisodeNeighbours
{
    /// <summary>The next/previous doors under the episode page's reader. NEXT is the NEWER neighbour and PREVIOUS the
    /// older, in EVERY order: for a serial that is the plan's "number + 1", for anything else "the newer one" (§5.2),
    /// and the prototype takes <c>chrono[k+1]</c> / <c>chrono[k-1]</c> for both. The order changes only the COPY —
    /// "next in the story" · next / previous for a serial, "around this episode" · newer / older otherwise — which the
    /// page reads from the show's order itself, so this rule takes none (a parameter no branch reads is a fact that can
    /// lie). Returns SLOTS from <paramref name="slotsNewestFirst"/>; -1 at either end, and both -1 when
    /// <paramref name="self"/> is not among them.</summary>
    public static (int Next, int Previous) Of(ReadOnlySpan<int> slotsNewestFirst, int self)
    {
        int at = slotsNewestFirst.IndexOf(self);
        if (at < 0) return (-1, -1);
        return (at > 0 ? slotsNewestFirst[at - 1] : -1, at + 1 < slotsNewestFirst.Length ? slotsNewestFirst[at + 1] : -1);
    }
}

// ══ 7. THE VIEW PREFS ════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Filter + sort per show in ONE setting (<see cref="Platform.Keys.PodcastViews"/>, D-6) — never a key per
/// uri, which grows the store by one value for every show ever opened.
/// <para>FORMAT: <c>id:status:order;id:status:order;…</c>, most recently written FIRST, at most <see cref="Cap"/>
/// entries: <see cref="Write"/> moves its show to the front and drops the least recent past the cap. <c>id</c> is the
/// show's BASE62 id — never its uri, whose colons are the separator: an id that is empty or holds ':' or ';' is
/// rejected (<see cref="Read"/> answers the defaults, <see cref="Write"/> returns the blob unchanged). status/order are
/// the caller's enums as non-negative decimal ints (<c>Episode.Rules.Status</c>; 0 newest · 1 oldest) — the caller
/// clamps a value its enum does not define, since the store is hand-editable.</para>
/// <para>MALFORMED input never throws: an entry that is not exactly three fields, an empty id, or a non-digit number is
/// skipped (and dropped by the next write); a blob with no valid entry for the show answers the defaults (0, 0).</para></summary>
public static class ShowViewPrefs
{
    public const int Cap = 64;

    public static bool IsValidId(ReadOnlySpan<char> id) => id.Length > 0 && id.IndexOfAny(':', ';') < 0;

    /// <summary>The show's stored (status, order); (0, 0) when absent, malformed or the id is invalid. Allocation-free.</summary>
    public static (int Status, int Order) Read(string? blob, ReadOnlySpan<char> showId)
    {
        if (!IsValidId(showId)) return (0, 0);
        var rest = blob.AsSpan();
        while (!rest.IsEmpty)
        {
            var entry = Next(ref rest);
            if (TryParse(entry, out var id, out int status, out int order) && id.SequenceEqual(showId)) return (status, order);
        }
        return (0, 0);
    }

    /// <summary>The blob with this show's entry FIRST (negative values stored as 0), every other valid entry after it in
    /// its old order, trimmed to <see cref="Cap"/>. An invalid id returns <paramref name="blob"/> unchanged.</summary>
    public static string Write(string? blob, ReadOnlySpan<char> showId, int status, int order)
    {
        if (!IsValidId(showId)) return blob ?? "";
        var sb = new StringBuilder((blob?.Length ?? 0) + showId.Length + 24);
        sb.Append(showId).Append(':').Append(Math.Max(0, status)).Append(':').Append(Math.Max(0, order));
        int kept = 1;
        var rest = blob.AsSpan();
        while (!rest.IsEmpty && kept < Cap)
        {
            var entry = Next(ref rest);
            if (!TryParse(entry, out var id, out _, out _) || id.SequenceEqual(showId)) continue;
            sb.Append(';').Append(entry);
            kept++;
        }
        return sb.ToString();
    }

    static ReadOnlySpan<char> Next(ref ReadOnlySpan<char> rest)
    {
        int cut = rest.IndexOf(';');
        var entry = cut < 0 ? rest : rest[..cut];
        rest = cut < 0 ? default : rest[(cut + 1)..];
        return entry;
    }

    static bool TryParse(ReadOnlySpan<char> entry, out ReadOnlySpan<char> id, out int status, out int order)
    {
        status = order = 0;
        int a = entry.IndexOf(':');
        id = a < 0 ? default : entry[..a];
        if (a <= 0) return false;
        var tail = entry[(a + 1)..];
        int b = tail.IndexOf(':');
        return b >= 0
            && int.TryParse(tail[..b], NumberStyles.None, CultureInfo.InvariantCulture, out status)
            && int.TryParse(tail[(b + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out order);
    }
}
