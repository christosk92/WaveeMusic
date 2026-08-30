using System;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Features.Browse;
using Wavee.Features.Concerts;

namespace Wavee;

// Maps a route key (+ optional display arg) to its (title, glyph). Shared by the tab strip, the content host and the
// sidebar so a destination's label/icon is defined in ONE place. A "pl:<uri>" key is a playlist; its display name rides
// in the route Arg.
static class ShellNav
{
    public static (string Title, string Glyph) Dest(string key, string? arg = null)
    {
        if (key.StartsWith("pl:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Playlist), Icons.MusicNote);
        if (key.StartsWith("album:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Album), Icons.Album);
        // An upcoming release renders the ordinary album page (ContentHost.IsDetail), so it wears the album's label and
        // glyph — without this it would fall through to the "Your Library" default in the tab strip and breadcrumb.
        if (key.StartsWith("prerelease:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Album), Icons.Album);
        if (key.StartsWith("artist:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Artist), Icons.Contact);
        // A podcast/show renders the shared detail surface (ContentHost.IsDetail) exactly like album/artist, so it needs its
        // own label + glyph here — without this it falls through to the "Your Library" default in the tab strip, the history
        // rows, and the sidebar's pinned rows (whose pin id IS the route key). Icons.RadioTower is the podcasts glyph below.
        if (key.StartsWith("show:", StringComparison.Ordinal)) return (arg ?? Loc.Get(Strings.Nav.Show), Icons.RadioTower);
        if (key.StartsWith(HomeSectionRoutes.Prefix, StringComparison.Ordinal))
            return (arg ?? Loc.Get(Strings.Nav.Home), Icons.MusicNote);
        if (key.StartsWith(BrowseRoutes.Prefix, StringComparison.Ordinal))
            return (arg ?? Loc.Get(Strings.Browse.HomeTitle), Icons.Globe);
        // A browse SECTION (paged via IBrowseService.GetSectionAsync) is a different route family from the category
        // page above — without this arm it fell past every prefix check into the exact-match switch and hit the
        // "Your Library" default, same as the "show:" regression this file already guards against.
        if (key.StartsWith(BrowseSectionRoutes.Prefix, StringComparison.Ordinal))
            return (arg ?? Loc.Get(Strings.Browse.HomeTitle), Icons.Globe);
        // A page a playback MODULE describes. The label is whatever the navigating surface knew (the hero title, the
        // channel name); with nothing to go on it says "Module" rather than falling through to "Your Library", which
        // would name a place the route does not go. Spelled as a LITERAL prefix for the same reason as the arms above:
        // this file is source-included by src/apps/Wavee.Tests, which cannot see the engine-bound page.
        if (key.StartsWith("module:", StringComparison.Ordinal))
            return (arg is { Length: > 0 } ? arg : Loc.Get(Strings.ModulePage.Title), Icons.Globe);
        if (ConcertRoutes.TryParse(key, out var concertRoute))
            return concertRoute.Kind switch
            {
                ConcertRouteKind.ArtistSchedule => (arg is { Length: > 0 }
                    ? Strings.Nav.ArtistConcerts(arg)
                    : Loc.Get(Strings.Nav.ArtistConcertsGeneric), Icons.Calendar),
                ConcertRouteKind.Detail => (arg is { Length: > 0 } ? arg : Loc.Get(Strings.Nav.ConcertDetails), Icons.Calendar),
                _ => (Loc.Get(Strings.Nav.Concerts), Icons.Calendar),
            };
        return key switch
        {
            "home"     => (Loc.Get(Strings.Nav.Home), Icons.Home),
            "browse"   => (Loc.Get(Strings.Browse.HomeTitle), Icons.Globe),
            "search"   => (arg is { Length: > 0 } ? arg : Loc.Get(Strings.Nav.Search), Icons.Search),
            "albums"   => (Loc.Get(Strings.Nav.Albums), Icons.Album),
            "artists"  => (Loc.Get(Strings.Nav.Artists), Icons.Contact),
            "liked"    => (Loc.Get(Strings.Nav.LikedSongs), Icons.Heart),
            "podcasts" => (Loc.Get(Strings.Nav.Podcasts), Icons.RadioTower),
            "local"    => (Loc.Get(Strings.Nav.LocalFiles), Icons.Folder),
            "history"  => (Loc.Get(Strings.Nav.History.Title), Icons.Clock),
            // Recently PLAYED (Features/Recents/RecentsPage) — a different destination from "history", which is the
            // app's NAVIGATION log. Headphones rather than Clock for exactly that reason: two surfaces wearing one
            // glyph in the tab strip and the sidebar would read as the same place. Spelled as a LITERAL like the two
            // arms below — this file is source-included by src/apps/Wavee.Tests, which cannot see the engine-bound page.
            "recents"  => (Loc.Get(Strings.Nav.Recents), Icons.Headphones),
            "settings" => (Loc.Get(Strings.Nav.Settings), Icons.Settings),
            "api-console" => (Loc.Get(Strings.Nav.ApiConsole), Icons.Code),
            // The local-playback ("Spotify.dll") locate/verify report. Spelled as a LITERAL for the same reason as
            // "sidebar-customize" below: this file is source-included by src/apps/Wavee.Tests, which cannot see the
            // engine-bound page class that owns the route constant.
            "playback-diagnostics" => (Loc.Get(Strings.Nav.PlaybackRuntime), Icons.MusicNote),
            // The full-page sidebar customizer (§C4.1) — a real destination, so the tab strip / breadcrumb need its label
            // and glyph here like any other page. The key is `SidebarLayoutMenu.CustomizeRoute`, spelled as a LITERAL
            // because this file is source-included by src/apps/Wavee.Tests and that engine-bound file is not.
            "sidebar-customize" => (Loc.Get(Strings.Sidebar.Customizer.Title), Icons.Edit),
            // Home layout customizer (T0.2). Literal key — this file is source-included by Wavee.Tests.
            "home-customize" => (Loc.Get(Strings.Home.Customizer.Title), Icons.Edit),
            _          => (Loc.Get(Strings.Nav.YourLibrary), Icons.MusicNote),
        };
    }

    public static (string Title, string Glyph) Dest(Route r) => Dest(r.Name, r.Arg);
}
