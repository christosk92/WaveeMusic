using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
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
        Tracks: Array.Empty<Track>(), AboutArtist: null, ReleaseKind: a.Kind)
        { Accent = SeedAccentArgb(a.Cover?.Url) };

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
        // Payload accent (a Pathfinder colorDark, e.g. a daylist's own branding) wins when the wire actually carried
        // one; otherwise fall through to the click-time grading seed, same as an album.
        { Accent = p.Accent != 0 ? p.Accent : SeedAccentArgb(p.Cover?.Url) };

    /// <summary>Audit S2 #9 ("header/accent colours arrive in steps"): the card the user just clicked was ON SCREEN,
    /// so its cover is very likely already graded — <see cref="SpotifyLive.CoverColorPlane"/>'s cache is a
    /// synchronous, allocation-free lookup keyed by the SAME image identity the card's own tile just resolved. Seed
    /// <see cref="DetailModel.Accent"/> from that cache at click time so the destination page's existing 0-guarded
    /// fallback (<c>m.Accent != 0 ? WaveePalette.ChromeFromPayload(m.Accent) : Tok.AccentDefault</c>, DetailShell.cs)
    /// has a real answer on its FIRST render instead of the semantic default while its own (readiness-gated) grading
    /// lookup catches up. 0 (no fresh grading yet — a genuinely unknown cover) leaves the existing fallback in place;
    /// this never invents a colour.
    ///
    /// <para>Packs the same raw-role selection <see cref="WaveePalette.ChromeAccent"/> will eventually read
    /// (<see cref="WaveePalette.Accent"/> — the most-saturated of the graded background/text roles, PRE-lift) as an
    /// opaque ARGB, matching the shape <see cref="WaveePalette.ChromeFromPayload"/> already expects from a real
    /// Pathfinder payload.</para></summary>
    static uint SeedAccentArgb(string? coverUrl)
    {
        if (SpotifyLive.CoverColorPlane.Current.TryGetScheme(coverUrl, Tok.Theme == ThemeKind.Light) is not { } scheme)
            return 0;
        var c = WaveePalette.Accent(scheme);
        if (c.A <= 0f) return 0;   // Accent() returns default(ColorF) when every role is missing
        return ((uint)Math.Clamp((int)MathF.Round(c.A * 255f), 0, 255) << 24)
             | ((uint)Math.Clamp((int)MathF.Round(c.R * 255f), 0, 255) << 16)
             | ((uint)Math.Clamp((int)MathF.Round(c.G * 255f), 0, 255) << 8)
             | (uint)Math.Clamp((int)MathF.Round(c.B * 255f), 0, 255);
    }

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
