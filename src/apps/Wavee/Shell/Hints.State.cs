// ── Shell/Hints.State.cs — the hint service's ONE persisted blob (`hints.state`): per-hint budget, lasting use facts, Recently changed ──
//
// Role: CORE (settings rework phase 3, handoff/settings-2026-11-18/DESIGN.md 6.2; owner decision 11). The `ShowViewPrefs` pattern: one
// string key, `Platform.Keys.HintsState`, read and written ONLY here; this file owns the format. A factory reset clears it with the rest of
// the store.
//
// FORMAT: `v1|h=<hint>;<hint>|f=<fact>;<fact>|r=<write>;<write>`, sections in any order, unknown sections ignored:
//   · a hint   `id,shows,lastDay,snoozedUntil,dismissals,retired`  (days are local day numbers, `DateOnly.DayNumber`; retired 0|1);
//   · a fact   `name,a,b`                                          (`bell,n` · `quit,unixMs` · `relaunch,day,n` · `signin,day,n`);
//   · a write  `dialId,surface,unixMs`                             (newest first, at most `HintRules.RecentCap`, one per dial).
// An id holds none of `|;,=`. MALFORMED input never throws: an entry that does not parse, or names a hint the table no longer has, is
// skipped and dropped by the next write.
//
// COST. Read once (lazily, or by `Reload` as a launch does); written only when something it holds changes: a show (once per hint per
// day), a dismissal, a retirement, a lasting fact, a dial write outside the coalescing window. Never per frame.

using System.Globalization;
using System.Text;

namespace Wavee;

/// <summary>The hint blob's typed door (see the file header).</summary>
public static class HintState
{
    struct Budget
    {
        public int Shows, LastDay, SnoozedUntil, Dismissals;
        public bool Retired;
    }

    /// <summary>One dial write of Recently changed: the dial, where it came from (a <see cref="SettingsSurfaces"/> name) and when (unix ms).</summary>
    public readonly record struct RecentWrite(string DialId, string Surface, long AtMs);

    static Budget[] s_budget = [];
    static readonly RecentWrite[] s_recent = new RecentWrite[HintRules.RecentCap];
    static int s_recentCount;
    static int s_bellOpens, s_relaunchDay, s_relaunchCount, s_signInDay, s_signInDays;
    static long s_lastQuitMs;
    static bool s_loaded;

    // ── budget (per hint, by its index in Hints.All) ──────────────────────────────────────────────────────────────────────

    public static int Shows(int hint) => Loaded()[hint].Shows;
    public static int LastDay(int hint) => Loaded()[hint].LastDay;
    public static int SnoozedUntil(int hint) => Loaded()[hint].SnoozedUntil;
    public static int Dismissals(int hint) => Loaded()[hint].Dismissals;
    public static bool Retired(int hint) => Loaded()[hint].Retired;

    /// <summary>The hint showed on <paramref name="day"/>: one show per day, however often it mounts.</summary>
    internal static void MarkShown(int hint, int day)
    {
        ref Budget b = ref Loaded()[hint];
        if (b.LastDay == day) return;
        b.Shows++;
        b.LastDay = day;
        Save();
    }

    /// <summary>"×" / "Not now": snoozed for <paramref name="snoozeDays"/>; the third dismissal (or the first, for a hint that says so) retires it.</summary>
    internal static void MarkDismissed(int hint, int day, int snoozeDays, bool retires)
    {
        ref Budget b = ref Loaded()[hint];
        b.Dismissals++;
        b.SnoozedUntil = day + snoozeDays;
        if (retires || b.Dismissals >= HintRules.RetiringDismissal) b.Retired = true;
        Save();
    }

    /// <summary>Retired for good (its dial was written, or it reached the state it suggests).</summary>
    internal static void MarkRetired(int hint)
    {
        ref Budget b = ref Loaded()[hint];
        if (b.Retired) return;
        b.Retired = true;
        Save();
    }

    /// <summary>"Reset tips": every hint may show again (show counts, snoozes, dismissals and retirements cleared). The use facts and
    /// Recently changed stay.</summary>
    public static void ResetHints()
    {
        Loaded();
        Array.Clear(s_budget);
        Save();
    }

    // ── lasting use facts (the ones a trigger counts across launches) ─────────────────────────────────────────────────────

    /// <summary>#7: how many times the bell has been opened.</summary>
    public static int BellOpens => Ensure(s_bellOpens);

