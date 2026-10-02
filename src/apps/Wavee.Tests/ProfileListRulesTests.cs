// ── Wavee.Tests/ProfileListRulesTests.cs — the Following / Followers list pages' pure rules (Entities/Profile.Lists.cs) ──
//
// Four rules, none of which needs a scope or an engine:
//   · the LOAD rule — Unavailable > Hidden > rows ⇒ Ready > Failed > Empty > Pending, over the profile row's verdict
//     (`ProfileLoadRule.Header`) and the list's readiness (`ProfileLoadRule.List`);
//   · the ENTRIES — display order (the library's a–z filing), the All / Artists / People chip and the find box, in the pooled
//     model the page owns;
//   · the LETTER ROWS — the flat "header | row of N cards" projection the bound list scrolls over, with PINNED extents so the
//     sticky letter and the jump strip agree with what is painted;
//   · the GRID FIT — columns, cell width, the strip and the tools' stacking, and the pinned card-row extent.

using System.Globalization;
using FluentGpu.Dsl;
using Xunit;

namespace Wavee.Tests;

public class ProfileListRulesTests
{
    // ══ the load rule ═════════════════════════════════════════════════════════════════════════════════════════════════════

    static ProfileListFacts Facts(ProfileLoad header = ProfileLoad.Ready, bool followsKnown = true, bool showFollows = true,
                                  bool current = false, EdgeState readiness = EdgeState.Complete, int failure = 0, int count = 0)
        => new(header, followsKnown, showFollows, current, readiness, failure, count);

    static ProfileListLoad Of(in ProfileListFacts f) => ProfileListLoadRule.Of(in f);

    [Fact]
    public void Load_is_unavailable_for_a_sealed_profile_an_invalid_user_or_a_list_with_no_route()
    {
        // The profile row says so (an invalid user reads Unavailable too): it wins over rows and over Hidden.
        Assert.Equal(ProfileListLoad.Unavailable, Of(Facts(header: ProfileLoad.Unavailable, count: 5)));
        Assert.Equal(ProfileListLoad.Unavailable, Of(Facts(header: ProfileLoad.Unavailable, showFollows: false)));
        // The provider had no route for the list: nothing will ever answer.
        Assert.Equal(ProfileListLoad.Unavailable, Of(Facts(readiness: EdgeState.Failed, failure: EdgeTableBase.NoRoute)));
    }

    [Fact]
    public void Load_hides_another_users_lists_once_ShowFollows_is_known_false()
    {
        Assert.Equal(ProfileListLoad.Hidden, Of(Facts(showFollows: false)));
        // …even over rows an earlier visit left behind.
        Assert.Equal(ProfileListLoad.Hidden, Of(Facts(showFollows: false, count: 12)));
        // Your own profile always shows its lists; a profile whose flags have not landed is not hidden yet.
        Assert.Equal(ProfileListLoad.Ready, Of(Facts(showFollows: false, current: true, count: 12)));
        Assert.Equal(ProfileListLoad.Pending, Of(Facts(followsKnown: false, showFollows: false, readiness: EdgeState.Unknown)));
        Assert.Equal(ProfileListLoad.Ready, Of(Facts(followsKnown: false, showFollows: false, count: 3)));
    }

    [Fact]
    public void Load_is_pending_while_the_list_is_unknown_or_partial_and_empty_or_the_profile_has_not_answered()
    {
        Assert.Equal(ProfileListLoad.Pending, Of(Facts(readiness: EdgeState.Unknown)));
        Assert.Equal(ProfileListLoad.Pending, Of(Facts(header: ProfileLoad.Loading, followsKnown: false, readiness: EdgeState.Unknown)));
        Assert.Equal(ProfileListLoad.Pending, Of(Facts(readiness: EdgeState.Partial)));
        // A private profile's list answers empty too: "nobody" never shows before the row says the follows are hidden.
        Assert.Equal(ProfileListLoad.Pending, Of(Facts(header: ProfileLoad.Loading, followsKnown: false, readiness: EdgeState.Complete)));
    }

