// ── Wavee.Tests/TrackFormatTests.cs — the shared track formats, the exact ring tables, not-yet-out, the row metrics ──
//
// Wave 4.5's NEW gate (ch 01 §8, ch 04 §8) for three things 0.2.9 never pinned directly:
//
//   THE TWO RINGS ARE EXACT TABLES. `TrackTable.Key` / `.Camelot` are one-based bytes written by the decoder
//   (`Spotify.Decode.KeyCode` / `CamelotCode`, DecodeTests pins the encoding). `Track.Format.KeyLabel(byte)` /
//   `CamelotLabel(byte)` must be their exact inverses — every value round-trips through the decoder here, so a table
//   that drifts off by one ring position fails instead of printing "G" beside an F# swatch.
//
//   THE ROW'S NUMBER FORMATS, with the clock INJECTED: the compact plays label, the duration cell's dash rule, the
//   relative → absolute date-added ladder and the not-yet-out release date. Dates are built from LOCAL wall-clock noons
//   (the formats convert in TimeZoneInfo.Local), so the facts hold in any zone; culture is pinned invariant.
//
//   `RowMetrics.ArtCentreIndent` / `DrawerIndent` — "untested — extract and test" (ch 01 §8): the drawer's rail lands
//   on the ARTWORK CENTRE, derived from the same lane table the grid is built from.
//
// Pure: no scope. Loc strings are compared against the same `Loc.Get` / `Strings.*` call, never an English literal.

using System.Globalization;
using System.Text;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Xunit;
using Format = Wavee.Track.Format;
using RowMetrics = Wavee.Track.RowMetrics;

namespace Wavee.Tests;

public class TrackFormatTests
{
    /// <summary>Pins the current culture for one fact and hands it back (the plays and date formats read it).</summary>
    sealed class InvariantCultureScope : IDisposable
    {
        readonly CultureInfo _saved = CultureInfo.CurrentCulture;
        public InvariantCultureScope() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        public void Dispose() => CultureInfo.CurrentCulture = _saved;
    }

    static long LocalNoon(int year, int month, int day)
        => new DateTimeOffset(new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds();

    // ── the two rings ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void KeyLabel_round_trips_every_pitch_class_through_the_decoder()
    {
        for (int code = 1; code <= 12; code++)
        {
            string? label = Format.KeyLabel((byte)code);
            Assert.NotNull(label);
            Assert.Equal(code, Spotify.Decode.KeyCode(Encoding.UTF8.GetBytes(label)));
        }
    }

    [Fact]
    public void KeyLabel_spells_the_ring_from_C_with_sharps()
    {
        var spelled = new string?[12];
        for (int code = 1; code <= 12; code++) spelled[code - 1] = Format.KeyLabel((byte)code);

        Assert.Equal(new string?[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }, spelled);
    }

    /// <summary>The column's zero is UNKNOWN — a label for it would make every tempo-but-no-key track read "C".</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(255)]
    public void KeyLabel_is_null_off_the_ring(int code)
        => Assert.Null(Format.KeyLabel((byte)code));

    [Fact]
    public void CamelotLabel_round_trips_every_wheel_slot_through_the_decoder()
    {
        for (int code = 1; code <= 24; code++)
        {
            string? label = Format.CamelotLabel((byte)code);
            Assert.NotNull(label);
            Assert.Equal(code, Spotify.Decode.CamelotCode(Encoding.UTF8.GetBytes(label)));
        }
    }

    [Theory]
    [InlineData(1, "1A")]
    [InlineData(2, "1B")]
    [InlineData(15, "8A")]
    [InlineData(16, "8B")]
    [InlineData(22, "11B")]
    [InlineData(23, "12A")]
    [InlineData(24, "12B")]
    public void CamelotLabel_is_one_based_minor_ring_first(int code, string expected)
        => Assert.Equal(expected, Format.CamelotLabel((byte)code));

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(255)]
    public void CamelotLabel_is_null_off_the_wheel(int code)
        => Assert.Null(Format.CamelotLabel((byte)code));

