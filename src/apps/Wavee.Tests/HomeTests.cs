// ── Wavee.Tests/HomeTests.cs — the three readiness gates and the section paging arithmetic ───────────────────────
//
// Wave 1's gate for Entities/Home.cs (plan §5). The three questions ch 10 §7 separates — is the PAGE ready, has the
// CHROME concluded, is this SECTION whole — are three different predicates with three different failure modes, and
// 0.2.9 got each of them wrong in a different way. All three are pure, so all three are pinned here without a scope
// (D17). The paging arithmetic is the ported `HomeSectionPaging`, whose two defects are measured rather than
// hypothetical.
//
// The LIFETIME facts at the bottom were added on 2026-09-12 with the packed identity
// (docs/plans/wavee/wavee-0.3-entity-identity-memory.md, defect 1). A Home subject, a section row and every chip are
// interned strings, and the engine reclaims one only when its last reference is released — a never-AddRef'd id is
// PERMANENT (the engine's `StringTable.cs:26`). They observe the engine's contract rather than a counter: the LAST
// release removes the map entry and ids are never reused, so re-interning the same content afterwards mints a
// DIFFERENT id. Each uses strings unique to its fact, because the interner is process-wide.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class HomeTests
{
    static StringId Uri(string s) => Entities.Strings.Intern(s);

    // ── the whole-page gate ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Warm_rows_do_not_make_the_page_ready()
    {
        // The store warms rows from sqlite at boot. Painting them and replacing them 1.5 s later is exactly the
        // regression this gate exists to prevent, so section count alone is never the answer.
        Assert.Equal(HomeState.Placeholder, Home.Classify(sectionCount: 12, concluded: false));
        Assert.Equal(HomeState.Placeholder, Home.Classify(sectionCount: 0, concluded: false));
    }

    [Fact]
    public void A_concluded_attempt_with_nothing_is_a_real_empty_and_not_a_skeleton_forever()
    {
        Assert.Equal(HomeState.Empty, Home.Classify(sectionCount: 0, concluded: true));
        Assert.Equal(HomeState.Ready, Home.Classify(sectionCount: 1, concluded: true));
    }

    [Fact]
    public void A_finished_fetch_during_a_reconnect_is_not_a_conclusion()
    {
        Assert.False(Home.Concluded(fetchInflight: true, sessionConnecting: false));
        Assert.False(Home.Concluded(fetchInflight: false, sessionConnecting: true));
        Assert.True(Home.Concluded(fetchInflight: false, sessionConnecting: false));
    }

    // ── the chrome hold ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_is_concluded_which_is_what_keeps_the_offline_launch_at_a_zero_hold()
    {
        // "Concluded" means NOTHING IS COMING. Idle = never fetched (offline, --fake). Treating it as pending is the
        // difference between a 0 ms hold and a 1,500 ms one on every offline launch.
        Assert.True(Home.ChromeConcluded(HomeLoad.Idle, hasNotifications: true, HomeLoad.Idle, HomeLoad.Idle));
    }

    [Fact]
    public void No_notification_bridge_at_all_is_concluded()
        => Assert.True(Home.ChromeConcluded(HomeLoad.Ready, hasNotifications: false, HomeLoad.Pending, HomeLoad.Pending));

    [Fact]
    public void Pending_is_the_one_state_worth_a_bounded_wait()
    {
        Assert.False(Home.ChromeConcluded(HomeLoad.Pending, hasNotifications: false, HomeLoad.Idle, HomeLoad.Idle));
        Assert.False(Home.ChromeConcluded(HomeLoad.Ready, hasNotifications: true, HomeLoad.Pending, HomeLoad.Idle));
        Assert.False(Home.ChromeConcluded(HomeLoad.Ready, hasNotifications: true, HomeLoad.Idle, HomeLoad.Pending));
    }

    [Fact]
    public void A_failed_feed_has_concluded_too()
        => Assert.True(Home.ChromeConcluded(HomeLoad.Failed, hasNotifications: true, HomeLoad.Failed, HomeLoad.Ready));

    // ── section paging: RAW vs DEDUPED ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_next_request_counts_what_the_server_sent_not_what_we_kept()
    {
        // 20 items arrived, 3 were duplicates or unsupported: the cursor is 20. Asking for 17 re-fetches what we just
        // dropped, and a page that is entirely already-seen uris never advances at all.
        Assert.Equal(20, SectionPaging.NextRequest(raw: 20, cards: 17));
    }

    [Fact]
    public void A_source_that_under_reported_its_raw_count_still_asks_past_what_is_on_screen()
        => Assert.Equal(17, SectionPaging.NextRequest(raw: 0, cards: 17));

    [Fact]
    public void Advancing_the_cursor_counts_the_whole_page_duplicates_included()
        => Assert.Equal(40, SectionPaging.Advance(raw: 20, pageItems: 20));

    // ── section paging: TERMINATION ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void With_no_cursor_the_total_is_the_arming_hint()
    {
        Assert.True(SectionPaging.HasMore(raw: 10, cards: 10, total: 50, serverNextOffset: SectionPaging.NoCursor));
        Assert.False(SectionPaging.HasMore(raw: 10, cards: 10, total: 10, serverNextOffset: SectionPaging.NoCursor));
    }

    [Fact]
    public void An_explicit_completion_terminates_even_when_the_total_still_claims_more()
    {
        // Measured: 7 of 31 sections of a captured Home disagreed with their own totalCount (8 items / total 9 /
        // nextOffset null). The cursor wins.
        Assert.False(SectionPaging.HasMore(raw: 8, cards: 8, total: 9, serverNextOffset: SectionPaging.Complete));
    }

    [Fact]
    public void A_cursor_that_equals_the_raw_position_is_still_a_cursor()
    {
        // offset 20 + 20 items -> nextOffset: 20 means "ask for 20 next", not "you are done".
        Assert.True(SectionPaging.HasMore(raw: 20, cards: 18, total: 0, serverNextOffset: 20));
    }

    [Fact]
    public void A_complete_section_answering_offset_zero_cannot_carry_us_forward()
    {
        // Measured: a complete section can answer nextOffset: 0 (6 items / total 6). Honouring it as a cursor
        // re-requests page one forever.
        Assert.False(SectionPaging.CanAdvance(requestedOffset: 0, serverNextOffset: 0));
        Assert.False(SectionPaging.CanAdvance(requestedOffset: 20, serverNextOffset: 20));
        Assert.False(SectionPaging.CanAdvance(requestedOffset: 0, serverNextOffset: SectionPaging.Complete));
        Assert.False(SectionPaging.CanAdvance(requestedOffset: 0, serverNextOffset: SectionPaging.NoCursor));
        Assert.True(SectionPaging.CanAdvance(requestedOffset: 0, serverNextOffset: 20));
    }

    // ── the section row ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_section_row_carries_the_whole_ledger_and_grows_with_the_table()
    {
        var t = new SectionTable();
        int slot = 0;
        for (int i = 0; i < 40; i++) slot = t.Alloc(Uri($"spotify:section:{i}"));

        t.SetText(ref t.Title, slot, Uri("Made for you"));      // the only sanctioned write to a text column
        t.Form[slot] = (byte)SectionKind.HomeBaseline;
        t.Raw[slot] = 20;
        t.Cards[slot] = 17;
        t.Unsupported[slot] = 2;
        t.Duplicates[slot] = 1;
        t.Total[slot] = 50;
        t.NextOffset[slot] = SectionPaging.NoCursor;

        // Raw == Cards + Unsupported + Duplicates is the ledger the paging arithmetic is checked against.
        Assert.Equal(t.Raw[slot], t.Cards[slot] + t.Unsupported[slot] + t.Duplicates[slot]);
        Assert.Equal(SectionKind.HomeBaseline, (SectionKind)t.Form[slot]);
    }

    [Fact]
    public void The_two_section_families_never_collide_on_a_code()
    {
        // One table, two composers (Home's __typename verdict and Browse's own kind). The codes are namespaced by
        // family precisely so a browse shelf can never be mistaken for a Home spotlight.
        Assert.True((byte)SectionKind.HomeRecentlyPlayed < (byte)SectionKind.BrowseShelf);
        Assert.NotEqual((byte)SectionKind.HomeGeneric, (byte)SectionKind.BrowseShelf);
    }

    [Fact]
    public void The_home_subject_is_one_row_per_facet()
    {
        var t = new HomeTable();
        int all = t.Slot(Home.FeedUri.AsSpan());
        int music = t.Slot((Home.FacetPrefix + "music").AsSpan());
        Assert.NotEqual(all, music);
        Assert.Equal(all, t.Slot(Home.FeedUri.AsSpan()));               // and switching back is the same row
    }

    [Fact]
    public void A_chip_strip_is_a_range_into_shared_slabs()
    {
        var t = new HomeTable();
        int slot = t.Slot(Home.FeedUri.AsSpan());
        t.SetChips(slot, [Uri("music"), Uri("pop")], [Uri("Music"), Uri("Pop")], [-1, 0]);

        Assert.Equal(2, t.ChipCount[slot]);
        Assert.Equal(Uri("Music"), t.ChipLabels[t.ChipStart[slot]]);
        Assert.Equal(-1, t.ChipParents[t.ChipStart[slot]]);             // top-level
        Assert.Equal(0, t.ChipParents[t.ChipStart[slot] + 1]);          // a sub-chip of the first
    }

    // ── identity: the synthetic subjects are the text form ────────────────────────────────────────────

    [Fact]
    public void A_home_subject_carries_its_own_uri_as_a_packed_text_identity()
    {
        // `wavee:` is not a provider the gid parse claims, so the subject keeps its string — which is exactly why it
        // has to be ref-counted, and why a facet switch is a row and not a mutation.
        var t = new HomeTable();
        int all = t.Slot(Home.FeedUri.AsSpan());
        int music = t.Slot((Home.FacetPrefix + "music").AsSpan());

        Assert.Equal(EntityForm.Text, t.Id[all].Form);
        Assert.Equal(Home.FeedUri, t.Id[all].Text);
        Assert.Equal(Home.FacetPrefix + "music", t.Id[music].Text);
        Assert.NotEqual(t.Id[all], t.Id[music]);
        Assert.Equal(2, t.TextRows);
        Assert.Equal(0, t.IndexedRows);
    }

    // ── defect 1: the subjects own their text, and give it back ──────────────────────────────────────

    [Fact]
    public void Rewriting_a_chip_strip_hands_the_previous_strip_back()
    {
        // The strip is rewritten whole every time the server answers, and the slab is a bump allocator that ABANDONS
        // the old range (P5). Abandoning the cells is the design; abandoning the strings was the leak.
        var t = new HomeTable();
        int slot = t.Slot(Home.FeedUri.AsSpan());
        StringId id = Uri("HomeTests/chips/music-id");
        StringId label = Uri("HomeTests/chips/Music-label");
        t.SetChips(slot, [id], [label], [-1]);

        t.SetChips(slot, [Uri("HomeTests/chips/pop-id")], [Uri("HomeTests/chips/Pop-label")], [-1]);

        Assert.NotEqual(id, Uri("HomeTests/chips/music-id"));
        Assert.NotEqual(label, Uri("HomeTests/chips/Music-label"));
        Assert.Equal(1, t.ChipCount[slot]);
        Assert.Equal("HomeTests/chips/Pop-label", Entities.Strings.Resolve(t.ChipLabels[t.ChipStart[slot]]));
    }

    [Fact]
    public void A_retired_home_subject_hands_back_its_greeting_facet_chips_and_uri()
    {
        var t = new HomeTable();
        StringId uri = Uri("wavee:home:HomeTests-retired");
        int slot = t.Alloc(uri);
        StringId greeting = Uri("HomeTests/retired/greeting");
        StringId facet = Uri("HomeTests/retired/facet");
        StringId chip = Uri("HomeTests/retired/chip");
        t.SetText(ref t.Greeting, slot, greeting);
        t.SetText(ref t.Facet, slot, facet);
        t.SetChips(slot, [chip], [chip], [-1]);

        t.ReleaseAllText();                                             // what Entities.Switch does to a dead scope

        Assert.NotEqual(uri, Uri("wavee:home:HomeTests-retired"));
        Assert.NotEqual(greeting, Uri("HomeTests/retired/greeting"));
        Assert.NotEqual(facet, Uri("HomeTests/retired/facet"));
        Assert.NotEqual(chip, Uri("HomeTests/retired/chip"));
        Assert.Equal(0, t.ChipCount[slot]);
        Assert.Equal(0, t.TextRows);
    }

    [Fact]
    public void A_freed_section_row_hands_back_both_of_its_strings()
    {
        // ~31 rows are minted per feed answer and a facet switch replaces all of them, so a column missing from
        // `SectionTable.ReleaseText` is a leak measured in feeds, not in rows.
        var t = new SectionTable();
        StringId uri = Uri("home-section:HomeTests-freed");
        int slot = t.Alloc(uri);
        StringId title = Uri("HomeTests/section/title");
        StringId subtitle = Uri("HomeTests/section/subtitle");
        t.SetText(ref t.Title, slot, title);
        t.SetText(ref t.Subtitle, slot, subtitle);

        t.FreeSlot(slot);

        Assert.NotEqual(uri, Uri("home-section:HomeTests-freed"));
        Assert.NotEqual(title, Uri("HomeTests/section/title"));
        Assert.NotEqual(subtitle, Uri("HomeTests/section/subtitle"));
        Assert.Equal(0, t.TextRows);
    }
}
