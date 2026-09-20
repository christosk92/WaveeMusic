// ── Entities/JumpIndex.cs — the ONE kernel behind every "jump to a group" strip in the app ──────────────────────────
//
// A–Z letters (User.cs's LibraryLetters) and a show reader's date/month rail (S-reader, plan §3.4) are the SAME shape:
// a flat item space that interleaves group HEADERS with ROWS, and a strip that needs two things from it — "where does
// group K's header sit in the flat space" (a tap target) and, given a header's position, which group it belongs to.
// This file is that shape, pure and engine-free (no Wavee entity, no signal, no allocation beyond the caller's own
// output buffer) so both callers share one tested kernel instead of two hand-rolled lookup tables that can drift.
//
// Role: CORE (pure)
// Owner: A2
// Spec: plan §3.4 (JumpIndex)

using System;

namespace Wavee;

/// <summary>One projected group: <see cref="Key"/> is the caller's group identity (a letter 0..26, a YYYYMM code, a
/// year) and <see cref="Index"/> is that group's HEADER position in the flat space <see cref="JumpIndex.Project{T}"/>
/// walked — what a jump strip's tap resolves to and hands the list ("bring this flat index into view").</summary>
public readonly record struct JumpGroup(int Key, int Index);

/// <summary>The shared kernel: given a flat item space that already interleaves headers and rows (the caller built
/// that part — a letter grouping, a month grouping, a chapter-order grouping — this file does not build one), project
/// the header positions into a compact <see cref="JumpGroup"/> table, and resolve a key back to a flat index from that
/// table. Both directions are O(n) worst case with no further allocation: <see cref="Project{T}"/> writes into the
/// caller's OWN buffer (never allocates one), and <see cref="Resolve"/> is a linear scan over that same small buffer
/// (a jump strip has at most a few dozen groups — 27 letters, a few hundred months of history at most — so a linear
/// scan beats maintaining a second index structure for it).</summary>
public static class JumpIndex
{
    /// <summary>Walks <paramref name="flat"/> once and writes one <see cref="JumpGroup"/> per HEADER item — the first
    /// flat position for which <paramref name="isHeader"/> is true, in the order encountered — into
    /// <paramref name="into"/>. Returns the count written (never more than <paramref name="into"/>'s length: a caller
    /// whose group count can exceed the buffer stops collecting further groups rather than throwing, exactly like
    /// <see cref="Resolve"/> failing closed on a key it never saw). A row for which <paramref name="isHeader"/> is
    /// false is skipped entirely — this is a projection onto headers only, never a row-by-row copy.</summary>
    public static int Project<T>(ReadOnlySpan<T> flat, Func<T, bool> isHeader, Func<T, int> keyOf, Span<JumpGroup> into)
    {
        int n = 0;
        for (int i = 0; i < flat.Length && n < into.Length; i++)
        {
            if (!isHeader(flat[i])) continue;
            into[n++] = new JumpGroup(keyOf(flat[i]), i);
        }
        return n;
    }

    /// <summary>The header flat index for <paramref name="key"/>, or -1 when no projected group carries it (an empty
    /// letter, a month with no episodes) — the ONE failure mode a jump strip's tap must no-op on rather than throw or
    /// jump somewhere wrong.</summary>
    public static int Resolve(ReadOnlySpan<JumpGroup> groups, int key)
    {
        for (int i = 0; i < groups.Length; i++)
            if (groups[i].Key == key) return groups[i].Index;
        return -1;
    }
}