    internal static void BellOpened()
    {
        Loaded();
        if (s_bellOpens >= HintRules.BellOpens) return;   // the trigger only counts to its threshold: no write past it
        s_bellOpens++;
        Save();
    }

    /// <summary>#8: when Wavee last quit by the window's ✕ (unix ms), 0 = never.</summary>
    public static long LastQuitMs => Ensure(s_lastQuitMs);

    /// <summary>#8: quick relaunches on <paramref name="day"/> (0 for any other day).</summary>
    public static int RelaunchesOn(int day) => Ensure(s_relaunchDay) == day ? s_relaunchCount : 0;

    /// <summary>#9: the days Wavee was started by hand right after signing in to Windows.</summary>
    public static int SignInDays => Ensure(s_signInDays);

    internal static void QuitByClose(long nowMs)
    {
        Loaded();
        s_lastQuitMs = nowMs;
        Save();
    }

    internal static void Relaunched(int day)
    {
        Loaded();
        if (s_relaunchDay != day) (s_relaunchDay, s_relaunchCount) = (day, 0);
        s_relaunchCount++;
        Save();
    }

    internal static void SignInLaunch(int day)
    {
        Loaded();
        if (s_signInDay == day) return;
        s_signInDay = day;
        s_signInDays++;
        Save();
    }

    /// <summary>Forget the use facts (the inspector's <c>hint reset</c>).</summary>
    public static void ResetFacts()
    {
        Loaded();
        s_bellOpens = s_relaunchDay = s_relaunchCount = s_signInDay = s_signInDays = 0;
        s_lastQuitMs = 0;
        Save();
    }

    // ── Recently changed ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How many writes the ring holds (at most <see cref="HintRules.RecentCap"/>).</summary>
    public static int RecentCount => Ensure(s_recentCount);

    /// <summary>The write at <paramref name="i"/>, newest first.</summary>
    public static RecentWrite Recent(int i)
    {
        Loaded();
        return (uint)i < (uint)s_recentCount ? s_recent[i] : default;
    }

    /// <summary>A dial was written (<see cref="SettingsWrites"/>): it moves to the front. The head dial written again from the same source
    /// within <see cref="HintRules.RecentCoalesceMs"/> moves in place and writes nothing.</summary>
    internal static void RecordWrite(string dialId, string surface, long nowMs)
    {
        Loaded();
        if (s_recentCount > 0)
        {
            ref RecentWrite head = ref s_recent[0];
            if (Same(head.DialId, dialId) && Same(head.Surface, surface) && nowMs - head.AtMs < HintRules.RecentCoalesceMs)
            {
                head = head with { AtMs = nowMs };
                return;
            }
        }
        int at = s_recentCount;
        for (int i = 0; i < s_recentCount; i++)
            if (Same(s_recent[i].DialId, dialId)) { at = i; break; }
        if (at == s_recentCount && s_recentCount < s_recent.Length) s_recentCount++;
        for (int i = Math.Min(at, s_recent.Length - 1); i > 0; i--) s_recent[i] = s_recent[i - 1];
        s_recent[0] = new RecentWrite(dialId, surface, nowMs);
        Save();
    }

    /// <summary>Empty the ring (the inspector's <c>recent clear=true</c>).</summary>
    public static void ClearRecent()
    {
        Loaded();
        Array.Clear(s_recent);
        s_recentCount = 0;
        Save();
    }

