// ── Entities/Recents.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the recents relation (the two edges, the revision, the staging rows, the commit, the handle) + the whole RecentsView
// rule set (0.2.9 `Features/Recents/RecentsView.cs`, ported verbatim) + the RecentsPage rules ch 16 §8 marks "pure but
// untested" (`RecentsLayout`)
//
// Role: CORE
// Owner: P
// Wave: 5
// Budget: 650 lines
// Spec: ch 16 §7.2 (the edge) · ch 16 §8 (the rules, verbatim) · ch 16 §9.1 (what must not be simplified)
//
// RECENTS IS AN EDGE OVER THE ACCOUNT (G6, D10): `Users[me] → Recents` (EdgeTable<RecentsEdge>, wire order) and
// `→ RecentsMembers` (the plays a GROUP collapsed — kept, because a header's `child_uri` list is server-truncated).
// Each payload carries its TARGET'S KIND (Wave 5): a slot is ambiguous in a six-table list, and Liked Songs has no row.
// THE RULES READ A SNAPSHOT: `RecentsSnapshot` copies the edge once per publication; every rule is a pure function of it
// plus the caller's clock and culture. A recents row is a POINTER — nothing here invents a string. `RecentsList.Group`
// (the wire fold) stays with the decoder. `CollectRange`/`CollectChildUris` are RETIRED per ch 16 §8 ("port, then
// retire — 0.3 demands the whole model"): `RecentsLayout.DemandSlots` is the whole-model demand that replaces them.

using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

// ── 1. the three bytes a recents edge carries ────────────────────────────────────────────────────────────────────────
/// <summary>Why an entry is in Recents — the semantic of the <c>recent_type_*</c> format-attribute KEY.</summary>
public enum RecentsReason : byte { Unknown = 0, Played = 1, Saved = 2 }

/// <summary>The suffix of a <c>content_type_*</c> key: the pivots' axis. An unrecognised token stays <see cref="None"/>.</summary>
public enum RecentsContentType : byte { None = 0, Music = 1, Podcasts = 2 }

/// <summary>A grouped recents ROW is either a single play or a collapsed group header.</summary>
public enum RecentsRowKind : byte { Single = 0, Group = 1 }

// ── 2. the payloads ──────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>One recents row's membership fact (ch 16 §7.2). <paramref name="ItemId"/> is the hex <c>item_id</c> — uris
/// repeat ~1,388× in a real list, so the accordion's identity check needs it. <paramref name="ChildCount"/> is the
/// header's DECLARED count, never the member run's length. <paramref name="TargetKind"/> is the target's
/// <see cref="EntityKind"/> (the slot's table; Collection for the row-less Liked Songs).</summary>
public readonly record struct RecentsEdge(
    StringId ItemId, long PlayedAtMs, int ChildCount,
    byte Reason, byte ContentType, byte Kind,
    int MembersStart, int MembersLen, byte TargetKind = 0)
{
    public RecentsReason Why => (RecentsReason)Reason;
    public RecentsContentType Axis => (RecentsContentType)ContentType;
    public RecentsRowKind Shape => (RecentsRowKind)Kind;
    public EntityKind Entity => (EntityKind)TargetKind;
    /// <summary>Is there a drawer to open? A group with children, and at least one member.</summary>
    public bool CanExpand => Shape == RecentsRowKind.Group && ChildCount > 0 && MembersLen > 0;
}

/// <summary>One play a group header collapsed, with its OWN instant (a header states one timestamp for an evening).</summary>
public readonly record struct RecentsMemberEdge(StringId ItemId, long PlayedAtMs, byte TargetKind = 0)
{
    public EntityKind Entity => (EntityKind)TargetKind;
}

// ── 3. the relations ─────────────────────────────────────────────────────────────────────────────────────────────────
public sealed partial class Edges
{
    /// <summary>The grouped recents snapshot: parent = <c>Scope.MeSlot</c>, targets = entity slots in wire order.</summary>
    public readonly EdgeTable<RecentsEdge> Recents = new();
    /// <summary>Every collapsed member of every group, under the SAME parent, in <see cref="RecentsEdge.MembersStart"/> order.</summary>
    public readonly EdgeTable<RecentsMemberEdge> RecentsMembers = new();

    // The snapshot revision per parent (lowercase hex, what `/recents/page/diff` is asked with).
    Column<StringId> _recentsRevision;
    int _recentsRevisionCount;
    /// <summary>The revision the parent's recents list was answered at, or <see cref="StringId.Empty"/>.</summary>
    public StringId RecentsRevision(int parent)
        => (uint)parent >= (uint)_recentsRevisionCount ? StringId.Empty : _recentsRevision[parent];
    /// <summary>Record the revision an answer carried. Ref-counted like every owned string (defect 1).</summary>
    public void SetRecentsRevision(int parent, StringId revision)
    {
        if (parent < 0) return;
        if (parent >= _recentsRevisionCount)
        {
            _recentsRevision.EnsureCapacity(parent + 1);
            _recentsRevision.Clear(_recentsRevisionCount, parent + 1 - _recentsRevisionCount);
            _recentsRevisionCount = parent + 1;
        }
        Entities.RetainText(ref _recentsRevision[parent], revision);
    }
}

// ── 4. staging: what a recents answer hands the drain ────────────────────────────────────────────────────────────────
/// <summary>One answered recents PAGE: its parent, its revision, and the slices of rows and members it produced.</summary>
public struct StagedRecentsPage { public StagedId Parent; public TextRef Revision; public int RowStart, RowCount, MemberStart, MemberCount; }

/// <summary>One grouped, display-ready recents row, staged. The identity may be EMPTY for a single-context header.</summary>
public struct StagedRecents
{
    public StagedId Id;
    public TextRef ItemId;
    public long PlayedAtMs;
    /// <summary><see cref="MembersStart"/> indexes this page's member slice (the parent's member run after the commit).</summary>
    public int ChildCount, MembersStart, MembersLen;
    public byte Reason, ContentType, Kind;
}

/// <inheritdoc cref="RecentsMemberEdge"/>
public struct StagedRecentsMember { public StagedId Id; public TextRef ItemId; public long PlayedAtMs; }

