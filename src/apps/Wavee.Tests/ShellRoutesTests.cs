// ── Wavee.Tests/ShellRoutesTests.cs — THE route table, the key codec and the deep-link intake ─────────────────────
//
// Wave 4's gate for `Shell/Shell.cs` §1 and §3. Ported from _old/Wavee.Tests/{ShellRoutesTests, ShellNavDestTests,
// NavRouteNormalizerTests, DeepLinkParseTests}.cs (G6: a chapter's pure rules port WITH their existing tests), plus
// the facts those files could not have: 0.2.9 kept `IsKnown`, `Dest` and `PageFor` as THREE lists, and the two
// defects ch 18 §7 records are exactly what a single table makes untestable-by-construction. They are pinned here
// anyway, because the next person to add a kind is who they are for.
//
// The three that each cost a shipped bug:
//
//   NO FALL-THROUGH THAT NAMES A REAL DESTINATION. 0.2.9's `_ => ("Your Library", MusicNote)` labelled a discography
//   tab, a what's-new tab AND the Connect diagnostics page "Your Library" — in the tab strip, the back/forward
//   flyout, the sidebar's pinned rows and the not-found glyph.
//
//   THE LEGACY RECENTS KEY IS AN ALIAS, NOT A KIND. It is matched by the `home-section:` prefix, so `IsKnown`
//   accepted it, and 0.2.9 rewrote it to `recents` before any arm saw it. Persisted history documents and old Home
//   layout documents still carry it.
//
//   A DEEP LINK IS UNTRUSTED. An unknown key must be refused rather than opening a real tab on the not-found page and
//   being written into the persisted history log; a developer-only kind must be refused outright, because a link from
//   outside the app is not what turns developer mode on.

using FluentGpu.Localization;

using Xunit;

namespace Wavee.Tests;

public class ShellRouteTableTests
{
    [Fact]
    public void Every_kind_has_its_own_row_at_its_own_index()
    {
        // `Row` indexes straight into the table; a reorder that slipped past this would silently mislabel every
        // destination in the app.
        for (int i = 0; i < Shell.RouteKindCount; i++)
            Assert.Equal((Shell.RouteKind)i, Shell.Row((Shell.RouteKind)i).Kind);
        // 15 exact + 10 prefix + 3 concert + Episode (podcast rework wave P2) + ConnectDiagnostics + NotFound
        Assert.Equal(31, Shell.RouteKindCount);
    }

    [Fact]
    public void Episode_is_a_kind_with_its_own_label_and_claims_material_like_show()
    {
        // Podcast rework wave P2 (plan §5.11): an episode renders the shared detail surface exactly like show/album,
        // so it needs its own label + glyph — without this it would fall through to "Your Library" (ch 18 §7).
        Assert.Equal(Strings.Nav.Episode, Shell.Row(Shell.RouteKind.Episode).TitleLocKey);
        Assert.True(Shell.Row(Shell.RouteKind.Episode).ClaimsMaterial);
        Assert.False(Shell.Row(Shell.RouteKind.Episode).KeyedByArg);
        Assert.False(Shell.Row(Shell.RouteKind.Episode).DeveloperOnly);
    }

    [Fact]
    public void Is_known_is_the_enum_and_the_two_lists_cannot_drift()
    {
        Assert.True(Shell.Row(Shell.RouteKind.Home).IsKnown);
        Assert.False(Shell.Row(Shell.RouteKind.NotFound).IsKnown);
    }

    [Fact]
    public void Connect_diagnostics_is_a_kind_so_its_deep_link_is_no_longer_refused()
    {
        // The 0.2.9 defect: PageFor rendered it, s_exact did not list it, so `wavee://open?route=connect-diagnostics`
        // answered `deeplink.route.unknown` while the Settings door to the same page worked.
        var r = Shell.Parse("connect-diagnostics");
        Assert.Equal(Shell.RouteKind.ConnectDiagnostics, r.Kind);
        Assert.True(Shell.IsKnown(r, developerMode: true));
    }

    [Fact]
    public void Developer_only_kinds_are_refused_unless_developer_mode_is_on()
    {
        foreach (var kind in new[] { Shell.RouteKind.ConnectDiagnostics, Shell.RouteKind.PlaybackDiagnostics })
        {
            Assert.True(Shell.Row(kind).DeveloperOnly);
            Assert.False(Shell.IsKnown(new Shell.Route(kind), developerMode: false));
            Assert.True(Shell.IsKnown(new Shell.Route(kind), developerMode: true));
        }
    }

