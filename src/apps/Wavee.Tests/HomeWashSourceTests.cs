using FluentGpu.Foundation;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// The shell wash's SOURCE selection. Two properties carry the whole feature: the three slots are chosen structurally
// (kind + ordinal — never a title, so a renamed or re-localized section resolves identically), and a slot's colour is
// the server's payload accent first, the graded cover second, and NOTHING third — an invented default-accent wash under
// the entire shell would be a lie about the content.
public sealed class HomeWashSourceTests
{
    // OWNER GATING is deliberately NOT covered here. The gate ("a page clears the shell material only if it is still the
    // owner", ShellMaterialState.Owner + the ClearWash closures in HomePage/DetailShell) lives in mounted-component
    // effects over a Signal<ShellMaterialState>, and App/ShellMaterial.cs cannot be source-included into this assembly —
    // its Signal<T> would bind against VirtualCollectionSignalShim rather than the engine's. A local replica of the
    // record + the closure would assert only that the replica works, so the contract is left to the runtime (nav
    // park/activate ordering) instead of being given a green test that proves nothing.

    // A real Spotify cover url: the artwork identity is the trailing 24 chars, which is what the wash keys on.
    const string Cover = "https://i.scdn.co/image/ab67616d0000b273e86f30ec6f14a30f1cf9bb9d";
    // The SAME artwork at another rendition — Spotify varies only the 16-char size prefix of the 40-char image id.
    const string Cover300 = "https://i.scdn.co/image/ab67616d00001e02e86f30ec6f14a30f1cf9bb9d";

    // The wire shape (see CoverColorPlaneTests): the chroma lives in the background roles, textBrightAccent is ink.
    static CoverColorPlane.Scheme Graded => new(0xFF101040u, 0xFF3C4478u, 0xFFFFFFFFu, 0xFFB3B3B3u, 0xFFFFFFFFu);

    static HomeCard Card(string id, uint accent = 0, string? cover = null) => new(
        "spotify:playlist:" + id, id, null, cover is null ? null : new Image(cover), HomeCardKind.Playlist,
        Meta: accent == 0 ? null : new HomeCardMeta(Accent: accent));

    static HomeFeed Feed(params HomeGroup[] groups) => new("", groups);

    [Fact]
    public void PayloadAccent_WinsOverTheGradedPlane_AndKeysOnTheArtwork()
    {
        const uint accent = 0xFF1E3A5Fu;
        var feed = Feed(new HomeGroup(HomeGroupKind.Hero, "Good morning", [Card("hero", accent, Cover)]));

        var picks = HomeWashSource.Select(feed, _ => Graded);
        Assert.True(picks.Hero.HasValue);
        var hero = picks.Hero!.Value;

        // The SAME lift HomePage.GroupAccent applies to colorDark — the raw near-black tone would vanish into the ground.
        Assert.Equal(WaveePalette.Lift(WaveePalette.ToColor(accent)) with { A = 1f }, hero.Color);
        // Full alpha: ShellMaterialLayer owns wash strength per theme and re-stamps A onto both gradient stops.
        Assert.Equal(1f, hero.Color.A);
        Assert.Equal(CoverColorPlane.KeyForUrl(Cover), hero.Key);
        // Already resolved ⇒ nothing to wait for, so the page takes no plane subscription for this card.
        Assert.Null(HomeWashSource.PlaneUrl(HomeWashSource.Sources(feed).Hero));
    }

    [Fact]
    public void NoPayloadAccent_FallsBackToTheGradedCover()
    {
        var feed = Feed(new HomeGroup(HomeGroupKind.MixBand, "Made for you", [Card("mix", cover: Cover)]));

        var picks = HomeWashSource.Select(feed, url => url == Cover ? Graded : null);
        Assert.True(picks.Mix.HasValue);
        var mix = picks.Mix!.Value;

        Assert.Equal(WaveePalette.ChromeAccent(Graded) with { A = 1f }, mix.Color);
        Assert.Equal(CoverColorPlane.KeyForUrl(Cover), mix.Key);
        // …and until that grading lands, THIS is the one artwork the page watches for the slot.
        Assert.Equal(Cover, HomeWashSource.PlaneUrl(HomeWashSource.Sources(feed).Mix));
    }

    [Fact]
    public void NeitherAnAccentNorAGrading_LeavesTheSlotEmpty_NeverADefaultColour()
    {
        var feed = Feed(
            new HomeGroup(HomeGroupKind.Hero, "Good morning", [Card("hero", cover: Cover)]),
            new HomeGroup(HomeGroupKind.WeeklyPair, null, [Card("weekly")]));

        var picks = HomeWashSource.Select(feed, _ => null);   // nothing graded yet

        Assert.Null(picks.Hero);
        Assert.Null(picks.Weekly);
        Assert.Null(picks.Mix);
        Assert.Equal(HomeWashSource.Fingerprint(default), HomeWashSource.Fingerprint(picks));
    }