public sealed partial class Staging
{
    StagedList<StagedRecentsPage>? _recentsPages;
    StagedList<StagedRecents>? _recentsRows;
    StagedList<StagedRecentsMember>? _recentsMembers;
    /// <inheritdoc cref="StagedRecentsPage"/>
    public StagedList<StagedRecentsPage> RecentsPages => _recentsPages ??= Register(new StagedList<StagedRecentsPage>());
    /// <inheritdoc cref="StagedRecents"/>
    public StagedList<StagedRecents> RecentsRows => _recentsRows ??= Register(new StagedList<StagedRecents>());
    /// <inheritdoc cref="StagedRecentsMember"/>
    public StagedList<StagedRecentsMember> RecentsMembers => _recentsMembers ??= Register(new StagedList<StagedRecentsMember>());

    internal StagedList<StagedRecentsPage>? StagedRecentsPages => _recentsPages;
    internal StagedList<StagedRecents>? StagedRecentsRows => _recentsRows;
    internal StagedList<StagedRecentsMember>? StagedRecentsMembers => _recentsMembers;
}

// ── 5. the commit ────────────────────────────────────────────────────────────────────────────────────────────────────
public static partial class Entities
{
    // UI thread only (C1); grown to the widest snapshot seen and never again (P8).
    static int[] s_recentsTargets = new int[64], s_recentsMemberTargets = new int[64];
    static RecentsEdge[] s_recentsPayload = new RecentsEdge[64];
    static RecentsMemberEdge[] s_recentsMemberPayload = new RecentsMemberEdge[64];
    /// <summary>Land a recents snapshot: the member run, then the rows that index into it, then the revision. One
    /// <c>Replace</c> each — a recents answer IS the whole list — and the relation settles Complete, empty included.</summary>
    static partial void CommitRecents(Staging s)
    {
        var pages = s.StagedRecentsPages;
        if (pages is null || pages.Count == 0) return;

        var rows = s.StagedRecentsRows is { } r ? r.Span : default;
        var members = s.StagedRecentsMembers is { } m ? m.Span : default;
        var edges = Current.Edges;

        foreach (ref readonly var page in pages.Span)
        {
            int parent = s.Slot(Current.Users, in page.Parent);
            if (parent == Table.None) continue;
            if (page.RowStart < 0 || page.RowCount < 0 || page.RowStart + page.RowCount > rows.Length) continue;
            if (page.MemberStart < 0 || page.MemberCount < 0 || page.MemberStart + page.MemberCount > members.Length) continue;

            GrowRecents(page.RowCount, page.MemberCount);

            var memberPage = members.Slice(page.MemberStart, page.MemberCount);
            for (int i = 0; i < memberPage.Length; i++)
            {
                ref readonly var member = ref memberPage[i];
                s_recentsMemberTargets[i] = s.Slot(in member.Id);
                s_recentsMemberPayload[i] = new RecentsMemberEdge(s.Intern(member.ItemId), member.PlayedAtMs,
                    member.Id.IsEmpty ? (byte)0 : (byte)member.Id.Kind(s));
            }
            edges.RecentsMembers.Replace(parent, s_recentsMemberTargets.AsSpan(0, memberPage.Length),
                                         s_recentsMemberPayload.AsSpan(0, memberPage.Length),
                                         EdgeState.Complete, memberPage.Length);

            var rowPage = rows.Slice(page.RowStart, page.RowCount);
            for (int i = 0; i < rowPage.Length; i++)
            {
                ref readonly var row = ref rowPage[i];
                s_recentsTargets[i] = s.Slot(in row.Id);
                s_recentsPayload[i] = new RecentsEdge(
                    s.Intern(row.ItemId), row.PlayedAtMs, row.ChildCount,
                    row.Reason, row.ContentType, row.Kind,
                    row.MembersLen > 0 ? row.MembersStart : 0, row.MembersLen,
                    row.Id.IsEmpty ? (byte)0 : (byte)row.Id.Kind(s));
            }
            edges.Recents.Replace(parent, s_recentsTargets.AsSpan(0, rowPage.Length),
                                  s_recentsPayload.AsSpan(0, rowPage.Length),
                                  EdgeState.Complete, rowPage.Length);

            edges.SetRecentsRevision(parent, s.Intern(page.Revision));
        }
    }

    static void GrowRecents(int rows, int members)
    {
        if (rows > s_recentsTargets.Length)
        {
            int size = s_recentsTargets.Length;
            while (size < rows) size *= 2;
            s_recentsTargets = new int[size];
            s_recentsPayload = new RecentsEdge[size];
        }
        if (members > s_recentsMemberTargets.Length)
        {
            int size = s_recentsMemberTargets.Length;
            while (size < members) size *= 2;
            s_recentsMemberTargets = new int[size];
            s_recentsMemberPayload = new RecentsMemberEdge[size];
        }
    }
}

// ── 6. the handle ────────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>The account's recents snapshot, as a handle over the parent slot.</summary>
public readonly partial struct Recents(int slot)
{
    static Edges E => Entities.Current.Edges;
    /// <summary>The account row the snapshot hangs off.</summary>
    public int Slot { get; } = slot;
    /// <summary>The signed-in account's snapshot (G6: the parent is the user row).</summary>
    public static Recents Me => new(Entities.Current.MeSlot);
    /// <summary>The rows, newest first, in wire order. Never held across a UI drain (Edges.cs's span rule).</summary>
    public ReadOnlySpan<int> Slots => E.Recents.Targets(Slot);
    /// <inheritdoc cref="RecentsEdge"/>
    public ReadOnlySpan<RecentsEdge> Rows => E.Recents.Payload(Slot);
    /// <summary>Unknown until somebody answers — skeleton for Unknown, "no recent plays" only for Complete.</summary>
    public EdgeState State => E.Recents.State(Slot);
    /// <summary><see cref="State"/>, with a failed ask surfaced as <see cref="EdgeState.Failed"/> (the error arm).</summary>
    public EdgeState Readiness => E.Recents.Readiness(Slot);
    public uint Version => E.Recents.Version(Slot);
    public int Count => E.Recents.Count(Slot);
    /// <summary>The relation's publication signal (subscribe from a render; read <c>Entities.ScopeEpoch</c> first).</summary>
    public static FluentGpu.Signals.Signal<uint> Changed => E.Recents.Changed;
    /// <summary>The plays one group row collapsed (its <see cref="RecentsEdge.MembersStart"/> range).</summary>
    public ReadOnlySpan<int> MemberSlots(in RecentsEdge row)
        => Slice(E.RecentsMembers.Targets(Slot), row.MembersStart, row.MembersLen);
    /// <inheritdoc cref="MemberSlots"/>
    public ReadOnlySpan<RecentsMemberEdge> Members(in RecentsEdge row)
        => Slice(E.RecentsMembers.Payload(Slot), row.MembersStart, row.MembersLen);
    /// <summary>The revision this snapshot was answered at — what the diff endpoint is asked with.</summary>
    public StringId Revision => E.RecentsRevision(Slot);

    static ReadOnlySpan<T> Slice<T>(ReadOnlySpan<T> all, int start, int length)
        => (uint)start > (uint)all.Length || length < 0 || start + length > all.Length
            ? default : all.Slice(start, length);
}

