// ── Entities/Recents.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the whole RecentsView rule set (19 classes, §6) — WAVE 1's half only: the columns, the two edges and the commit
//
// Role: CORE
// Owner: P
// Wave: 5
// Budget: 650 lines
// Spec: ch 16 §9.4 (the rules) · ch 16 §7.2 (the edge, filled in wave 2)
//
// RECENTS IS AN EDGE OVER THE ACCOUNT, exactly like the library (G6). Ch 16 §7.2's first DATA GAP row: there is no
// Recents kind and there must not be one — a recents row IS a track, an album, a playlist or a show, and what makes it
// "recent" is the membership fact, which belongs on the edge (D10).
//
//        Users[me] ──┬── Recents         EdgeTable<RecentsEdge>        → entity slots, newest first, wire order
//                    └── RecentsMembers  EdgeTable<RecentsMemberEdge>  → the plays a GROUP row collapsed
//
// TWO TABLES AND NOT ONE, and the members are kept rather than dropped. A recents answer collapses runs of plays into
// group headers; a header's own `child_uri` list is SERVER-TRUNCATED, so the members are the only complete account of
// which plays a card stands for (`RecentsList.cs:58-61`). `RecentsEdge.MembersStart/MembersLen` index the member run
// under the SAME parent — the smallest shape that satisfies ch 16 §7.2's "MembersStart/Len on the parent edge index
// into it" without minting a synthetic slot per group. The chapter offers a shape there and not a count; the smaller
// of its two readings is the one taken, and it is written down here rather than left implicit.
//
// WHAT IS NOT HERE: the whole `RecentsView` rule set — the section builder, the day buckets, the pivot predicate, the
// calendar density ramp, the sticky metrics, the summary sentence. Ch 16 §8 lists 25 of them and they are owner P's, in
// Wave 5, in this same file. Wave 1 owns what they READ. `RecentsList.Group` is deliberately elsewhere and stays there:
// it is a WIRE fold and it lives with the decoder (`Spotify/Spotify.Decode.cs`, ch 16 §8's own destination column).

using FluentGpu.Foundation;

namespace Wavee;

// ── 1. the three bytes a recents edge carries ────────────────────────────────────────────────────────────────────────

/// <summary>Why an entry is in Recents — the semantic of the <c>recent_type_*</c> format-attribute KEY (the value is
/// empty). Declared here, beside the edge that stores it, and read by the decoder that fills it.</summary>
public enum RecentsReason : byte { Unknown = 0, Played = 1, Saved = 2 }

/// <summary>The suffix of a <c>content_type_*</c> key: the filter chips' axis. An unrecognised token stays
/// <see cref="None"/> — the chip strip labels itself from the wire token when the app has no key for it (ch 16 §8's
/// <c>ChipLabel</c>), and minting a member per unseen token would make the byte unstable across releases.</summary>
public enum RecentsContentType : byte { None = 0, Music = 1, Podcasts = 2 }

/// <summary>A grouped recents ROW is either a single play or a collapsed group header.</summary>
public enum RecentsRowKind : byte { Single = 0, Group = 1 }

// ── 2. the payloads ──────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One recents row's membership fact (ch 16 §7.2). Everything a day header, a chip, a chevron, the calendar
/// heat and the "played N tracks" sentence need comes off this payload alone — which is what lets the whole STRUCTURE
/// of the page render complete and at once while only per-row identity trails (ch 16 §7.1's readiness rule).
///
/// <para><paramref name="ItemId"/> is the hex <c>item_id</c> and it is load-bearing: URIS REPEAT ~1,388× in a real
/// list, so the accordion's identity check and the recycle keys break without it.</para>
/// <para><paramref name="ChildCount"/> is the header's DECLARED child count, never the length of the member run — the
/// server truncates the list it sends and states the real number separately.</para></summary>
public readonly record struct RecentsEdge(
    StringId ItemId, long PlayedAtMs, int ChildCount,
    byte Reason, byte ContentType, byte Kind,
    int MembersStart, int MembersLen)
{
    public RecentsReason Why => (RecentsReason)Reason;
    public RecentsContentType Axis => (RecentsContentType)ContentType;
    public RecentsRowKind Shape => (RecentsRowKind)Kind;

    /// <summary>Is there a drawer to open? A group with children, and at least one member we can name.</summary>
    public bool CanExpand => Shape == RecentsRowKind.Group && ChildCount > 0 && MembersLen > 0;
}

/// <summary>One play a group header collapsed, with its OWN instant — which is the whole reason the members are kept
/// (a header states one timestamp for a run of plays spread over an evening).</summary>
public readonly record struct RecentsMemberEdge(StringId ItemId, long PlayedAtMs);