    [Fact]
    public void SlotsAreChosenByKindAndOrdinal_NotByTitleAndNotByAnyOtherModule()
    {
        // Deliberately hostile order + copy: the shelf sits first and carries the most vivid accent, a second Hero
        // group follows the first, and every title is a different piece of prose.
        var feed = Feed(
            new HomeGroup(HomeGroupKind.Shelf, "Hero", [Card("shelf", 0xFFFF0000u)]),
            new HomeGroup(HomeGroupKind.Hero, "Jump back in", [Card("hero-first", 0xFF102030u), Card("hero-second", 0xFF405060u)]),
            new HomeGroup(HomeGroupKind.Hero, "Your daylist", [Card("hero-later", 0xFF708090u)]),
            new HomeGroup(HomeGroupKind.MixBand, "Radio", [Card("mix-first", 0xFF203040u)]),
            new HomeGroup(HomeGroupKind.WeeklyPair, "Discover Weekly", [Card("weekly-first", 0xFF304050u)]));

        var cards = HomeWashSource.Sources(feed);

        Assert.Equal("spotify:playlist:hero-first", cards.Hero!.Uri);
        Assert.Equal("spotify:playlist:weekly-first", cards.Weekly!.Uri);
        Assert.Equal("spotify:playlist:mix-first", cards.Mix!.Uri);
        // A card with no artwork still gets a distinct identity, or two accent-only heroes would snap instead of fading.
        Assert.Equal("spotify:playlist:hero-first", HomeWashSource.KeyOf(cards.Hero));
    }

    [Fact]
    public void TheLoadingSeed_ResolvesToAnEmptyWash()
    {
        // The pending seed is a silhouette: blank cards, no accents, no artwork. It must publish NO wash, so the shell
        // shows its deterministic ground while Home loads rather than a colour it would then have to correct.
        var picks = HomeWashSource.Select(FakeData.HomeSeed, _ => Graded);

        Assert.Null(picks.Hero);
        Assert.Null(picks.Weekly);
        Assert.Null(picks.Mix);
        Assert.Equal(HomeWashSource.Fingerprint(default), HomeWashSource.Fingerprint(picks));

        // …and it takes NO plane subscriptions either: the seed's blanks have no artwork, so there is nothing whose
        // grading could ever land, and a watch on "" would answer for the wrong thing.
        var seed = HomeWashSource.Sources(FakeData.HomeSeed);
        Assert.Null(HomeWashSource.PlaneUrl(seed.Hero));
        Assert.Null(HomeWashSource.PlaneUrl(seed.Weekly));
        Assert.Null(HomeWashSource.PlaneUrl(seed.Mix));
    }

    // A mosaic tile card (a user playlist whose "cover" is four track thumbnails) has NO Image at all. Its colour can
    // only come from the payload accent, and the plane must never be asked: a lookup keyed on the empty string answers
    // for whatever else happens to have been filed under "".
    [Fact]
    public void AMosaicCard_ResolvesFromItsPayloadAccentAlone_AndNeverAsksThePlane()
    {
        int asked = 0;
        CoverColorPlane.Scheme? Plane(string? url) { asked++; return Graded; }

        const uint accent = 0xFF2E7D32u;
        var mosaic = new HomeCard("spotify:playlist:mosaic", "Liked from radio", null, Image: null, HomeCardKind.Playlist,
            MosaicTiles: [Cover, Cover300], Meta: new HomeCardMeta(Accent: accent));

        var pick = HomeWashSource.Pick(mosaic, Plane);

        Assert.Equal(0, asked);
        Assert.Equal(WaveePalette.Lift(WaveePalette.ToColor(accent)) with { A = 1f }, pick!.Value.Color);
        // No artwork ⇒ the uri IS the identity (the mosaic tiles are decoration, not one gradeable cover).
        Assert.Equal("spotify:playlist:mosaic", pick.Value.Key);
        Assert.Null(HomeWashSource.PlaneUrl(mosaic));

        // …and the same card WITHOUT an accent is an empty slot, still without a plane call — there is no third tier.
        var bare = mosaic with { Meta = null };
        Assert.Null(HomeWashSource.Pick(bare, Plane));
        Assert.Equal(0, asked);
    }

    // The leg's identity is the ARTWORK, not the URL: one cover served at two sizes is one wash, so scrolling a grid
    // (64px) after a hero (640px) must not remount and cross-fade the shell to the colour it already shows.
    [Fact]
    public void TheLegIdentity_IsSizeIndependent_AndFallsBackToTheUriWithoutArtwork()
    {
        Assert.Equal(HomeWashSource.KeyOf(Card("a", cover: Cover)), HomeWashSource.KeyOf(Card("b", cover: Cover300)));
        Assert.Equal(24, HomeWashSource.KeyOf(Card("a", cover: Cover)).Length);   // the size-independent tail
        // Two accent-only cards must still be two distinct layers, or the shell would snap between them.
        Assert.Equal("spotify:playlist:none", HomeWashSource.KeyOf(Card("none")));
        Assert.NotEqual(HomeWashSource.KeyOf(Card("none")), HomeWashSource.KeyOf(Card("other")));
    }