// ── 7. the snapshot the rules read ───────────────────────────────────────────────────────────────────────────────────
/// <summary>ONE publication of the recents relation, copied (ch 16 §9.1 #1: the page swaps one immutable reference).
/// <see cref="Targets"/> is parallel to <see cref="Rows"/> and <see cref="MemberTargets"/> to <see cref="Members"/>; a
/// row's members are <c>Members[MembersStart .. +MembersLen]</c>. The Liked collection is <c>(Collection, 0)</c>.</summary>
public sealed class RecentsSnapshot
{
    public static readonly RecentsSnapshot Empty = new([], [], [], []);
    public readonly RecentsEdge[] Rows;
    public readonly EntityRef[] Targets;
    public readonly RecentsMemberEdge[] Members;
    public readonly EntityRef[] MemberTargets;
    public RecentsSnapshot(RecentsEdge[] rows, EntityRef[] targets, RecentsMemberEdge[] members, EntityRef[] memberTargets)
    {
        if (targets.Length != rows.Length || memberTargets.Length != members.Length)
            throw new ArgumentException("targets must be parallel to their payloads");
        Rows = rows; Targets = targets; Members = members; MemberTargets = memberTargets;
    }
    public int Count => Rows.Length;
    /// <summary>Copy the live relation (UI thread). Allocates per publication, never per frame.</summary>
    public static RecentsSnapshot Of(in Recents r)
    {
        var payload = r.Rows;
        if (payload.Length == 0) return Empty;
        var slots = r.Slots;
        var rows = payload.ToArray();
        var targets = new EntityRef[rows.Length];
        for (int i = 0; i < rows.Length; i++) targets[i] = new EntityRef(rows[i].Entity, i < slots.Length ? slots[i] : 0);
        var e = Entities.Current.Edges;
        var members = e.RecentsMembers.Payload(r.Slot).ToArray();
        var memberSlots = e.RecentsMembers.Targets(r.Slot);
        var memberTargets = new EntityRef[members.Length];
        for (int i = 0; i < members.Length; i++)
            memberTargets[i] = new EntityRef(members[i].Entity, i < memberSlots.Length ? memberSlots[i] : 0);
        return new RecentsSnapshot(rows, targets, members, memberTargets);
    }
    /// <summary>Value equality over all four arrays — a publication that changed nothing must not rebuild the page.</summary>
    public bool SameAs(RecentsSnapshot other)
        => ReferenceEquals(this, other)
           || (Rows.AsSpan().SequenceEqual(other.Rows) && Targets.AsSpan().SequenceEqual(other.Targets)
               && Members.AsSpan().SequenceEqual(other.Members) && MemberTargets.AsSpan().SequenceEqual(other.MemberTargets));
    /// <summary>A row's members, clamped.</summary>
    public ReadOnlySpan<RecentsMemberEdge> MembersOf(int row) => Range(Members, row);
    /// <summary>A row's member targets, clamped.</summary>
    public ReadOnlySpan<EntityRef> MemberTargetsOf(int row) => Range(MemberTargets, row);

    ReadOnlySpan<T> Range<T>(T[] all, int row)
    {
        if ((uint)row >= (uint)Rows.Length) return default;
        int start = Rows[row].MembersStart, length = Rows[row].MembersLen;
        return (uint)start > (uint)all.Length || length <= 0 || start + length > all.Length
            ? default : all.AsSpan(start, length);
    }
}

// ── 8. the RecentsView rule set (0.2.9 RecentsView.cs, verbatim; only the input types changed) ───────────────────────
/// <summary>The recycle-pool kinds in the grouped projection — CONTENT ONLY, no trailing dock-reserve item: the shell
/// clips the content region above the player bar, so a reserve only parked a dead band the rail advertised.</summary>
public enum RecentsFlatItemKind : byte { DateHeader, Row }

/// <summary>One entry in the grouped list. A header has no source row (<see cref="OriginalRowIndex"/> −1).</summary>
public readonly record struct RecentsFlatItem(RecentsFlatItemKind Kind, int OriginalRowIndex, int DayIndex, int MonthIndex);

/// <summary>A filtered row vector with one synthetic header before each calendar day; every map indexed by the FLAT list.</summary>
public sealed record RecentsSections(
    RecentsFlatItem[] Items, int[] HeaderIndices, string[] HeaderLabels, DateOnly[] HeaderDates,
    int[] FlatToRow, int[] FlatToDay, int[] FlatToMonth, int[] RowToFlat, int[] RowToDay);
/// <summary>Which localized metadata branch a row renders.</summary>
public enum RecentsMetaKind : byte { PlayedAt, PlayedCount, SavedCount }

/// <summary>Pure reason/count decision; formatting stays in the rendered half.</summary>
public readonly record struct RecentsMeta(RecentsMetaKind Kind, int Count);

/// <summary>The row contributing the most plays to one day (identity kept, no title copied).</summary>
public readonly record struct RecentsDayTopItem(int OriginalRowIndex, StringId ItemId, int PlayCount);

/// <summary>One calendar day. <see cref="DensityLevel"/> is 0 for no plays and 1..5 for the log ramp.</summary>
public sealed record RecentsCalendarDay(DateOnly Date, int PlayCount, int DensityLevel, RecentsDayTopItem? TopItem);