    [Fact]
    public void Load_is_failed_only_with_nothing_to_show()
    {
        Assert.Equal(ProfileListLoad.Failed, Of(Facts(readiness: EdgeState.Failed, failure: 500)));
        Assert.Equal(ProfileListLoad.Failed, Of(Facts(readiness: EdgeState.Failed, failure: EdgeTableBase.Transport)));
        // The profile row itself failed: Retry covers it, ahead of both Empty and Pending.
        Assert.Equal(ProfileListLoad.Failed, Of(Facts(header: ProfileLoad.Failed, followsKnown: false, readiness: EdgeState.Unknown)));
        Assert.Equal(ProfileListLoad.Failed, Of(Facts(header: ProfileLoad.Failed, followsKnown: false, readiness: EdgeState.Complete)));
    }

    [Fact]
    public void Load_stays_ready_when_a_refresh_fails_over_present_rows()
    {
        Assert.Equal(ProfileListLoad.Ready, Of(Facts(readiness: EdgeState.Complete, failure: 503, count: 3)));
        Assert.Equal(ProfileListLoad.Ready, Of(Facts(readiness: EdgeState.Failed, failure: 503, count: 3)));
        Assert.Equal(ProfileListLoad.Ready, Of(Facts(header: ProfileLoad.Failed, readiness: EdgeState.Partial, count: 3)));
        Assert.Equal(ProfileListLoad.Ready, Of(Facts(readiness: EdgeState.Partial, count: 1)));
    }

    [Fact]
    public void Load_is_empty_only_on_a_complete_empty_list_of_an_answered_profile()
    {
        Assert.Equal(ProfileListLoad.Empty, Of(Facts(readiness: EdgeState.Complete, count: 0)));
        Assert.NotEqual(ProfileListLoad.Empty, Of(Facts(readiness: EdgeState.Partial, count: 0)));
        Assert.NotEqual(ProfileListLoad.Empty, Of(Facts(readiness: EdgeState.Unknown, count: 0)));
        Assert.NotEqual(ProfileListLoad.Empty, Of(Facts(readiness: EdgeState.Complete, count: 2)));
    }

    [Fact]
    public void The_two_facets_map_onto_the_data_layers_shelves_and_surfaces()
    {
        Assert.Equal(ProfileShelf.Following, ProfileListFacets.ShelfOf(ProfileFacet.Following));
        Assert.Equal(ProfileShelf.Followers, ProfileListFacets.ShelfOf(ProfileFacet.Followers));
        Assert.Equal(ProfileSurface.Following, ProfileListFacets.SurfaceOf(ProfileFacet.Following));
        Assert.Equal(ProfileSurface.Followers, ProfileListFacets.SurfaceOf(ProfileFacet.Followers));
        // The selector bar: Following first, then Followers; the index round-trips.
        Assert.Equal([ProfileFacet.Following, ProfileFacet.Followers], ProfileListFacets.Order);
        Assert.Equal(0, ProfileListFacets.IndexOf(ProfileFacet.Following));
        Assert.Equal(1, ProfileListFacets.IndexOf(ProfileFacet.Followers));
        Assert.Equal(ProfileFacet.Following, ProfileListFacets.At(0));
        Assert.Equal(ProfileFacet.Followers, ProfileListFacets.At(1));
        // Only Following mixes artists and people.
        Assert.True(ProfileListFacets.HasChips(ProfileFacet.Following));
        Assert.False(ProfileListFacets.HasChips(ProfileFacet.Followers));
    }

    // ══ the entries: order, chip, find ═══════════════════════════════════════════════════════════════════════════════════

    static ProfileEntry Artist(string name, int slot = 1, int followers = 0) => new(EntityKind.Artist, slot, name, followers);
    static ProfileEntry Person(string name, int slot = 1, int followers = 0) => new(EntityKind.User, slot, name, followers);

    static ProfileListModel Model(params ProfileEntry[] entries)
    {
        var m = new ProfileListModel();
        m.Begin();
        foreach (var e in entries) m.Add(e);
        m.Seal();
        return m;
    }