    /// <summary>DecodeTests' own fixture ("F#", "11B") reads back as the wire spelled it, and a FLAT folds onto its sharp
    /// because the ring is the same ring — the label is always the sharp spelling.</summary>
    [Fact]
    public void The_decoders_fixture_reads_back_as_the_wire_spelled_it_and_flats_fold_onto_sharps()
    {
        Assert.Equal("F#", Format.KeyLabel(Spotify.Decode.KeyCode("F#"u8)));
        Assert.Equal("11B", Format.CamelotLabel(Spotify.Decode.CamelotCode("11B"u8)));
        Assert.Equal("F#", Format.KeyLabel(Spotify.Decode.KeyCode("Gb"u8)));
        Assert.Equal("B", Format.KeyLabel(Spotify.Decode.KeyCode("Cb"u8)));
    }

    // ── the row's number formats ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE compact stream count. Plays is 52 DIP because "1.85B" is the widest thing it ever holds.</summary>
    [Theory]
    [InlineData(1_850_000_000L, "1.85B")]
    [InlineData(11_800_000L, "11.8M")]
    [InlineData(654_800L, "654.8K")]
    [InlineData(1_000L, "1K")]
    [InlineData(999L, "999")]
    [InlineData(0L, "0")]
    public void PlaysLabel_is_the_one_compact_count(long plays, string expected)
    {
        using var _ = new InvariantCultureScope();
        Assert.Equal(expected, Format.PlaysLabel(plays));
    }

    /// <summary>Unknown is an em dash, never a zero: 0 ms is "not known yet", never a zero-second track.</summary>
    [Theory]
    [InlineData(0L, "—")]
    [InlineData(-1L, "—")]
    [InlineData(59_000L, "0:59")]
    [InlineData(214_000L, "3:34")]
    [InlineData(7_199_000L, "1:59:59")]
    public void DurationCell_dashes_an_unknown_length(long ms, string expected)
    {
        Assert.Equal(expected, Format.DurationCell(ms));
        Assert.Equal(Format.Dash, Track.Facts.Dash);                  // one em dash, one meaning, row and drawer
    }

    /// <summary>The Tempo lane renders EMPTY until kind 222 lands — a dash would flicker to a value a moment later.</summary>
    [Theory]
    [InlineData(0, "")]
    [InlineData(1284, "128.4")]
    [InlineData(1280, "128")]
    [InlineData(1711, "171.1")]
    public void TempoLabel_reads_the_x10_column_and_is_empty_until_known(int tempoX10, string expected)
        => Assert.Equal(expected, Format.TempoLabel((ushort)tempoX10));

    [Fact]
    public void TotalTime_is_hours_and_minutes_and_never_zero_minutes()
    {
        Assert.Equal(Strings.Detail.DurationHrMin(2, 59), Format.TotalTime((2 * 60 + 59) * 60_000L + 30_000L));
        Assert.Equal(Strings.Detail.DurationMin(47), Format.TotalTime(47 * 60_000L));
        Assert.Equal(Strings.Detail.DurationMin(1), Format.TotalTime(0));
    }

    // ── dates, with the clock injected ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DateAddedLabel_is_empty_for_no_stamp()
        => Assert.Equal("", Format.DateAddedLabel(0, LocalNoon(2026, 9, 13)));

    /// <summary>Relative for the last week, counted in LOCAL calendar days; a future stamp (clock skew) is "Today".</summary>
    [Fact]
    public void DateAddedLabel_is_relative_for_the_last_week()
    {
        long now = LocalNoon(2026, 9, 13);

        Assert.Equal(Loc.Get(Strings.Detail.Today), Format.DateAddedLabel((int)now, now));
        Assert.Equal(Loc.Get(Strings.Detail.Today), Format.DateAddedLabel((int)LocalNoon(2026, 9, 14), now));
        Assert.Equal(Loc.Get(Strings.Detail.Yesterday), Format.DateAddedLabel((int)LocalNoon(2026, 9, 12), now));
        Assert.Equal(Strings.Detail.DaysAgo(3), Format.DateAddedLabel((int)LocalNoon(2026, 9, 10), now));
        Assert.Equal(Strings.Detail.DaysAgo(6), Format.DateAddedLabel((int)LocalNoon(2026, 9, 7), now));
    }

