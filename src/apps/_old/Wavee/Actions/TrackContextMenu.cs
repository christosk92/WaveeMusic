using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The virtualized-track-list context-menu glue: resolves the Explorer selection semantics
/// (<see cref="TrackTargetResolver"/> — right-click inside a ≥2 selection acts on ALL of it, outside collapses to the
/// clicked row) and builds the track menu. Return value feeds a <c>ContextMenu.Attach</c>/<c>WithContextMenu</c>
/// factory — the attach layer owns opening/placement/dismiss; a null result opens nothing (header/recommendation
/// slots). The selection mutation happens AT OPEN, inside the factory (UI thread, human rate).
/// </summary>
public static class TrackContextMenu
{
    /// <summary>Build for a row of a selection-backed list. <paramref name="itemIndex"/> is the ITEM index (the
    /// SelectionModel's space); <paramref name="trackAt"/> maps item index → track (null for non-track rows);
    /// <paramref name="host"/> resolves the hosting playlist (runs AFTER the selection settles, so its rows cover
    /// exactly the target set). <paramref name="showGoToAlbum"/> is false on album detail pages.</summary>
    public static ContextMenuModel? Build(
        ActionServices s, SelectionModel selection, Func<int, Track?> trackAt,
        int itemIndex, Func<PlaylistHost?> host, bool showGoToAlbum = true,
        Func<Track, IReadOnlyList<MenuFlyoutItem>?>? singleTrackExtras = null)
    {
        if (TrackTargetResolver.Resolve(selection, trackAt, itemIndex, host) is not { } target) return null;
        var extras = target.Single is { } track ? singleTrackExtras?.Invoke(track) : null;
        return Menus.Tracks(new ActionContext(target, s), showGoToAlbum, extras);
    }

    /// <summary>The eager-list overload (artist Popular pages without selection, search rows): one track, no host.</summary>
    public static ContextMenuModel? BuildSingle(ActionServices s, Track track, bool showGoToAlbum = true)
    {
        if (track.Uri.Length == 0 && track.Id.Length == 0) return null;
        return Menus.Tracks(new ActionContext(ActionTarget.ForTracks(new[] { track }), s), showGoToAlbum);
    }
}