    static string[] Names(ProfileListModel m, ProfileChip chip = ProfileChip.All, string? query = null)
    {
        m.Filter(chip, query);
        var visible = m.Visible.ToArray();
        return visible.Select(i => m.EntryAt(i).Name).ToArray();
    }

    static readonly ProfileEntry[] Mixed =
    [
        Person("Zed", 1), Artist("The Paper Hearts", 2), Person("ada", 3), Artist("9 lives", 4), Person("Ålborg", 5),
        Artist("Ada", 6), Person("Pablo", 7),
    ];

    [Fact]
    public void Order_files_by_letter_then_name_then_artists_first()
    {
        // "The Paper Hearts" files under P (the article is skipped for the LETTER, as in the library); "9 lives" and
        // "Ålborg" file under # and sort first; "Ada" the artist precedes "ada" the person (same name, artists first).
        Assert.Equal(["9 lives", "Ålborg", "Ada", "ada", "Pablo", "The Paper Hearts", "Zed"], Names(Model(Mixed)));
    }

    [Fact]
    public void Order_is_total_and_ends_on_the_slot()
    {
        var entries = new[] { Artist("Same", 7), Artist("Same", 3), Person("Same", 1) };
        var m = Model(entries);
        m.Filter(ProfileChip.All, null);
        Assert.Equal([3, 7, 1], m.Visible.ToArray().Select(i => m.EntryAt(i).Slot).ToArray());
    }

    [Fact]
    public void Filter_Artists_keeps_only_artists_and_People_only_people()
    {
        var m = Model(Mixed);
        Assert.Equal(["9 lives", "Ada", "The Paper Hearts"], Names(m, ProfileChip.Artists));
        Assert.Equal(["Ålborg", "ada", "Pablo", "Zed"], Names(m, ProfileChip.People));
    }

    [Fact]
    public void Filter_query_is_a_trimmed_case_insensitive_substring_of_the_name()
    {
        var m = Model(Mixed);
        Assert.Equal(["Pablo"], Names(m, ProfileChip.All, "  PAB "));
        Assert.Equal(["Ålborg"], Names(m, ProfileChip.All, "lbo"));
        Assert.Equal(["The Paper Hearts"], Names(m, ProfileChip.All, "hearts"));
        Assert.Empty(Names(m, ProfileChip.All, "nobody"));
        // The chip and the query compose.
        Assert.Equal(["Ada"], Names(m, ProfileChip.Artists, "ada"));
        Assert.Equal(["ada"], Names(m, ProfileChip.People, "ADA"));
    }

    [Fact]
    public void Filter_with_the_All_chip_and_a_blank_query_is_the_whole_ordered_list()
    {
        var m = Model(Mixed);
        var all = Names(m, ProfileChip.All, null);
        Assert.Equal(7, all.Length);
        Assert.Equal(all, Names(m, ProfileChip.All, ""));
        Assert.Equal(all, Names(m, ProfileChip.All, "   "));
        Assert.Equal(7, m.VisibleCount);
    }

    [Fact]
    public void The_visible_letters_and_artist_bits_follow_the_display_order()
    {
        var m = Model(Mixed);
        m.Filter(ProfileChip.All, null);
        // #, #, A, A, P, P, Z — non-decreasing, the grouping's one input.
        Assert.Equal([0, 0, 1, 1, 16, 16, 26], m.VisibleLetters.ToArray());
        Assert.Equal([true, false, true, false, false, true, false], m.VisibleArtists.ToArray());
        Assert.Equal(m.Visible.ToArray().Length, m.VisibleCount);
    }

    [Fact]
    public void Counts_split_by_kind()
    {
        var m = Model(Mixed);
        Assert.Equal(new ProfileChipCounts(7, 3, 4), m.Counts);
        Assert.Equal(new ProfileChipCounts(0, 0, 0), Model().Counts);
        // The counts are the LIST's, never the filter's.
        m.Filter(ProfileChip.Artists, "ada");
        Assert.Equal(new ProfileChipCounts(7, 3, 4), m.Counts);
    }

