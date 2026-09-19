// ── Wavee.Tests/SearchTests.cs — the facet chip strip and the ported total rule ──────────────────────────────────
//
// Wave 1's gate for Entities/Search.cs (plan §5). The one piece of ported semantics here is subtle enough to be the
// reason this file exists: a per-facet total of -1 means THE SERVER SENT NO TOTAL, and reproducing that exactly is
// what keeps a facet tab from showing a count it invented (0.2.9 `Library.cs:92-124`).
//
// Two families were added on 2026-09-12 with the packed identity
// (docs/plans/wavee/wavee-0.3-entity-identity-memory.md):
//
//   • A HIT KNOWS ITS TABLE (defect 5). `Edges.SearchResult` was an `EdgeTable<NoEdge>` over "entity slots" with
//     nothing recording which table each slot indexed — and the All facet mixes six kinds, where album slot 4 and
//     track slot 4 are the same `int`. It carries a `KindEdge` now; the facts below pin that two same-numbered slots
//     of different kinds stay two different hits.
//
//   • THE QUERY IS REF-COUNTED (defect 1). A search uri is never a gid, so the row keeps its string, and an omnibar
//     mints a row per query. The fact below observes the engine's contract: the LAST release removes the map entry
//     and ids are never reused, so re-interning the same content afterwards mints a DIFFERENT id.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class SearchTests
{
    static StringId Uri(string s) => Entities.Strings.Intern(s);

    // ── the facet list ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_facet_order_is_the_static_fallback_order_and_is_not_free_to_change()
    {
        // With no server chipOrder the strip falls back to THIS order (0.2.9 SearchPage.cs:328-356), so the enum's
        // numbering is load-bearing and the strided chip slab indexes by it.
        Assert.Equal(0, (int)SearchFacet.All);
        Assert.Equal(1, (int)SearchFacet.Tracks);
        Assert.Equal(2, (int)SearchFacet.Albums);
        Assert.Equal(3, (int)SearchFacet.Playlists);
        Assert.Equal(10, (int)SearchFacet.Authors);
        Assert.Equal(SearchTable.FacetCount, (int)SearchFacet.Authors + 1);
    }

    // ── the total rule ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_total_from_the_server_falls_back_to_the_local_count()
    {
        Assert.Equal(7, Search.TotalFor(Search.NoTotal, localCount: 7));
        Assert.Equal(0, Search.TotalFor(Search.NoTotal, localCount: 0));
    }

    [Fact]
    public void A_real_zero_is_a_real_zero_and_wins_over_the_local_count()
    {
        // The rule never distinguishes "not queried" from "queried and empty" — it just answers the server's number.
        Assert.Equal(0, Search.TotalFor(0, localCount: 5));
        Assert.Equal(120, Search.TotalFor(120, localCount: 5));
    }

    // ── the chip skeleton ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true, true)]     // nothing has ever supplied chips and a fetch is running: 11 pills
    [InlineData(false, false, false)]   // nothing pending: no skeleton, no chips
    [InlineData(true, true, false)]     // a LATER facet-switch fetch must not re-trigger the skeleton
    [InlineData(true, false, false)]
    public void The_chip_skeleton_shows_only_before_the_first_chip_source(bool hasChipSource, bool pending, bool expected)
        => Assert.Equal(expected, Search.ShowChipSkeleton(hasChipSource, pending));

    // ── the strided chip slab ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_row_starts_at_no_total_because_an_omission_is_not_a_zero()
    {
        var t = new SearchTable();
        int slot = t.Alloc(Uri("wavee:search:00:daft punk"));
        t.ResetChips(slot);

        for (int f = 0; f < SearchTable.FacetCount; f++)
        {
            Assert.Equal(Search.NoTotal, t.ChipTotals[slot * SearchTable.FacetCount + f]);
            Assert.Equal(Search.Unranked, t.ChipRanks[slot * SearchTable.FacetCount + f]);
        }
        Assert.Equal(0, t.ChipMask[slot]);
    }

    [Fact]
    public void Two_rows_chip_strips_do_not_bleed_into_each_other()
    {
        var t = new SearchTable();
        int a = t.Alloc(Uri("wavee:search:00:a"));
        int b = t.Alloc(Uri("wavee:search:00:b"));
        t.ResetChips(a);
        t.ResetChips(b);

        t.ChipTotals[a * SearchTable.FacetCount + (int)SearchFacet.Tracks] = 42;
        Assert.Equal(42, t.ChipTotals[a * SearchTable.FacetCount + (int)SearchFacet.Tracks]);
        Assert.Equal(Search.NoTotal, t.ChipTotals[b * SearchTable.FacetCount + (int)SearchFacet.Tracks]);
    }

    [Fact]
    public void The_strided_slab_survives_the_table_growing()
    {
        // The chip columns are sized rows x FacetCount, so they are the one place a GrowColumns arithmetic slip would
        // silently corrupt a neighbouring row instead of throwing.
        var t = new SearchTable();
        int slot = 0;
        for (int i = 0; i < 64; i++) { slot = t.Alloc(Uri($"wavee:search:00:q{i}")); t.ResetChips(slot); }

        t.ChipTotals[slot * SearchTable.FacetCount + (int)SearchFacet.Authors] = 9;
        Assert.Equal(9, t.ChipTotals[slot * SearchTable.FacetCount + (int)SearchFacet.Authors]);
        Assert.Equal(64, t.LiveCount);
    }

    [Fact]
    public void A_search_row_is_a_synthetic_subject_with_no_entity_kind()
    {
        // It has a uri and a Known mask like every row, but nothing in the catalog addresses it — the kind is Unknown
        // on purpose, and the store's warm/trim walk does not carry it (see Home.cs's Scope partial).
        Assert.Equal(EntityKind.Unknown, new SearchTable().Kind);
    }

    // ── defect 5: a hit knows which table it indexes ───────────────────────────────────────────────

    [Theory]
    [InlineData(SearchFacet.Tracks, EntityKind.Track)]
    [InlineData(SearchFacet.Albums, EntityKind.Album)]
    [InlineData(SearchFacet.Playlists, EntityKind.Playlist)]
    [InlineData(SearchFacet.Podcasts, EntityKind.Show)]
    [InlineData(SearchFacet.Artists, EntityKind.Artist)]
    [InlineData(SearchFacet.Episodes, EntityKind.Episode)]
    [InlineData(SearchFacet.Profiles, EntityKind.User)]
    // The mixed facet has no single kind, and the three 0.3 has no table for must not be guessed into one.
    [InlineData(SearchFacet.All, EntityKind.Unknown)]
    [InlineData(SearchFacet.Audiobooks, EntityKind.Unknown)]
    [InlineData(SearchFacet.Genres, EntityKind.Unknown)]
    [InlineData(SearchFacet.Authors, EntityKind.Unknown)]
    public void Each_single_kind_facet_names_the_table_its_hits_live_in(SearchFacet facet, EntityKind kind)
        => Assert.Equal(kind, Search.KindOf(facet));

    [Fact]
    public void The_all_facet_records_the_table_of_every_hit()
    {
        TestScope.Fresh();
        var all = Entities.Search("SearchTests mixed".AsSpan());
        EntityRef[] hits = [new(EntityKind.Artist, 4), new(EntityKind.Track, 4), new(EntityKind.Album, 9)];
        all.ApplyResults(0, hits, hits.Length);

        Assert.Equal(3, all.ResultCount);
        Assert.Equal(hits[0], all.ResultRef(0));
        Assert.Equal(hits[1], all.ResultRef(1));
        Assert.Equal(hits[2], all.ResultRef(2));
        // The whole defect in one line: slot 4 appears twice and they are two different entities.
        Assert.NotEqual(all.ResultRef(0), all.ResultRef(1));
        Assert.Equal(EntityKind.Album, all.ResultKinds[2].Kind);
        Assert.True(all.ResultRef(99).IsNone);                     // past the end is "nothing", not a throw
    }

    [Fact]
    public void A_single_kind_facet_stamps_the_same_table_on_every_hit()
    {
        TestScope.Fresh();
        var songs = Entities.Search("SearchTests songs".AsSpan(), SearchFacet.Tracks);
        int[] hits = [4, 7, 11];
        songs.ApplyResults(0, hits, Search.KindOf(SearchFacet.Tracks), hits.Length);

        Assert.Equal(3, songs.ResultCount);
        for (int i = 0; i < hits.Length; i++)
            Assert.Equal(new EntityRef(EntityKind.Track, hits[i]), songs.ResultRef(i));
    }

    [Fact]
    public void A_later_page_lands_beside_the_first_without_forgetting_its_kinds()
    {
        TestScope.Fresh();
        var all = Entities.Search("SearchTests paged".AsSpan());
        EntityRef[] first = [new(EntityKind.Artist, 4), new(EntityKind.Track, 4)];
        EntityRef[] second = [new(EntityKind.Album, 9)];
        all.ApplyResults(0, first, total: 3);
        all.ApplyResults(2, second, total: 3);

        Assert.Equal(3, all.ResultCount);
        Assert.Equal(EdgeState.Complete, all.ResultState);
        Assert.Equal(first[0], all.ResultRef(0));
        Assert.Equal(first[1], all.ResultRef(1));
        Assert.Equal(second[0], all.ResultRef(2));
    }

    // ── defect 1: the row owns its query text ─────────────────────────────────────────────────────

    [Fact]
    public void A_retired_search_row_hands_back_its_query_and_its_uri()
    {
        // An omnibar mints a row per query, and a query row is the text form twice over — its uri AND its query
        // column. Without the release half an afternoon of searching pins every prefix the user ever typed.
        var t = new SearchTable();
        StringId uri = Uri("wavee:search:00:SearchTests-lifetime");
        int slot = t.Alloc(uri);
        t.ResetChips(slot);
        StringId query = Uri("SearchTests/lifetime/query");
        t.SetText(ref t.Query, slot, query);

        t.FreeSlot(slot);

        Assert.NotEqual(uri, Uri("wavee:search:00:SearchTests-lifetime"));
        Assert.NotEqual(query, Uri("SearchTests/lifetime/query"));
        Assert.Equal(0, t.TextRows);
    }

    [Fact]
    public void A_search_subject_is_the_text_form_and_two_facets_are_two_ids()
    {
        var t = new SearchTable();
        int all = t.Slot("wavee:search:00:daft punk".AsSpan());
        int songs = t.Slot("wavee:search:01:daft punk".AsSpan());

        Assert.NotEqual(all, songs);
        Assert.Equal(EntityForm.Text, t.Id[all].Form);
        Assert.Equal(EntityKind.Unknown, t.Id[all].Kind);
        Assert.Equal(EntityProvider.None, t.Id[all].Provider);      // nobody owns `wavee:search:` — and nothing 404s on it
        Assert.Equal("wavee:search:00:daft punk", t.Id[all].Text);
    }
}
