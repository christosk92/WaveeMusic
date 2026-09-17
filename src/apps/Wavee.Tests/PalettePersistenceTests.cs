// ── Wavee.Tests/PalettePersistenceTests.cs — the codec, the persisted bits and the two TTLs read back ──────────────
//
// Wave 1's gate for `Entities/PalettePersistence.cs` (plan §WS-C). Pure: no table, no sqlite, no clock of its own —
// every fact here is a static function answering for itself, which is why the class needs no `EntitiesCollection`.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class PalettePersistenceTests
{
    static readonly Scheme Sample = new(0xFF102030, 0xFF203040, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF);

    [Fact]
    public void A_scheme_round_trips_through_twenty_bytes()
    {
        Span<byte> buf = stackalloc byte[PalettePersistence.SchemeBytes];
        PalettePersistence.Encode(in Sample, buf);
        Scheme back = PalettePersistence.Decode(buf);

        Assert.Equal(Sample, back);
    }

    [Fact]
    public void A_short_buffer_decodes_empty_rather_than_reading_garbage()
    {
        Span<byte> buf = stackalloc byte[PalettePersistence.SchemeBytes];
        PalettePersistence.Encode(in Sample, buf);

        Scheme back = PalettePersistence.Decode(buf[..(PalettePersistence.SchemeBytes - 1)]);
        Assert.True(back.IsEmpty);
    }

    [Theory]
    [InlineData(0u, false)]                                                       // nothing known: not worth a write
    [InlineData((uint)PaletteBits.Queued, false)]                                 // queued-only: no answer yet
    [InlineData((uint)PaletteBits.Dark, true)]
    [InlineData((uint)PaletteBits.Light, true)]
    [InlineData((uint)PaletteBits.Negative, true)]
    [InlineData((uint)(PaletteBits.Dark | PaletteBits.Queued), true)]              // answered AND still asked again
    public void Only_an_answered_row_is_worth_persisting(uint known, bool expected)
        => Assert.Equal(expected, PalettePersistence.IsWorthPersisting(known));

    [Fact]
    public void Restore_mask_strips_queued_and_keeps_the_answer()
    {
        uint stored = (uint)(PaletteBits.Dark | PaletteBits.Light | PaletteBits.BestFitIsLight | PaletteBits.Queued);
        uint restored = PalettePersistence.RestoreMask(stored);

        Assert.Equal(0u, restored & (uint)PaletteBits.Queued);
        Assert.Equal((uint)(PaletteBits.Dark | PaletteBits.Light | PaletteBits.BestFitIsLight), restored);
    }

    [Fact]
    public void A_graded_row_is_fresh_at_179_days_and_stale_at_181()
    {
        const long ts = 1_000_000;
        uint known = (uint)PaletteBits.Dark;

        Assert.True(PalettePersistence.FreshOnLoad(known, ts, ts + 179L * 24 * 60 * 60));
        Assert.False(PalettePersistence.FreshOnLoad(known, ts, ts + 181L * 24 * 60 * 60));
    }

    [Fact]
    public void A_negative_row_is_fresh_at_six_days_and_stale_at_eight()
    {
        const long ts = 1_000_000;
        uint known = (uint)PaletteBits.Negative;

        Assert.True(PalettePersistence.FreshOnLoad(known, ts, ts + 6L * 24 * 60 * 60));
        Assert.False(PalettePersistence.FreshOnLoad(known, ts, ts + 8L * 24 * 60 * 60));
    }

    [Fact]
    public void A_queued_only_row_never_reads_back_as_fresh()
        => Assert.False(PalettePersistence.FreshOnLoad((uint)PaletteBits.Queued, 1_000_000, 1_000_001));

    [Fact]
    public void The_load_cutoff_is_the_hit_ttl_behind_now()
    {
        const long now = 5_000_000;
        Assert.Equal(now - Palette.HitTtlSeconds, PalettePersistence.LoadCutoffUnix(now));
    }
}