    [Fact]
    public void A_bare_prefix_addresses_nothing_and_is_not_a_route()
    {
        Assert.Equal(Shell.RouteKind.NotFound, Shell.Parse("album:").Kind);
        Assert.Equal(Shell.RouteKind.NotFound, Shell.Parse("pl:").Kind);
        Assert.Equal(Shell.RouteKind.NotFound, Shell.Parse("home-section:").Kind);
    }

    [Fact]
    public void Browse_section_claims_the_material_and_browse_category_does_not()
    {
        // The asymmetry that is easy to lose: a browse SECTION drill carries a colour, a browse CATEGORY eases to the
        // neutral ground. Two effects in one flush would otherwise decide the colour by ordering.
        Assert.True(Shell.Row(Shell.RouteKind.BrowseSection).ClaimsMaterial);
        Assert.False(Shell.Row(Shell.RouteKind.BrowseCategory).ClaimsMaterial);
        Assert.False(Shell.Row(Shell.RouteKind.Browse).ClaimsMaterial);
        Assert.True(Shell.Row(Shell.RouteKind.Home).ClaimsMaterial);
        Assert.True(Shell.Row(Shell.RouteKind.Recents).ClaimsMaterial);
    }

    [Fact]
    public void The_arg_keyed_kinds_are_exactly_the_ones_whose_slot_depends_on_it()
    {
        var keyed = new[]
        {
            Shell.RouteKind.Search, Shell.RouteKind.WhatsNew, Shell.RouteKind.SidebarCustomize,
            Shell.RouteKind.HomeCustomize, Shell.RouteKind.Discography, Shell.RouteKind.Module,
        };
        for (int i = 0; i < Shell.RouteKindCount; i++)
        {
            var kind = (Shell.RouteKind)i;
            Assert.Equal(Array.IndexOf(keyed, kind) >= 0, Shell.Row(kind).KeyedByArg);
        }
    }

    [Fact]
    public void No_kind_falls_through_to_another_surfaces_name()
    {
        // Every row carries its OWN loc key. `nav.yourLibrary` is what 0.2.9's default said, and no row may say it.
        for (int i = 0; i < Shell.RouteKindCount; i++)
            Assert.NotEqual(Strings.Nav.YourLibrary, Shell.Row((Shell.RouteKind)i).TitleLocKey);
    }

    [Fact]
    public void Disco_and_whatsnew_and_connect_diagnostics_each_have_their_own_label()
    {
        // The three that read "Your Library" in 0.2.9.
        Assert.NotEqual(Shell.Row(Shell.RouteKind.Discography).TitleLocKey, Shell.Row(Shell.RouteKind.NotFound).TitleLocKey);
        Assert.Equal(Strings.Nav.Discography, Shell.Row(Shell.RouteKind.Discography).TitleLocKey);
        Assert.Equal(Strings.WhatsNew.Title, Shell.Row(Shell.RouteKind.WhatsNew).TitleLocKey);
        Assert.Equal(Strings.Nav.ConnectDiagnostics, Shell.Row(Shell.RouteKind.ConnectDiagnostics).TitleLocKey);
    }

    [Fact]
    public void History_and_recents_are_different_destinations_with_different_glyphs()
    {
        // Two surfaces wearing one glyph in the tab strip and the sidebar would read as the same place.
        Assert.NotEqual(Shell.Row(Shell.RouteKind.History).Glyph, Shell.Row(Shell.RouteKind.Recents).Glyph);
    }

    [Fact]
    public void Page_for_and_the_row_are_the_same_index()
    {
        Assert.Null(Shell.PageFor(new Shell.Route(Shell.RouteKind.Settings)));
        Shell.SetPage(Shell.RouteKind.Settings, static (in Shell.Route _) => new FluentGpu.Dsl.BoxEl());
        Assert.NotNull(Shell.PageFor(new Shell.Route(Shell.RouteKind.Settings)));
    }
}

public class ShellNavDestTests
{
    [Fact]
    public void A_live_title_wins_over_everything()
    {
        var r = Shell.Parse("album:spotify:album:1TSZDcvlPtAnekTaItI3qO", "Ignored");
        Assert.Equal("Random Access Memories", Shell.Dest(r, "Random Access Memories").Title);
    }

