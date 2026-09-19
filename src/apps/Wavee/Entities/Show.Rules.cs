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

// ══ 5. THE READER'S ITEM LIST ════════════════════════════════════════════════════════════════════════════════════════

/// <summary>What the reader's bound list realizes (§3.1): item 0 the sticky word rail (<see cref="Prefix"/> — the
/// list's <c>PersistentPrefixCount</c>), 1 the visit head, 2 the "episodes N" header, then the rows in VIEW order with a
/// month Group before each run of one UTC month, then the load-more Foot and the "more like this" shelf. Groups FOLLOW
/// the view, so an oldest-first view reads its months ascending. Rebuilt when its inputs publish, never per frame.
/// <para>ITEM FIELDS. Row: <c>Slot</c> = the caller's value from <c>viewSlots</c>, copied through uninterpreted (entity
/// slot or resident index — the rule never looks); <c>GroupKey</c> = the group it sits under. Group: <c>GroupKey</c> =
/// <c>year*12 + month-1</c> (<see cref="GroupKeyOf"/>), <c>Slot</c> = its FIRST row's slot, so (Group, Slot) is a unique
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

    /// <summary><c>year*12 + month-1</c> of a unix-seconds date in UTC; -1 when undated.</summary>
    public static int GroupKeyOf(int unixSeconds)
    {
        if (unixSeconds <= 0) return -1;
        var d = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        return d.Year * 12 + d.Month - 1;
    }

    public static int YearOf(int groupKey) => groupKey / 12;
    /// <summary>1-12.</summary>
    public static int MonthOf(int groupKey) => groupKey % 12 + 1;

    /// <param name="viewSlots">the rows, in VIEW order (filtered, found, sorted).</param>
    /// <param name="publishedAt">unix seconds PARALLEL to <paramref name="viewSlots"/> (entry k dates row k); 0 = undated.</param>
    /// <param name="into">at least <see cref="MaxItems"/>(viewSlots.Length) long; a shorter span gets what fits, in
    /// order, and never a group header without its first row.</param>
    /// <returns>the number of items written.</returns>
    public static int Build(ReadOnlySpan<int> viewSlots, ReadOnlySpan<int> publishedAt, bool canLoadMore, bool hasSimilar, Span<Item> into)
    {
        int n = 0;
        if (!Put(into, ref n, new Item(ItemKind.Rail, -1, -1)) || !Put(into, ref n, new Item(ItemKind.Head, -1, -1))
            || !Put(into, ref n, new Item(ItemKind.Header, -1, -1)))
            return n;

        int open = -1;
        for (int k = 0; k < viewSlots.Length; k++)
        {
            int key = GroupKeyOf(k < publishedAt.Length ? publishedAt[k] : 0);
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
