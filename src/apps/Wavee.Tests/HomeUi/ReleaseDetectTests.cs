// ── Wavee.Tests/HomeUi/ReleaseDetectTests.cs — Wave 1, owner A1 ───────────────────────────────────────────────────

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

[Collection(EntitiesCollection.Name)]
public class ReleaseDetectTests
{
    const long NowMs = 1_800_000_000_000L;
    const long Day = 24 * 60 * 60 * 1000L;

    static SectionInput Of(Section s) => SectionReader.Of(s);

    [Fact]
    public void Mostly_recent_albums_are_releases()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("From artists you follow", SectionKind.HomeGeneric,
            HomeUiFixtures.Album("spotify:album:rd-1", "New Album 1", NowMs - 5 * Day),
            HomeUiFixtures.Album("spotify:album:rd-2", "New Album 2", NowMs - 10 * Day),
            HomeUiFixtures.Album("spotify:album:rd-3", "New Album 3", NowMs - 20 * Day)));

        Assert.True(ReleaseDetect.IsReleases(s, NowMs));
    }

    [Fact]
    public void Upcoming_albums_count_as_released()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("From artists you follow", SectionKind.HomeGeneric,
            HomeUiFixtures.Album("spotify:album:rd-u1", "Upcoming 1", NowMs + 3 * Day),
            HomeUiFixtures.Album("spotify:album:rd-u2", "Upcoming 2", NowMs + 7 * Day),
            HomeUiFixtures.Album("spotify:album:rd-u3", "Upcoming 3", NowMs - 2 * Day)));

        Assert.True(ReleaseDetect.IsReleases(s, NowMs));
    }

    [Fact]
    public void Fewer_than_three_cards_is_not_releases()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Too small", SectionKind.HomeGeneric,
            HomeUiFixtures.Album("spotify:album:rd-s1", "Album 1", NowMs - 1 * Day),
            HomeUiFixtures.Album("spotify:album:rd-s2", "Album 2", NowMs - 1 * Day)));

        Assert.False(ReleaseDetect.IsReleases(s, NowMs));
    }

    [Fact]
    public void Mostly_non_album_cards_is_not_releases()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Mixed", SectionKind.HomeGeneric,
            HomeUiFixtures.Album("spotify:album:rd-m1", "Album 1", NowMs - 1 * Day),
            HomeUiFixtures.Artist("spotify:artist:rd-m2", "Artist 1"),
            HomeUiFixtures.Artist("spotify:artist:rd-m3", "Artist 2"),
            HomeUiFixtures.Artist("spotify:artist:rd-m4", "Artist 3")));

        Assert.False(ReleaseDetect.IsReleases(s, NowMs));
    }

    [Fact]
    public void Mostly_old_albums_is_not_releases()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Old shelf", SectionKind.HomeGeneric,
            HomeUiFixtures.Album("spotify:album:rd-o1", "Album 1", NowMs - 400 * Day),
            HomeUiFixtures.Album("spotify:album:rd-o2", "Album 2", NowMs - 500 * Day),
            HomeUiFixtures.Album("spotify:album:rd-o3", "Album 3", NowMs - 600 * Day)));

        Assert.False(ReleaseDetect.IsReleases(s, NowMs));
    }

    [Fact]
    public void Unplayable_and_dj_cards_are_excluded_from_the_ratio()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Shelf", SectionKind.HomeGeneric,
            HomeUiFixtures.Album("spotify:album:rd-x1", "Album 1", NowMs - 1 * Day),
            HomeUiFixtures.Album("spotify:album:rd-x2", "Album 2", NowMs - 1 * Day),
            HomeUiFixtures.Album("spotify:album:rd-x3", "Album 3", NowMs - 1 * Day),
            new HomeUiFixtures.CardSpec("spotify:playlist:rd-ghost", "Ghost", Unplayable: true)));

        Assert.True(ReleaseDetect.IsReleases(s, NowMs));
    }
}
