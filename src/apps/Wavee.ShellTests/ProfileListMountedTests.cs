// ── Wavee.ShellTests/ProfileListMountedTests.cs — the Following / Followers list page, mounted (themes plan 6.1, T10c2) ─────────────────────────
//
// The list page is the structure's since T10c2; `ProfileLists.PageHost` keeps the demand, the model, the load rule, the grid plan and the letter projection (`Wavee.Tests` pins the pure
// ones: `ProfileListRulesTests`, `ProfileListRouteTests`). What only a mounted tree can say:
//
//   registered   Classic is the registered template of the key
//   faces        each load face of the region stands as ONE keyed child of a stable root (a face change remounts); the tools stay mounted for both facets and for a find that matched nothing

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ProfileListMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic(float width = 1180f) => new() { Composition = ClassicTheme.Create(), WidthDip = width };

    [Fact]
    public void Classic_is_the_registered_template_of_the_profile_list_key()
        => host.Run(() => Assert.Equal("classic.profile.list", ThemeHost.Registry.TemplateIdOf(ViewKeys.ProfileListPage.Id)));

    static ProfileListShape ShapeOf(ProfileListLoad load, ProfileFacet facet = ProfileFacet.Following, int visible = 0)
        => new(load, facet, visible, visible, ProfileChip.All, default, new ProfileGridPlan(false, false, 4, 200f), 0, "people:test", 1u);

    static ProfileListProps PropsOf(ProfileListShape shape, bool parsed = true) => new()
    {
        Parsed = parsed,
        Head = new ProfileListHead("Jane", "Following", shape.Facet, ["Following", "Followers"], static () => { }, static _ => { }),
        Pending = () => shape.Load == ProfileListLoad.Pending, Failed = () => shape.Load == ProfileListLoad.Failed, Retry = static () => { },
        Shape = new Signal<ProfileListShape>(shape), Facet = () => shape.Facet, BodyWidth = static () => 800f, OnBodyBounds = static _ => { },
        DisplayName = static () => "Jane",
        Query = static () => new Signal<string>(""), SelectChip = static _ => { },
        Mount = static _ => throw new InvalidOperationException("no rows: the mount is never asked for"),
        Rows = BoundItems.Project(new Signal<ProfileListShape>(shape), static s => s.Rows, static (_, _) => default(ProfileRowItem), default(ProfileRowItem)),
        StickyLetter = new Signal<int>(-1), StickyPush = new Signal<float>(0f), Present = new Signal<uint>(0u), Jump = static _ => { },
        ExtentOf = static _ => 0f, VisibleCount = static () => 0, VisibleAt = static _ => default,
    };

    [Theory]
    [InlineData(ProfileListLoad.Empty, "people:empty")]
    [InlineData(ProfileListLoad.Hidden, "people:hidden")]
    [InlineData(ProfileListLoad.Unavailable, "people:unavailable")]
    public void A_settled_face_stands_as_one_keyed_child(ProfileListLoad load, string key)
        => ViewHarness.With(host, ViewKeys.ProfileListPage, PropsOf(ShapeOf(load)), harness =>
        {
            harness.RunFrames(60);
            Assert.True(harness.Contains(key));
            Assert.False(harness.Contains("people:ready"));
        }, UnderClassic());

    [Theory]
    [InlineData(ProfileFacet.Following)]
    [InlineData(ProfileFacet.Followers)]
    public void A_find_that_matched_nothing_keeps_the_tools_mounted(ProfileFacet facet)
        => ViewHarness.With(host, ViewKeys.ProfileListPage, PropsOf(ShapeOf(ProfileListLoad.Ready, facet)), harness =>
        {
            harness.RunFrames(60);
            Assert.True(harness.Contains("people:ready"));
            Assert.True(harness.Contains("people:tools"));
            Assert.True(harness.Contains("people:find"));
            Assert.True(harness.Contains("people:nomatch"));
            Assert.False(harness.Contains("people:list"));
        }, UnderClassic());

    [Fact]
    public void A_pending_list_shows_the_shimmer_and_no_face()
        => ViewHarness.With(host, ViewKeys.ProfileListPage, PropsOf(ShapeOf(ProfileListLoad.Pending)), harness =>
        {
            harness.RunFrames(30);
            Assert.False(harness.Contains("people:ready"));
            Assert.False(harness.Contains("people:empty"));
            Assert.False(harness.Contains("people:find"));
        }, UnderClassic());
}
