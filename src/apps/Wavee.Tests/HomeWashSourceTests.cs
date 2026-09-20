// ── Wavee.Tests/HomeWashSourceTests.cs — the shell wash's SOURCE selection (Wave 5, owner P; ported from 0.2.9) ─────────
//
// Two properties carry the whole feature: the three slots are chosen structurally (kind + ordinal — never a title), and a
// slot's colour is the server's payload accent first, the graded cover second, and NOTHING third. Cards are handles
// committed through `HomeFixtures`; the plane is the injected `Func<string?, Scheme?>`.
//
// OWNER GATING is deliberately not covered (as in 0.2.9): it lives in mounted-component effects, and a local replica would
// prove only that the replica works.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class HomeWashSourceTests
{
    // A real Spotify cover url: the artwork identity is the trailing 24 chars, which is what the wash keys on.
    const string Cover = "https://i.scdn.co/image/ab67616d0000b273e86f30ec6f14a30f1cf9bb9d";
    // The SAME artwork at another rendition — Spotify varies only the 16-char size prefix of the 40-char image id.
    const string Cover300 = "https://i.scdn.co/image/ab67616d00001e02e86f30ec6f14a30f1cf9bb9d";

    // The wire shape: the chroma lives in the background roles, textBrightAccent is ink.
    static Scheme Graded => new(0xFF101040u, 0xFF3C4478u, 0xFFFFFFFFu, 0xFFB3B3B3u, 0xFFFFFFFFu);

    static HomeCard Card(string id, uint accent = 0, string? cover = null, string? format = null)
        => HomeFixtures.Card(HomeFixtures.Playlist("spotify:playlist:" + id, id, format, accent: accent, image: cover));

    static HomeFeedView Feed(params HomeGroup[] groups) => new("", groups);

    [Fact]
    public void PayloadAccent_WinsOverTheGradedPlane_AndKeysOnTheArtwork()
    {
        TestScope.Fresh();
        const uint accent = 0xFF1E3A5Fu;
        var feed = Feed(new HomeGroup(HomeGroupKind.Hero, "Good morning", [Card("hero", accent, Cover)]));

        var picks = HomeWashSource.Select(feed, _ => Graded);
        Assert.True(picks.Hero.HasValue);
        var hero = picks.Hero!.Value;

        Assert.Equal(Design.Palette.Lift(Design.Palette.ToColor(accent)) with { A = 1f }, hero.Color);
        Assert.Equal(1f, hero.Color.A);
        Assert.Equal(Palette.KeyOf(Cover).ToString(), hero.Key);
        Assert.Null(HomeWashSource.PlaneUrl(HomeWashSource.Sources(feed).Hero));
    }

    [Fact]
    public void NoPayloadAccent_FallsBackToTheGradedCover()
    {
        TestScope.Fresh();
        var feed = Feed(new HomeGroup(HomeGroupKind.MixBand, "Made for you", [Card("mix", cover: Cover)]));

        var picks = HomeWashSource.Select(feed, url => url == Cover ? Graded : (Scheme?)null);
        Assert.True(picks.Mix.HasValue);
        var mix = picks.Mix!.Value;

        Assert.Equal(Design.Palette.ChromeAccent(Graded) with { A = 1f }, mix.Color);
        Assert.Equal(Palette.KeyOf(Cover).ToString(), mix.Key);
        Assert.Equal(Cover, HomeWashSource.PlaneUrl(HomeWashSource.Sources(feed).Mix));
    }

    [Fact]
    public void NeitherAnAccentNorAGrading_LeavesTheSlotEmpty_NeverADefaultColour()
    {
        TestScope.Fresh();
        var feed = Feed(
            new HomeGroup(HomeGroupKind.Hero, "Good morning", [Card("hero", cover: Cover)]),
            new HomeGroup(HomeGroupKind.WeeklyPair, null, [Card("weekly")]));

        var picks = HomeWashSource.Select(feed, _ => null);

        Assert.Null(picks.Hero);
        Assert.Null(picks.Weekly);
        Assert.Null(picks.Mix);
        Assert.Equal(HomeWashSource.Fingerprint(default), HomeWashSource.Fingerprint(picks));
    }

    [Fact]
    public void SlotsAreChosenByKindAndOrdinal_NotByTitleAndNotByAnyOtherModule()
    {
        TestScope.Fresh();
        var feed = Feed(
            new HomeGroup(HomeGroupKind.Shelf, "Hero", [Card("shelf", 0xFFFF0000u)]),
            new HomeGroup(HomeGroupKind.Hero, "Jump back in", [Card("hero-first", 0xFF102030u), Card("hero-second", 0xFF405060u)]),
            new HomeGroup(HomeGroupKind.Hero, "Your daylist", [Card("hero-later", 0xFF708090u)]),
            new HomeGroup(HomeGroupKind.MixBand, "Radio", [Card("mix-first", 0xFF203040u)]),
            new HomeGroup(HomeGroupKind.WeeklyPair, "Discover Weekly", [Card("weekly-first", 0xFF304050u)]));

        var cards = HomeWashSource.Sources(feed);

        Assert.Equal("spotify:playlist:hero-first", cards.Hero!.Value.Uri);
        Assert.Equal("spotify:playlist:weekly-first", cards.Weekly!.Value.Uri);
        Assert.Equal("spotify:playlist:mix-first", cards.Mix!.Value.Uri);
        // A card with no artwork still gets a distinct identity, or two accent-only heroes would snap instead of fading.
        Assert.Equal("spotify:playlist:hero-first", HomeWashSource.KeyOf(cards.Hero!.Value));
    }

    [Fact]
    public void TheLoadingSeed_ResolvesToAnEmptyWash()
    {
        var picks = HomeWashSource.Select(HomeFeedView.Seed, _ => Graded);

        Assert.Null(picks.Hero);
        Assert.Null(picks.Weekly);
        Assert.Null(picks.Mix);
        Assert.Equal(HomeWashSource.Fingerprint(default), HomeWashSource.Fingerprint(picks));

        var seed = HomeWashSource.Sources(HomeFeedView.Seed);
        Assert.Null(HomeWashSource.PlaneUrl(seed.Hero));
        Assert.Null(HomeWashSource.PlaneUrl(seed.Weekly));
        Assert.Null(HomeWashSource.PlaneUrl(seed.Mix));
    }

    // 0.2.9's MOSAIC card (no Image, four tile urls). 0.3 has no mosaic on a card (`HomeCard.MosaicTiles` is null), so the
    // fact is the shape it protected: a card with NO artwork resolves from its payload accent alone and never asks the
    // plane — a lookup keyed on "" answers for whatever else was filed under it.
    [Fact]
    public void ACardWithoutArtwork_ResolvesFromItsPayloadAccentAlone_AndNeverAsksThePlane()
    {
        TestScope.Fresh();
        int asked = 0;
        Scheme? Plane(string? url) { asked++; return Graded; }

        const uint accent = 0xFF2E7D32u;
        var noArt = Card("mosaic", accent);

        var pick = HomeWashSource.Pick(noArt, Plane);

        Assert.Equal(0, asked);
        Assert.Equal(Design.Palette.Lift(Design.Palette.ToColor(accent)) with { A = 1f }, pick!.Value.Color);
        Assert.Equal("spotify:playlist:mosaic", pick.Value.Key);
        Assert.Null(HomeWashSource.PlaneUrl(noArt));

        // …and the same shape WITHOUT an accent is an empty slot, still without a plane call — there is no third tier.
        var bare = Card("mosaic-bare");
        Assert.Null(HomeWashSource.Pick(bare, Plane));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void TheLegIdentity_IsSizeIndependent_AndFallsBackToTheUriWithoutArtwork()
    {
        TestScope.Fresh();
        Assert.Equal(HomeWashSource.KeyOf(Card("a", cover: Cover)), HomeWashSource.KeyOf(Card("b", cover: Cover300)));
        Assert.Equal(24, HomeWashSource.KeyOf(Card("a", cover: Cover)).Length);
        Assert.Equal("spotify:playlist:none", HomeWashSource.KeyOf(Card("none")));
        Assert.NotEqual(HomeWashSource.KeyOf(Card("none")), HomeWashSource.KeyOf(Card("other")));
    }

    [Fact]
    public void PlaneWatches_AreExactlyTheSlotsStillWaitingOnAGrading()
    {
        TestScope.Fresh();
        Assert.Null(HomeWashSource.PlaneUrl(null));
        Assert.Null(HomeWashSource.PlaneUrl(Card("accent+art", 0xFF1E3A5Fu, Cover)));
        Assert.Null(HomeWashSource.PlaneUrl(Card("accent-only", 0xFF1E3A5Fu)));
        Assert.Null(HomeWashSource.PlaneUrl(Card("nothing")));
        Assert.Equal(Cover, HomeWashSource.PlaneUrl(Card("art-only", cover: Cover)));
        // A card fact that exists but carries accent 0 is the same as no accent — the server simply had no colours.
        Assert.Equal(Cover, HomeWashSource.PlaneUrl(Card("art-only-formatted", cover: Cover, format: "daily-mix")));
    }

    [Fact]
    public void TheFingerprint_TracksColourArtworkAndSlot_AndNothingElse()
    {
        var one = new HomeWashPick(ColorF.FromRgba(0x10, 0x20, 0x30), "art-a");
        var picks = new HomeWashPicks(one, null, null);

        static int Fp(in HomeWashPicks p) => HomeWashSource.Fingerprint(p);

        Assert.Equal(Fp(picks), Fp(new HomeWashPicks(new HomeWashPick(ColorF.FromRgba(0x10, 0x20, 0x30), "art-a"), null, null)));
        Assert.NotEqual(Fp(picks), Fp(new HomeWashPicks(one with { Color = ColorF.FromRgba(0x10, 0x20, 0x31) }, null, null)));
        Assert.NotEqual(Fp(picks), Fp(new HomeWashPicks(one with { Key = "art-b" }, null, null)));
        Assert.NotEqual(Fp(picks), Fp(new HomeWashPicks(null, one, null)));
        Assert.NotEqual(Fp(picks), Fp(default));
        // Alpha is deliberately OUT of the hash: the shell layer re-stamps the theme's wash strength.
        Assert.Equal(Fp(picks), Fp(new HomeWashPicks(one with { Color = one.Color with { A = 0.5f } }, null, null)));
    }

    [Fact]
    public void TheThemeAxisIsTheAlphaRamp_NotTheResolvedColour()
    {
        TestScope.Fresh();
        const uint accent = 0xFF1E3A5Fu;
        var feed = Feed(
            new HomeGroup(HomeGroupKind.Hero, "Good morning", [Card("hero", accent, Cover)]),
            new HomeGroup(HomeGroupKind.MixBand, "Made for you", [Card("mix", cover: Cover)]));

        var picks = HomeWashSource.Select(feed, _ => Graded);

        Assert.Equal(Design.Palette.Lift(Design.Palette.ToColor(accent)) with { A = 1f }, picks.Hero!.Value.Color);
        Assert.Equal(Design.Palette.ChromeAccent(Graded) with { A = 1f }, picks.Mix!.Value.Color);
        Assert.Equal(1f, picks.Hero!.Value.Color.A);
        Assert.Equal(1f, picks.Mix!.Value.Color.A);
        Assert.NotEqual(Design.Wash.HeroAlpha(light: true), Design.Wash.HeroAlpha(light: false));
        Assert.NotEqual(Design.Wash.ShelfAlpha(light: true), Design.Wash.ShelfAlpha(light: false));
    }
}
