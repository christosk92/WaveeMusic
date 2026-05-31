using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.Services.DragDrop.Payloads;
using Wavee.UI.WinUI.Diagnostics;
using Wavee.UI.WinUI.DragDrop;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Click routing, navigation, drag-source payload construction, and
/// connected-animation prep for <see cref="ContentCard"/>. Extracted from the
/// main code-behind so the entry-action surface (mouse/middle-click/right-click
/// → navigate / select / open context menu / drag) stays separate from
/// image-loading and playback-highlight concerns.
/// </summary>
public sealed partial class ContentCard
{
    // ── Drag source ──────────────────────────────────────────────────────────
    //
    // Manual drag detection (not XAML CanDrag) because the inner Button
    // captures pointer on PointerPressed for its own Click pipeline, which
    // blocks the framework's drag threshold from firing DragStarting. The
    // helper hooks pointer events and calls StartDragAsync past a movement
    // threshold — which raises DragStarting even when CanDrag is false.

    private bool _manualDragAttached;

    private void EnsureManualDragAttached()
    {
        if (_manualDragAttached) return;
        _manualDragAttached = true;
        ManualDragAttachment.AttachWithPackageWriter(this, BuildDragPayload);
    }

    private IDragPayload? BuildDragPayload()
    {
        var uri = NavigationUri;
        if (string.IsNullOrEmpty(uri)) return null;
        return ResolveDragPayload(uri, Title ?? string.Empty, ImageUrl);
    }

