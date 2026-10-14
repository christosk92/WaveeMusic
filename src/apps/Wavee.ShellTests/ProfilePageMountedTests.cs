// ── Wavee.ShellTests/ProfilePageMountedTests.cs — the profile page's view, mounted (themes plan 6.1, T10c2) ─────────────────────────
//
// The profile page's scroll column, skeleton region, hero, context band, magazine, sections and cards are the structure's since T10c2; `Profile.PageHost` keeps the demands, the
// load faces, the section plan, the hero metrics, the accent, the scroll spy and the verbs (`Wavee.Tests` pins the contract: `ProfilePageContractTests`). What only a mounted tree
// can say:
//
//   registered    Classic is the registered template of `Pages.User`
//   pending       the page is the keyed column over its keyed scroller and a body child, the body is NOT composed (no magazine) and the host's size observer hears the page's width
//   unavailable   the 404 face keeps the column and composes no magazine (no Retry)
//   composed      a ready page mounts the magazine under the band and one keyed anchor per planned section; your own page has no Follow word

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ProfilePageMountedTests(ShellHost host)
{
    const string Uri = "spotify:user:fixture";

    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static ProfilePageProps Props(bool pending, bool failed, bool unavailable = false, bool ready = false, bool own = false, Action<RectF>? measure = null)
    {
        var accent = ColorF.FromRgba(30, 144, 255, 255);
        var compact = new Signal<bool>(false);
        Action go = static () => { };
        return new ProfilePageProps
        {
            RouteKey = "fixture",
            ScopeKey = "7",
            ScrollKey = "test:user:fixture",
            Uri = Uri,
            Name = "Jane",
            Own = own,
            Pending = () => pending,
            Failed = () => failed,
            Retry = go,
            Unavailable = unavailable,
            BodyReady = ready,
            Width = ArtistHeroLayout.WideWidth,
            Metrics = ProfileHeroLayout.For(ArtistHeroLayout.WideWidth, ArtistHeroTier.Wide),
            Measure = measure ?? (static _ => { }),
            Accent = accent,
            AccentFn = () => accent,
            AccentPeek = () => accent,
            PageAccent = null,
            PayloadAccent = 0u,
            Tint = new BoxEl(),
            Hero = new ProfileHeroText("Jane", null, 0u, 5, 5, 3, own, true, false),
            HeroActs = new ProfileHeroActs(go, static () => null, static _ => { }),
            Compact = false,
            CompactEdge = () => compact,
            Active = new Signal<int>(-1),
            Scroll = new ScrollHandle(),
            CaptureViewport = static _ => { },
            ScrollY = new Signal<float>(0f),
            ScrollToTop = go,
            Sections = ready
                ? [new ProfileSectionSlot(ProfileSection.Playlists, ProfileSectionBody.Empty, [], null, go, go, static _ => { })]
                : [],
        };
    }

    [Fact]
    public void Classic_is_the_registered_template_of_the_profile_page()
        => host.Run(() => Assert.Equal("classic.profile.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.ProfilePage.Id)));

    [Fact]
    public void A_pending_page_is_the_keyed_column_over_its_scroller_and_composes_no_body()
    {
        float measured = 0f;
        ViewHarness.With(host, ViewKeys.ProfilePage, Props(pending: true, failed: false, measure: r => measured = r.W),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("profile-page:fixture"), "the page column is keyed by the route");
                Assert.True(harness.Contains("profile-scroll:fixture"), "the scroller is keyed by the route");
                Assert.True(harness.Contains("profile-body:7"), "the body child is keyed by the scope epoch");
                Assert.False(harness.Contains("profile-under-band"), "a pending page composes no magazine");
                Assert.True(measured > 0f, "the page column reports its width to the host");
            }, UnderClassic());
    }

    [Fact]
    public void The_unavailable_face_keeps_the_column_and_composes_no_magazine()
        => ViewHarness.With(host, ViewKeys.ProfilePage, Props(pending: false, failed: false, unavailable: true),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("profile-page:fixture"));
                Assert.True(harness.Contains("profile-scroll:fixture"));
                Assert.False(harness.Contains("profile-under-band"), "the 404 face has no magazine");
            }, UnderClassic());

    [Fact]
    public void A_failed_page_keeps_the_column_and_composes_no_body()
        => ViewHarness.With(host, ViewKeys.ProfilePage, Props(pending: false, failed: true),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("profile-page:fixture"));
                Assert.False(harness.Contains("profile-under-band"));
            }, UnderClassic());

    [Fact]
    public void Your_own_ready_page_mounts_the_magazine_and_no_follow_word()
        => ViewHarness.With(host, ViewKeys.ProfilePage, Props(pending: false, failed: false, ready: true, own: true),
            harness =>
            {
                harness.RunFrames(60);
                Assert.True(harness.Contains("profile-under-band"), "a ready page composes the magazine under the band");
                Assert.True(harness.Contains("anchor:playlists"), "each planned section is one keyed anchor");
                Assert.True(harness.Contains("sec:playlists"));
                Assert.False(harness.Contains("profile-band-follow:" + Uri), "your own page has no Follow word");
                Assert.False(harness.Contains("profile-follow:" + Uri), "your own page has no Follow toggle");
            }, UnderClassic());
}
