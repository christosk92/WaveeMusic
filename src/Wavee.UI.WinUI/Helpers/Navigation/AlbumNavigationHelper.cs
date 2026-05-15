using System;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.Helpers.Navigation;

/// <summary>
/// Single entry point for navigating to <c>AlbumPage</c> from a card-like
/// control. Centralises three concerns that every album-launch surface (the
/// universal <c>ContentCard</c>, <c>PopularReleaseRow</c>,
/// <c>SpotlightReleaseCard</c>, and the discography ContentCard's "Open
/// album" secondary button) needs to do consistently:
/// <list type="number">
/// <item>Prepare a connected animation on the source cover element so the
/// destination's AlbumArtContainer can morph into place.</item>
/// <item>Build a <see cref="ContentNavigationParameter"/> carrying
/// <c>TotalTracks</c> so <c>TrackDataGrid.LoadingRowCount</c> renders the
/// exact-count skeleton even before the viewport prefetch lands.</item>
/// <item>Route the actual navigation through <see cref="NavigationHelpers.OpenAlbum"/>
/// honouring the Ctrl+click "open in new tab" gesture.</item>
/// </list>
/// </summary>
public static class AlbumNavigationHelper
{
    private const string AlbumUriPrefix = "spotify:album:";

    /// <summary>
    /// Navigate to the album identified by <paramref name="albumUri"/>.
    /// </summary>
    /// <param name="albumUri">A <c>spotify:album:…</c> URI. Anything else is a no-op.</param>
    /// <param name="title">Album name for the tab header / nav-prefill.</param>
    /// <param name="subtitle">Optional artist name, threaded into the nav prefill.</param>
    /// <param name="imageUrl">Cover URL for the nav prefill.</param>
    /// <param name="totalTracks">Origin-known track count (used to size the
    /// skeleton row count immediately). Pass <c>null</c> or 0 if unknown.</param>
    /// <param name="connectedAnimationSource">The source UI element to morph
    /// from. Pass the cover image / Border. Skipped when <paramref name="openInNewTab"/>
    /// is true — the new tab's OnNavigatedTo won't find the source visual
    /// anyway, and starting an animation against a soon-to-be-orphaned element
    /// throws E_ABORT.</param>
    /// <param name="openInNewTab">Override the Ctrl-key default. Pass
    /// <c>null</c> to honour <see cref="NavigationHelpers.IsCtrlPressed"/>.</param>
    public static void NavigateToAlbum(
        string? albumUri,
        string? title,
        string? subtitle = null,
        string? imageUrl = null,
        int? totalTracks = null,
        UIElement? connectedAnimationSource = null,
        bool? openInNewTab = null)
    {
        if (string.IsNullOrEmpty(albumUri)) return;
        if (!albumUri.StartsWith(AlbumUriPrefix, StringComparison.Ordinal)) return;

        var inNewTab = openInNewTab ?? NavigationHelpers.IsCtrlPressed();

        if (!inNewTab && connectedAnimationSource is not null)
            ConnectedAnimationHelper.PrepareAnimation(ConnectedAnimationHelper.AlbumArt, connectedAnimationSource);

        var param = new ContentNavigationParameter
        {
            Uri = albumUri,
            Title = title,
            Subtitle = subtitle,
            ImageUrl = imageUrl,
            TotalTracks = totalTracks is > 0 ? totalTracks : null,
        };

        NavigationHelpers.OpenAlbum(param, title ?? "Album", inNewTab);
    }
}
