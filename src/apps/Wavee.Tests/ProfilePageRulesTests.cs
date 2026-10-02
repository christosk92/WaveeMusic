// ── Wavee.Tests/ProfilePageRulesTests.cs — the profile page's pure decisions (Entities/Profile.Rules.cs) ─────────────
// The LOAD verdict is the data layer's (ProfileLoadRule, ProfileAskTests); this file pins what the page adds on top of it:
// the reveal latch, the section plan and bodies, the stat cells and the tone.
using System.Globalization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class ProfilePageRulesTests
{
    // ── ProfileReveal ──
    [Fact]
    public void BodyReady_WaitsForReadyAndTheMeasure_ThenLatches()
    {
        Assert.False(ProfileReveal.BodyReady(ProfileLoad.Ready, measured: false, revealed: false));
        Assert.True(ProfileReveal.BodyReady(ProfileLoad.Ready, measured: true, revealed: false));
        Assert.False(ProfileReveal.BodyReady(ProfileLoad.Loading, measured: true, revealed: false));
        Assert.False(ProfileReveal.BodyReady(ProfileLoad.Failed, measured: true, revealed: false));
        Assert.False(ProfileReveal.BodyReady(ProfileLoad.Unavailable, measured: true, revealed: false));
        // latched: a refresh that reads Loading/Failed again never re-shimmers a revealed page
        Assert.True(ProfileReveal.BodyReady(ProfileLoad.Loading, measured: true, revealed: true));
        Assert.True(ProfileReveal.BodyReady(ProfileLoad.Failed, measured: false, revealed: true));
    }

    // ── ProfileSections.BodyOf ──
    [Theory]
    [InlineData(EdgeState.Unknown, 0, ProfileSectionBody.Seed)]
    [InlineData(EdgeState.Partial, 0, ProfileSectionBody.Seed)]
    [InlineData(EdgeState.Complete, 0, ProfileSectionBody.Empty)]
    [InlineData(EdgeState.Failed, 0, ProfileSectionBody.Error)]
    [InlineData(EdgeState.Failed, 3, ProfileSectionBody.Cards)]
    [InlineData(EdgeState.Complete, 3, ProfileSectionBody.Cards)]
    [InlineData(EdgeState.Unknown, 3, ProfileSectionBody.Cards)]
    public void BodyOf_RowsWin_ThenFailedEmptySeed(EdgeState state, int count, ProfileSectionBody expected)
        => Assert.Equal(expected, ProfileSections.BodyOf(state, count));

    // ── ProfileSections.Plan ──
    static ProfileFacts Facts(bool own = false, bool show = true,
        EdgeState top = EdgeState.Complete, int topN = 0, EdgeState pl = EdgeState.Complete, int plN = 3,
        EdgeState ar = EdgeState.Complete, int arN = 3, EdgeState fg = EdgeState.Complete, int fgN = 3,
        EdgeState fr = EdgeState.Complete, int frN = 3)
        => new(own, show, top, topN, pl, plN, ar, arN, fg, fgN, fr, frN);

    static ProfileSection[] Plan(in ProfileFacts f)
    {
        var into = new ProfileSection[ProfileSections.Count];
        return into[..ProfileSections.Plan(in f, into)];
    }

    [Fact]
    public void Own_Full_IsAllFive_InTheFixedOrder()
        => Assert.Equal(
            [ProfileSection.TopArtists, ProfileSection.Playlists, ProfileSection.RecentArtists, ProfileSection.Following, ProfileSection.Followers],
            Plan(Facts(own: true, topN: 10)));

    [Fact]
    public void Other_Minimal_IsPublicPlaylistsAlone_EvenEmpty()
        => Assert.Equal([ProfileSection.Playlists], Plan(Facts(show: false, plN: 0, arN: 0)));

    [Fact]
    public void TopArtists_IsOwnOnly_AndDropsOnFailureOrEmpty()
    {
        Assert.DoesNotContain(ProfileSection.TopArtists, Plan(Facts(own: false, topN: 10)));
        Assert.Contains(ProfileSection.TopArtists, Plan(Facts(own: true, top: EdgeState.Unknown, topN: 0)));
        Assert.DoesNotContain(ProfileSection.TopArtists, Plan(Facts(own: true, top: EdgeState.Failed, topN: 0)));
        Assert.DoesNotContain(ProfileSection.TopArtists, Plan(Facts(own: true, top: EdgeState.Complete, topN: 0)));
    }

    [Fact]
    public void People_HiddenWhenNotShown_AlwaysOnOwn()
    {
        Assert.DoesNotContain(ProfileSection.Following, Plan(Facts(own: false, show: false)));
        Assert.Contains(ProfileSection.Following, Plan(Facts(own: true, show: false)));
    }

    [Fact]
    public void People_EmptyDrops_FailedAndPendingStay()
    {
        Assert.DoesNotContain(ProfileSection.Followers, Plan(Facts(fr: EdgeState.Complete, frN: 0)));
        Assert.Contains(ProfileSection.Followers, Plan(Facts(fr: EdgeState.Failed, frN: 0)));
        Assert.Contains(ProfileSection.Followers, Plan(Facts(fr: EdgeState.Unknown, frN: 0)));
    }

    [Fact]
    public void RecentArtists_AbsentWhenTheOwnerHidesThem_OrTheAskFailed()
    {
        Assert.DoesNotContain(ProfileSection.RecentArtists, Plan(Facts(ar: EdgeState.Complete, arN: 0)));
        Assert.DoesNotContain(ProfileSection.RecentArtists, Plan(Facts(ar: EdgeState.Failed, arN: 0)));
        Assert.Contains(ProfileSection.RecentArtists, Plan(Facts(ar: EdgeState.Unknown, arN: 0)));
    }

    // ── the data layer's verdicts, read as the page's edge states ──
    [Theory]
    [InlineData(HomeLoad.Pending, 0, EdgeState.Unknown)]
    [InlineData(HomeLoad.Failed, 0, EdgeState.Failed)]
    [InlineData(HomeLoad.Idle, 0, EdgeState.Complete)]
    [InlineData(HomeLoad.Ready, 0, EdgeState.Complete)]
    [InlineData(HomeLoad.Failed, 4, EdgeState.Complete)]
    [InlineData(HomeLoad.Pending, 4, EdgeState.Complete)]
    public void TopState_MapsTheFeedVerdict(HomeLoad load, int count, EdgeState expected)
        => Assert.Equal(expected, ProfileSections.TopState(load, count));

    [Theory]
    [InlineData(ProfileLoad.Loading, EdgeState.Unknown)]
    [InlineData(ProfileLoad.Failed, EdgeState.Failed)]
    [InlineData(ProfileLoad.Ready, EdgeState.Complete)]
    [InlineData(ProfileLoad.Unavailable, EdgeState.Complete)]
    public void StateOf_ConcludedMeansComplete(ProfileLoad load, EdgeState expected)
        => Assert.Equal(expected, ProfileSections.StateOf(load));

    [Fact]
    public void ListState_ANoRouteListIsConcludedEmpty_EveryOtherStateIsItsOwn()
    {
        Assert.Equal(EdgeState.Complete, ProfileSections.ListState(EdgeState.Failed, EdgeTableBase.NoRoute));
        Assert.Equal(EdgeState.Failed, ProfileSections.ListState(EdgeState.Failed, 500));
        Assert.Equal(EdgeState.Failed, ProfileSections.ListState(EdgeState.Failed, EdgeTableBase.Transport));
        Assert.Equal(EdgeState.Unknown, ProfileSections.ListState(EdgeState.Unknown, 0));
        Assert.Equal(EdgeState.Partial, ProfileSections.ListState(EdgeState.Partial, 0));
        Assert.Equal(EdgeState.Complete, ProfileSections.ListState(EdgeState.Complete, 0));
    }

    [Fact]
    public void A_NoRouteFollowList_DropsItsSection_ButAFailedOneKeepsItsRetry()
    {
        var noRoute = ProfileSections.ListState(EdgeState.Failed, EdgeTableBase.NoRoute);
        var failed = ProfileSections.ListState(EdgeState.Failed, 503);
        Assert.DoesNotContain(ProfileSection.Followers, Plan(Facts(fr: noRoute, frN: 0)));
        Assert.Contains(ProfileSection.Followers, Plan(Facts(fr: failed, frN: 0)));
    }

    // ── SeeAll / keys / caps ──
    [Fact]
    public void SeeAll_OnlyForThePeopleSections_WhenThereIsMore()
    {
        Assert.True(ProfileSections.SeeAll(ProfileSection.Following, total: 1063, shown: 20));
        Assert.True(ProfileSections.SeeAll(ProfileSection.Followers, total: 21, shown: 20));
        Assert.False(ProfileSections.SeeAll(ProfileSection.Following, total: 20, shown: 20));
        // public playlists has no list route: never a "See all", however many there are
        Assert.False(ProfileSections.SeeAll(ProfileSection.Playlists, total: 94, shown: 10));
        Assert.False(ProfileSections.SeeAll(ProfileSection.TopArtists, total: 50, shown: 10));
        Assert.False(ProfileSections.SeeAll(ProfileSection.RecentArtists, total: 50, shown: 10));
    }

    [Fact]
    public void Keys_AreUnique_AndCountMatchesTheEnum()
    {
        Assert.Equal(ProfileSections.Count, Enum.GetValues<ProfileSection>().Length);
        var keys = new HashSet<string>();
        for (int i = 0; i < ProfileSections.Count; i++) Assert.True(keys.Add(ProfileSections.Key((ProfileSection)i)));
    }

    [Fact]
    public void Caps_AreTheShelfPreviewSizes()
    {
        Assert.Equal(10, ProfileSections.CapOf(ProfileSection.TopArtists));
        Assert.Equal(10, ProfileSections.CapOf(ProfileSection.Playlists));
        Assert.Equal(10, ProfileSections.CapOf(ProfileSection.RecentArtists));
        Assert.Equal(20, ProfileSections.CapOf(ProfileSection.Following));
        Assert.Equal(20, ProfileSections.CapOf(ProfileSection.Followers));
    }

    // ── ProfileStats ──
    [Fact]
    public void Stats_Own_AreFollowersFollowingPlaylists_InOrder()
    {
        Span<ProfileStatCell> c = stackalloc ProfileStatCell[ProfileStats.Max];
        int n = ProfileStats.Plan(own: true, showFollows: false, 1, 18, 30, c);
        Assert.Equal(3, n);
        Assert.Equal(ProfileStat.Followers, c[0].Kind);
        Assert.Equal(ProfileStat.Following, c[1].Kind);
        Assert.Equal(ProfileStat.Playlists, c[2].Kind);
    }

    [Fact]
    public void Stats_HiddenFollows_LeaveOnlyPlaylists()
    {
        Span<ProfileStatCell> c = stackalloc ProfileStatCell[ProfileStats.Max];
        Assert.Equal(1, ProfileStats.Plan(own: false, showFollows: false, 118, 1063, 94, c));
        Assert.Equal(ProfileStat.Playlists, c[0].Kind);
    }

    [Fact]
    public void Stats_AZeroNeverLinks_ANegativeReadsZero()
    {
        Span<ProfileStatCell> c = stackalloc ProfileStatCell[ProfileStats.Max];
        ProfileStats.Plan(own: false, showFollows: true, 0, -4, 7, c);
        Assert.False(c[0].Links);
        Assert.Equal(0, c[1].Value);
        Assert.False(c[1].Links);
    }

    [Fact]
    public void Stats_ThePlaylistsCellNeverLinks_ThePeopleCellsLinkWhenNonZero()
    {
        Span<ProfileStatCell> c = stackalloc ProfileStatCell[ProfileStats.Max];
        ProfileStats.Plan(own: false, showFollows: true, 118, 1063, 94, c);
        Assert.True(c[0].Links);
        Assert.True(c[1].Links);
        Assert.Equal(ProfileStat.Playlists, c[2].Kind);
        Assert.Equal(94, c[2].Value);
        Assert.False(c[2].Links);
    }

    [Theory]
    [InlineData(3, 1, 3)] [InlineData(3, 2, 2)] [InlineData(1, 2, 1)] [InlineData(2, 2, 1)]
    public void PerRow_SplitsTheBudget(int cells, int rows, int expected) => Assert.Equal(expected, ProfileStats.PerRow(cells, rows));

    [Fact]
    public void Numeral_GroupsInTheGivenCulture()
    {
        static CultureInfo Grouped(string separator)
        {
            var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            c.NumberFormat.NumberGroupSeparator = separator;
            c.NumberFormat.NumberDecimalSeparator = separator == "." ? "," : ".";
            return c;
        }
        // The app runs in globalization-invariant mode (Directory.Build.props), so named cultures do not exist here: the two
        // grouping conventions are built from the invariant culture with their separators set.
        Assert.Equal("1,063", ProfileStats.Numeral(1063, Grouped(",")));
        Assert.Equal("1.063", ProfileStats.Numeral(1063, Grouped(".")));
        Assert.Equal("0", ProfileStats.Numeral(-1, CultureInfo.InvariantCulture));
    }

    // ── ProfileTone ──
    const string Hex40 = "ab6775700000ee85aabbccddeeff00112233aabb";

    [Fact]
    public void Tone_GradeableAvatar_LeadsWithNoPayload()
        => Assert.Equal(new ProfileToneSource("https://i.scdn.co/image/" + Hex40, 0u),
                        ProfileTone.Of("https://i.scdn.co/image/" + Hex40, 0x6D6CF7, gradeable: true));

    [Fact]
    public void Tone_NoOrUngradeableAvatar_IsTheProfileColourAsOpaquePayload()
    {
        Assert.Equal(new ProfileToneSource(null, 0xFF6D6CF7u), ProfileTone.Of(null, 0x6D6CF7, gradeable: false));
        Assert.Equal(new ProfileToneSource(null, 0xFFF573A0u), ProfileTone.Of("https://fb.example/a.jpg", 0xF573A0, gradeable: false));
    }

    [Fact]
    public void Tone_ColourIsAlreadyOpaqueArgb_FromTheRow_StaysTheSame()
        => Assert.Equal(new ProfileToneSource(null, 0xFF6D6CF7u), ProfileTone.Of(null, 0xFF6D6CF7u, gradeable: false));

    [Fact]
    public void Argb_ZeroStaysNone_AndTheHighByteIsForced()
    {
        Assert.Equal(0u, ProfileTone.Argb(0));
        Assert.Equal(0xFF123456u, ProfileTone.Argb(0x00123456));
        Assert.Equal(0xFF123456u, ProfileTone.Argb(0x7F123456));
    }
}