    [Fact]
    public void WordCount_appends_the_count_only_when_known_and_positive()
    {
        var c = CultureInfo.InvariantCulture;
        Assert.Equal("Following · 1,063", ProfileListFilter.WordCount("Following", 1063, known: true, c));
        Assert.Equal("Following", ProfileListFilter.WordCount("Following", 0, known: true, c));
        Assert.Equal("Following", ProfileListFilter.WordCount("Following", 1063, known: false, c));
        Assert.Equal("Followers · 7", ProfileListFilter.WordCount("Followers", 7, known: true, c));
    }

    [Fact]
    public void The_model_is_reusable_and_grows_past_its_first_buffers()
    {
        var m = new ProfileListModel();
        m.Begin();
        for (int i = 0; i < 200; i++) m.Add(Person("n" + (200 - i).ToString("000", CultureInfo.InvariantCulture), i + 1));
        m.Seal();
        m.Filter(ProfileChip.All, null);
        Assert.Equal(200, m.Count);
        Assert.Equal(200, m.VisibleCount);
        Assert.Equal("n001", m.VisibleAt(0).Name);
        Assert.Equal("n200", m.VisibleAt(199).Name);

        // A smaller second fill forgets the first entirely.
        m.Begin();
        m.Add(Artist("Only", 1));
        m.Seal();
        m.Filter(ProfileChip.All, null);
        Assert.Equal(1, m.Count);
        Assert.Equal(1, m.VisibleCount);
        Assert.Equal("Only", m.VisibleAt(0).Name);
        Assert.Equal(new ProfileChipCounts(1, 1, 0), m.Counts);
    }

    [Fact]
    public void An_unnamed_person_is_shown_by_the_id_in_their_uri()
    {
        Assert.Equal("Jane", ProfileEntryName.OrId("Jane", "spotify:user:jane"));
        Assert.Equal("jane", ProfileEntryName.OrId("", "spotify:user:jane"));
        Assert.Equal("jane", ProfileEntryName.OrId("", "jane"));
        Assert.Equal("", ProfileEntryName.OrId("", ""));
    }

    // ══ the letter rows ═══════════════════════════════════════════════════════════════════════════════════════════════════

    const float Card = 200f;
    const float Header = LibraryLetters.HeaderExtent;   // 28
    const float ActionLine = ProfileGridFit.ActionLine; // 40

    static ProfileLetterRows Rows(int[] letters, bool[]? artist = null, int columns = 1, float cardRow = Card, uint epoch = 1u)
    {
        var r = new ProfileLetterRows();
        r.Build(letters, artist ?? new bool[letters.Length], columns, cardRow, epoch);
        return r;
    }

    [Fact]
    public void Rows_open_one_header_per_letter_and_chunk_each_letter_by_columns()
    {
        // A×5 and B×1 at two columns: H(A), [0,2], [2,2], [4,1], H(B), [5,1].
        var r = Rows([1, 1, 1, 1, 1, 2], columns: 2);
        Assert.Equal(
            [
                new ProfileRowItem(1, 0, 0, false, 1u),
                new ProfileRowItem(1, 0, 2, false, 1u), new ProfileRowItem(1, 2, 2, false, 1u), new ProfileRowItem(1, 4, 1, false, 1u),
                new ProfileRowItem(2, 5, 0, false, 1u),
                new ProfileRowItem(2, 5, 1, false, 1u),
            ],
            r.Items.ToArray());
        Assert.Equal(6, r.FlatCount);
        Assert.True(r.IsHeader(0) && !r.IsHeader(1) && r.IsHeader(4) && !r.IsHeader(5));
        Assert.False(r.IsHeader(-1) || r.IsHeader(6));
    }