/// <summary>One newest-first month, including empty days so the grid is a stable 7 columns.</summary>
public sealed record RecentsCalendarMonth(int Year, int Month, int FirstDayOffset, int TotalPlays, DateOnly? BusiestDay,
    int BusiestDayPlays, bool IsCurrentMonth, RecentsCalendarDay[] Days)
{
    /// <summary>The WEEK ROWS this month occupies in the culture-rotated grid: 4 through 6, never a fixed 6.</summary>
    public int WeekCount => (FirstDayOffset + Days.Length + 6) / 7;
}

/// <summary>The calendar overview derived solely from the filtered rows.</summary>
public sealed record RecentsCalendar(RecentsCalendarMonth[] Months, int MaximumDayPlays);

/// <summary>The Recents page's PURE half — every decision that is not a rendered element (ch 16 §8).</summary>
public static class RecentsView
{
    /// <summary><c>kind:artist</c> is the one pivot with no wire <c>content_type_*</c> — decided from the hydration target.</summary>
    public const string PivotMusic = "music", PivotPodcasts = "podcasts", PivotArtists = "kind:artist";
    /// <summary>The stored content-type token of an axis (the mapper strips <c>content_type_</c>).</summary>
    public static string? TokenOf(RecentsContentType axis)
        => axis == RecentsContentType.Music ? PivotMusic : axis == RecentsContentType.Podcasts ? PivotPodcasts : null;

    // ── content-type chips ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The distinct content-type tokens present, in FIRST-SEEN (wire) order; untyped rows contribute nothing.</summary>
    public static IReadOnlyList<string> ContentTypes(RecentsSnapshot rows)
    {
        if (rows.Count == 0) return Array.Empty<string>();
        var seen = new List<string>(2);
        for (int i = 0; i < rows.Count; i++)
            if (TokenOf(rows.Rows[i].Axis) is { } t && !seen.Contains(t)) seen.Add(t);
        return seen;
    }
    /// <summary>The chip predicate: null is "All"; <c>kind:artist</c> reads the hydration target; else the token.</summary>
    public static bool Matches(RecentsSnapshot rows, int row, string? token)
        => token is null
            || ((uint)row < (uint)rows.Count
                && (string.Equals(token, PivotArtists, StringComparison.OrdinalIgnoreCase)
                    ? TargetKind(rows, row) == EntityKind.Artist
                    : string.Equals(TokenOf(rows.Rows[row].Axis), token, StringComparison.OrdinalIgnoreCase)));
    /// <summary>Whether the fixed pivot strip enables <paramref name="token"/> — the same predicate as <see cref="Matches"/>.</summary>
    public static bool PivotAvailable(RecentsSnapshot rows, string token)
    {
        for (int i = 0; i < rows.Count; i++)
            if (Matches(rows, i, token)) return true;
        return false;
    }
    /// <summary>The display map (display index → row), wire order. CLIENT-SIDE: a chip never reaches the network.</summary>
    public static int[] Filter(RecentsSnapshot rows, string? token)
    {
        if (token is null)
        {
            var all = new int[rows.Count];
            for (int i = 0; i < all.Length; i++) all[i] = i;
            return all;
        }
        var kept = new List<int>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
            if (Matches(rows, i, token)) kept.Add(i);
        return kept.ToArray();
    }
    /// <summary>One synthetic header before each calendar day in an ALREADY-filtered display map. Does not sort.</summary>
    public static RecentsSections BuildSections(RecentsSnapshot rows, IReadOnlyList<int> display, DateTimeOffset now,
                                                CultureInfo culture, Func<string, string>? localize = null)
    {
        var items = new List<RecentsFlatItem>(display.Count + Math.Min(display.Count, 64));
        var headers = new List<int>(); var labels = new List<string>(); var dates = new List<DateOnly>();
        var flatRows = new List<int>(items.Capacity); var flatDays = new List<int>(items.Capacity);
        var flatMonths = new List<int>(items.Capacity);
        var rowToFlat = new int[rows.Count]; var rowToDay = new int[rows.Count];
        Array.Fill(rowToFlat, -1);
        Array.Fill(rowToDay, -1);
        var months = new Dictionary<int, int>();
        DateOnly prior = default;
        bool havePrior = false;
        int dayIndex = -1, monthIndex = -1;

        for (int i = 0; i < display.Count; i++)
        {
            int rowIndex = display[i];
            if ((uint)rowIndex >= (uint)rows.Count) continue;
            DateOnly date = DateOf(rows.Rows[rowIndex].PlayedAtMs, now.Offset);
            if (!havePrior || date != prior)
            {
                havePrior = true;
                prior = date;
                dayIndex++;
                monthIndex = MonthIndex(date, months);
                headers.Add(items.Count);
                labels.Add(date == DateOnly.MinValue ? "" : DayBucketLabel(date.ToDateTime(TimeOnly.MinValue), now, culture, localize));
                dates.Add(date);
                items.Add(new RecentsFlatItem(RecentsFlatItemKind.DateHeader, -1, dayIndex, monthIndex));
                flatRows.Add(-1);
                flatDays.Add(dayIndex);
                flatMonths.Add(monthIndex);
            }
            rowToFlat[rowIndex] = items.Count;
            rowToDay[rowIndex] = dayIndex;
            items.Add(new RecentsFlatItem(RecentsFlatItemKind.Row, rowIndex, dayIndex, monthIndex));
            flatRows.Add(rowIndex);
            flatDays.Add(dayIndex);
            flatMonths.Add(monthIndex);
        }

        return new RecentsSections(items.ToArray(), headers.ToArray(), labels.ToArray(), dates.ToArray(),
            flatRows.ToArray(), flatDays.ToArray(), flatMonths.ToArray(), rowToFlat, rowToDay);
    }
    /// <summary>Midnight rollover: rebuild ONLY the labels from the settled dates; every map stays the same instance.</summary>
    public static RecentsSections Relabel(RecentsSections sections, DateTimeOffset now, CultureInfo culture,
                                          Func<string, string>? localize = null)
    {
        var dates = sections.HeaderDates;
        var labels = new string[dates.Length];
        for (int i = 0; i < dates.Length; i++)
            labels[i] = dates[i] == DateOnly.MinValue ? "" : DayBucketLabel(dates[i].ToDateTime(TimeOnly.MinValue), now, culture, localize);
        return sections with { HeaderLabels = labels };
    }
    /// <summary>How many ROWS a day bucket holds (its header to the next, minus itself). Out of range answers 0.</summary>
    public static int CountForDay(RecentsSections sections, int dayIndex)
    {
        var headers = sections.HeaderIndices;
        if ((uint)dayIndex >= (uint)headers.Length) return 0;
        int start = headers[dayIndex];
        int end = dayIndex + 1 < headers.Length ? headers[dayIndex + 1] : sections.Items.Length;
        return Math.Max(0, end - start - 1);
    }
    /// <summary>Today, Yesterday, the weekday for days 2..6, then the abbreviated month/day — compared in now's offset.</summary>
    public static string DayBucketLabel(DateTimeOffset at, DateTimeOffset now, CultureInfo culture,
                                        Func<string, string>? localize = null)
    {
        DateTimeOffset localNow = now.ToOffset(now.Offset);
        DateTimeOffset localAt = at.ToOffset(now.Offset);
        int days = DateOnly.FromDateTime(localNow.DateTime).DayNumber - DateOnly.FromDateTime(localAt.DateTime).DayNumber;
        Func<string, string> resolve = localize ?? Loc.Get;
        if (days == 0) return resolve(Strings.Detail.Today);
        if (days == 1) return resolve(Strings.Detail.Yesterday);
        if ((uint)(days - 2) <= 4u) return culture.DateTimeFormat.GetDayName(localAt.DayOfWeek);
        return localAt.ToString(ShortMonthDay(culture), culture);
    }
    /// <summary>Unix-ms convenience; 0/negative (the wire's "unknown") yields "".</summary>
    public static string DayBucketLabel(long atMs, DateTimeOffset now, CultureInfo culture, Func<string, string>? localize = null)
        => atMs <= 0 ? "" : DayBucketLabel(DateTimeOffset.FromUnixTimeMilliseconds(atMs), now, culture, localize);
    /// <summary>The 8 skeleton rows, stamped minutes apart off the CALLER's clock (never UtcNow: one Today, not two).</summary>
    public static RecentsSnapshot PendingSeedRows(DateTimeOffset now)
    {
        var rows = new RecentsEdge[8];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new RecentsEdge(StringId.Empty, now.AddMinutes(-i).ToUnixTimeMilliseconds(), 1,
                (byte)RecentsReason.Played, 0, (byte)RecentsRowKind.Group, 0, 0);
        return new RecentsSnapshot(rows, new EntityRef[8], [], []);
    }