// ── 3. the relations ─────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class Edges
{
    /// <summary>The grouped recents snapshot: parent = the account's own row (<c>Scope.MeSlot</c>), targets = entity
    /// slots in wire order (newest first).</summary>
    public readonly EdgeTable<RecentsEdge> Recents = new();

    /// <summary>Every collapsed member of every group in the snapshot, under the SAME parent and in the order the rows
    /// reference by <see cref="RecentsEdge.MembersStart"/> / <see cref="RecentsEdge.MembersLen"/>.</summary>
    public readonly EdgeTable<RecentsMemberEdge> RecentsMembers = new();

    // The snapshot revision, per parent. Lowercase hex, as `/playlist/v2/list/recents/page/diff` wants it back; it is
    // one string for the whole list, so a column keyed by parent is the honest shape. Ch 16 §7.2 offers it on the user
    // table OR on the edge table's own state word; it sits here, beside the edge it describes, because it is a property
    // of the SNAPSHOT and not of the account.
    Column<StringId> _recentsRevision;
    int _recentsRevisionCount;

    /// <summary>The revision the parent's recents list was answered at, or <see cref="StringId.Empty"/>.</summary>
    public StringId RecentsRevision(int parent)
        => (uint)parent >= (uint)_recentsRevisionCount ? StringId.Empty : _recentsRevision[parent];

    /// <summary>Record the revision an answer carried. Ref-counted like every owned string (defect 1): re-answering the
    /// same list a hundred times owns exactly one revision string at the end of it.</summary>
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

/// <summary>One answered recents PAGE: the parent it hangs off, the revision it came at, and the slices of
/// <see cref="Staging.RecentsRows"/> and <see cref="Staging.RecentsMembers"/> it produced. A header ROW rather than
/// three loose fields on the <see cref="Staging"/>, so two answers in one batch cannot overwrite each other's parent
/// and <c>Staging.Reset</c> clears it with everything else.</summary>
public struct StagedRecentsPage
{
    public StagedId Parent;
    public TextRef Revision;
    public int RowStart, RowCount;
    public int MemberStart, MemberCount;
}

/// <summary>One grouped, display-ready recents row, staged (<c>RecentsList.Group</c>'s output). The identity may be
/// EMPTY for a single-context group header, which is rendered from its children.</summary>
public struct StagedRecents
{
    public StagedId Id;
    public TextRef ItemId;
    public long PlayedAtMs;
    public int ChildCount;
    /// <summary>Into this page's member slice, and therefore into the parent's member run after the commit.</summary>
    public int MembersStart, MembersLen;
    public byte Reason, ContentType, Kind;
}

/// <inheritdoc cref="RecentsMemberEdge"/>
public struct StagedRecentsMember
{
    public StagedId Id;
    public TextRef ItemId;
    public long PlayedAtMs;
}

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
    // UI thread only (C1); grown to the widest snapshot seen and never again (P8). A real list is ~1,700 rows, so this
    // is one pair of arrays for the life of the process rather than two allocations per refresh.
    static int[] s_recentsTargets = new int[64], s_recentsMemberTargets = new int[64];
    static RecentsEdge[] s_recentsPayload = new RecentsEdge[64];
    static RecentsMemberEdge[] s_recentsMemberPayload = new RecentsMemberEdge[64];

    /// <summary>Land a recents snapshot: the member run first, then the rows that index into it, then the revision. One
    /// <c>Replace</c> each — a recents answer IS the whole list (the diff endpoint replaces it too), so there is no page
    /// arm here and the relation settles Complete, empty included: "no recent plays" is a real, renderable answer and
    /// the skeleton must stop (ch 16 §7.1).</summary>
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
                s_recentsMemberTargets[i] = s.Slot(in memberPage[i].Id);
                s_recentsMemberPayload[i] = new RecentsMemberEdge(s.Intern(memberPage[i].ItemId), memberPage[i].PlayedAtMs);
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
                    row.MembersLen > 0 ? row.MembersStart : 0, row.MembersLen);
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

/// <summary>The account's recents snapshot, as a handle over the parent slot. Wave 5 adds ch 16 §8's whole rule set to
/// this same type; Waves 1/2 give it only what the columns say.</summary>
public readonly partial struct Recents(int slot)
{
    static Edges E => Entities.Current.Edges;

    /// <summary>The account row the snapshot hangs off.</summary>
    public int Slot { get; } = slot;

    /// <summary>The signed-in account's snapshot (G6: the parent is the user row, as for every library relation).</summary>
    public static Recents Me => new(Entities.Current.MeSlot);

    /// <summary>The rows, newest first, in wire order. Never held across a UI drain (Edges.cs's span rule).</summary>
    public ReadOnlySpan<int> Slots => E.Recents.Targets(Slot);
    /// <inheritdoc cref="RecentsEdge"/>
    public ReadOnlySpan<RecentsEdge> Rows => E.Recents.Payload(Slot);
    /// <summary>Unknown until somebody answers — a page renders a skeleton for Unknown and "no recent plays" only for
    /// Complete, which is exactly the distinction ch 16 §7.1 asks the summary line to respect.</summary>
    public EdgeState State => E.Recents.State(Slot);
    public uint Version => E.Recents.Version(Slot);
    public int Count => E.Recents.Count(Slot);

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
