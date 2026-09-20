// ── Wavee.Tests/DeckLetteringTests.cs — when a cover/lettering leaf changes what it shows ─────────────────────────────
//
// `Deck.Lettering.Adopt` is the one rule behind ch 23 §0(10) (the art changes when the MECHANISM says so, not when the
// track changes), the cassette's "a same-album advance re-letters in place", and §7's deliberate 0.3 difference: a new
// item's text is adopted only once it is known, so a leaf never swaps its lettering for a blank.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DeckLetteringTests
{
    const int A1 = 101, A2 = 102, B1 = 201;       // two tracks on album A, one on album B
    const int AlbumA = 7_001, AlbumB = 7_002;

    [Fact]
    public void The_first_latch_adopts_whatever_is_playing_even_before_its_text_lands()
        => Assert.True(Deck.Lettering.Adopt(A1, AlbumA, currentKnown: false, shownRow: 0, shownAlbum: 0, generationMoved: false));

    [Fact]
    public void The_same_item_is_always_readopted_so_late_text_letters_it()
        => Assert.True(Deck.Lettering.Adopt(A1, AlbumA, currentKnown: false, A1, AlbumA, generationMoved: false));

    [Fact]
    public void A_new_album_waits_for_the_mechanism()
    {
        Assert.False(Deck.Lettering.Adopt(B1, AlbumB, currentKnown: true, A1, AlbumA, generationMoved: false));
        Assert.True(Deck.Lettering.Adopt(B1, AlbumB, currentKnown: true, A1, AlbumA, generationMoved: true));
    }

    [Fact]
    public void A_same_album_advance_reletters_in_place_without_a_generation()
        => Assert.True(Deck.Lettering.Adopt(A2, AlbumA, currentKnown: true, A1, AlbumA, generationMoved: false));

    [Fact]
    public void An_unknown_item_never_replaces_a_lettered_one()
    {
        Assert.False(Deck.Lettering.Adopt(B1, AlbumB, currentKnown: false, A1, AlbumA, generationMoved: true));
        Assert.False(Deck.Lettering.Adopt(A2, AlbumA, currentKnown: false, A1, AlbumA, generationMoved: false));
    }

    [Fact]
    public void An_unknown_album_is_never_the_same_album()
        => Assert.False(Deck.Lettering.Adopt(B1, currentAlbum: 0, currentKnown: true, A1, shownAlbum: 0, generationMoved: false));

    [Fact]
    public void Nothing_playing_keeps_what_is_shown()
        => Assert.False(Deck.Lettering.Adopt(0, 0, currentKnown: false, A1, AlbumA, generationMoved: true));
}