    [Fact]
    public void The_route_arg_is_the_label_for_an_entity_family()
    {
        var r = Shell.Parse("pl:spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "Today's Top Hits");
        Assert.Equal("Today's Top Hits", Shell.Dest(r).Title);
    }

    [Fact]
    public void An_entity_family_with_no_arg_says_its_own_kind_not_your_library()
    {
        var r = Shell.Parse("show:spotify:show:4rOoJ6Egrf8K2IrywzwOMk");
        Assert.Equal(Loc.Get(Strings.Nav.Show), Shell.Dest(r).Title);
    }

    [Fact]
    public void An_arg_keyed_kind_never_renders_its_discriminator_as_a_label()
    {
        // `whatsnew`'s arg is a VERSION, not a name; `disco:`'s is a facet.
        var whatsNew = Shell.Parse("whatsnew", "0.3.0");
        Assert.Equal(Loc.Get(Strings.WhatsNew.Title), Shell.Dest(whatsNew).Title);

        var disco = Shell.Parse("disco:2:spotify:artist:4tZwfgrHOc3mvqYlEYSvVi");
        Assert.Equal(Loc.Get(Strings.Nav.Discography), Shell.Dest(disco).Title);
    }

    [Fact]
    public void An_artist_schedule_names_the_artist()
    {
        var r = Shell.Parse("artist-concerts:4tZwfgrHOc3mvqYlEYSvVi", "Daft Punk");
        Assert.Equal(Strings.Nav.ArtistConcerts("Daft Punk"), Shell.Dest(r).Title);
    }
}

public class ShellRouteCodecTests
{
    [Theory]
    [InlineData("home", Shell.RouteKind.Home)]
    [InlineData("browse", Shell.RouteKind.Browse)]
    [InlineData("albums", Shell.RouteKind.LibraryAlbums)]
    [InlineData("artists", Shell.RouteKind.LibraryArtists)]
    [InlineData("podcasts", Shell.RouteKind.LibraryPodcasts)]
    [InlineData("liked", Shell.RouteKind.Liked)]
    [InlineData("local", Shell.RouteKind.Local)]
    [InlineData("history", Shell.RouteKind.History)]
    [InlineData("recents", Shell.RouteKind.Recents)]
    [InlineData("settings", Shell.RouteKind.Settings)]
    [InlineData("playback-diagnostics", Shell.RouteKind.PlaybackDiagnostics)]
    [InlineData("whatsnew", Shell.RouteKind.WhatsNew)]
    [InlineData("sidebar-customize", Shell.RouteKind.SidebarCustomize)]
    [InlineData("home-customize", Shell.RouteKind.HomeCustomize)]
    [InlineData("concerts", Shell.RouteKind.Concerts)]
    public void Every_exact_key_resolves_to_its_kind(string key, Shell.RouteKind expected)
        => Assert.Equal(expected, Shell.Parse(key).Kind);

    [Fact]
    public void Api_console_is_deleted_and_resolves_to_nothing()
    {
        // Plan §9.6 Q7: a real 0.2.9 route, removed by decision. It must not quietly resolve to anything.
        Assert.Equal(Shell.RouteKind.NotFound, Shell.Parse("api-console").Kind);
    }

    [Theory]
    [InlineData("album:spotify:album:1TSZDcvlPtAnekTaItI3qO", Shell.RouteKind.Album)]
    [InlineData("pl:spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", Shell.RouteKind.Playlist)]
    [InlineData("artist:spotify:artist:4tZwfgrHOc3mvqYlEYSvVi", Shell.RouteKind.Artist)]
    [InlineData("show:spotify:show:4rOoJ6Egrf8K2IrywzwOMk", Shell.RouteKind.Show)]
    [InlineData("module:wavee:module:youtube", Shell.RouteKind.Module)]
    [InlineData("browse:spotify:genre:pop", Shell.RouteKind.BrowseCategory)]
    [InlineData("home-section:spotify:section:abc", Shell.RouteKind.HomeSection)]
    [InlineData("browse-section:spotify:section:abc", Shell.RouteKind.BrowseSection)]
    [InlineData("episode:spotify:episode:4uLU6hMCjMI75M1A2tKUQC", Shell.RouteKind.Episode)]
    public void Every_prefix_family_resolves_to_its_kind(string key, Shell.RouteKind expected)
        => Assert.Equal(expected, Shell.Parse(key).Kind);

    [Fact]
    public void An_entity_key_round_trips_through_name_of()
    {
        const string key = "album:spotify:album:1TSZDcvlPtAnekTaItI3qO";
        var r = Shell.Parse(key, "Random Access Memories");
        Assert.Equal(key, Shell.NameOf(r));
        Assert.Equal("Random Access Memories", Shell.ArgOf(r));
    }

    [Fact]
    public void A_concert_key_round_trips_through_its_bare_id()
    {
        const string key = "concert:3AbCdEf";
        var r = Shell.Parse(key, "Daft Punk at Alexandra Palace");
        Assert.Equal(Shell.RouteKind.Concert, r.Kind);
        Assert.Equal(key, Shell.NameOf(r));
    }