    /// <summary>A week or more is absolute; the same calendar year drops the year so the 88-DIP lane stays readable.</summary>
    [Fact]
    public void DateAddedLabel_is_absolute_past_a_week_and_names_the_year_only_across_one()
    {
        using var _ = new InvariantCultureScope();
        long now = LocalNoon(2026, 9, 13);

        Assert.Equal("Sep 6", Format.DateAddedLabel((int)LocalNoon(2026, 9, 6), now));
        Assert.Equal("Jan 2", Format.DateAddedLabel((int)LocalNoon(2026, 1, 2), now));
        Assert.Equal("Dec 30, 2025", Format.DateAddedLabel((int)LocalNoon(2025, 12, 30), now));
    }

    // ── the three hot formats are cached (fluentgpu rule 13: never format a number/duration/date per recycle) ────────

    /// <summary>Pins an arbitrary culture for one fact — a CLONE of invariant with a comma decimal, so the fact holds
    /// under InvariantGlobalization too (a named culture may not exist there).</summary>
    sealed class CommaCultureScope : IDisposable
    {
        readonly CultureInfo _saved = CultureInfo.CurrentCulture;
        public CommaCultureScope()
        {
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = comma;
        }
        public void Dispose() => CultureInfo.CurrentCulture = _saved;
    }

    /// <summary>The same play count hands back the SAME string instance: thirteen rows re-rendering per publish must
    /// not mint thirteen "11.8M"s.</summary>
    [Fact]
    public void PlaysLabel_returns_the_same_instance_for_the_same_count()
    {
        using var _ = new InvariantCultureScope();

        Assert.Same(Format.PlaysLabel(11_800_000L), Format.PlaysLabel(11_800_000L));
        Assert.Same(Format.PlaysLabel(999L), Format.PlaysLabel(999L));
        Assert.NotSame(Format.PlaysLabel(11_800_000L), Format.PlaysLabel(11_900_000L));
    }

    /// <summary>The cache is keyed by the culture too: a flip re-formats instead of handing back the other culture's
    /// spelling (a test's invariant scope, a user's language change).</summary>
    [Fact]
    public void PlaysLabel_reformats_when_the_culture_moves()
    {
        string invariant, comma;
        using (new InvariantCultureScope()) invariant = Format.PlaysLabel(1_850_000_000L);
        using (new CommaCultureScope()) comma = Format.PlaysLabel(1_850_000_000L);

        Assert.Equal("1.85B", invariant);
        Assert.Equal("1,85B", comma);
        using (new InvariantCultureScope()) Assert.Equal("1.85B", Format.PlaysLabel(1_850_000_000L));
    }

    /// <summary>Bounded: the key domain (every count a user scrolls past) can be huge, so the cache never grows past the
    /// engine's <see cref="FormatCache{TKey}.Capacity"/> — it resets and keeps answering correctly.</summary>
    [Fact]
    public void PlaysLabel_cache_stays_bounded_and_correct_past_capacity()
    {
        using var culture = new InvariantCultureScope();
        int distinct = FormatCache<long>.Capacity + 64;
        for (int i = 0; i < distinct; i++) Format.PlaysLabel(i + 1);

        Assert.InRange(Format.PlaysCacheCount, 1, FormatCache<long>.Capacity);
        Assert.Equal("999", Format.PlaysLabel(999L));
        Assert.Equal("1K", Format.PlaysLabel(1_000L));
        Assert.Equal("1.85B", Format.PlaysLabel(1_850_000_000L));
        Assert.Same(Format.PlaysLabel(1_850_000_000L), Format.PlaysLabel(1_850_000_000L));
    }