    // ── hydration targets ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Does this ref name something? The Liked collection has no row but IS a destination.</summary>
    public static bool Names(EntityRef r) => r.Kind == EntityKind.Collection || !r.IsNone;
    /// <summary>0.2.9 <c>HydrationUri</c>, renamed (ch 16 §8): the row's own target (the context the card stands for),
    /// else its first named member (a single-context header rendered from its children), else nothing.</summary>
    public static EntityRef EntitySlotOf(RecentsSnapshot rows, int row)
    {
        if ((uint)row >= (uint)rows.Count) return default;
        if (Names(rows.Targets[row])) return rows.Targets[row];
        var kids = rows.MemberTargetsOf(row);
        for (int i = 0; i < kids.Length; i++)
            if (Names(kids[i])) return kids[i];
        return default;
    }
    /// <summary>The entity kind the hydration target points at.</summary>
    public static EntityKind TargetKind(RecentsSnapshot rows, int row) => EntitySlotOf(rows, row).Kind;

    // ── the expand drawer ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A chevron: at least one play AND a named member to list. Nothing is invented for a count without members.</summary>
    public static bool CanExpand(RecentsSnapshot rows, int row)
    {
        if ((uint)row >= (uint)rows.Count || rows.Rows[row].ChildCount <= 0) return false;
        var kids = rows.MemberTargetsOf(row);
        for (int i = 0; i < kids.Length; i++)
            if (Names(kids[i])) return true;
        return false;
    }
    /// <summary>A play count with nothing listable — what the page reports once per session.</summary>
    public static bool MissingMembers(RecentsSnapshot rows, int row)
        => (uint)row < (uint)rows.Count && rows.Rows[row].ChildCount > 0 && !CanExpand(rows, row);
    /// <summary>What the drawer lists, in wire order, as sent — an unnamed entry stays in place (keys vs ordinals).</summary>
    public static ReadOnlySpan<RecentsMemberEdge> DrawerEntries(RecentsSnapshot rows, int row) => rows.MembersOf(row);
    /// <summary>The targets parallel to <see cref="DrawerEntries"/>.</summary>
    public static ReadOnlySpan<EntityRef> DrawerTargets(RecentsSnapshot rows, int row) => rows.MemberTargetsOf(row);
    /// <summary>The metadata sentence: Saved states at least one; a played group its authoritative count; else a time.</summary>
    public static RecentsMeta MetaFor(in RecentsEdge row)
        => row.Why switch
        {
            RecentsReason.Saved => new RecentsMeta(RecentsMetaKind.SavedCount, Math.Max(1, row.ChildCount)),
            RecentsReason.Played when row.Shape == RecentsRowKind.Group
                => new RecentsMeta(RecentsMetaKind.PlayedCount, Math.Max(0, row.ChildCount)),
            _ => new RecentsMeta(RecentsMetaKind.PlayedAt, 0),
        };

