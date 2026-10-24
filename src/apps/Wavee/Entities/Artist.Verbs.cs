// ── Entities/Artist.Verbs.cs — what the artist page's view may do (themes plan 6.1, T10b1) ─────────────────────────────────────
//
// A theme's view may not name `Shell`, `Playback` or `Entities` (WVTH002); the artist page's cards, banners and shelves still have to open a route, play a
// context, resolve an interned string and open a link. This is that seam: static verbs and one read, each the single line the page always ran, so the view
// calls them as handlers (the way Home calls `HomeVerbs`). The decisions behind them (what Play does while the context plays, what a pick opens) are the
// callees', not this file's.

using System.Diagnostics;
using FluentGpu.Controls;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>The verbs and reads the artist page's view calls instead of naming core state.</summary>
public static class ArtistVerbs
{
    /// <summary>The text behind an interned string id (empty for the empty id).</summary>
    public static string Text(StringId id) => Entities.Strings.Resolve(id);

    /// <summary>Open the route a shell key spells (a biography's inline link).</summary>
    public static void OpenRoute(string key) => Shell.GoTo(Shell.Parse(key));

    /// <summary>Open an entity's page under a title (a pick, a pre-release's View, a playlist card).</summary>
    public static void OpenEntity(EntityUri uri, string title) => Shell.GoTo(Shell.For(uri, title));

    /// <summary>Open an entity's page from its uri text.</summary>
    public static void OpenEntity(string uri, string title) => OpenEntity(EntityUri.Parse(uri), title);

    /// <summary>Open a playlist card's page.</summary>
    public static void OpenPlaylist(Playlist playlist) => OpenEntity(playlist.Uri, Entities.Strings.Resolve(playlist.TitleId));

    /// <summary>Open a concert's detail page.</summary>
    public static void OpenConcert(Concert concert) => Shell.GoTo(Concert.DetailRoute(concert));

    /// <summary>Open the artist's concert schedule (the tour banner).</summary>
    public static void OpenSchedule(Artist artist) => Shell.GoTo(Concert.ScheduleRoute(artist));

    /// <summary>Play the context, or toggle it when it is the one already playing (a card's hover play).</summary>
    public static void PlayOrToggle(EntityId id) => Playback.PlayOrToggleContext(id);

    /// <summary>Play the context named by a uri, or toggle it when it is the one already playing (the pick's Play).</summary>
    public static void PlayOrToggle(string uri) => Playback.PlayOrToggleContext(uri);

    /// <summary>Play the context (a music video's card).</summary>
    public static void Play(EntityId id) => Playback.PlayContext(id);

    /// <summary>The pick's menu: the record's own kind decides — an album, playlist, artist or show gets the one container menu, a track the track menu;
    /// any other kind has none.</summary>
    public static ContextMenuModel? PickMenu(EntityUri uri, string title, string? cover, string subtitle)
    {
        if (!uri.IsValid) return null;
        if (uri.Kind == EntityKind.Track)
        {
            Track.EnsureActions();
            return Track.Menu([Entities.Track(uri)], new Track.MenuOptions(ShowGoToAlbum: true));
        }
        var target = uri.Kind switch
        {
            EntityKind.Album => ActionTarget.ForAlbum(uri, title),
            EntityKind.Playlist => ActionTarget.ForPlaylist(uri, title),
            EntityKind.Artist => ActionTarget.ForArtist(uri, title),
            EntityKind.Show => ActionTarget.ForShow(uri, title),
            _ => default,
        };
        return target.Kind == TargetKind.None ? null : Menus.Container(in target, cover, subtitle);
    }

    /// <summary>Open an http(s) link through the actions seam, else the shell. Anything else is refused: a decoded url
    /// must never launch a program.</summary>
    public static void OpenLink(string url)
    {
        if (!System.Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != System.Uri.UriSchemeHttps && parsed.Scheme != System.Uri.UriSchemeHttp)) return;
        if (Actions.Services.OpenExternal is { } open) { open(url); return; }
        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("artist", "could not open an external link", ex);
        }
    }
}