    [Fact]
    public void Row_extent_adds_the_action_line_only_to_rows_holding_an_artist()
    {
        // Two columns: [0,1] holds an artist, [2] does not.
        var r = Rows([1, 1, 1], [false, true, false], columns: 2);
        Assert.Equal(Header, r.ExtentOf(0));
        Assert.Equal(Card + ActionLine, r.ExtentOf(1));
        Assert.Equal(Card, r.ExtentOf(2));
        Assert.True(r.Items[1].HasArtist);
        Assert.False(r.Items[2].HasArtist);
        // The prefix sums are those extents, to the DIP: the sticky letter and the jump read them.
        Assert.Equal(0f, r.OffsetOf(0));
        Assert.Equal(Header, r.OffsetOf(1));
        Assert.Equal(Header + Card + ActionLine, r.OffsetOf(2));
        Assert.Equal(Header + 2 * Card + ActionLine, r.OffsetOf(3));
        // Out of range: a header's extent, never a throw.
        Assert.Equal(Header, r.ExtentOf(99));
        Assert.Equal(r.OffsetOf(3), r.OffsetOf(99));
    }

    [Fact]
    public void HeaderFlat_resolves_through_JumpIndex_and_absent_letters_answer_minus_one()
    {
        var r = Rows([1, 1, 3]);   // H(A) R R H(C) R
        Assert.Equal(0, r.HeaderFlat(1));
        Assert.Equal(3, r.HeaderFlat(3));
        Assert.Equal(-1, r.HeaderFlat(2));
        Assert.Equal(-1, r.HeaderFlat(0));
        Assert.Equal(-1, r.HeaderFlat(26));
        Assert.Equal(-1, r.HeaderFlat(99));
    }

    [Fact]
    public void Present_has_one_bit_per_letter_shown()
    {
        Assert.Equal((1u << 0) | (1u << 1) | (1u << 3), Rows([0, 1, 1, 3]).Present);
        Assert.Equal(0u, Rows([]).Present);
        var r = Rows([0, 1, 3]);
        Assert.True(r.Has(0) && r.Has(1) && r.Has(3));
        Assert.False(r.Has(2) || r.Has(26) || r.Has(27) || r.Has(-1));
    }

    [Fact]
    public void StickyLetterAt_follows_the_prefix_sums_across_boundaries()
    {
        // H(A) 0–28 · R 28–228 · R 228–428 · H(B) 428–456 · R 456–656
        var r = Rows([1, 1, 2]);
        Assert.Equal(-1, r.StickyLetterAt(-1f));
        Assert.Equal(1, r.StickyLetterAt(0f));
        Assert.Equal(1, r.StickyLetterAt(27f));
        Assert.Equal(1, r.StickyLetterAt(427.9f));
        Assert.Equal(2, r.StickyLetterAt(428f));
        Assert.Equal(2, r.StickyLetterAt(1000f));
        Assert.Equal(-1, Rows([]).StickyLetterAt(0f));
    }

    [Fact]
    public void Key_tracks_geometry_not_the_epoch()
    {
        var a = Rows([1, 1, 2], [false, true, false], columns: 2, epoch: 1u);
        var b = Rows([1, 1, 2], [false, true, false], columns: 2, epoch: 9u);
        Assert.Equal(a.Key(), b.Key());                                            // a data landing is not a remount
        Assert.NotEqual(a.Key(), Rows([1, 1, 2], [false, true, false], columns: 1).Key());   // columns re-chunk the rows
        Assert.NotEqual(a.Key(), Rows([1, 1, 2], [false, false, false], columns: 2).Key());  // an artist row is taller
        Assert.NotEqual(a.Key(), Rows([1, 2, 2], [false, true, false], columns: 2).Key());   // the grouping moved
        // The epoch IS on the items: that is what re-fires a realized slot.
        Assert.Equal(1u, a.Item(0).Epoch);
        Assert.Equal(9u, b.Item(0).Epoch);
    }

    [Fact]
    public void Rows_are_reusable_and_never_crash_on_unsorted_input()
    {
        var r = new ProfileLetterRows();
        r.Build(new int[100], new bool[100], 3, Card, 1u);
        Assert.True(r.FlatCount > 30);

        // A smaller second build forgets the first; the default item answers out of range.
        r.Build([1, 2], [false, false], 1, Card, 2u);
        Assert.Equal(4, r.FlatCount);
        Assert.Equal(default, r.Item(4));

        // Letters that flip back and forth open a band per flip (the order is the model's job): the buffers must hold it.
        r.Build([2, 1, 2, 1], [false, false, false, false], 1, Card, 3u);
        Assert.Equal(8, r.FlatCount);
        Assert.Equal(2, r.HeaderFlat(1));   // the FIRST band of a repeated letter wins
        Assert.Equal(0, r.HeaderFlat(2));
    }

