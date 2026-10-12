// ── Wavee.Tests/ContentStateTests.cs — which state the content region is in (themes plan 6.0, T4a) ────────────────────────
//
// `Shell.ContentState` is a pure core value: the route's BODY (a page, an empty body, the not-found page) and whether the OFFLINE
// LANE floats over it. The core decides it (`Shell.FrameRules.ContentFor`), the structure's view draws it (the mounted facts are
// `Wavee.ShellTests\ContentStateMountedTests.cs`). Everything here is the decision and its edges, engine-free:
//
//   body              known + page -> Page; known, no page yet -> EmptyBody (never the not-found page); unknown -> NotFound; a
//                     developer-only route is unknown while developer mode is off
//   offline lane      exactly the Offline auth state, over every body (a page, an empty body, the not-found page)
//   quick changes     the decision is stateless: a run of route changes decides each route alone, in any order
//   keys              the node key of each body: the ones the frame mounted before the theme drew them
//   props             the factories carry the kind and the key; equal inputs make equal props (the slot skips its template)

using FluentGpu.Dsl;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ContentStateTests
{
    static Shell.Route Of(Shell.RouteKind kind) => new(kind);

    static ContentState For(Shell.Route route, bool hasPage = true, bool developer = false, Shell.AuthState auth = Shell.AuthState.Live)
        => Shell.FrameRules.ContentFor(route, hasPage, developer, auth);

    // ── the body ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Shell.RouteKind.Home)]
    [InlineData(Shell.RouteKind.Browse)]
    [InlineData(Shell.RouteKind.Search)]
    [InlineData(Shell.RouteKind.LibraryAlbums)]
    [InlineData(Shell.RouteKind.Liked)]
    [InlineData(Shell.RouteKind.Settings)]
    [InlineData(Shell.RouteKind.History)]
    public void A_known_route_with_a_page_shows_the_page(Shell.RouteKind kind)
        => Assert.Equal(ContentStateKind.Page, For(Of(kind)).Body);

    [Theory]
    [InlineData(Shell.RouteKind.Home)]
    [InlineData(Shell.RouteKind.Liked)]
    [InlineData(Shell.RouteKind.Settings)]
    public void A_known_route_whose_page_is_not_registered_yet_is_an_empty_body_not_the_not_found_page(Shell.RouteKind kind)
        => Assert.Equal(ContentStateKind.EmptyBody, For(Of(kind), hasPage: false).Body);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_route_that_addresses_nothing_is_the_not_found_page_with_or_without_a_page(bool hasPage)
        => Assert.Equal(ContentStateKind.NotFound, For(Shell.Route.None, hasPage).Body);

    [Fact]
    public void A_prefix_route_with_nothing_after_the_prefix_is_not_found_and_with_a_subject_it_is_a_page()
    {
        Assert.Equal(ContentStateKind.NotFound, For(Of(Shell.RouteKind.User)).Body);

        var jane = Shell.For(EntityUri.Parse("spotify:user:jane"), "Jane");
        Assert.Equal(ContentStateKind.Page, For(jane).Body);
        Assert.Equal(ContentStateKind.EmptyBody, For(jane, hasPage: false).Body);
    }

    [Theory]
    [InlineData(true, false, ContentStateKind.NotFound)]    // a developer route with developer mode off is a route nothing claims, page or not
    [InlineData(false, false, ContentStateKind.NotFound)]
    [InlineData(true, true, ContentStateKind.Page)]
    [InlineData(false, true, ContentStateKind.EmptyBody)]
    public void A_developer_only_route_is_unknown_until_developer_mode_is_on(bool hasPage, bool developer, ContentStateKind expected)
        => Assert.Equal(expected, For(Of(Shell.RouteKind.PlaybackDiagnostics), hasPage, developer).Body);

    [Fact]
    public void The_contract_body_is_the_frames_own_decision_for_every_route_kind()
    {
        for (int i = 0; i < Shell.RouteKindCount; i++)
        {
            var route = Of((Shell.RouteKind)i);
            foreach (bool hasPage in new[] { true, false })
            {
                foreach (bool developer in new[] { true, false })
                {
                    ContentStateKind expected = !Shell.IsKnown(route, developer) ? ContentStateKind.NotFound
                        : hasPage ? ContentStateKind.Page : ContentStateKind.EmptyBody;
                    Assert.Equal(expected, Shell.FrameRules.ContentBodyFor(route, hasPage, developer));
                }
            }
        }
    }

    // ── the offline lane ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Shell.AuthState.Live, false)]
    [InlineData(Shell.AuthState.Connecting, false)]
    [InlineData(Shell.AuthState.Offline, true)]
    [InlineData(Shell.AuthState.SignInRequired, false)]
    public void The_lane_floats_only_while_a_stored_credential_exists_and_the_session_is_down(Shell.AuthState auth, bool expected)
        => Assert.Equal(expected, For(Of(Shell.RouteKind.Home), auth: auth).OfflineLane);

    [Fact]
    public void The_lane_floats_over_a_page_an_empty_body_and_the_not_found_page_alike()
    {
        var onPage = For(Of(Shell.RouteKind.Liked), auth: Shell.AuthState.Offline);
        Assert.Equal(new ContentState(ContentStateKind.Page, true), onPage);
        Assert.True(onPage.Shows(ContentStateKind.Page));
        Assert.True(onPage.Shows(ContentStateKind.OfflineLane));

        var onEmpty = For(Of(Shell.RouteKind.Liked), hasPage: false, auth: Shell.AuthState.Offline);
        Assert.Equal(new ContentState(ContentStateKind.EmptyBody, true), onEmpty);

        var onMissing = For(Shell.Route.None, auth: Shell.AuthState.Offline);
        Assert.Equal(new ContentState(ContentStateKind.NotFound, true), onMissing);
    }

    [Fact]
    public void Going_offline_changes_only_the_lane_and_coming_back_removes_it()
    {
        var route = Of(Shell.RouteKind.Settings);
        var live = For(route);
        var offline = For(route, auth: Shell.AuthState.Offline);
        var back = For(route, auth: Shell.AuthState.Live);

        Assert.Equal(live.Body, offline.Body);
        Assert.False(live.OfflineLane);
        Assert.True(offline.OfflineLane);
        Assert.Equal(live, back);
    }

    // ── quick route changes ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_quick_run_of_route_changes_decides_each_route_on_its_own()
    {
        Shell.Route[] run =
        [
            Of(Shell.RouteKind.Home), Of(Shell.RouteKind.PlaybackDiagnostics), Of(Shell.RouteKind.Liked), Shell.Route.None,
            Of(Shell.RouteKind.Settings), Of(Shell.RouteKind.PlaybackDiagnostics), Of(Shell.RouteKind.Home),
        ];

        ContentState[] forward = run.Select(r => For(r)).ToArray();
        ContentState[] backward = Enumerable.Reverse(Enumerable.Reverse(run).Select(r => For(r))).ToArray();

        Assert.Equal(forward, backward);   // no memory of the route before
        Assert.Equal(forward[0], forward[6]);   // Home, then the rest of the run, then Home again
        Assert.Equal(forward[1], forward[5]);   // the same unknown route twice
        Assert.Equal(ContentStateKind.NotFound, forward[1].Body);
        Assert.Equal(ContentStateKind.NotFound, forward[3].Body);
        Assert.Equal(ContentStateKind.Page, forward[2].Body);
    }

    [Fact]
    public void The_route_and_the_session_move_independently_in_a_run()
    {
        (Shell.RouteKind Kind, Shell.AuthState Auth, ContentStateKind Body, bool Lane)[] steps =
        [
            (Shell.RouteKind.Home, Shell.AuthState.Live, ContentStateKind.Page, false),
            (Shell.RouteKind.Home, Shell.AuthState.Offline, ContentStateKind.Page, true),
            (Shell.RouteKind.PlaybackDiagnostics, Shell.AuthState.Offline, ContentStateKind.NotFound, true),
            (Shell.RouteKind.Liked, Shell.AuthState.Connecting, ContentStateKind.Page, false),
            (Shell.RouteKind.PlaybackDiagnostics, Shell.AuthState.Live, ContentStateKind.NotFound, false),
        ];

        foreach (var step in steps)
            Assert.Equal(new ContentState(step.Body, step.Lane), For(Of(step.Kind), auth: step.Auth));
    }

    // ── what each state says about itself ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_state_shows_its_body_and_the_lane_only_when_it_is_up()
    {
        var empty = new ContentState(ContentStateKind.EmptyBody, false);
        Assert.True(empty.Shows(ContentStateKind.EmptyBody));
        Assert.False(empty.Shows(ContentStateKind.Page));
        Assert.False(empty.Shows(ContentStateKind.NotFound));
        Assert.False(empty.Shows(ContentStateKind.OfflineLane));

        var missing = new ContentState(ContentStateKind.NotFound, true);
        Assert.True(missing.Shows(ContentStateKind.NotFound));
        Assert.True(missing.Shows(ContentStateKind.OfflineLane));
        Assert.False(missing.Shows(ContentStateKind.Page));
    }

    [Theory]
    [InlineData(ContentStateKind.Page, true)]
    [InlineData(ContentStateKind.EmptyBody, true)]
    [InlineData(ContentStateKind.NotFound, true)]
    [InlineData(ContentStateKind.OfflineLane, false)]
    public void The_lane_is_not_a_body(ContentStateKind kind, bool expected)
        => Assert.Equal(expected, ContentState.IsBody(kind));

    // ── the node keys ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Each_body_keeps_the_node_key_the_frame_mounted_before_the_theme_drew_it()
    {
        Assert.Equal("page:liked", Shell.FrameRules.ContentKey(ContentStateKind.Page, "liked", null));
        Assert.Equal("page-empty:liked", Shell.FrameRules.ContentKey(ContentStateKind.EmptyBody, "liked", null));
        Assert.Equal("page-notfound:playback-diagnostics|", Shell.FrameRules.ContentKey(ContentStateKind.NotFound, "playback-diagnostics", null));
        Assert.Equal("page-notfound:old-route|Some Arg", Shell.FrameRules.ContentKey(ContentStateKind.NotFound, "old-route", "Some Arg"));
        Assert.Equal("", Shell.FrameRules.ContentKey(ContentStateKind.OfflineLane, "liked", null));
    }

    [Fact]
    public void A_route_that_changes_state_changes_its_node_key_so_the_swap_remounts()
    {
        string page = Shell.FrameRules.ContentKey(ContentStateKind.Page, "settings", null);
        string empty = Shell.FrameRules.ContentKey(ContentStateKind.EmptyBody, "settings", null);
        string missing = Shell.FrameRules.ContentKey(ContentStateKind.NotFound, "settings", null);

        Assert.Equal(3, new HashSet<string> { page, empty, missing }.Count);
        Assert.Equal(page, Shell.FrameRules.ContentKey(ContentStateKind.Page, "settings", null));   // and the same state keeps its key
    }

    // ── the props the core builds ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_factories_carry_the_kind_and_the_key_of_their_state()
    {
        var page = new BoxEl { Key = "inner" };

        var forPage = ContentStateProps.ForPage("page:liked", page);
        Assert.Equal(ContentStateKind.Page, forPage.Kind);
        Assert.Equal("page:liked", forPage.Key);
        Assert.Same(page, forPage.Page);

        var forEmpty = ContentStateProps.ForEmptyBody("page-empty:liked");
        Assert.Equal(ContentStateKind.EmptyBody, forEmpty.Kind);
        Assert.Equal("page-empty:liked", forEmpty.Key);
        Assert.Null(forEmpty.Page);

        Action home = static () => { };
        var forMissing = ContentStateProps.ForNotFound("page-notfound:x|", "", home);
        Assert.Equal(ContentStateKind.NotFound, forMissing.Kind);
        Assert.Equal("page-notfound:x|", forMissing.Key);
        Assert.Equal("", forMissing.Glyph);
        Assert.Same(home, forMissing.GoHome);

        Func<Element> strip = static () => new BoxEl();
        var forLane = ContentStateProps.ForOfflineLane(strip);
        Assert.Equal(ContentStateKind.OfflineLane, forLane.Kind);
        Assert.Equal("", forLane.Key);
        Assert.Same(strip, forLane.OfflineStrip);
    }

    [Fact]
    public void Equal_inputs_make_equal_props_so_an_unchanged_state_skips_the_template()
    {
        Action home = static () => { };
        Func<Element> strip = static () => new BoxEl();

        Assert.Equal(ContentStateProps.ForEmptyBody("page-empty:a"), ContentStateProps.ForEmptyBody("page-empty:a"));
        Assert.Equal(ContentStateProps.ForNotFound("page-notfound:a|", "g", home), ContentStateProps.ForNotFound("page-notfound:a|", "g", home));
        Assert.Equal(ContentStateProps.ForOfflineLane(strip), ContentStateProps.ForOfflineLane(strip));

        Assert.NotEqual(ContentStateProps.ForEmptyBody("page-empty:a"), ContentStateProps.ForEmptyBody("page-empty:b"));
        Assert.NotEqual(ContentStateProps.ForEmptyBody("page-empty:a"), ContentStateProps.ForNotFound("page-empty:a", "", home));
    }

    // ── Classic registers both ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_registers_the_frame_and_the_content_states_under_its_template_ids()
    {
        var classic = ClassicTheme.Create();

        Assert.Equal("classic.shell.frame", classic.Registry.TemplateIdOf(ViewKeys.ShellFrame.Id));
        Assert.Equal("classic.shell.content-state", classic.Registry.TemplateIdOf(ViewKeys.ShellContentState.Id));
        Assert.Empty(classic.Validate());
    }
}
