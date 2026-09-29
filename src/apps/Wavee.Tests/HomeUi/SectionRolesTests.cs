// ── Wavee.Tests/HomeUi/SectionRolesTests.cs — Wave 1, owner A1 ────────────────────────────────────────────────────

using Wavee;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

[Collection(EntitiesCollection.Name)]
public class SectionRolesTests
{
    const long NowMs = 1_800_000_000_000L;

    static SectionInput Of(Section s) => SectionReader.Of(s);

    [Fact]
    public void Spotlight_section_is_daylist()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("scream teen pop", SectionKind.HomeSpotlight,
            HomeUiFixtures.Playlist("spotify:playlist:sr-daylist", "scream teen pop", "daylist")));
        Assert.Equal(SectionRole.Daylist, SectionRoles.Of(s, NowMs, allFacet: true));
    }

    [Fact]
    public void Recently_played_is_recents_on_all_facet_only()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Recently played", SectionKind.HomeRecentlyPlayed,
            HomeUiFixtures.Playlist("spotify:playlist:sr-recent", "Liked Songs")));
        Assert.Equal(SectionRole.Recents, SectionRoles.Of(s, NowMs, allFacet: true));
        Assert.Equal(SectionRole.Drop, SectionRoles.Of(s, NowMs, allFacet: false));
    }

    [Fact]
    public void Shorts_section_is_browse()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Browse", SectionKind.HomeShorts,
            HomeUiFixtures.Playlist("spotify:playlist:sr-shorts", "Some tile")));
        Assert.Equal(SectionRole.Browse, SectionRoles.Of(s, NowMs, allFacet: true));
    }

    [Fact]
    public void Majority_personal_format_is_personal()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Made for Chris", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:sr-dm1", "Daily Mix 1", "daily-mix"),
            HomeUiFixtures.Playlist("spotify:playlist:sr-dm2", "Daily Mix 2", "daily-mix"),
            HomeUiFixtures.Playlist("spotify:playlist:sr-other", "Other", null)));
        Assert.Equal(SectionRole.Personal, SectionRoles.Of(s, NowMs, allFacet: true));
    }

    [Fact]
    public void Minority_personal_format_is_not_personal()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Mixed shelf", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:sr-dm3", "Daily Mix 1", "daily-mix"),
            HomeUiFixtures.Playlist("spotify:playlist:sr-o1", "Other 1", null),
            HomeUiFixtures.Playlist("spotify:playlist:sr-o2", "Other 2", null)));
        Assert.NotEqual(SectionRole.Personal, SectionRoles.Of(s, NowMs, allFacet: true));
    }

    [Fact]
    public void Majority_radio_format_is_radio()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Popular radio", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:sr-radio1", "Coldplay Radio", "radio"),
            HomeUiFixtures.Playlist("spotify:playlist:sr-radio2", "Dua Lipa Radio", "radio")));
        Assert.Equal(SectionRole.Radio, SectionRoles.Of(s, NowMs, allFacet: true));
    }

    [Fact]
    public void Baseline_kind_is_cluster()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("More like Avril Lavigne", SectionKind.HomeBaseline,
            HomeUiFixtures.Playlist("spotify:playlist:sr-cl1", "Avril Lavigne Radio"),
            HomeUiFixtures.Playlist("spotify:playlist:sr-cl2", "00s Pop Rock")));
        Assert.Equal(SectionRole.Cluster, SectionRoles.Of(s, NowMs, allFacet: true));
    }

    [Fact]
    public void Mostly_header_images_is_wide_editorial()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("It's New Music Friday!", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:sr-w1", "NMF 1", "editorial", headerImage: "https://img/1.jpg"),
            HomeUiFixtures.Playlist("spotify:playlist:sr-w2", "NMF 2", "editorial", headerImage: "https://img/2.jpg"),
            HomeUiFixtures.Playlist("spotify:playlist:sr-w3", "NMF 3", "editorial")));
        Assert.Equal(SectionRole.WideEditorial, SectionRoles.Of(s, NowMs, allFacet: true));
    }

    [Fact]
    public void Headered_descripto_mood_mixes_are_personal_not_wide()
    {
        TestScope.Fresh();
        // "Soundtrack your…" ships generated `descripto` mixes; with header images on every card the header ratio alone
        // would call it WideEditorial — the personal check runs first and claims it.
        var s = Of(HomeUiFixtures.Band("Soundtrack your Tuesday morning", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:sr-d1", "Workout Pop Mix", "descripto", headerImage: "https://img/d1.jpg"),
            HomeUiFixtures.Playlist("spotify:playlist:sr-d2", "Hopeless Romantic Love Mix", "descripto", headerImage: "https://img/d2.jpg")));
        Assert.Equal(SectionRole.Personal, SectionRoles.Of(s, NowMs, allFacet: true));
    }

    [Fact]
    public void Plain_generic_section_falls_back_to_generic()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Jump back in", SectionKind.HomeGeneric,
            HomeUiFixtures.Playlist("spotify:playlist:sr-g1", "Some Mix"),
            HomeUiFixtures.Album("spotify:album:sr-g2", "Some Album")));
        Assert.Equal(SectionRole.Generic, SectionRoles.Of(s, NowMs, allFacet: true));
    }

    [Fact]
    public void All_unplayable_cards_drop_the_whole_section()
    {
        TestScope.Fresh();
        var s = Of(HomeUiFixtures.Band("Ghost shelf", SectionKind.HomeGeneric,
            new HomeUiFixtures.CardSpec("spotify:playlist:sr-ghost1", "Ghost 1", Unplayable: true),
            new HomeUiFixtures.CardSpec("spotify:playlist:sr-ghost2", "Ghost 2", Format: "dj")));
        Assert.Equal(SectionRole.Drop, SectionRoles.Of(s, NowMs, allFacet: true));
    }

    [Fact]
    public void Is_usable_rejects_blank_dj_and_unplayable_cards()
    {
        TestScope.Fresh();
        Assert.False(SectionRoles.IsUsable(default));                    // a blank handle
        Assert.True(SectionRoles.IsDj("dj"));
        Assert.True(SectionRoles.IsDj("DJ"));
        Assert.False(SectionRoles.IsDj("daily-mix"));
    }
}