    /// <summary>The duration cell rides the engine's whole-second cache: one "3:34" per length, for the row, the drawer
    /// and the facts strip alike.</summary>
    [Fact]
    public void DurationCell_returns_the_same_instance_for_the_same_length()
    {
        Assert.Same(Format.DurationCell(214_000L), Format.DurationCell(214_000L));
        Assert.Same(Format.DurationCell(214_000L), Format.DurationCell(214_999L));   // sub-second never reaches the label
        Assert.Same(Format.TrackTime(7_199_000L), Format.DurationCell(7_199_000L));
        Assert.Same(Format.Dash, Format.DurationCell(0L));
    }

    /// <summary>Keyed on (stamp, now's LOCAL day): the clock ticking through the same day is a hit, the day rolling over
    /// is a miss — and the bound holds over a long playlist's worth of stamps.</summary>
    [Fact]
    public void DateAddedLabel_is_one_instance_per_stamp_and_day_and_stays_bounded()
    {
        using var _ = new InvariantCultureScope();
        long now = LocalNoon(2026, 9, 13);
        int added = (int)LocalNoon(2026, 9, 6);

        string first = Format.DateAddedLabel(added, now);
        Assert.Equal("Sep 6", first);
        Assert.Same(first, Format.DateAddedLabel(added, now));
        Assert.Same(first, Format.DateAddedLabel(added, now + 3_600));            // later the same local day: a hit
        Assert.Equal("Sep 6", Format.DateAddedLabel(added, LocalNoon(2026, 9, 14)));
        Assert.Equal(Loc.Get(Strings.Detail.Today), Format.DateAddedLabel(added, LocalNoon(2026, 9, 6)));

        int distinct = FormatCache<(int, int)>.Capacity + 64;
        for (int i = 0; i < distinct; i++) Format.DateAddedLabel(added - i * 86_400, now);
        Assert.InRange(Format.DateAddedCacheCount, 1, FormatCache<(int, int)>.Capacity);
        Assert.Equal("Sep 6", Format.DateAddedLabel(added, now));
    }

    // ── the credit stamp: the row's artist discriminator ─────────────────────────────────────────────────────────────

    /// <summary>Deterministic and sensitive to exactly what the row prints — the billed slot, its interned name id and
    /// whether the span links — and to their order. Nothing else about an artist (a follower count landing) is in it.</summary>
    [Fact]
    public void CreditStamp_changes_only_with_what_the_credit_line_prints()
    {
        ulong a = Track.CreditStamp.Add(Track.CreditStamp.Seed, slot: 7, nameId: 120, link: true);
        ulong same = Track.CreditStamp.Add(Track.CreditStamp.Seed, slot: 7, nameId: 120, link: true);

        Assert.Equal(a, same);
        Assert.NotEqual(Track.CreditStamp.Seed, a);
        Assert.NotEqual(a, Track.CreditStamp.Add(Track.CreditStamp.Seed, 7, 121, true));    // the name landed / changed
        Assert.NotEqual(a, Track.CreditStamp.Add(Track.CreditStamp.Seed, 7, 120, false));   // the uri arrived: a link now
        Assert.NotEqual(a, Track.CreditStamp.Add(Track.CreditStamp.Seed, 8, 120, true));    // a different artist

        ulong ab = Track.CreditStamp.Add(a, 9, 300, true);
        ulong ba = Track.CreditStamp.Add(Track.CreditStamp.Add(Track.CreditStamp.Seed, 9, 300, true), 7, 120, true);
        Assert.NotEqual(ab, ba);                                                            // billing order is printed order
        Assert.NotEqual(a, ab);                                                             // a second credit is a change
    }

