// ── Wavee.Tests/QueueUidTests.cs — the q<n> uids of custom-queued rows and the queued_by rule ────────────────────────
//
// Pure: the counter, the uid book's mint/seed/forget, and the attribution rule. The official client numbers the rows a
// user queues q0, q1, … per player activation, increasing across contexts; a q<n> that ARRIVED booked (set_queue, a
// transfer, a cluster row) must never be minted again, or a next_track / remove / reorder names two rows.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class QueueUidTests
{
    static string Text(Playback.UidBook book, ulong itemId)
    {
        Span<char> chars = stackalloc char[Playback.UidBook.MaxChars];
        return new string(chars[..book.Format(itemId, chars)]);
    }

    [Fact]
    public void The_counter_numbers_from_q0_and_resets_to_q0()
    {
        var counter = new Playback.QueueUidCounter();
        Assert.Equal("q0", counter.Next());
        Assert.Equal("q1", counter.Next());
        Assert.Equal("q2", counter.Next());
        counter.Reset();
        Assert.Equal("q0", counter.Next());
    }

    [Fact]
    public void A_booked_q_uid_lifts_the_counter_above_itself_and_other_shapes_are_ignored()
    {
        var counter = new Playback.QueueUidCounter();
        counter.NoteBooked("q7");
        Assert.Equal("q8", counter.Next());

        counter.NoteBooked("q3");                          // below the counter: no change
        Assert.Equal("q9", counter.Next());

        counter.NoteBooked("0ab15c9f39e1de3b");            // packed hex
        counter.NoteBooked("5bd5aabfe4434940c96f");        // 20 hex
        counter.NoteBooked("q");
        counter.NoteBooked("qx1");
        counter.NoteBooked("q-5");
        counter.NoteBooked("");
        Assert.Equal("q10", counter.Next());

        counter.NoteBooked("q21"u8);                       // the wire's own spelling
        Assert.Equal("q22", counter.Next());
    }

    [Fact]
    public void A_track_queued_twice_gets_two_uids_and_both_read_back_verbatim()
    {
        var book = new Playback.UidBook();
        ulong first = book.MintQueued(), second = book.MintQueued();
        Assert.NotEqual(0UL, first);
        Assert.NotEqual(first, second);
        Assert.Equal("q0", Text(book, first));
        Assert.Equal("q1", Text(book, second));
        Assert.Equal(first, book.FindByHash(Hash("q0")));       // a controller naming q0 finds the row
        Assert.Equal(second, book.FindByHash(Hash("q1")));
    }

    [Fact]
    public void The_mint_is_seeded_above_every_uid_that_arrived_booked()
    {
        var book = new Playback.UidBook();
        ulong a = book.ItemIdOf("q4"u8);                        // a set_queue / transfer row
        ulong b = book.ItemIdOf("q7"u8);
        book.ItemIdOf("5bd5aabfe4434940c96f"u8);                // context rows do not count

        ulong ours = book.MintQueued();
        Assert.Equal("q8", Text(book, ours));
        Assert.Equal("q4", Text(book, a));
        Assert.Equal("q7", Text(book, b));
        Assert.NotEqual(a, ours);
    }

    [Fact]
    public void A_reset_then_a_reseed_restarts_above_the_rows_still_live()
    {
        var book = new Playback.UidBook();
        ulong live = book.ItemIdOf("q3"u8);
        book.ItemIdOf("q9"u8);                                  // not live any more

        book.ReseedCounter([new QueueEdge(live, (byte)QueueProvider.Queue, (byte)QueueBucket.UserQueue)]);
        Assert.Equal("q4", Text(book, book.MintQueued()));

        book.ReseedCounter([]);                                 // nothing live: back to q0
        Assert.Equal("q0", Text(book, book.MintQueued()));
    }

    [Fact]
    public void Retain_keeps_a_live_minted_uid_and_forgets_the_rest()
    {
        var book = new Playback.UidBook();
        ulong kept = book.MintQueued(), gone = book.MintQueued();
        book.Retain([new QueueEdge(kept, (byte)QueueProvider.Queue, (byte)QueueBucket.UserQueue)]);
        Assert.Equal("q0", Text(book, kept));
        Assert.Equal(0UL, book.FindByHash(Hash("q1")));
        Assert.Equal(16, Text(book, gone).Length);                      // a forgotten mint falls back to the packed 16-hex form
        Assert.Equal("q2", Text(book, book.MintQueued()));               // the counter never goes back within an activation
    }

    [Fact]
    public void An_owners_queued_by_travels_with_the_row_and_a_row_queued_here_has_none_kept()
    {
        var book = new Playback.UidBook();
        ulong owners = book.ItemIdOf("q2"u8), blank = book.ItemIdOf("q5"u8), mine = book.MintQueued();
        book.NoteQueuedBy(owners, "someoneelse28charsaccountname");
        book.NoteQueuedBy(blank, "");

        Assert.True(book.TryQueuedBy(owners, out string who));
        Assert.Equal("someoneelse28charsaccountname", who);
        Assert.True(book.TryQueuedBy(blank, out string none));
        Assert.Equal("", none);
        Assert.False(book.TryQueuedBy(mine, out _));
        Assert.False(book.TryQueuedBy(0, out _));

        book.Retain([new QueueEdge(owners, (byte)QueueProvider.Queue, (byte)QueueBucket.UserQueue)]);
        Assert.False(book.TryQueuedBy(blank, out _));
    }

    [Theory]
    [InlineData("alice28charsaccountnameabcd", false, "me", "alice28charsaccountnameabcd")]   // an owner's value is kept exactly
    [InlineData("alice28charsaccountnameabcd", true, "me", "alice28charsaccountnameabcd")]
    [InlineData("", false, "me", "")]                                                          // the Web Player's "": not relabelled as ours
    [InlineData(null, false, "me", "")]
    [InlineData(null, true, "me", "me")]                                                       // a row we queued: the signed-in user
    [InlineData("", true, "me", "me")]
    [InlineData(null, true, "", "")]                                                           // logged out: omitted
    [InlineData(null, true, null, "")]
    public void Queued_by_keeps_the_owners_value_and_stamps_only_our_own_rows(string? carried, bool queuedHere, string? user, string expected)
        => Assert.Equal(expected, QueueAttribution.QueuedBy(carried, queuedHere, user));

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData("q3", null)]
    [InlineData("0ab15c9f39e1de3b", null)]
    [InlineData("5bd5aabfe4434940c96f", null)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_a_32_hex_context_uid_may_ride_a_skip_to(string? uid, string? expected)
        => Assert.Equal(expected, Spotify.Connect.SkipUidOf(uid));

    // FNV-1a over the uid's characters — the hash a decoded command carries (Spotify.Decode.Connect.cs).
    static ulong Hash(string text)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in text) { h ^= (byte)c; h *= 1099511628211UL; }
        return h;
    }
}
