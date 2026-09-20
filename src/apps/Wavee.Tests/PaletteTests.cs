// ── Wavee.Tests/PaletteTests.cs — the artwork key, the two TTLs, the demand queue and the three "no colour" states ─
//
// Wave 1's gate for `Entities/Palette.cs` (arbitration A5). No engine, no network: the filler is `Palette.Host.cs`'s
// and it does not exist yet, so `PalettePump` is erased and every fact below is the table answering for itself.
//
// The palette table is PROCESS-WIDE by design (a cover's colours do not change with locale or account), which means it
// is NOT reset by `TestScope.Fresh()`. Every fact here therefore mints its own 40-hex image ids, and the class joins
// `EntitiesCollection` so nothing runs beside it.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PaletteTests
{
    /// <summary>A believable Spotify image id: 40 hex characters whose first 16 are the size/kind marker and whose last
    /// 24 identify the artwork. <paramref name="size"/> varies only the marker, so two calls with the same tail are two
    /// renditions of ONE cover.</summary>
    static string Id(string tail24, string size = "ab67616d0000b273") => size + tail24;

    const string Small = "ab67616d00004851";       // 64 px
    const string Large = "ab67616d0000b273";       // 640 px

    static string Url(string id) => "https://i.scdn.co/image/" + id;

    // ── the key identity ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_sizes_of_one_cover_are_one_row()
    {
        // The colour identity is the size-independent 24-char tail, so a 64 px row thumbnail and a 640 px hero share an
        // entry — and one grading tints both. 0.2.9 keyed the cache on the same rule precisely so the colour cache and
        // the detail hero's cover latch could never disagree about "same art".
        string tail = "111111111111111111111111";
        Assert.True(Palette.KeyOf(Url(Id(tail, Small)).AsSpan()).SequenceEqual(tail));
        Assert.True(Palette.KeyOf(Url(Id(tail, Large)).AsSpan()).SequenceEqual(tail));
        Assert.True(Palette.KeyOf(("spotify:image:" + Id(tail, Large)).AsSpan()).SequenceEqual(tail));

        Assert.Equal(Palette.Images.Slot(Palette.KeyOf(Url(Id(tail, Small)).AsSpan())),
                     Palette.Images.Slot(Palette.KeyOf(Url(Id(tail, Large)).AsSpan())));
    }

    [Fact]
    public void A_url_with_a_query_or_an_unusual_shape_still_keys_on_something_stable()
    {
        string tail = "222222222222222222222222";
        Assert.True(Palette.ImageIdOf((Url(Id(tail)) + "?w=300").AsSpan()).SequenceEqual(Id(tail)));
        // A non-Spotify shape keys on itself: it will never be gradeable, but it must not collide with anything.
        Assert.True(Palette.KeyOf("https://example.com/covers/mine.png".AsSpan()).SequenceEqual("mine.png"));
        Assert.True(Palette.KeyOf(default).IsEmpty);
    }

    [Fact]
    public void Only_a_full_40_hex_id_can_be_sent_to_the_colour_endpoint()
    {
        Assert.True(Palette.CanGrade(Url(Id("333333333333333333333333")).AsSpan()));
        Assert.False(Palette.CanGrade("https://example.com/covers/mine.png".AsSpan()));
        Assert.False(Palette.CanGrade(Url("ab67616d0000b273tooshort").AsSpan()));
        Assert.False(Palette.CanGrade(default));
        Assert.Equal("spotify:image:abc", Palette.ImageUriFor("abc"));
    }

    // ── the three "no colour yet" states (ch 00 §4.1) ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_miss_is_not_an_error_it_is_a_request()
    {
        Entities.Now = 1_000_000;
        string id = Id("444444444444444444444444");
        int before = Palette.Pending;

        Assert.False(Palette.TryScheme(Url(id).AsSpan(), lightTheme: false, out _));
        Assert.Equal(before + 1, Palette.Pending);

        // Deduped by artwork: asking again — at any size — does not queue a second request.
        Assert.False(Palette.TryTint(Url(Id("444444444444444444444444", Small)).AsSpan(), false, out _));
        Assert.Equal(before + 1, Palette.Pending);
    }

    [Fact]
    public void A_dark_only_entry_misses_on_a_light_page_and_stays_queued()
    {
        // Kind 179 only ever ships dark treatments. Dropping one onto a pale page is the "graded, but wrong half" state
        // ch 00 §4.1 names: the tile stays neutral and the image stays queued so the filler can complete it.
        Entities.Now = 1_000_000;
        string id = Id("555555555555555555555555");
        Palette.SetDark(Url(id).AsSpan(), new Scheme(0xFF1C1C1C, 0xFF2A2A2A, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF));

        Assert.True(Palette.TryScheme(Url(id).AsSpan(), lightTheme: false, out var dark));
        Assert.Equal(0xFF2A2A2Au, dark.BackgroundTintedBase);

        int before = Palette.Pending;
        Assert.False(Palette.TryScheme(Url(id).AsSpan(), lightTheme: true, out _));
        Assert.Equal(before + 1, Palette.Pending);
    }

    [Fact]
    public void A_full_grading_answers_both_halves_and_clears_the_queue_bit()
    {
        Entities.Now = 1_000_000;
        string id = Id("666666666666666666666666");
        Assert.False(Palette.TryScheme(Url(id).AsSpan(), false, out _));      // queue it

        Palette.SetGraded(id.AsSpan(),
            dark: new Scheme(0xFF102030, 0xFF203040, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF),
            light: new Scheme(0xFFE0E8F0, 0xFFD0D8E0, 0xFF000000, 0xFF555555, 0xFF000000),
            hasLight: true, bestFitIsLight: true);

        Assert.True(Palette.TryTint(Url(id).AsSpan(), lightTheme: false, out uint darkTint));
        Assert.Equal(0xFF102030u, darkTint);
        Assert.True(Palette.TryTint(Url(id).AsSpan(), lightTheme: true, out uint lightTint));
        Assert.Equal(0xFFE0E8F0u, lightTint);

        int slot = Palette.Images.Slot(Palette.KeyOf(Url(id).AsSpan()));
        Assert.Equal(0u, Palette.Images.Known[slot] & (uint)PaletteBits.Queued);
        Assert.NotEqual(0u, Palette.Images.Known[slot] & (uint)PaletteBits.BestFitIsLight);
    }

    [Fact]
    public void A_negative_is_an_answer_with_its_own_shorter_ttl()
    {
        Entities.Now = 1_000_000;
        string id = Id("777777777777777777777777");
        Palette.SetNegative(id.AsSpan());

        // Still inside the 7-day miss TTL: no colour, and — the point — no repeat request.
        int before = Palette.Pending;
        Assert.False(Palette.TryScheme(Url(id).AsSpan(), false, out _));
        Assert.Equal(before, Palette.Pending);

        // Past it, the cover is asked about again: a negative recovers, it does not condemn.
        Entities.Now = 1_000_000 + Palette.MissTtlSeconds + 1;
        Assert.False(Palette.TryScheme(Url(id).AsSpan(), false, out _));
        Assert.Equal(before + 1, Palette.Pending);
    }

    [Fact]
    public void A_hit_lasts_half_a_year_and_then_asks_again()
    {
        Entities.Now = 1_000_000;
        string id = Id("888888888888888888888888");
        Palette.SetGraded(id.AsSpan(), new Scheme(0xFF010203, 0, 0, 0, 0), default, hasLight: false, bestFitIsLight: false);

        Entities.Now = 1_000_000 + Palette.HitTtlSeconds - 1;
        Assert.True(Palette.TryScheme(Url(id).AsSpan(), false, out _));

        Entities.Now = 1_000_000 + Palette.HitTtlSeconds + 1;
        Assert.False(Palette.TryScheme(Url(id).AsSpan(), false, out _));
    }

    // ── the probe that must not create work ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Has_fresh_dark_never_enqueues_because_its_caller_is_not_rendering()
    {
        // The kind-179 trait projector asks it before a request is PLANNED. Enqueuing from a planning question would
        // turn every warm page into a getDynamicColorsByUris batch.
        Entities.Now = 1_000_000;
        string id = Id("999999999999999999999999");
        int before = Palette.Pending;

        Assert.False(Palette.HasFreshDark(Url(id).AsSpan()));
        Assert.Equal(before, Palette.Pending);

        Palette.SetDark(Url(id).AsSpan(), new Scheme(0xFF0A0B0C, 0xFF1A1B1C, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF));
        Assert.True(Palette.HasFreshDark(Url(id).AsSpan()));
        Assert.Equal(before, Palette.Pending);

        // A negative is not a 179 answer: a cover the colour server declined can still get one.
        Palette.SetNegative(id.AsSpan());
        Assert.False(Palette.HasFreshDark(Url(id).AsSpan()));
    }

    [Fact]
    public void An_ungradeable_url_is_never_queued()
    {
        int before = Palette.Pending;
        Assert.False(Palette.TryScheme("https://example.com/covers/custom.png".AsSpan(), false, out _));
        Assert.Equal(before, Palette.Pending);      // a request that can only 404 is not worth making
    }

    // ── the queue and the watch signal ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Drain_hands_the_pump_a_bounded_batch_and_a_failure_frees_the_rows_for_a_retry()
    {
        Entities.Now = 1_000_000;
        Span<StringId> flush = stackalloc StringId[64];
        while (Palette.Pending > 0) Palette.Drain(flush);            // start from an empty queue, whatever ran before

        string a = Id("aaaaaaaaaaaaaaaaaaaaaaa1");
        string b = Id("aaaaaaaaaaaaaaaaaaaaaaa2");
        Palette.TryScheme(Url(a).AsSpan(), false, out _);
        Palette.TryScheme(Url(b).AsSpan(), false, out _);
        Assert.Equal(2, Palette.Pending);

        Span<StringId> batch = stackalloc StringId[8];
        int n = Palette.Drain(batch);
        Assert.Equal(2, n);
        Assert.Equal(0, Palette.Pending);

        // While the request is in flight the rows stay marked, so a second render does not re-ask …
        Palette.TryScheme(Url(a).AsSpan(), false, out _);
        Assert.Equal(0, Palette.Pending);

        // … and when the batch FAILS the marks come off, because a failure is not an answer.
        Palette.Failed(batch[..n]);
        Palette.TryScheme(Url(a).AsSpan(), false, out _);
        Assert.Equal(1, Palette.Pending);
    }

    [Fact]
    public void Watch_fires_for_the_image_that_was_graded_and_not_for_its_neighbour()
    {
        // Page chrome watches ONE cover so its wash lands when that cover resolves, with no coupling to the batches a
        // scrolling grid is finishing (ch 00 §9: the subscription lives in a leaf, never at page scope).
        Entities.Now = 1_000_000;
        string mine = Id("bbbbbbbbbbbbbbbbbbbbbbb1");
        string other = Id("bbbbbbbbbbbbbbbbbbbbbbb2");

        var watchMine = Palette.Watch(Url(mine).AsSpan());
        var watchOther = Palette.Watch(Url(other).AsSpan());
        uint mineBefore = watchMine.Peek();
        uint otherBefore = watchOther.Peek();

        Palette.SetDark(Url(mine).AsSpan(), new Scheme(0xFF334455, 0xFF445566, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF));

        Assert.Equal(mineBefore + 1, watchMine.Peek());
        Assert.Equal(otherBefore, watchOther.Peek());
    }

    [Fact]
    public void A_grading_batch_marks_the_table_dirty_so_the_art_tiles_repaint_once()
    {
        Entities.Now = 1_000_000;
        Entities.Publish();                                          // start from a settled drain
        uint before = Palette.Changed.Peek();

        Palette.SetDark(Url(Id("ccccccccccccccccccccccc1")).AsSpan(), new Scheme(0xFF111111, 0, 0, 0, 0));
        Palette.SetDark(Url(Id("ccccccccccccccccccccccc2")).AsSpan(), new Scheme(0xFF222222, 0, 0, 0, 0));
        uint publication = Entities.Publish();

        Assert.Equal(publication, Palette.Changed.Peek());
        Assert.NotEqual(before, Palette.Changed.Peek());             // two gradings, ONE repaint (C3)
    }

    // ── what this table owes the interner (defect 1, doc §4.4) ───────────────────────────────────

    /// <summary>The key stays a <see cref="StringId"/> and not a packed <c>EntityId</c> (an artwork is not an entity —
    /// see the file header for the whole argument), which makes the table's OWNERSHIP of it the thing that has to be
    /// right: <c>ByKey</c>'s comparer hashes the RESOLVED text, so a key the row did not own would be unfiled the
    /// moment its last other owner released it. Here the interning caller gives its reference back and the row is
    /// still filed under the same content.</summary>
    [Fact]
    public void The_table_owns_its_key_so_the_interning_caller_can_let_go()
    {
        Entities.Now = 1_000_000;
        const string tail = "eeeeeeeeeeeeeeeeeeeeeee1";
        int before = Entities.Strings.MapCount;

        var mine = Entities.Strings.Intern(tail);
        Entities.Strings.AddRef(mine);                               // a caller takes ownership …
        int slot = Palette.Images.Slot(mine);
        Entities.Strings.Release(mine);                              // … and gives it back

        Assert.Equal(before + 1, Entities.Strings.MapCount);         // the ROW still owns it
        Assert.Equal(tail, Entities.Strings.Resolve(Palette.Images.Key[slot]));
        Assert.True(Palette.Images.TryGetSlot(tail.AsSpan(), out int found));
        Assert.Equal(slot, found);                                   // …and it is still ONE row, not two
    }

    /// <summary>The demand queue used to intern the full 40-character image id and drop it on <c>Drain</c> — one
    /// permanent string per artwork per run. The queue now carries SLOTS and the ROW owns its fetch id, handed back the
    /// moment an answer lands. The durable key stays: this table is process-wide on purpose.</summary>
    [Fact]
    public void An_answered_cover_hands_back_the_full_image_id_it_was_queued_with()
    {
        Entities.Now = 1_000_000;
        Span<StringId> flush = stackalloc StringId[64];
        while (Palette.Pending > 0) Palette.Drain(flush);            // start from an empty queue, whatever ran before

        string id = Id("ddddddddddddddddddddddd1");
        int before = Entities.Strings.MapCount;

        Assert.False(Palette.TryScheme(Url(id).AsSpan(), false, out _));
        Assert.Equal(before + 2, Entities.Strings.MapCount);         // the durable key AND the full id to ask with

        Span<StringId> batch = stackalloc StringId[4];
        Assert.Equal(1, Palette.Drain(batch));
        Assert.Equal(id, Entities.Strings.Resolve(batch[0]));        // the pump still gets the full id it must send

        Palette.SetGraded(id.AsSpan(), new Scheme(0xFF0D0E0F, 0, 0, 0, 0), default, hasLight: false, bestFitIsLight: false);
        Assert.Equal(before + 1, Entities.Strings.MapCount);         // the fetch id is back; the key is durable
        Assert.Equal(0u, Palette.Images.Known[Palette.Images.Slot(Palette.KeyOf(Url(id).AsSpan()))] & (uint)PaletteBits.Queued);
    }

    /// <summary>An answer that beats the drain cancels the request: the row's fetch id is already back, so the pump is
    /// never handed a cover it no longer needs to ask about. This is the case a kind-179 payload creates on every warm
    /// page — the trait lands for a cover the render path queued a frame earlier.</summary>
    [Fact]
    public void A_grading_that_beats_the_drain_takes_the_cover_out_of_the_batch()
    {
        Entities.Now = 1_000_000;
        Span<StringId> flush = stackalloc StringId[64];
        while (Palette.Pending > 0) Palette.Drain(flush);

        string early = Id("ddddddddddddddddddddddd2");
        string still = Id("ddddddddddddddddddddddd3");
        Assert.False(Palette.TryScheme(Url(early).AsSpan(), false, out _));
        Assert.False(Palette.TryScheme(Url(still).AsSpan(), false, out _));
        Assert.Equal(2, Palette.Pending);

        Palette.SetDark(Url(early).AsSpan(), new Scheme(0xFF141516, 0xFF242526, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF));

        Span<StringId> batch = stackalloc StringId[4];
        int n = Palette.Drain(batch);
        Assert.Equal(1, n);                                          // both were queued; only one still needs asking
        Assert.Equal(still, Entities.Strings.Resolve(batch[0]));
        Assert.Equal(0, Palette.Pending);
    }
}
