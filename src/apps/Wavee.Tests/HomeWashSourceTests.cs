// ── Wavee.Tests/HomeWashSourceTests.cs — a card's wash colour: accent first, graded cover second (Wave 5, owner P) ──────
//
// The Home-page selection this file used to cover (which THREE cards feed the shell wash, off a `HomeFeedView`) went
// with the rest of the Wave-5 Home UI (docs/plans/wavee/home-rebuild-implementation.md §7; the new Home page's own wash
// selection is `Wavee.HomeUi.WashPick`). `HomeWashSource.Pick`/`PlaneUrl`/`KeyOf` — the one-card resolution — stayed:
// Recents.Page.cs's own wash still calls it. Two properties carry the whole rule: the payload accent wins over the
// graded cover, and NOTHING invents a colour when neither resolved.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class HomeWashSourceTests
{
    // A real Spotify cover url: the artwork identity is the trailing 24 chars, which is what the wash keys on.
    const string Cover = "https://i.scdn.co/image/ab67616d0000b273e86f30ec6f14a30f1cf9bb9d";

    // The wire shape: the chroma lives in the background roles, textBrightAccent is ink.
    static Scheme Graded => new(0xFF101040u, 0xFF3C4478u, 0xFFFFFFFFu, 0xFFB3B3B3u, 0xFFFFFFFFu);

    static HomeCard Card(string id, uint accent = 0, string? cover = null)
        => HomeFixtures.Card(HomeFixtures.Playlist("spotify:playlist:" + id, id, accent: accent, image: cover));

    [Fact]
    public void PayloadAccent_WinsOverTheGradedPlane_AndKeysOnTheArtwork()
    {
        TestScope.Fresh();
        const uint accent = 0xFF1E3A5Fu;
        var card = Card("hero", accent, Cover);

        var pick = HomeWashSource.Pick(card, _ => Graded);
        Assert.True(pick.HasValue);
        Assert.Equal(Design.Palette.Lift(Design.Palette.ToColor(accent)) with { A = 1f }, pick!.Value.Color);
        Assert.Equal(1f, pick.Value.Color.A);
        Assert.Equal(Palette.KeyOf(Cover).ToString(), pick.Value.Key);
        // A resolved accent needs no grading — the plane is never awaited.
        Assert.Null(HomeWashSource.PlaneUrl(card));
    }

    [Fact]
    public void NoPayloadAccent_FallsBackToTheGradedCover()
    {
        TestScope.Fresh();
        var card = Card("mix", cover: Cover);

        Assert.Equal(Cover, HomeWashSource.PlaneUrl(card));
        var pick = HomeWashSource.Pick(card, url => url == Cover ? Graded : (Scheme?)null);
        Assert.True(pick.HasValue);
        Assert.Equal(Design.Palette.ChromeAccent(Graded) with { A = 1f }, pick!.Value.Color);
        Assert.Equal(Palette.KeyOf(Cover).ToString(), pick.Value.Key);
    }

    [Fact]
    public void NeitherAnAccentNorAGrading_ResolvesToNoPick_NeverADefaultColour()
    {
        TestScope.Fresh();
        var card = Card("weekly", cover: Cover);
        Assert.Null(HomeWashSource.Pick(card, _ => null));
    }

    [Fact]
    public void ABlankCard_ResolvesToNoPickAndNoPlane()
    {
        TestScope.Fresh();
        Assert.Null(HomeWashSource.Pick(null, _ => Graded));
        Assert.Null(HomeWashSource.PlaneUrl(null));
    }

    [Fact]
    public void KeyOf_UsesTheArtworkIdentity_NotTheUri_WhenCoverIsPresent()
    {
        TestScope.Fresh();
        var card = Card("keyed", cover: Cover);
        Assert.Equal(Palette.KeyOf(Cover).ToString(), HomeWashSource.KeyOf(card));
    }

    [Fact]
    public void KeyOf_FallsBackToTheUri_WhenThereIsNoCover()
    {
        TestScope.Fresh();
        var card = Card("no-cover");
        Assert.Equal("spotify:playlist:no-cover", HomeWashSource.KeyOf(card));
    }
}
