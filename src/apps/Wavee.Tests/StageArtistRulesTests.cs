// ── Wavee.Tests/StageArtistRulesTests.cs — the stage Artist pane's pure decisions (Stage.ArtistRules) ─────────────────
//
// The column split, the header height, the reading measure, the listener line, credits grouped by person, and the fit
// counts for avatars and gallery tiles. All pure: `Stage.ArtistRules` is System-only (no Element, no signal, no entity
// read), so these drive the real arithmetic the pane lays itself out with.

using System.Globalization;
using Wavee;
using Xunit;

using R = Wavee.Stage.ArtistRules;

namespace Wavee.Tests;

public class StageArtistRulesTests
{
    static T WithCulture<T>(CultureInfo culture, Func<T> read)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try { return read(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── the layout ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1500f, true)]
    [InlineData(1100f, true)]
    [InlineData(1099f, false)]
    [InlineData(700f, false)]
    public void Two_columns_from_1100_dip(float paneW, bool expected) => Assert.Equal(expected, R.TwoColumns(paneW));

    [Fact]
    public void The_header_is_three_tenths_of_the_pane_clamped()
    {
        Assert.Equal(270f, R.HeaderHeight(900f, 1f));       // 0.3 × 900
        Assert.Equal(R.HeaderMin, R.HeaderHeight(400f, 1f)); // floor
        Assert.Equal(R.HeaderMax, R.HeaderHeight(2400f, 1f)); // ceiling
        Assert.Equal(300f, R.HeaderHeight(800f, 1.5f));     // the floor grows with the type scale: 200 × 1.5
        Assert.True(R.HeaderHeight(100f, 10f) <= R.HeaderMax); // and never past the ceiling
    }

    [Fact]
    public void The_reading_measure_is_about_68_characters()
    {
        Assert.Equal(612f, R.ReadingMeasure(18f));
        Assert.Equal(918f, R.ReadingMeasure(27f));
    }

    [Fact]
    public void The_left_column_holds_the_measure_between_45_and_60_percent()
    {
        // 1500 pane, 32 gap → a 1468 row: 45 % = 660.6, 60 % = 880.8
        Assert.Equal(660f, R.LeftColumnW(1500f, 32f, 612f));   // a short measure is lifted to 45 %
        Assert.Equal(700f, R.LeftColumnW(1500f, 32f, 700f));   // inside the band it is the measure
        Assert.Equal(880f, R.LeftColumnW(1500f, 32f, 1200f));  // a long measure stops at 60 %
        Assert.Equal(0f, R.LeftColumnW(10f, 32f, 612f));       // a degenerate pane never goes negative
    }

    // ── the listener line ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Listener_counts_are_compact()
    {
        var inv = CultureInfo.InvariantCulture;
        Assert.Equal("7.9M", WithCulture(inv, () => R.ListenerCount(7_912_345)));
        Assert.Equal("1.2B", WithCulture(inv, () => R.ListenerCount(1_234_000_000)));
        Assert.Equal("654.8K", WithCulture(inv, () => R.ListenerCount(654_800)));
        Assert.Equal("999", WithCulture(inv, () => R.ListenerCount(999)));
    }

    [Fact]
    public void The_meta_line_joins_only_what_is_there()
    {
        Assert.Equal("7.9M monthly listeners · #312 in the world", R.MetaLine("7.9M monthly listeners", "#312 in the world"));
        Assert.Equal("7.9M monthly listeners", R.MetaLine("7.9M monthly listeners", ""));
        Assert.Equal("#312 in the world", R.MetaLine("", "#312 in the world"));
        Assert.Equal("", R.MetaLine("", ""));
    }

    [Fact]
    public void Read_more_shows_only_when_the_bio_overflows_its_lines()
    {
        // 612 wide at 18 → 68 characters a line → six lines hold 408
        Assert.False(R.BioClamps(400, 612f, 18f, 6));
        Assert.True(R.BioClamps(409, 612f, 18f, 6));
        Assert.False(R.BioClamps(0, 612f, 18f, 6));
        Assert.False(R.BioClamps(5000, 612f, 18f, 0));
    }

    // ── credits grouped by person ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Credits_group_by_person_with_distinct_roles_in_wire_order()
    {
        string[] names = ["Joey Tempest", "Joey Tempest", "Kee Marcello", "joey tempest", "Kevin Elson"];
        string[] roles = ["Composer", "Lyricist", "Guitar", "composer", "Producer"];
        int[] slots = [0, 0, 0, 0, 0];
        var people = R.GroupCredits(names, roles, slots, [], [], 10);

        Assert.Equal(3, people.Length);
        Assert.Equal(new R.CreditPerson("Joey Tempest", "Composer, Lyricist", 0), people[0]);   // the repeat role folds
        Assert.Equal(new R.CreditPerson("Kee Marcello", "Guitar", 0), people[1]);
        Assert.Equal(new R.CreditPerson("Kevin Elson", "Producer", 0), people[2]);
    }

    [Fact]
    public void A_credit_links_through_its_own_target_else_a_billed_artist_of_that_name()
    {
        string[] names = ["Europe", "Joey Tempest", "Joey Tempest", "Session Player"];
        string[] roles = ["Main Artist", "Composer", "Lyricist", "Bass"];
        int[] slots = [0, 0, 42, 0];
        string[] billedNames = ["EUROPE"];
        int[] billedSlots = [7];
        var people = R.GroupCredits(names, roles, slots, billedNames, billedSlots, 10);

        Assert.Equal(7, people[0].ArtistSlot);     // no target → the billed artist, matched case-insensitively
        Assert.Equal(42, people[1].ArtistSlot);    // a later row's target still links the person
        Assert.Equal(0, people[2].ArtistSlot);     // unresolved → plain text
    }

    [Fact]
    public void Blank_names_drop_blank_roles_fold_and_the_cap_holds()
    {
        string[] names = ["", "  ", "A", "B", "C", "A"];
        string[] roles = ["X", "Y", "", "Writer", "Writer", "Producer"];
        int[] slots = [];
        var people = R.GroupCredits(names, roles, slots, [], [], 2);

        Assert.Equal(2, people.Length);
        Assert.Equal(new R.CreditPerson("A", "Producer", 0), people[0]);   // a known person past the cap still gains roles
        Assert.Equal(new R.CreditPerson("B", "Writer", 0), people[1]);
        Assert.Empty(R.GroupCredits(names, roles, slots, [], [], 0));
    }

    // ── fit counts ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Avatars_fit_the_column_capped_by_what_is_there()
    {
        Assert.Equal(5, R.FitCount(420f, 72f, 12f, 10, 8));    // (420 + 12) / 84 = 5.14
        Assert.Equal(3, R.FitCount(420f, 72f, 12f, 3, 8));     // only three related artists
        Assert.Equal(8, R.FitCount(2000f, 72f, 12f, 20, 8));   // the cap
        Assert.Equal(0, R.FitCount(0f, 72f, 12f, 10, 8));
    }

    [Fact]
    public void Gallery_tiles_split_the_width_equally()
    {
        Assert.Equal(147f, R.TileW(612f, 8f, 4));               // (612 − 24) / 4
        Assert.Equal(612f, R.TileW(612f, 8f, 1));
        Assert.Equal(0f, R.TileW(612f, 8f, 0));
    }

    [Fact]
    public void A_city_bar_is_its_share_of_the_loudest_city()
    {
        Assert.Equal(1f, R.CityFraction(500, 500));
        Assert.Equal(0.5f, R.CityFraction(250, 500));
        Assert.Equal(0f, R.CityFraction(250, 0));
    }
}