    // ── the calendar ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The newest-first calendar for the filtered map. Only PLAYED rows add heat (a group its count, ≥ 1; a
    /// single 1); Saved/Unknown still extend the range. Ascending walk + <c>&gt;=</c>: a tied busiest day is the newest.</summary>
    public static RecentsCalendar DayDensity(RecentsSnapshot rows, IReadOnlyList<int> display, DateTimeOffset now,
                                             CultureInfo culture)
    {
        var byDay = new Dictionary<DateOnly, DayAccumulator>();
        DateOnly today = DateOnly.FromDateTime(now.ToOffset(now.Offset).DateTime);
        DateOnly oldest = today;
        bool hasDatedRow = false;

        for (int i = 0; i < display.Count; i++)
        {
            int rowIndex = display[i];
            if ((uint)rowIndex >= (uint)rows.Count) continue;
            var row = rows.Rows[rowIndex];
            DateOnly date = DateOf(row.PlayedAtMs, now.Offset);
            if (date == DateOnly.MinValue) continue;
            if (!hasDatedRow || date < oldest) oldest = date;
            hasDatedRow = true;
            if (!byDay.TryGetValue(date, out var day)) byDay.Add(date, day = new DayAccumulator());

            int contribution = PlayContribution(in row);
            if (contribution <= 0) continue;
            day.Plays += contribution;
            if (day.Top is null || contribution > day.Top.Value.PlayCount)
                day.Top = new RecentsDayTopItem(rowIndex, row.ItemId, contribution);
        }

        DateOnly firstMonth = new(today.Year, today.Month, 1);
        DateOnly lastMonth = hasDatedRow && oldest < firstMonth ? new DateOnly(oldest.Year, oldest.Month, 1) : firstMonth;
        int maximum = 0;
        foreach (var day in byDay.Values) maximum = Math.Max(maximum, day.Plays);

        var months = new List<RecentsCalendarMonth>();
        for (DateOnly month = firstMonth; month >= lastMonth; month = month.AddMonths(-1))
        {
            int count = DateTime.DaysInMonth(month.Year, month.Month);
            var days = new RecentsCalendarDay[count];
            int total = 0, busiestCount = 0;
            DateOnly? busiest = null;
            for (int d = 1; d <= count; d++)
            {
                var date = new DateOnly(month.Year, month.Month, d);
                byDay.TryGetValue(date, out var value);
                int plays = value?.Plays ?? 0;
                total += plays;
                if (plays > 0 && plays >= busiestCount) { busiestCount = plays; busiest = date; }
                days[d - 1] = new RecentsCalendarDay(date, plays, DensityLevel(plays, maximum), value?.Top);
            }
            int offset = ((int)new DateTime(month.Year, month.Month, 1).DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
            months.Add(new RecentsCalendarMonth(month.Year, month.Month, offset, total, busiest, busiestCount,
                month.Year == today.Year && month.Month == today.Month, days));
        }
        return new RecentsCalendar(months.ToArray(), maximum);
    }
    /// <summary>The tallest month in week rows — the grid estimate's seed — floored at 1 for an empty calendar.</summary>
    public static int MaxWeeks(RecentsCalendar calendar)
    {
        int weeks = 1;
        var months = calendar.Months;
        for (int i = 0; i < months.Length; i++) weeks = Math.Max(weeks, months[i].WeekCount);
        return weeks;
    }

    static int PlayContribution(in RecentsEdge row)
        => row.Why != RecentsReason.Played ? 0 : row.Shape == RecentsRowKind.Group ? Math.Max(1, row.ChildCount) : 1;
    /// <summary><c>clamp(ceil(ln(1+count) / ln(1+max) × 5), 1, 5)</c>; 0 for no plays.</summary>
    public static int DensityLevel(int count, int maximum)
    {
        if (count <= 0 || maximum <= 0) return 0;
        double level = Math.Ceiling(Math.Log(1d + count) / Math.Log(1d + maximum) * 5d);
        return Math.Clamp((int)level, 1, 5);
    }

    sealed class DayAccumulator
    {
        public int Plays;
        public RecentsDayTopItem? Top;
    }
    /// <summary>Which rows may claim the morph tag: the FIRST occurrence of each target only (one tagged node per key).</summary>
    public static bool[] FirstOccurrence(RecentsSnapshot rows)
    {
        var flags = new bool[rows.Count];
        var seen = new HashSet<EntityRef>();
        for (int i = 0; i < rows.Count; i++)
        {
            var target = EntitySlotOf(rows, i);
            if (Names(target)) flags[i] = seen.Add(target);
        }
        return flags;
    }

    // ── formatting (culture tables only — no authored copy) ───────────────────────────────────────────────────────────

    /// <summary>Today → the time, the last week → the abbreviated weekday, this year → month-day, older → short date.</summary>
    public static string PlayedAt(DateTimeOffset at, DateTimeOffset now, CultureInfo culture)
    {
        if (at.Year <= 1) return "";
        if (at.Date == now.Date) return at.ToString("t", culture);
        var age = now - at;
        if (age >= TimeSpan.Zero && age < TimeSpan.FromDays(7)) return culture.DateTimeFormat.GetAbbreviatedDayName(at.DayOfWeek);
        if (at.Year == now.Year) return at.ToString(ShortMonthDay(culture), culture);
        return at.ToString("d", culture);
    }
    /// <summary>Unix-ms convenience. 0/negative yields "".</summary>
    public static string PlayedAt(long playedAtMs, DateTimeOffset now, CultureInfo culture)
        => playedAtMs <= 0 ? "" : PlayedAt(DateTimeOffset.FromUnixTimeMilliseconds(playedAtMs).ToOffset(now.Offset), now, culture);
    /// <summary>The culture's month-day pattern with the FULL month narrowed to the abbreviated one.</summary>
    public static string ShortMonthDay(CultureInfo culture)
    {
        string pattern = culture.DateTimeFormat.MonthDayPattern;
        return pattern.Contains("MMMM", StringComparison.Ordinal) ? pattern.Replace("MMMM", "MMM", StringComparison.Ordinal) : pattern;
    }
    /// <summary>Calendar day of a unix-ms stamp in <paramref name="offset"/>; 0/negative is MinValue, never the epoch.</summary>
    public static DateOnly DateOf(long unixMs, TimeSpan offset)
        => unixMs <= 0 ? DateOnly.MinValue
            : DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(offset).DateTime);