    [Fact]
    public void A_discography_key_round_trips_with_its_facet()
    {
        const string key = "disco:2:spotify:artist:4tZwfgrHOc3mvqYlEYSvVi";
        var r = Shell.Parse(key);
        Assert.Equal(key, Shell.NameOf(r));
        // The facet rides in Arg, so it is NOT offered as a display name.
        Assert.Null(Shell.ArgOf(r));
        Assert.Equal(EntityKind.Artist, r.Subject.Kind);
    }

    [Fact]
    public void An_empty_search_becomes_the_browse_directory()
    {
        // NavRouteNormalizer, verbatim.
        Assert.Equal(Shell.RouteKind.Browse, Shell.Parse("search").Kind);
        Assert.Equal(Shell.RouteKind.Browse, Shell.Parse("search", "   ").Kind);
        Assert.Equal(Shell.RouteKind.Search, Shell.Parse("search", "daft punk").Kind);
    }

    [Fact]
    public void The_legacy_recents_route_is_an_alias_not_a_home_section()
    {
        var r = Shell.Parse(Shell.LegacyRecentsRoute);
        Assert.Equal(Shell.RouteKind.Recents, r.Kind);
        // And it normalises on the way OUT too, so a restored document stops carrying it.
        Assert.Equal("recents", Shell.NameOf(r));
    }

    [Fact]
    public void A_whitespace_padded_key_is_trimmed_before_it_is_resolved()
    {
        // `route=pl` + `arg=<uri>%20` composed the key `pl:<uri> `, which `IsKnown` accepted on the prefix and
        // Spotify rejected with HTTP 400 — leaving the playlist in placeholder rows forever.
        Assert.Equal(Shell.RouteKind.Home, Shell.Parse(" home ").Kind);
    }

    [Fact]
    public void Every_liked_spelling_routes_to_liked_songs()
    {
        Assert.Equal(Shell.RouteKind.Liked, Shell.For(EntityUri.Parse("spotify:collection:tracks")).Kind);
        Assert.Equal(Shell.RouteKind.Liked, Shell.For(EntityUri.Parse("spotify:user:jane:collection")).Kind);
    }

    [Fact]
    public void Shell_for_routes_an_episode_uri_to_the_episode_kind()
    {
        // Podcast rework wave P2 (plan §5.11 item 2): the composer every "go to this thing" call site shares.
        var r = Shell.For(EntityUri.Parse("spotify:episode:4uLU6hMCjMI75M1A2tKUQC"));
        Assert.Equal(Shell.RouteKind.Episode, r.Kind);
    }

    [Fact]
    public void Same_slot_ignores_the_arg_unless_the_kind_is_keyed_by_it()
    {
        var a = Shell.Parse("album:spotify:album:1TSZDcvlPtAnekTaItI3qO", "One");
        var b = Shell.Parse("album:spotify:album:1TSZDcvlPtAnekTaItI3qO", "Two");
        Assert.True(Shell.SameSlot(a, b));     // the same album, two display names

        var s1 = Shell.Parse("search", "pop");
        var s2 = Shell.Parse("search", "rock");
        Assert.False(Shell.SameSlot(s1, s2));  // each committed query is its OWN slot; Back walks query history
    }

    [Fact]
    public void The_slot_key_carries_the_tab_and_the_arg_discriminator_but_never_the_direction()
    {
        var a = Shell.Parse("whatsnew", "0.2.9") with { Tab = 1 };
        var b = Shell.Parse("whatsnew", "0.3.0") with { Tab = 1 };
        var c = Shell.Parse("whatsnew", "0.3.0") with { Tab = 2 };
        Assert.NotEqual(Shell.SlotKey(a), Shell.SlotKey(b));   // two versions are two slots
        Assert.NotEqual(Shell.SlotKey(b), Shell.SlotKey(c));   // the same page in two tabs is two slots
    }
}

public class DeepLinkTests
{
    [Fact]
    public void An_entity_verb_composes_its_key_and_the_uri_leaves_the_arg()
    {
        var v = Shell.DeepLink("wavee://open?route=album&arg=spotify:album:1TSZDcvlPtAnekTaItI3qO");
        Assert.Equal(Shell.DeepLinkKind.Open, v.Kind);
        Assert.Equal(Shell.RouteKind.Album, v.Route.Kind);
        // The uri lives in the KEY; Arg is the display name, which a deep link does not carry.
        Assert.Null(Shell.ArgOf(v.Route));
    }

