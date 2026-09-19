// ── Wavee.Tests/JumpIndexTests.cs — the shared jump-strip kernel (Entities/JumpIndex.cs, A2 plan §3.4) ──────────────
//
// Pure over caller-supplied flat spans: no entity, no signal, no Wavee scope. Two shapes are pinned — a letter-keyed
// grouping (LibraryLetters' own shape: a header item where RowOrNegative < 0, keyed by a letter 0..26) and a
// month-keyed grouping (the show reader's date rail: keyed by a YYYYMM code) — because the kernel is meant to serve
// both without caring which.

using Xunit;

namespace Wavee.Tests;

public class JumpIndexTests
{
    /// <summary>One flat item: negative <see cref="Row"/> marks a header (mirrors <c>LibraryLetters</c>' own
    /// <c>_flatToRow</c> convention), <see cref="Group"/> is the key a header/row belongs to.</summary>
    readonly record struct Item(int Row, int Group);

    static bool IsHeader(Item i) => i.Row < 0;
    static int KeyOf(Item i) => i.Group;

    // ── Project: letter-keyed (LibraryLetters' own shape) ───────────────────────────────────────────────────────────

    [Fact]
    public void Project_FindsEveryHeader_LetterKeyed()
    {
        // '#'-band (1 row), A-band (2 rows), B-band (1 row) — three headers at flat indices 0, 2, 5.
        Item[] flat =
        [
            new(-1, 0), new(0, 0),                          // '#' header + its row
            new(-1, 1), new(0, 1), new(1, 1),                // A header + two rows
            new(-1, 2), new(0, 2),                          // B header + its row
        ];
        Span<JumpGroup> into = stackalloc JumpGroup[27];
        int n = JumpIndex.Project<Item>(flat, IsHeader, KeyOf, into);

        Assert.Equal(3, n);
        Assert.Equal(new JumpGroup(0, 0), into[0]);
        Assert.Equal(new JumpGroup(1, 2), into[1]);
        Assert.Equal(new JumpGroup(2, 5), into[2]);
    }

    [Fact]
    public void Project_SkipsRows_OnlyHeadersAreProjected()
    {
        Item[] flat = [new(-1, 0), new(0, 0), new(1, 0), new(2, 0), new(3, 0)];
        Span<JumpGroup> into = stackalloc JumpGroup[27];
        int n = JumpIndex.Project<Item>(flat, IsHeader, KeyOf, into);

        Assert.Equal(1, n);
        Assert.Equal(new JumpGroup(0, 0), into[0]);
    }

    [Fact]
    public void Project_EmptyFlat_WritesNothing()
    {
        Span<JumpGroup> into = stackalloc JumpGroup[27];
        int n = JumpIndex.Project<Item>([], IsHeader, KeyOf, into);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Project_StopsAtTheBufferInsteadOfOverrunning()
    {
        // Five headers, a two-slot buffer: only the first two are collected, no throw.
        Item[] flat = [new(-1, 0), new(-1, 1), new(-1, 2), new(-1, 3), new(-1, 4)];
        Span<JumpGroup> into = stackalloc JumpGroup[2];
        int n = JumpIndex.Project<Item>(flat, IsHeader, KeyOf, into);

        Assert.Equal(2, n);
        Assert.Equal(0, into[0].Key);
        Assert.Equal(1, into[1].Key);
    }

    // ── Project: month-keyed (the show reader's date rail shape) ────────────────────────────────────────────────────

    [Fact]
    public void Project_FindsEveryHeader_MonthKeyed()
    {
        // YYYYMM codes, most-recent-first (the reader's Newest sort): 202609 (2 rows), 202608 (1 row), 202601 (1 row).
        Item[] flat =
        [
            new(-1, 202609), new(0, 202609), new(1, 202609),
            new(-1, 202608), new(0, 202608),
            new(-1, 202601), new(0, 202601),
        ];
        Span<JumpGroup> into = stackalloc JumpGroup[12];
        int n = JumpIndex.Project<Item>(flat, IsHeader, KeyOf, into);

        Assert.Equal(3, n);
        Assert.Equal(new JumpGroup(202609, 0), into[0]);
        Assert.Equal(new JumpGroup(202608, 3), into[1]);
        Assert.Equal(new JumpGroup(202601, 5), into[2]);
    }

    // ── Resolve ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ReturnsTheHeaderFlatIndex_ForAKnownKey()
    {
        ReadOnlySpan<JumpGroup> groups = [new(0, 0), new(1, 2), new(2, 5)];
        Assert.Equal(0, JumpIndex.Resolve(groups, 0));
        Assert.Equal(2, JumpIndex.Resolve(groups, 1));
        Assert.Equal(5, JumpIndex.Resolve(groups, 2));
    }

    [Fact]
    public void Resolve_ReturnsMinusOne_WhenTheKeyIsAbsent()
    {
        ReadOnlySpan<JumpGroup> groups = [new(0, 0), new(2, 5)];
        Assert.Equal(-1, JumpIndex.Resolve(groups, 1));
        Assert.Equal(-1, JumpIndex.Resolve(groups, 26));
        Assert.Equal(-1, JumpIndex.Resolve(groups, -1));
    }

    [Fact]
    public void Resolve_EmptyGroups_IsAlwaysMinusOne()
        => Assert.Equal(-1, JumpIndex.Resolve(ReadOnlySpan<JumpGroup>.Empty, 0));

    [Fact]
    public void ProjectThenResolve_RoundTripsEveryHeaderThatWasProjected()
    {
        Item[] flat =
        [
            new(-1, 202609), new(0, 202609),
            new(-1, 202607), new(0, 202607), new(1, 202607),
            new(-1, 202512), new(0, 202512),
        ];
        Span<JumpGroup> into = stackalloc JumpGroup[12];
        int n = JumpIndex.Project<Item>(flat, IsHeader, KeyOf, into);
        var groups = into[..n];

        Assert.Equal(0, JumpIndex.Resolve(groups, 202609));
        Assert.Equal(2, JumpIndex.Resolve(groups, 202607));
        Assert.Equal(5, JumpIndex.Resolve(groups, 202512));
        Assert.Equal(-1, JumpIndex.Resolve(groups, 202601));   // an undated/never-projected month
    }
}