    static int MonthIndex(DateOnly date, Dictionary<int, int> months)
    {
        if (date == DateOnly.MinValue) return -1;
        int key = date.Year * 12 + date.Month - 1;
        if (months.TryGetValue(key, out int existing)) return existing;
        int index = months.Count;
        months.Add(key, index);
        return index;
    }
    /// <summary>The masthead's thin line: the count, the day-word window, and "grouped from N plays" only when grouping
    /// hid plays. "" for an empty list (render nothing, never "0").</summary>
    public static string Summary(RecentsSnapshot rows, DateTimeOffset now, CultureInfo culture,
                                 Func<int, string>? countPhrase = null, Func<int, string>? groupedPhrase = null,
                                 Func<string, string>? localize = null)
    {
        if (rows.Count == 0) return "";
        long oldest = long.MaxValue, newest = long.MinValue;
        int totalPlays = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            long t = rows.Rows[i].PlayedAtMs;
            if (t > 0)
            {
                if (t < oldest) oldest = t;
                if (t > newest) newest = t;
            }
            totalPlays += Math.Max(1, rows.Rows[i].ChildCount);
        }
        string result = countPhrase is null ? rows.Count.ToString("N0", culture) : countPhrase(rows.Count);
        if (newest != long.MinValue)
        {
            string from = DayBucketLabel(oldest, now, culture, localize), to = DayBucketLabel(newest, now, culture, localize);
            if (from.Length > 0 && to.Length > 0)
                result += string.Equals(from, to, StringComparison.Ordinal) ? " · " + to : " · " + from + " – " + to;
        }
        if (groupedPhrase is not null && totalPlays > rows.Count) result += " · " + groupedPhrase(totalPlays);
        return result;
    }
    /// <summary>A chip's label: the wire token IS the label when the app has no key.</summary>
    public static string ChipLabel(string token, CultureInfo culture)
        => token.Length == 0 ? token : char.ToUpper(token[0], culture) + token[1..];

    // ── owner display names ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>0.2.9 <c>UserProfileIds.Prefix</c>.</summary>
    public const string UserUriPrefix = "spotify:user:";
    /// <summary>0.2.9 <c>UserProfileIds.BareId</c>: a user uri or a bare id → the bare id.</summary>
    public static string BareUserId(string userUriOrId)
        => userUriOrId.StartsWith(UserUriPrefix, StringComparison.Ordinal) ? userUriOrId[UserUriPrefix.Length..] : userUriOrId;
    /// <summary>Never a raw base62 id: a resolved name wins; the store name shows only when it is more than the id
    /// (either spelling); null (never "") means render nothing.</summary>
    public static string? OwnerSubtitle(string? storeOwnerName, string? rawOwnerId, string? resolvedName)
    {
        if (resolvedName is { Length: > 0 }) return resolvedName;
        if (storeOwnerName is not { Length: > 0 }) return null;
        if (rawOwnerId is not { Length: > 0 }) return storeOwnerName;
        return string.Equals(BareUserId(storeOwnerName), BareUserId(rawOwnerId), StringComparison.Ordinal) ? null : storeOwnerName;
    }

    // ── viewport-derived accent ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The row the accent grades from: the first Row item at/after the sticky header INSIDE THE SAME DAY,
    /// bounded to an 8-item walk; −1 otherwise.</summary>
    public static int AccentSourceRow(RecentsSections sections, RecentsSnapshot rows, int stickyFlat)
    {
        var items = sections.Items;
        if ((uint)stickyFlat >= (uint)items.Length) return -1;
        int day = items[stickyFlat].DayIndex;
        if (day < 0) return -1;
        int limit = Math.Min(items.Length, stickyFlat + 8);
        for (int i = stickyFlat; i < limit; i++)
        {
            RecentsFlatItem item = items[i];
            if (item.DayIndex != day) break;
            if (item.Kind != RecentsFlatItemKind.Row) continue;
            if ((uint)item.OriginalRowIndex >= (uint)rows.Count) continue;
            return item.OriginalRowIndex;
        }
        return -1;
    }
}

// ── 9. the RecentsPage rules ch 16 §8 marks "pure but untested" ─────────────────────────────────────────────────────
/// <summary>The page's geometry and anchor decisions, out of the component so they are pinned (ch 16 §8 rows 20-24).</summary>
public static class RecentsLayout
{
    /// <summary>The group card and the single row (64), the day band (48 = the sticky inset), a drawer child (40).</summary>
    public const float RowHeight = 64f, DateHeaderHeight = 48f, ChildRowHeight = 40f;
    /// <summary>The calendar's fixed rung: 38 × 32 cells, a 20 weekday band, 4 gutters, a 28 title line, an 8 card gap.</summary>
    public const float CalCellW = 38f, CalCellH = 32f, CalHeaderH = 20f, CalGap = 4f, CalTitleH = 28f, CalCardGap = 8f;
    /// <summary>A month card's exact width: 7 cells + 6 gutters = 290 (the card width AND the grid's min cell).</summary>
    public const float CalGridW = 7f * CalCellW + 6f * CalGap;
    /// <summary>The sticky push is quantized to <c>Spacing.XXS</c>.</summary>
    public const float PushQuantum = 2f;
    /// <summary><c>56 + 36·weeks</c> — derived from the same consts the card lays out with (200 / 236 / 272).</summary>
    public static float MonthCardHeight(int weeks)
        => CalTitleH + CalCardGap + CalHeaderH + CalGap + weeks * (CalCellH + CalGap) - CalGap;
    /// <summary>The pinned header for an offset and its push: <c>min(0, offsetOf(nextHeader) − y − 48)</c>; −1 / 0 when
    /// the projection has no headers or the offset is above the first.</summary>
    public static void StickyMetrics(RecentsSections sections, FluentGpu.Scene.GroupedListVirtualLayout layout,
                                     float offsetY, float viewportW, out int header, out float push)
    {
        header = -1;
        push = 0f;
        if (sections.HeaderIndices.Length == 0) return;
        int at = layout.StickyHeaderIndexAt(offsetY);
        if (at < 0 || (uint)at >= (uint)sections.Items.Length) return;
        header = at;
        int day = sections.Items[at].DayIndex;
        if ((uint)(day + 1) >= (uint)sections.HeaderIndices.Length) return;
        push = MathF.Min(0f, layout.OffsetOf(sections.HeaderIndices[day + 1], viewportW) - offsetY - DateHeaderHeight);
    }
    /// <summary>The push, snapped to <see cref="PushQuantum"/> (no sub-pixel jitter).</summary>
    public static float QuantizePush(float push) => MathF.Round(push / PushQuantum) * PushQuantum;
    /// <summary>The scroll-geometry gate's packed key: bits 63..24 header+1 · 23..8 the measured version's low 16 · 7..0 the
    /// quantized push + 128. Only compared for equality.</summary>
    public static long ProjectSticky(int header, int measuredVersion, float push)
    {
        long headerPart = (long)(header + 1) << 24;
        long measuredPart = (long)(uint)(measuredVersion & 0xFFFF) << 8;
        long pushPart = (uint)(byte)Math.Clamp((int)MathF.Round(push / PushQuantum) + 128, 0, 255);
        return headerPart | measuredPart | pushPart;
    }

