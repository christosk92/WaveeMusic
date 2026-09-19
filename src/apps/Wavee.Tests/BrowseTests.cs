// ── Wavee.Tests/BrowseTests.cs — one node, two spellings, and the tile/page split ────────────────────────────────
//
// Wave 1's gate for Entities/Browse.cs (plan §5). The load-bearing fact is that a genre tile and a browse page are the
// SAME row (ch 13 §7 gap 3): `spotify:genre:<id>` and `spotify:page:<id>` address one node, and folding them is what
// stops the tile's colour and the page's accent living in two places and disagreeing after a refresh.
//
// The identity facts at the bottom were added on 2026-09-12 with the packed identity
// (docs/plans/wavee/wavee-0.3-entity-identity-memory.md): a browse node is the TEXT form even though its id LOOKS
// like a gid — `page` names no catalog kind, so the gid parse declines it — and its two strings are ref-counted
// (defect 1). The lifetime fact observes the engine's contract: the LAST release removes the map entry and ids are
// never reused, so re-interning the same content afterwards mints a DIFFERENT id.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class BrowseTests
{
    static StringId Uri(string s) => Entities.Strings.Intern(s);

    // ── the uri fold ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_genre_uri_folds_onto_its_page_uri()
    {
        Span<char> buffer = stackalloc char[128];
        var folded = Browse.PageUriOf("spotify:genre:0JQ5DAqbMKFEC4WFtoNRpw".AsSpan(), buffer);
        Assert.True(folded.SequenceEqual("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw"));
    }

    [Fact]
    public void Everything_that_is_not_a_genre_passes_straight_through()
    {
        // The caller routes every tile through this without a kind test, so a pass-through has to be exact.
        Span<char> buffer = stackalloc char[128];
        Assert.True(Browse.PageUriOf("spotify:page:abc".AsSpan(), buffer).SequenceEqual("spotify:page:abc"));
        Assert.True(Browse.PageUriOf("spotify:concerts".AsSpan(), buffer).SequenceEqual("spotify:concerts"));
        Assert.True(Browse.PageUriOf("".AsSpan(), buffer).SequenceEqual(""));
    }

    [Fact]
    public void An_id_too_long_for_the_buffer_is_passed_through_rather_than_thrown_at()
    {
        // This runs on the UI thread inside a drain (C1). An absurd uri is a wire problem, not a reason to take the
        // frame down.
        Span<char> tiny = stackalloc char[4];
        var input = "spotify:genre:0JQ5DAqbMKFEC4WFtoNRpw".AsSpan();
        Assert.True(Browse.PageUriOf(input, tiny).SequenceEqual(input));
    }

    [Theory]
    [InlineData("spotify:page:abc", true)]
    [InlineData("spotify:genre:abc", true)]
    [InlineData("spotify:playlist:abc", false)]
    [InlineData("wavee:browse", false)]
    public void A_browse_node_is_recognised_in_either_spelling(string uri, bool expected)
        => Assert.Equal(expected, Browse.IsNodeUri(uri.AsSpan()));

    // ── the row ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_tile_and_the_page_are_two_groups_of_one_row()
    {
        // A node can know its tile and not its page (the directory answered, nobody opened it) or its page and not its
        // tile (a deep link straight in). Both are legal, which is why they are two Known bits.
        var t = new BrowseTable();
        int slot = t.Alloc(Uri("spotify:page:0JQ5"));

        t.SetText(ref t.Title, slot, Uri("Pop"));      // the only sanctioned write to a text column (defect 1)
        t.Color[slot] = 0xFF1DB954;
        t.Applied(slot, (uint)BrowseFields.Identity, Authority.Full, ref t.IdentityAuthority);

        Assert.True(t.Knows(slot, (uint)BrowseFields.Identity));
        Assert.False(t.Knows(slot, (uint)BrowseFields.Page));

        t.Accent[slot] = 0xFF101010;
        t.TotalSections[slot] = 6;
        t.Applied(slot, (uint)BrowseFields.Page, Authority.Full, ref t.PageAuthority);
        Assert.True(t.Knows(slot, (uint)BrowseFields.All));
    }

    [Fact]
    public void The_tile_colour_and_the_page_accent_are_different_values()
    {
        // The server grades the directory tile and the page header separately; folding them into one column is how a
        // refresh makes one of them wrong.
        var t = new BrowseTable();
        int slot = t.Alloc(Uri("spotify:page:0JQ5"));
        t.Color[slot] = 0xFF1DB954;
        t.Accent[slot] = 0xFF101010;
        Assert.NotEqual(t.Color[slot], t.Accent[slot]);
    }

    [Fact]
    public void A_client_feature_tile_is_flagged_so_it_never_pushes_a_browse_route()
    {
        var t = new BrowseTable();
        int slot = t.Alloc(Uri("spotify:page:live-events"));
        t.Flags[slot] = (uint)BrowseFlags.ClientFeature;
        Assert.Equal((uint)BrowseFlags.ClientFeature, t.Flags[slot] & (uint)BrowseFlags.ClientFeature);
    }

    [Fact]
    public void Every_column_grows_with_the_table()
    {
        var t = new BrowseTable();
        int slot = 0;
        for (int i = 0; i < 80; i++) slot = t.Alloc(Uri($"spotify:page:{i}"));
        t.NextSectionOffset[slot] = SectionPaging.Complete;
        Assert.Equal(SectionPaging.Complete, t.NextSectionOffset[slot]);
        Assert.Equal(80, t.LiveCount);
    }

    // ── the band taxonomy ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_band_order_is_the_enums_declaration_order_and_more_is_last()
    {
        // The wire has no grouping at all: these six bands and their order are a product decision, and an unmapped
        // category falls into More rather than into nothing.
        Assert.Equal(0, (int)BrowseGroup.Top);
        Assert.Equal(1, (int)BrowseGroup.ForYou);
        Assert.Equal(2, (int)BrowseGroup.Genres);
        Assert.Equal(3, (int)BrowseGroup.MoodActivity);
        Assert.Equal(4, (int)BrowseGroup.Charts);
        Assert.Equal(5, (int)BrowseGroup.More);
    }

    // ── identity: one node, one row, and it is the TEXT form ────────────────────────────────────────

    [Fact]
    public void The_fold_makes_the_tile_and_the_page_literally_one_row()
    {
        // The uri fold is pinned above; this pins that it reaches the TABLE, which is where the disagreement the fold
        // exists to prevent would actually live.
        var t = new BrowseTable();
        Span<char> buffer = stackalloc char[128];
        int viaGenre = t.Slot(Browse.PageUriOf("spotify:genre:0JQ5DAqbMKFEC4WFtoNRpw".AsSpan(), buffer));
        int viaPage = t.Slot("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw".AsSpan());

        Assert.Equal(viaGenre, viaPage);
        Assert.Equal(1, t.LiveCount);
        Assert.Equal("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", t.Id[viaPage].Text);
    }

    [Fact]
    public void A_browse_id_that_looks_like_a_gid_is_still_the_text_form()
    {
        // `0JQ5DAqbMKFEC4WFtoNRpw` IS 22 base62 characters — but `page` is not one of the six kinds the metadata
        // transport addresses by gid, so the packed identity declines it and the row keeps its uri string. Pinned
        // because "22 characters" is the tempting shortcut and it is the wrong test.
        var t = new BrowseTable();
        int slot = t.Slot("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw".AsSpan());

        Assert.Equal(EntityForm.Text, t.Id[slot].Form);
        Assert.Equal(EntityKind.Unknown, t.Id[slot].Kind);
        Assert.Equal(EntityProvider.Spotify, t.Id[slot].Provider);
        Assert.Equal(1, t.TextRows);
        Assert.Equal(0, t.IndexedRows);
    }

    // ── defect 1: the node owns its text, and gives it back ───────────────────────────────────────

    [Fact]
    public void A_retired_directory_hands_back_every_tile_string_it_holds()
    {
        // ~70 tiles per directory listing, per scope, for the life of the process was the shape of the leak.
        var t = new BrowseTable();
        StringId uri = Uri("spotify:page:BrowseTests-retired");
        int slot = t.Alloc(uri);
        StringId title = Uri("BrowseTests/retired/title");
        StringId image = Uri("BrowseTests/retired/image");
        t.SetText(ref t.Title, slot, title);
        t.SetText(ref t.Image, slot, image);

        t.ReleaseAllText();                                          // what Entities.Switch does to a dead scope

        Assert.NotEqual(uri, Uri("spotify:page:BrowseTests-retired"));
        Assert.NotEqual(title, Uri("BrowseTests/retired/title"));
        Assert.NotEqual(image, Uri("BrowseTests/retired/image"));
        Assert.Equal(0, t.TextRows);
    }
}