    /// <summary>A not-yet-out row's release date: a bare day within the year, the year once it crosses one (a bare day a
    /// year out reads as imminent).</summary>
    [Fact]
    public void ShortDate_names_the_year_only_when_the_release_is_in_another_one()
    {
        using var _ = new InvariantCultureScope();
        long now = LocalNoon(2026, 9, 13);

        Assert.Equal("4 Sep", Format.ShortDate((int)LocalNoon(2026, 9, 4), now));
        Assert.Equal("4 Sep 2027", Format.ShortDate((int)LocalNoon(2027, 9, 4), now));
    }

    /// <summary>The chart header states WHEN the chart rolled over — always absolute, never "3 days ago".</summary>
    [Fact]
    public void ChartUpdatedDateLabel_is_always_absolute()
    {
        using var _ = new InvariantCultureScope();
        long now = LocalNoon(2026, 9, 13);

        Assert.Equal("", Format.ChartUpdatedDateLabel(0, now));
        Assert.Equal("Sep 12", Format.ChartUpdatedDateLabel(LocalNoon(2026, 9, 12) * 1000L, now));
        Assert.Equal("Sep 4, 2025", Format.ChartUpdatedDateLabel(LocalNoon(2025, 9, 4) * 1000L, now));
    }

    // ── not yet out ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ONE "is this row out yet?" rule. An unruled row is never pending; a ruled one is pending until its
    /// release instant passes, which heals a stale verdict with no refetch.</summary>
    [Theory]
    [InlineData(false, true, 0, false)]           // nobody ruled: never dimmed, never hidden
    [InlineData(true, false, 0, false)]           // ruled playable
    [InlineData(true, true, 0, true)]             // ruled unavailable, no instant (region-blocked stays hidden)
    [InlineData(true, true, 2_000, true)]         // announced for later
    [InlineData(true, true, 1_000, false)]        // the release moment is now: out
    [InlineData(true, true, 500, false)]          // released since the verdict was fetched
    public void NotYetOutOf_is_ruled_unavailable_before_the_release_instant(
        bool known, bool unavailable, int availableAt, bool expected)
        => Assert.Equal(expected, Track.NotYetOutOf(known, unavailable, availableAt, nowUnixSeconds: 1_000));

    // ── row metrics: the alignment invariant and the drawer's rail ───────────────────────────────────────────────────

    static Track.ColumnSet Set(int tier, bool heart, bool thumb, bool classic = false)
        => new(Album: false, By: false, Date: false, Video: false, Plays: false, Heart: heart, Thumb: thumb,
               Tier: tier, Classic: classic);

    [Theory]
    [InlineData(0, 16f, 12f)]
    [InlineData(3, 16f, 12f)]
    [InlineData(4, 12f, 12f)]
    [InlineData(5, 12f, 8f)]
    [InlineData(6, 8f, 8f)]
    public void Padding_and_gap_tighten_with_the_tier(int tier, float padX, float gap)
    {
        Assert.Equal(padX, RowMetrics.PadXFor(tier));
        Assert.Equal(gap, RowMetrics.ColGapFor(tier));
    }

    /// <summary>The grid constants are the design tokens and the lane table — one number each, never a second copy.</summary>
    [Fact]
    public void Row_constants_are_the_tokens_and_the_lane_table()
    {
        Assert.Equal(Spacing.M, RowMetrics.ColGap);
        Assert.Equal(Spacing.L, RowMetrics.PadX);
        Assert.Equal(Spacing.S, RowMetrics.RowInset);
        Assert.Equal(Track.Lane.Thumb, RowMetrics.ThumbSize);
        Assert.Equal(Track.Lane.Heart, RowMetrics.HeartCol);
        Assert.Equal(RowMetrics.HeaderHeight, Track.TableRules.HeaderHeightFor(classic: false));
        for (int density = 0; density <= 3; density++)
        {
            Assert.Equal(Track.TableRules.RowHeightFor(density, classic: false), RowMetrics.RowHeightFor(density));
            Assert.Equal(Track.TableRules.ArtSizeFor(density, classic: false), RowMetrics.ArtSizeFor(density));
        }
    }

