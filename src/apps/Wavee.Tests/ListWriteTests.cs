// ── Wavee.Tests/ListWriteTests.cs — the list-write gate and the list head's rules (wave D2), pure ─────────────────────
//
// `ListWrite` (Entities/Store.Lists.cs) is the ONE gate every list write passes (plan §2: "only a well-formed revision
// may be stored, on every writer"): a poisoned persisted list is forever (plan §7), so the gate refuses on any doubt —
// not Complete, a partial window, an optimistic row, a revision that is not a playlist4 head — and the head carries a
// generation stamp a later decoder fix can refuse by. Pure: no table, no file, no clock.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ListWriteTests
{
    const string Head = "7,0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void A_settled_whole_list_with_a_well_formed_head_may_be_written()
        => Assert.True(ListWrite.MayPersist(EdgeState.Complete, rows: 3, total: 3, wholeAnswer: true, anyPending: false, Head));

    [Fact]
    public void An_empty_settled_list_is_an_answer_and_may_be_written()
        => Assert.True(ListWrite.MayPersist(EdgeState.Complete, rows: 0, total: 0, wholeAnswer: true, anyPending: false, Head));

    [Theory]
    [InlineData(EdgeState.Unknown)]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Failed)]
    public void Only_a_complete_list_is_written(EdgeState state)
        => Assert.False(ListWrite.MayPersist(state, rows: 3, total: 3, wholeAnswer: true, anyPending: false, Head));

    /// <summary>A partial window is refused whatever its state claims: fewer rows than the list says it has, or a list
    /// completed by pages — read across requests that may straddle revisions, so one revision cannot vouch for it.</summary>
    [Fact]
    public void A_partial_window_is_refused()
    {
        Assert.False(ListWrite.MayPersist(EdgeState.Complete, rows: 2, total: 3, wholeAnswer: true, anyPending: false, Head));
        Assert.False(ListWrite.MayPersist(EdgeState.Complete, rows: 3, total: 3, wholeAnswer: false, anyPending: false, Head));
        Assert.False(ListWrite.MayPersist(EdgeState.Complete, rows: -1, total: -1, wholeAnswer: true, anyPending: false, Head));
    }

    /// <summary>Only SETTLED membership is written. The optimistic bits are the table's pending column, one byte per row.</summary>
    [Fact]
    public void A_list_with_an_optimistic_row_is_refused()
    {
        Assert.False(ListWrite.MayPersist(EdgeState.Complete, rows: 3, total: 3, wholeAnswer: true, anyPending: true, Head));

        Assert.True(ListWrite.AnyPending([0, (byte)EdgePending.Remove, 0]));
        Assert.True(ListWrite.AnyPending([(byte)EdgePending.Add]));
        Assert.False(ListWrite.AnyPending([0, 0, 0]));
        Assert.False(ListWrite.AnyPending(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData("7,0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0,0000000000" + "0000000000" + "0000000000" + "0000000000")]
    [InlineData("4294967295,ffffffffff" + "ffffffffff" + "ffffffffff" + "ffffffffff")]       // the largest counter
    [InlineData("135,fedcba9876543210fedcba9876543210fedcba98")]
    public void A_counter_a_comma_and_a_twenty_byte_lowercase_hash_is_a_revision(string revision)
    {
        Assert.True(ListWrite.IsWellFormedRevision(revision));
        Assert.True(ListWrite.MayPersist(EdgeState.Complete, rows: 1, total: 1, wholeAnswer: true, anyPending: false, revision));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(",0123456789abcdef0123456789abcdef01234567")]                    // no counter
    [InlineData("7,")]                                                            // no hash
    [InlineData("7,0123")]                                                        // a truncated hash
    [InlineData("7,0123456789abcdef0123456789abcdef0123456")]                    // 39 hex
    [InlineData("7,0123456789abcdef0123456789abcdef012345678")]                  // 41 hex
    [InlineData("7,0123456789ABCDEF0123456789abcdef01234567")]                   // upper case: not the wire spelling
    [InlineData("7,0123456789abcdef0123456789abcdef0123456g")]                   // not hex
    [InlineData("-1,0123456789abcdef0123456789abcdef01234567")]                  // a signed counter
    [InlineData("4294967296,0123456789abcdef0123456789abcdef01234567")]          // past a uint
    [InlineData("12345678901,0123456789abcdef0123456789abcdef01234567")]         // eleven digits
    [InlineData("7;0123456789abcdef0123456789abcdef01234567")]                   // the wrong separator
    [InlineData(" 7,0123456789abcdef0123456789abcdef01234567")]                  // padded
    [InlineData("7,0123456789abcdef0123456789abcdef01234567,")]                  // trailing text
    [InlineData("spotify:user:christos:rootlist")]                               // 0.2.x's poison: uri bytes as a revision
    public void Anything_else_is_not_a_revision_and_is_never_written(string? revision)
    {
        Assert.False(ListWrite.IsWellFormedRevision(revision));
        Assert.False(ListWrite.MayPersist(EdgeState.Complete, rows: 1, total: 1, wholeAnswer: true, anyPending: false, revision));
    }

    /// <summary>The poison mitigation: a head is read back only under the generation that wrote it — so bumping
    /// <see cref="ListWrite.Generation"/> after a decoder fix turns every older list into a miss, with no migration.</summary>
    [Fact]
    public void A_head_is_trusted_only_under_the_generation_that_wrote_it()
    {
        string stamp = ListWrite.Stamp("0.3.0-beta.1+02f22cce");
        Assert.True(ListWrite.Trusts(stamp));
        Assert.Contains("0.3.0-beta.1+02f22cce", stamp);
        Assert.True(ListWrite.Trusts(ListWrite.Stamp(null)));                  // an unnamed build is still this generation

        Assert.False(ListWrite.Trusts(null));
        Assert.False(ListWrite.Trusts(""));
        Assert.False(ListWrite.Trusts("0.3.0-beta.1+02f22cce"));                 // no generation at all
        Assert.False(ListWrite.Trusts("g" + (ListWrite.Generation - 1) + " 0.3.0"));   // written before a decoder fix
        Assert.False(ListWrite.Trusts("g" + (ListWrite.Generation + 1) + " 0.4.0"));   // written by a later decoder
        Assert.False(ListWrite.Trusts("g" + ListWrite.Generation + "0 0.9.0"));        // a generation that only starts alike
    }

    [Fact]
    public void The_rootlist_key_names_the_account()
        => Assert.Equal("rootlist:spotify:user:christos", ListWrite.RootlistKey("spotify:user:christos"));
}
