using System;
using System.Collections.Generic;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

// Click→detail handoff: a Home card already knows the cover, title, artist/owner and year, so it stashes a PARTIAL
// DetailModel keyed by the route key. The detail page renders its HEADER from this immediately while only the track list
// streams in via the engine's Skel.Region — instead of the whole
// page sitting on a bare skeleton. One-shot: the detail page Takes it on mount.
sealed class NavPreviewStore
{
    public static readonly Context<NavPreviewStore?> Slot = new(null);

    readonly Dictionary<string, DetailModel> _map = new();
    public void Set(string routeKey, DetailModel partial) => _map[routeKey] = partial;
    public DetailModel? Take(string routeKey) => _map.Remove(routeKey, out var m) ? m : null;
}

// Builds the partial DetailModel from the data a card carries at click time (header only — empty Tracks; the full model
// loads behind it). No connected-animation identity is assigned.
static class DetailPreview
{
    public static DetailModel FromAlbum(Album a) => new(
        Title: a.Name, Cover: a.Cover, ContextUri: a.Uri,
        BadgeType: AlbumBadge(a.Kind), Year: a.Year > 0 ? a.Year.ToString() : null, OwnerName: null, OwnerImage: null,
        Artists: a.Artists, Description: null, MetaLine: Strings.Detail.SongCount(a.TrackCount),
        Tracks: Array.Empty<Track>(), AboutArtist: null, ReleaseKind: a.Kind);

    public static DetailModel FromPlaylist(PlaylistSummary p) => new(
        Title: p.Name, Cover: p.Cover, ContextUri: p.Uri,
        BadgeType: null, Year: null, OwnerName: p.OwnerName, OwnerImage: null,
        Artists: Array.Empty<ArtistRef>(), Description: null, MetaLine: Strings.Detail.SongCount(p.TrackCount),
        Tracks: Array.Empty<Track>(), AboutArtist: null,
        // The daylist window the Home card already knew — the countdown paints with the header instead of waiting
        // for the full load, and survives a playlist4 response that omits the format attributes. The payload accent
        // rides along for the same reason: CoverColorPlane often has no grading for a daylist cover, and without
        // this the detail Play/heart fall back to the semantic blue.
        ExpiresAtMs: p.DaylistExpiresAtMs, CreatedAtMs: p.DaylistCreatedAtMs)
        { Accent = p.Accent };

    static string AlbumBadge(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
        AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
        _ => Loc.Get(Strings.Detail.Badge.Album),
    };
}

// Open a detail target the way a Home card does: stash the PARTIAL model the card already carries (cover/title/artist),
// then navigate. The stashed preview is the load-bearing bit — DetailPage.Take
// finds it and reconciles the existing shell IN PLACE (the fast path) instead of mounting a throwaway full-page skeleton
// and then the real shell (two mounts + a signal-graph teardown/rebuild). Any in-app card holding an Album/PlaylistSummary
// should open through here so it gets the same cheap nav Home already does. `preview` may be null (a no-op then).
static class DetailNav
{
    public static void OpenAlbum(NavPreviewStore? preview, Action<string, string?> go, Album a)
    {
        string key = "album:" + a.Uri;
        preview?.Set(key, DetailPreview.FromAlbum(a));
        go(key, a.Name);
    }

    public static void OpenPlaylist(NavPreviewStore? preview, Action<string, string?> go, PlaylistSummary p)
    {
        string key = "pl:" + p.Uri;
        preview?.Set(key, DetailPreview.FromPlaylist(p));
        go(key, p.Name);
    }
}
