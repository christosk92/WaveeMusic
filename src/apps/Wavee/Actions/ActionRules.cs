using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

// The pure decision core behind the AppAction enablement / checked predicates — extracted so the rules are unit-testable
// engine-free (Wavee.Tests source-includes this file). The AppAction lambdas (Actions/TrackActions.cs etc.) are thin
// adapters over these: services in, rule here.
public static class ActionRules
{
    /// <summary>ToggleLike checked-state: checked iff EVERY track (≥1) is saved. A track without a uri counts unsaved.</summary>
    public static bool AllSaved(IReadOnlyList<Track> tracks, Func<string, bool> isSaved)
    {
        if (tracks is not { Count: > 0 }) return false;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.Uri.Length == 0 || !isSaved(t.Uri)) return false;
        }
        return true;
    }

    /// <summary>View-credits gate: a single track carrying a primary artist uri (the NPV fetch keys off artistUri +
    /// trackUri, so both must be present).</summary>
    public static bool CanViewCredits(in ActionTarget target)
        => target.Single is { Uri.Length: > 0, Artists: { Count: > 0 } artists } && artists[0].Uri.Length > 0;

    /// <summary>Go-to-album gate: a single row whose album ref names a real RELEASE. An EPISODE rides the same
    /// <c>Track</c> read-model but carries its SHOW in that slot (<c>EpisodeAsTrack</c>, design §1.5), so an unguarded
    /// "Go to album" offered a podcast episode a route into the album page of a show — a page that does not exist. That
    /// row gets "Go to podcast" instead (<c>Menus.TrackRows</c>). Only a SHOW ref is excluded — a uri the parser cannot
    /// classify keeps the row it has always had, so this narrows one wrong destination rather than becoming an
    /// allow-list.</summary>
    public static bool CanGoToAlbum(in ActionTarget target)
        => target.Single is { Album.Uri.Length: > 0 } t && EntityUri.KindOf(t.Album.Uri) != EntityKind.Show;

    /// <summary>Go-to-podcast gate: the same single row, when the ref in the album slot IS a show. A name-only show
    /// (no uri) has nowhere to go, so the row is absent rather than dead — the <c>GoToArtistItem</c> rule.</summary>
    public static bool CanGoToPodcast(in ActionTarget target)
        => target.Single is { Album.Uri.Length: > 0 } t && EntityUri.KindOf(t.Album.Uri) == EntityKind.Show;

    /// <summary>Go-to-artist gate: a single track whose PRIMARY artist carries a uri. A name-only artist (a projected
    /// sidebar row, a search row without an artist link) is not navigable — offering the row anyway would navigate to
    /// an empty <c>artist:</c> route, i.e. a dead page.</summary>
    public static bool CanGoToArtist(in ActionTarget target)
        => target.Single is { Artists: { Count: > 0 } artists } && artists[0].Uri.Length > 0;

    /// <summary>Song-radio gate: exactly one track carrying a <c>spotify:track:</c> uri (a player-present check rides at
    /// the action). Radio seeds a single track — a multi-select or non-track uri is disabled.</summary>
    public static bool CanStartTrackRadio(in ActionTarget target)
        => target.Single is { Uri.Length: > 0 } t && EntityUri.Parse(t.Uri) is { IsSpotify: true, Kind: EntityKind.Track };

    /// <summary>Artist-radio gate: an Artist container target carrying a <c>spotify:artist:</c> uri.</summary>
    public static bool CanStartArtistRadio(in ActionTarget target)
        => target.Kind == TargetKind.Artist && target.Uri is { Length: > 0 } uri
           && EntityUri.Parse(uri) is { IsSpotify: true, Kind: EntityKind.Artist };

    /// <summary>Remove-from-this-playlist gate: an editable host with resolved rows.</summary>
    public static bool CanRemoveFromPlaylist(PlaylistHost? host) =>
        host is { Caps.CanEditItems: true, Rows.Count: > 0 };

    /// <summary>The route key <c>go(key, name)</c> takes for a container target (the app's nav scheme:
    /// <c>album:</c> / <c>artist:</c> / <c>pl:</c> / <c>liked</c>). Null = not navigable.</summary>
    public static string? RouteFor(in ActionTarget target)
    {
        if (target.Uri is not { Length: > 0 } uri) return null;
        // Every liked spelling, through THE parser — a card built from a Home/recents section item can carry
        // `spotify:user:<u>:collection`, and that used to route to a playlist page instead of Liked Songs.
        if (EntityUri.IsLikedCollection(uri)) return "liked";
        return target.Kind switch
        {
            TargetKind.Album => "album:" + uri,
            TargetKind.Artist => "artist:" + uri,
            TargetKind.Playlist or TargetKind.SidebarItem => "pl:" + uri,
            _ => null,
        };
    }
}