    [Fact]
    public void An_unknown_key_is_refused_rather_than_opening_a_not_found_tab()
    {
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("wavee://open?route=not-a-route").Kind);
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("wavee://open?route=album").Kind);   // bare prefix
    }

    [Fact]
    public void A_developer_route_is_refused_from_outside_the_app()
    {
        Assert.Equal(Shell.DeepLinkKind.None,
            Shell.DeepLink("wavee://open?route=connect-diagnostics", developerMode: false).Kind);
        Assert.Equal(Shell.DeepLinkKind.Open,
            Shell.DeepLink("wavee://open?route=connect-diagnostics", developerMode: true).Kind);
    }

    [Fact]
    public void A_report_verb_is_a_dialog_and_never_a_tab()
    {
        var v = Shell.DeepLink("wavee://open?route=report&arg=crash");
        Assert.Equal(Shell.DeepLinkKind.Report, v.Kind);
        Assert.Equal("crash", v.Arg);
    }

    [Fact]
    public void Play_takes_a_context_or_a_module_link_and_nothing_else()
    {
        Assert.Equal(Shell.DeepLinkKind.Play, Shell.DeepLink("wavee://play?ctx=spotify:album:abc").Kind);
        Assert.Equal(Shell.DeepLinkKind.Play, Shell.DeepLink("wavee://play?link=https://youtu.be/abc").Kind);
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("wavee://play?link=not-a-url").Kind);
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("wavee://play").Kind);
    }

    [Fact]
    public void Resume_and_pause_need_no_arguments()
    {
        Assert.Equal(Shell.DeepLinkKind.Resume, Shell.DeepLink("wavee://resume").Kind);
        Assert.Equal(Shell.DeepLinkKind.Pause, Shell.DeepLink("wavee://pause").Kind);
    }

    [Fact]
    public void A_bare_spotify_track_uri_means_play_this()
        => Assert.Equal(Shell.DeepLinkKind.Play, Shell.DeepLink("spotify:track:4uLU6hMCjMI75M1A2tKUQC").Kind);

    [Fact]
    public void A_bare_spotify_episode_uri_opens_its_page_rather_than_playing()
    {
        // Podcast rework decision D-2 (plan §12), superseding the earlier 0.2.9-parity fix that widened the play
        // gate to Track-or-Episode (the historical defect that fix closed: gating on Track alone meant a shared
        // episode link fell through to "route is null ⇒ refuse" and did nothing). Now an episode link OPENS the
        // episode page — whose own primary is one click from Play — exactly like every other shared entity link.
        var v = Shell.DeepLink("spotify:episode:4uLU6hMCjMI75M1A2tKUQC");
        Assert.Equal(Shell.DeepLinkKind.Open, v.Kind);
        Assert.Equal(Shell.RouteKind.Episode, v.Route.Kind);
    }

    [Fact]
    public void A_bare_spotify_page_uri_opens_its_page()
    {
        var v = Shell.DeepLink("spotify:album:1TSZDcvlPtAnekTaItI3qO");
        Assert.Equal(Shell.DeepLinkKind.Open, v.Kind);
        Assert.Equal(Shell.RouteKind.Album, v.Route.Kind);
    }

    [Fact]
    public void The_nested_and_web_spotify_forms_are_refused_rather_than_guessed_at()
    {
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("spotify:user:jane:playlist:abc").Kind);
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("https://open.spotify.com/album/abc").Kind);
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("spotify:user:jane").Kind);
    }

    [Fact]
    public void A_percent_encoded_trailing_space_is_trimmed_after_unescaping()
    {
        // The exact shipped bug: trimming the whole URI cannot see whitespace that only EXISTS once decoded.
        var v = Shell.DeepLink("wavee://open?route=pl&arg=spotify%3Aplaylist%3A37i9dQZF1DXcBWIGoYBM5M%20");
        Assert.Equal(Shell.RouteKind.Playlist, v.Route.Kind);
        Assert.Equal("pl:spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", Shell.NameOf(v.Route));
    }

    [Fact]
    public void Garbage_never_throws()
    {
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("").Kind);
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("   ").Kind);
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("wavee://").Kind);
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("http://example.com").Kind);
        Assert.Equal(Shell.DeepLinkKind.None, Shell.DeepLink("\"wavee://open?route=\"").Kind);
    }

    [Fact]
    public void A_uri_embedded_in_a_command_line_is_extracted()
        => Assert.Equal(Shell.DeepLinkKind.Open,
            Shell.DeepLink("C:\\Wavee\\Wavee.exe wavee://open?route=home --other").Kind);
}