    private static IDragPayload? ResolveDragPayload(string uri, string title, string? imageUrl)
    {
        if (uri.StartsWith("spotify:album:", StringComparison.Ordinal))
            return new AlbumDragPayload(uri, title, imageUrl);
        if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            return new PlaylistDragPayload(uri, title, imageUrl: imageUrl);
        if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal))
            return new ArtistDragPayload(uri, title, imageUrl);
        if (uri.StartsWith("spotify:show:", StringComparison.Ordinal))
            return new ShowDragPayload(uri, title, imageUrl);
        // "spotify:collection" or "spotify:collection:tracks" → the user's
        // Liked Songs. Match either spelling for forward compatibility.
        if (uri.StartsWith("spotify:collection", StringComparison.Ordinal))
            return new LikedSongsDragPayload();
        return null;
    }

    // ── Click handlers ───────────────────────────────────────────────────────

    private void CardButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPlayButtonSource(e.OriginalSource))
            return;

        if (IsSubtitleNavigationSource(e.OriginalSource)
            && NavigateSubtitle(openInNewTab: Helpers.Navigation.NavigationHelpers.IsCtrlPressed()))
            return;

        // Passive mode: the card lives inside an ItemsView/ItemContainer and a
        // click should select the item rather than navigate. Ctrl+click still
        // opens a new tab to preserve the "open in background" affordance.
        if (IsPassive)
        {
            if (!string.IsNullOrEmpty(NavigationUri) && Helpers.Navigation.NavigationHelpers.IsCtrlPressed())
            {
                NavigationDiagnostics.RecordClickIntent("ContentCard." + ClickIntentKindFromUri(NavigationUri) + ".NewTab");
                ResetInteractionState();
                if (NavigateToUri(openInNewTab: true))
                    return;
            }

            SelectParentItemContainer();
            return;
        }

        // Self-navigation: if NavigationUri is set AND auto-routing is enabled,
        // navigate directly. The AutoNavigateOnTap gate lets surfaces opt out
        // of the auto-route while still benefiting from NavigationUri-driven
        // viewport prefetch + the SecondaryAction "Open album" button (e.g.
        // artist-page discography cards whose primary tap expands inline).
        if (AutoNavigateOnTap && !string.IsNullOrEmpty(NavigationUri))
        {
            var openInNewTab = Helpers.Navigation.NavigationHelpers.IsCtrlPressed();
            NavigationDiagnostics.RecordClickIntent(
                "ContentCard." + ClickIntentKindFromUri(NavigationUri) + (openInNewTab ? ".NewTab" : ""));

            ResetInteractionState();
            if (NavigateToUri(openInNewTab))
                return;
        }

        CardClick?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Short kind label for the click-intent diagnostic (Album / Playlist /
    /// Artist / Show / Episode / Collection / User / Browse). Falls back to
    /// "Unknown" if the URI doesn't match a known prefix — never throws.
    /// </summary>
    private static string ClickIntentKindFromUri(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "Unknown";
        var parts = uri.Split(':');
        if (parts.Length < 2) return "Unknown";
        return parts[1] switch
        {
            "album" => "Album",
            "playlist" => "Playlist",
            "artist" => "Artist",
            "show" => "Show",
            "episode" => "Episode",
            "collection" => "Collection",
            "user" => "User",
            "page" or "section" or "genre" => "Browse",
            _ => parts[1]
        };
    }

    /// <summary>
    /// Click handler for the optional SecondaryAction overlay button. Routes
    /// to AlbumPage with full prefetch + count prefill
    /// via <see cref="Helpers.Navigation.AlbumNavigationHelper.NavigateToAlbum"/>. Marks the event
    /// handled so the underlying card Tapped / CardClick doesn't also fire —
    /// critical on surfaces whose primary tap triggers a different action
    /// (expand, select, etc.).
    /// </summary>
    private void SecondaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        var uri = NavigationUri;
        if (string.IsNullOrEmpty(uri)) return;
        NavigationDiagnostics.RecordClickIntent("ContentCard.SecondaryAction.Album");
        Helpers.Navigation.AlbumNavigationHelper.NavigateToAlbum(
            uri,
            title: Title,
            subtitle: Subtitle,
            imageUrl: ImageUrl,
            totalTracks: NavigationTotalTracks > 0 ? NavigationTotalTracks : null);

        // RoutedEventArgs from Button.Click does not bubble to ancestor Tapped
        // handlers under the WinUI 3 input model — the Button consumes the
        // pointer before Tapped fires, so we don't need e.Handled = true here.
        // CardClick is only invoked from CardButton_Click (which never sees the
        // inner Button's click), so the expand path also stays untouched.
    }

    private void SelectParentItemContainer()
    {
        DependencyObject? current = VisualTreeHelper.GetParent(this);
        while (current != null)
        {
            if (current is ItemContainer itemContainer)
            {
                itemContainer.IsSelected = true;
                return;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }

    private void ExternalButton_Click(object sender, RoutedEventArgs e)
    {
        // Mirror the play button: the overlay is the explicit affordance, but the
        // semantic action (open URL) belongs to the consumer. ExternalActionRequested
        // is the precise hook; CardClick also fires so consumers wired only to
        // CardClick (most current Merch usages) keep working.
        ExternalActionRequested?.Invoke(this, EventArgs.Empty);
        CardClick?.Invoke(this, EventArgs.Empty);
    }

    private void CardButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed)
        {
            if (IsSubtitleNavigationSource(e.OriginalSource)
                && NavigateSubtitle(openInNewTab: true))
            {
                e.Handled = true;
                return;
            }

            if (!string.IsNullOrEmpty(NavigationUri))
            {
                NavigationDiagnostics.RecordClickIntent(
                    "ContentCard." + ClickIntentKindFromUri(NavigationUri) + ".MiddleClick");
                ResetInteractionState();
                if (NavigateToUri(openInNewTab: true))
                    return;
            }
            CardMiddleClick?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsSubtitleNavigationSource(object? source)
    {
        if (!IsArtistSubtitleNavigationUri(SubtitleNavigationUri))
            return false;

        var current = source as DependencyObject;
        while (current != null)
        {
            if (ReferenceEquals(current, SubtitleText))
                return true;
            if (ReferenceEquals(current, CardButton) || ReferenceEquals(current, this))
                return false;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool NavigateSubtitle(bool openInNewTab)
    {
        var uri = SubtitleNavigationUri;
        if (!IsArtistSubtitleNavigationUri(uri))
            return false;

        var title = SubtitleNavigationTitle ?? Subtitle ?? "Artist";
        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = uri!,
            Title = title,
        };

        NavigationDiagnostics.RecordClickIntent(
            "ContentCard.ArtistSubtitle" + (openInNewTab ? ".NewTab" : ""));
        ResetInteractionState();
        Helpers.Navigation.NavigationHelpers.OpenArtist(param, title, openInNewTab);
        return true;
    }

    private void CardButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CardRightTapped?.Invoke(this, e);
        if (e.Handled)
            return;

        if (!string.IsNullOrEmpty(NavigationUri))
        {
            var items = Controls.ContextMenu.Builders.CardContextMenuBuilder.BuildForUri(
                uri: NavigationUri!,
                title: Title ?? string.Empty,
                imageUrl: ImageUrl,
                openAction: openInNewTab =>
                {
                    NavigationDiagnostics.RecordClickIntent(
                        "ContentCard." + ClickIntentKindFromUri(NavigationUri!) + ".ContextMenu");
                    ResetInteractionState();
                    NavigateToUri(openInNewTab);
                });
            Controls.ContextMenu.ContextMenuHost.Show(this, items, e.GetPosition(this));
            e.Handled = true;
            return;
        }
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private bool NavigateToUri(bool openInNewTab)
    {
        var uri = NavigationUri!;
        var parts = uri.Split(':');
        if (parts.Length < 3) return false;

        var type = parts[1];
        var title = NavigationTitle ?? Title ?? type;

        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = uri,
            Title = title,
            Subtitle = SubtitleText?.Text,
            ImageUrl = ImageUrl,
            TotalTracks = NavigationTotalTracks > 0 ? NavigationTotalTracks : null
        };

        switch (type)
        {
            case "collection" when uri.Contains("your-episodes", StringComparison.OrdinalIgnoreCase):
                Helpers.Navigation.NavigationHelpers.OpenYourEpisodes(openInNewTab);
                return true;
            case "collection":
                Helpers.Navigation.NavigationHelpers.OpenLikedSongs(openInNewTab);
                return true;
            case "artist":
                Helpers.Navigation.NavigationHelpers.OpenArtist(param, title, openInNewTab);
                return true;
            case "album":
                OpenAlbumAfterClick(param, title, openInNewTab);
                return true;
            case "playlist":
                Helpers.Navigation.NavigationHelpers.OpenPlaylist(param, title, openInNewTab);
                return true;
            case "user" when uri.Contains(":collection", StringComparison.OrdinalIgnoreCase):
                Helpers.Navigation.NavigationHelpers.OpenLikedSongs(openInNewTab);
                return true;
            case "user":
                Helpers.Navigation.NavigationHelpers.OpenProfile(param, title, openInNewTab);
                return true;
            case "page":
            case "section":
            case "genre":
                Helpers.Navigation.NavigationHelpers.OpenBrowsePage(param, openInNewTab);
                return true;
            case "show":
                Helpers.Navigation.NavigationHelpers.OpenShowPage(param, openInNewTab);
                return true;
            case "episode":
                Helpers.Navigation.NavigationHelpers.OpenEpisodePage(
                    uri,
                    title,
                    ImageUrl,
                    openInNewTab: openInNewTab);
                return true;
        }

        return false;
    }

    private void OpenAlbumAfterClick(Data.Parameters.ContentNavigationParameter parameter, string title, bool openInNewTab)
    {
        Helpers.Navigation.NavigationHelpers.OpenAlbum(parameter, title, openInNewTab);
    }
}