    [Fact]
    public void Columns_are_clamped_to_at_least_one()
    {
        var r = Rows([1, 1, 1], columns: 0);
        Assert.Equal(4, r.FlatCount);   // H + three single-card rows
    }

    // ══ the grid fit ══════════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void GridFit_columns_never_fall_below_one_once_measured()
    {
        Assert.Equal(0, ProfileGridFit.Columns(0f, 12f));
        Assert.Equal(1, ProfileGridFit.Columns(100f, 12f));
        Assert.Equal(1, ProfileGridFit.Columns(176f, 12f));
        Assert.Equal(2, ProfileGridFit.Columns(520f, 12f));
        Assert.Equal(6, ProfileGridFit.Columns(1200f, 12f));
        // Exactly N cards and N-1 gaps fit N columns.
        Assert.Equal(3, ProfileGridFit.Columns(3 * 176f + 2 * 12f, 12f));
        Assert.Equal(2, ProfileGridFit.Columns(3 * 176f + 2 * 12f - 1f, 12f));
    }

    [Fact]
    public void GridFit_cells_floor_to_whole_dips_and_never_overhang()
    {
        Assert.Equal(0f, ProfileGridFit.CellWidth(0f, 0, 12f));
        Assert.Equal(254f, ProfileGridFit.CellWidth(520f, 2, 12f));
        float w = ProfileGridFit.CellWidth(1000f, 5, 12f);
        Assert.Equal(MathF.Floor(w), w);
        Assert.True(5 * w + 4 * 12f <= 1000f);
    }

    [Fact]
    public void ShowsStrip_only_from_640_and_the_plan_takes_it_off_the_list_width()
    {
        Assert.False(ProfileGridFit.ShowsStrip(639f));
        Assert.True(ProfileGridFit.ShowsStrip(640f));

        // Unmeasured: no columns, wide tools (the arrangement most windows have).
        Assert.Equal(new ProfileGridPlan(false, false, 0, 0f), ProfileGridFit.For(0f, 12f));
        // Narrow: no strip, stacked tools, two columns of 254.
        Assert.Equal(new ProfileGridPlan(false, true, 2, 254f), ProfileGridFit.For(520f, 12f));
        // 640 exactly: the strip shows and the tools sit side by side. List = 640 − 18 − 8 = 614 → 3 columns of 196.
        Assert.Equal(new ProfileGridPlan(true, false, 3, 196f), ProfileGridFit.For(640f, 12f));
        // 1000: list = 974 → 5 columns of 185.
        Assert.Equal(new ProfileGridPlan(true, false, 5, 185f), ProfileGridFit.For(1000f, 12f));
    }

    [Fact]
    public void The_pinned_card_row_is_never_shorter_than_the_card_it_holds()
    {
        // The fluid grid card renders 8 + (w − 16) + 8 + 20 + 2 + 18 + 12 at the most = w + 52; the pin is the surface's own
        // estimate (w + 66), so the seed extent the layout used is ≥ what measures and the list never re-pins its anchor.
        foreach (float w in new[] { 176f, 196f, 200f, 254f, 400f })
        {
            Assert.True(ProfileGridFit.CardRow(w) >= w + 50f);
            Assert.Equal(w + 66f, ProfileGridFit.CardRow(w));
        }
    }

    [Fact]
    public void The_grid_constants_agree_with_their_tokens()
    {
        Assert.Equal(Spacing.S, ProfileGridFit.StripGap);
        Assert.Equal(12f, Spacing.Card);   // the plan above is asserted at this gap
        Assert.Equal(32f + Spacing.S, ProfileGridFit.ActionLine);
        Assert.Equal(LibraryLetters.HeaderExtent, ProfileLetterRows.HeaderExtent);
        Assert.Equal(176f, ProfileGridFit.MinCell);
        Assert.Equal(18f, ProfileGridFit.StripWidth);   // User.JumpStrip's own width
    }
}