    static bool Same(string a, string b) => ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.Ordinal);

    // ── the store ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The stored blob ("" when nothing was ever written).</summary>
    public static string Blob() => Platform.Settings.Get(Platform.Keys.HintsState);

    /// <summary>Read the blob again, as a launch does (the inspector's <c>hint state blob=</c> after writing it).</summary>
    public static void Reload()
    {
        s_loaded = false;
        Loaded();
    }

    static T Ensure<T>(T _)
    {
        Loaded();
        return _;
    }

    static Budget[] Loaded()
    {
        if (s_loaded) return s_budget;
        s_loaded = true;
        Parse(Blob());
        return s_budget;
    }

    static void Parse(string blob)
    {
        s_budget = new Budget[Hints.All.Count];
        Array.Clear(s_recent);
        s_recentCount = 0;
        s_bellOpens = s_relaunchDay = s_relaunchCount = s_signInDay = s_signInDays = 0;
        s_lastQuitMs = 0;
        var rest = blob.AsSpan();
        while (!rest.IsEmpty)
        {
            var section = Cut(ref rest, '|');
            if (section.Length < 2 || section[1] != '=') continue;
            var body = section[2..];
            while (!body.IsEmpty)
            {
                var entry = Cut(ref body, ';');
                switch (section[0])
                {
                    case 'h': ParseHint(entry); break;
                    case 'f': ParseFact(entry); break;
                    case 'r': ParseWrite(entry); break;
                }
            }
        }
    }

    static void ParseHint(ReadOnlySpan<char> entry)
    {
        var id = Cut(ref entry, ',');
        int index = Hints.IndexOf(id);
        if (index < 0) return;
        if (!Int(ref entry, out int shows) || !Int(ref entry, out int lastDay) || !Int(ref entry, out int snoozed)
            || !Int(ref entry, out int dismissals) || !Int(ref entry, out int retired)) return;
        s_budget[index] = new Budget { Shows = shows, LastDay = lastDay, SnoozedUntil = snoozed, Dismissals = dismissals, Retired = retired != 0 };
    }

    static void ParseFact(ReadOnlySpan<char> entry)
    {
        var name = Cut(ref entry, ',');
        if (name.SequenceEqual("bell") && Int(ref entry, out int bell)) s_bellOpens = bell;
        else if (name.SequenceEqual("quit") && long.TryParse(Cut(ref entry, ','), NumberStyles.None, CultureInfo.InvariantCulture, out long quit)) s_lastQuitMs = quit;
        else if (name.SequenceEqual("relaunch") && Int(ref entry, out int rd) && Int(ref entry, out int rn)) (s_relaunchDay, s_relaunchCount) = (rd, rn);
        else if (name.SequenceEqual("signin") && Int(ref entry, out int sd) && Int(ref entry, out int sn)) (s_signInDay, s_signInDays) = (sd, sn);
    }

    static void ParseWrite(ReadOnlySpan<char> entry)
    {
        if (s_recentCount >= s_recent.Length) return;
        var dial = Cut(ref entry, ',');
        var surface = Cut(ref entry, ',');
        if (dial.IsEmpty || surface.IsEmpty || !long.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out long at)) return;
        string id = dial.ToString();
        for (int i = 0; i < s_recentCount; i++)
            if (Same(s_recent[i].DialId, id)) return;   // one entry per dial; the first (newest) wins
        s_recent[s_recentCount++] = new RecentWrite(id, surface.ToString(), at);
    }

    static ReadOnlySpan<char> Cut(ref ReadOnlySpan<char> rest, char separator)
    {
        int cut = rest.IndexOf(separator);
        var head = cut < 0 ? rest : rest[..cut];
        rest = cut < 0 ? default : rest[(cut + 1)..];
        return head;
    }

    static bool Int(ref ReadOnlySpan<char> rest, out int value)
        => int.TryParse(Cut(ref rest, ','), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>Write the blob if it changed.</summary>
    static void Save()
    {
        string next = Format();
        if (string.Equals(next, Blob(), StringComparison.Ordinal)) return;
        Platform.Settings.Set(Platform.Keys.HintsState, next);
    }

    static string Format()
    {
        var sb = new StringBuilder(256);
        sb.Append("v1|h=");
        bool first = true;
        for (int i = 0; i < s_budget.Length; i++)
        {
            Budget b = s_budget[i];
            if (b.Shows == 0 && b.Dismissals == 0 && !b.Retired && b.SnoozedUntil == 0) continue;
            if (!first) sb.Append(';');
            first = false;
            sb.Append(Hints.All[i].Id).Append(',').Append(b.Shows).Append(',').Append(b.LastDay).Append(',').Append(b.SnoozedUntil)
              .Append(',').Append(b.Dismissals).Append(',').Append(b.Retired ? 1 : 0);
        }
        sb.Append("|f=bell,").Append(s_bellOpens).Append(";quit,").Append(s_lastQuitMs)
          .Append(";relaunch,").Append(s_relaunchDay).Append(',').Append(s_relaunchCount)
          .Append(";signin,").Append(s_signInDay).Append(',').Append(s_signInDays);
        sb.Append("|r=");
        for (int i = 0; i < s_recentCount; i++)
        {
            if (i > 0) sb.Append(';');
            sb.Append(s_recent[i].DialId).Append(',').Append(s_recent[i].Surface).Append(',').Append(s_recent[i].AtMs);
        }
        return sb.ToString();
    }
}