    // ── the semantic-zoom anchor maps ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The flat index of <paramref name="date"/>'s header, −1 when the list has none (no dead "Jump to").</summary>
    public static int HeaderFlatFor(RecentsSections sections, DateOnly date)
    {
        for (int i = 0; i < sections.HeaderDates.Length; i++)
            if (sections.HeaderDates[i] == date) return sections.HeaderIndices[i];
        return -1;
    }
    /// <summary>The calendar month index holding <paramref name="date"/>, −1 when outside the range.</summary>
    public static int MonthFor(RecentsCalendar calendar, DateOnly date)
    {
        for (int i = 0; i < calendar.Months.Length; i++)
            if (calendar.Months[i].Year == date.Year && calendar.Months[i].Month == date.Month) return i;
        return -1;
    }
    /// <summary>List → overview: the month of the flat item's day.</summary>
    public static int MapInToOut(RecentsSections sections, RecentsCalendar calendar, int flatIndex)
    {
        if ((uint)flatIndex >= (uint)sections.Items.Length) return -1;
        int day = sections.Items[flatIndex].DayIndex;
        return (uint)day < (uint)sections.HeaderDates.Length ? MonthFor(calendar, sections.HeaderDates[day]) : -1;
    }
    /// <summary>Overview → list: the EXACT selected day's header when it is in that month, else the month's first header.</summary>
    public static int MapOutToIn(RecentsSections sections, RecentsCalendar calendar, DateOnly selected, int monthIndex)
    {
        int exact = HeaderFlatFor(sections, selected);
        if (exact >= 0 && MonthFor(calendar, selected) == monthIndex) return exact;
        if ((uint)monthIndex >= (uint)calendar.Months.Length) return -1;
        var month = calendar.Months[monthIndex];
        for (int i = 0; i < sections.HeaderDates.Length; i++)
            if (sections.HeaderDates[i].Year == month.Year && sections.HeaderDates[i].Month == month.Month)
                return sections.HeaderIndices[i];
        return -1;
    }
    /// <summary>One day of the calendar, or null outside it.</summary>
    public static RecentsCalendarDay? CalendarDay(RecentsCalendar calendar, DateOnly date)
    {
        int m = MonthFor(calendar, date);
        if (m < 0) return null;
        var days = calendar.Months[m].Days;
        return (uint)(date.Day - 1) < (uint)days.Length ? days[date.Day - 1] : null;
    }

    // ── recycling, heat, the clock and the mount demand ──────────────────────────────────────────────────────────────

    /// <summary>Does this row render through the track grid (a single play of a track or episode) rather than the card?</summary>
    public static bool UsesTrackArm(RecentsSnapshot rows, int row)
        => (uint)row < (uint)rows.Count && rows.Rows[row].Shape == RecentsRowKind.Single
           && rows.Targets[row].Kind is EntityKind.Track or EntityKind.Episode;
    /// <summary>The recycle-pool id: 0 a header, <c>1 + (int)Kind</c> a row — the kind of the ARM it renders through, so a
    /// card slot never rebinds into the track-grid shape.</summary>
    public static int ContentTypeOf(RecentsSections sections, RecentsSnapshot rows, int index)
    {
        if ((uint)index >= (uint)sections.Items.Length) return 0;
        var flat = sections.Items[index];
        if (flat.Kind == RecentsFlatItemKind.DateHeader) return 0;
        if ((uint)flat.OriginalRowIndex >= (uint)rows.Count) return 1;
        return 1 + (int)(UsesTrackArm(rows, flat.OriginalRowIndex) ? RecentsRowKind.Single : RecentsRowKind.Group);
    }
    /// <summary>The heat's alpha: <c>A_subtle + (inkA − A_subtle)·clamp(level,1,5)/5</c>; 0 at level 0.</summary>
    public static float DensityAlpha(int level, float subtleA, float inkA)
        => level <= 0 ? 0f : subtleA + (inkA - subtleA) * (Math.Clamp(level, 1, 5) / 5f);
    /// <summary>The heat's paint: the accent ink at <see cref="DensityAlpha"/>, transparent at level 0.</summary>
    public static ColorF DensityFill(int level, ColorF ink, float subtleA)
        => level <= 0 ? ColorF.Transparent : ink with { A = DensityAlpha(level, subtleA, ink.A) };
    /// <summary>When the day words go stale: the next local midnight plus a 1 s settle, never under 1 s.</summary>
    public static float RolloverDelayMs(DateTimeOffset now)
    {
        var next = new DateTimeOffset(DateOnly.FromDateTime(now.DateTime).AddDays(1).ToDateTime(TimeOnly.MinValue), now.Offset);
        return (float)Math.Max(1000d, (next - now).TotalMilliseconds + 1000d);
    }
    /// <summary>The distinct slots of <paramref name="kind"/> the page shows on mount: each row's hydration target, and a
    /// Saved row's first two named members (its stacked covers). <paramref name="seen"/> is caller scratch.</summary>
    public static void DemandSlots(RecentsSnapshot rows, EntityKind kind, List<int> into, HashSet<int> seen)
    {
        seen.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            var target = RecentsView.EntitySlotOf(rows, i);
            if (target.Kind == kind && target.Slot > 0 && seen.Add(target.Slot)) into.Add(target.Slot);
            if (rows.Rows[i].Why != RecentsReason.Saved) continue;
            var kids = rows.MemberTargetsOf(i);
            for (int k = 0, shown = 0; k < kids.Length && shown < 2; k++)
            {
                if (!RecentsView.Names(kids[k])) continue;
                shown++;
                if (kids[k].Kind == kind && kids[k].Slot > 0 && seen.Add(kids[k].Slot)) into.Add(kids[k].Slot);
            }
        }
    }
}