    // What the page SUBSCRIBES to. A watch exists only where a colour is still pending — anything else costs a plane
    // subscription that can never fire, and Home deliberately does not ride the plane's global epoch (every scrolling
    // grid batch bumps it).
    [Fact]
    public void PlaneWatches_AreExactlyTheSlotsStillWaitingOnAGrading()
    {
        Assert.Null(HomeWashSource.PlaneUrl(null));                                            // no card
        Assert.Null(HomeWashSource.PlaneUrl(Card("accent+art", 0xFF1E3A5Fu, Cover)));          // already resolved
        Assert.Null(HomeWashSource.PlaneUrl(Card("accent-only", 0xFF1E3A5Fu)));                // resolved, no art
        Assert.Null(HomeWashSource.PlaneUrl(Card("nothing")));                                 // nothing to grade
        Assert.Equal(Cover, HomeWashSource.PlaneUrl(Card("art-only", cover: Cover)));          // pending
        // A Meta that exists but carries accent 0 is the same as no accent — the server simply had no colours.
        var zeroAccent = Card("art-only", cover: Cover) with { Meta = new HomeCardMeta(Format: "daily-mix") };
        Assert.Equal(Cover, HomeWashSource.PlaneUrl(zeroAccent));
    }

    // The fingerprint is what the publishing effect keys on, so it has to move on exactly the two facts a layer is built
    // from — the colour and the artwork — and on the SLOT they land in, and on nothing else.
    [Fact]
    public void TheFingerprint_TracksColourArtworkAndSlot_AndNothingElse()
    {
        var one = new HomeWashPick(ColorF.FromRgba(0x10, 0x20, 0x30), "art-a");
        var picks = new HomeWashPicks(one, null, null);

        int Fp(in HomeWashPicks p) => HomeWashSource.Fingerprint(p);

        Assert.Equal(Fp(picks), Fp(new HomeWashPicks(new HomeWashPick(ColorF.FromRgba(0x10, 0x20, 0x30), "art-a"), null, null)));
        Assert.NotEqual(Fp(picks), Fp(new HomeWashPicks(one with { Color = ColorF.FromRgba(0x10, 0x20, 0x31) }, null, null)));
        Assert.NotEqual(Fp(picks), Fp(new HomeWashPicks(one with { Key = "art-b" }, null, null)));
        // The same leg in another slot is a different composition — the three washes stack in a fixed order.
        Assert.NotEqual(Fp(picks), Fp(new HomeWashPicks(null, one, null)));
        Assert.NotEqual(Fp(picks), Fp(default));
        // Alpha is deliberately OUT of the hash: ShellMaterialLayer re-stamps the theme's wash strength onto both
        // gradient stops, so a leg's alpha is never the reason to republish.
        Assert.Equal(Fp(picks), Fp(new HomeWashPicks(one with { Color = one.Color with { A = 0.5f } }, null, null)));
    }

    // The theme axis of the whole feature is the ALPHA RAMP, not the pick: Select takes no ThemeKind, and the colour it
    // resolves is full-alpha and theme-free by construction. That split is why the shell can publish one wash and let
    // the layer restrengthen it on a light/dark flip without re-selecting anything.
    [Fact]
    public void TheThemeAxisIsTheAlphaRamp_NotTheResolvedColour()
    {
        const uint accent = 0xFF1E3A5Fu;
        var feed = Feed(
            new HomeGroup(HomeGroupKind.Hero, "Good morning", [Card("hero", accent, Cover)]),
            new HomeGroup(HomeGroupKind.MixBand, "Made for you", [Card("mix", cover: Cover)]));

        var picks = HomeWashSource.Select(feed, _ => Graded);

        // Both tiers land on the theme-free derivations, at full alpha…
        Assert.Equal(WaveePalette.Lift(WaveePalette.ToColor(accent)) with { A = 1f }, picks.Hero!.Value.Color);
        Assert.Equal(WaveePalette.ChromeAccent(Graded) with { A = 1f }, picks.Mix!.Value.Color);
        Assert.Equal(1f, picks.Hero!.Value.Color.A);
        Assert.Equal(1f, picks.Mix!.Value.Color.A);
        // …while strength — the one thing that DOES differ — lives entirely in the geometry table.
        Assert.NotEqual(ShellWashGeometry.HeroAlpha(light: true), ShellWashGeometry.HeroAlpha(light: false));
        Assert.NotEqual(ShellWashGeometry.ShelfAlpha(light: true), ShellWashGeometry.ShelfAlpha(light: false));
    }
}
