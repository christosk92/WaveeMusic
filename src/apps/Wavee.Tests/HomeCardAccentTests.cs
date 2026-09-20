// ── Wavee.Tests/HomeCardAccentTests.cs — a card's identity colour, the ONE derivation (Wave 5, owner P) ─────────────
//
// 0.2.9 `HomeCards.RawAccent / Accent / SpineFallback / SpineAccent / AccentOrChrome` (HomeCards.cs:35-88), which had no
// tests. The ladder: the payload accent (`extractedColors.colorDark`) → the graded cover's BackgroundTintedBase, else
// BackgroundBase → NOTHING. The hash palette only re-hues a REAL near-neutral seed; it never invents a colour for a card
// that has none. Theme-dependent hairlines are compared against the same `Design.Palette` call under the same theme
// state, never against literal pixels (no `Tok.Theme` mutation, as DesignTests does).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class HomeCardAccentTests
{
    const uint Green = 0xFF2E7D32;          // a saturated payload / grading
    const uint Teal = 0xFF14485C;
    const uint Grey = 0xFF808080;           // saturation 0 — a near-neutral seed

    static readonly Scheme Graded = new(BackgroundBase: 0xFF1F2F5C, BackgroundTintedBase: Teal, TextBase: 0xFFFFFFFF,
        TextSubdued: 0xFFB3B3B3, TextBrightAccent: 0xFFFFFFFF);

    static HomeCard CardOf(string uri, uint accent = 0, string? image = null)
        => HomeFixtures.Card(HomeFixtures.Playlist(uri, "Card " + uri, accent: accent, image: image));

    static Func<string?, Scheme?> Answer(Scheme? scheme, List<string?>? asked = null)
        => url => { asked?.Add(url); return scheme; };

    [Fact]
    public void Raw_is_the_payload_accent_alone()
    {
        TestScope.Fresh();
        Assert.Equal(Design.Palette.ToColor(Green), HomeCardAccent.Raw(CardOf("spotify:playlist:acc-raw", Green)));
        Assert.Null(HomeCardAccent.Raw(CardOf("spotify:playlist:acc-raw-none", image: "https://i.example/raw.jpg")));
    }

    [Fact]
    public void The_payload_accent_wins_over_a_landed_grading()
    {
        TestScope.Fresh();
        var card = CardOf("spotify:playlist:acc-payload", Green, "https://i.example/payload.jpg");
        var asked = new List<string?>();
        Assert.Equal(Design.Palette.Lift(Design.Palette.ToColor(Green)), HomeCardAccent.Accent(card, Answer(Graded, asked)));
        Assert.Empty(asked);                                       // resolved before the plane is ever consulted
    }

    [Fact]
    public void Without_a_payload_the_graded_cover_speaks_through_its_tinted_base()
    {
        TestScope.Fresh();
        var card = CardOf("spotify:playlist:acc-graded", image: "https://i.example/graded.jpg");
        var asked = new List<string?>();
        Assert.Equal(Design.Palette.Lift(Design.Palette.ToColor(Teal)), HomeCardAccent.Accent(card, Answer(Graded, asked)));
        Assert.Equal("https://i.example/graded.jpg", Assert.Single(asked));   // keyed by the card's own artwork
    }

    [Fact]
    public void A_grading_with_no_tinted_base_falls_back_to_its_base()
    {
        TestScope.Fresh();
        var card = CardOf("spotify:playlist:acc-base", image: "https://i.example/base.jpg");
        var untinted = Graded with { BackgroundTintedBase = 0u };
        Assert.Equal(Design.Palette.ToColor(untinted.BackgroundBase), HomeCardAccent.Seed(card, Answer(untinted)));
    }

    [Fact]
    public void No_payload_and_no_grading_is_NOTHING_and_the_chrome_accent_only_for_chrome()
    {
        TestScope.Fresh();
        var card = CardOf("spotify:playlist:acc-none", image: "https://i.example/none.jpg");
        Assert.Null(HomeCardAccent.Accent(card, Answer(null)));
        Assert.Null(HomeCardAccent.SpineAccent(card, false, Answer(null)));
        Assert.Null(HomeCardAccent.Seed(card, Answer(default(Scheme))));        // an empty grading is not a colour
        Assert.Equal(Tok.AccentDefault, HomeCardAccent.AccentOrChrome(card, Tok.AccentDefault, Answer(null)));
    }

    [Fact]
    public void A_card_with_no_artwork_is_never_asked()
    {
        TestScope.Fresh();
        var card = CardOf("spotify:playlist:acc-noart");
        var asked = new List<string?>();
        Assert.Null(HomeCardAccent.Accent(card, Answer(Graded, asked)));
        Assert.Empty(asked);
    }

    [Fact]
    public void AccentOrChrome_prefers_the_identity_colour()
    {
        TestScope.Fresh();
        var card = CardOf("spotify:playlist:acc-chrome", Green);
        Assert.Equal(Design.Palette.Lift(Design.Palette.ToColor(Green)),
            HomeCardAccent.AccentOrChrome(card, Tok.AccentDefault, Answer(null)));
    }

    [Fact]
    public void A_saturated_seed_is_the_contrast_solved_hairline_of_itself()
    {
        TestScope.Fresh();
        var card = CardOf("spotify:playlist:acc-spine", Green);
        var seed = Design.Palette.ToColor(Green);
        Assert.Equal(Design.Palette.Hairline(seed), HomeCardAccent.SpineAccent(card, false, Answer(null)));
        Assert.Equal(Design.Palette.HairlineHover(seed), HomeCardAccent.SpineAccent(card, true, Answer(null)));
    }

    [Fact]
    public void Only_a_near_neutral_seed_is_re_hued_from_the_cards_stable_hash()
    {
        TestScope.Fresh();
        var grey = CardOf("spotify:playlist:acc-grey", Grey);
        var (_, saturation, _) = Design.Palette.ToColor(Grey).ToHsv();
        Assert.True(saturation <= Design.Palette.NeutralS);

        var fallback = HomeCardAccent.SpineFallback(grey);
        Assert.Equal(Design.Palette.Hairline(fallback), HomeCardAccent.SpineAccent(grey, false, Answer(null)));
        Assert.Contains(fallback, new[] { Tok.AccentDefault, Tok.SystemFillSuccess, Tok.SystemFillCaution, Tok.SystemFillCritical });
    }

    [Fact]
    public void The_stable_hash_is_a_function_of_the_identity_alone()
    {
        TestScope.Fresh();
        var a = CardOf("spotify:playlist:acc-hash-a", Green);
        uint first = HomeCardAccent.StableHash(a);

        // The same identity read again — and read from a SECOND band with different facts — hashes identically.
        Assert.Equal(first, HomeCardAccent.StableHash(a));
        var again = CardOf("spotify:playlist:acc-hash-a", Grey);
        Assert.Equal(a.DedupeKey, again.DedupeKey);
        Assert.Equal(first, HomeCardAccent.StableHash(again));

        Assert.NotEqual(first, HomeCardAccent.StableHash(CardOf("spotify:playlist:acc-hash-b", Green)));
        Assert.Equal(2166136261u, HomeCardAccent.StableHash(HomeCard.Blank()));   // a blank card is the FNV basis
    }
}