    [Fact]
    public void ColumnSet_defaults_keep_the_overflow_lane_and_tier_zero()
    {
        var set = new Track.ColumnSet(false, false, false, false, false, false, false);

        Assert.True(set.Actions);
        Assert.Equal(0, set.Tier);
        Assert.False(set.Tempo || set.Expand || set.Artist || set.Classic);
    }

    /// <summary>BPM·Key shows only when the surface asks AND the tier admits it — the row and the tracks read this one gate.</summary>
    [Theory]
    [InlineData(true, 3, true)]
    [InlineData(true, 4, false)]
    [InlineData(false, 0, false)]
    public void ShowTempo_is_the_wish_and_the_tier(bool tempo, int tier, bool expected)
        => Assert.Equal(expected, RowMetrics.ShowTempo(new Track.ColumnSet(false, false, false, false, false, false, false,
                                                                           Tier: tier, Tempo: tempo)));

    /// <summary>The rail drops from the ART CENTRE: Modern pays <c>PadX − RowInset</c> (its skin margin supplies the rest),
    /// then # (28), then ♥ (gap + 28), then half the art. The old hard-coded 52 landed mid-♥.</summary>
    [Fact]
    public void ArtCentreIndent_lands_on_the_middle_of_the_modern_art()
    {
        var set = Set(tier: 0, heart: true, thumb: true);

        Assert.Equal(8f + 28f + 12f + 28f + 12f + 16f, RowMetrics.ArtCentreIndent(set, 32f));
        Assert.Equal(104f, RowMetrics.ArtCentreIndent(set, 32f));
        Assert.Equal(97f, RowMetrics.DrawerIndent(set, 32f));          // the rail's own 7-DIP offset comes back off
    }

    /// <summary>Classic has no skin margin (the grid pays the full PadX) and no thumb lane, so the rail lands one gap past ♥.</summary>
    [Fact]
    public void ArtCentreIndent_for_classic_pays_the_full_padding_and_stops_past_the_heart()
    {
        var set = Set(tier: 0, heart: true, thumb: false, classic: true);

        Assert.Equal(96f, RowMetrics.ArtCentreIndent(set, 32f));
        Assert.Equal(89f, RowMetrics.DrawerIndent(set, 32f));
    }

    /// <summary>DERIVED, not constant: the tier's padding and gap, which lanes survived, and the art size all move it.</summary>
    [Fact]
    public void ArtCentreIndent_follows_the_tier_the_lanes_and_the_art_size()
    {
        // Tier 4, Cozy/Comfortable art: pad 12 − 8, # 28, ♥ 12 + 28, half of 48.
        Assert.Equal(108f, RowMetrics.ArtCentreIndent(Set(tier: 4, heart: true, thumb: true), 48f));
        Assert.Equal(101f, RowMetrics.DrawerIndent(Set(tier: 4, heart: true, thumb: true), 48f));

        // Tier 6 (relief took ♥ and the art): pad 8 − 8, # 28, one 8-DIP gap.
        Assert.Equal(36f, RowMetrics.ArtCentreIndent(Set(tier: 6, heart: false, thumb: false), 32f));
        Assert.Equal(29f, RowMetrics.DrawerIndent(Set(tier: 6, heart: false, thumb: false), 32f));

        // A bigger cover moves the rail by half the growth; dropping ♥ pulls it back by exactly its lane plus a gap.
        var wide = Set(tier: 0, heart: true, thumb: true);
        Assert.Equal(8f, RowMetrics.ArtCentreIndent(wide, 48f) - RowMetrics.ArtCentreIndent(wide, 32f));
        Assert.Equal(RowMetrics.ColGapFor(0) + RowMetrics.HeartCol,
            RowMetrics.ArtCentreIndent(wide, 32f) - RowMetrics.ArtCentreIndent(wide with { Heart = false }, 32f));
    }
}
