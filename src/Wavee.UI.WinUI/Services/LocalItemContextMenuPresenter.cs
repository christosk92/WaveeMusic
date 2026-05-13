using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.Local.Classification;
using Wavee.UI.Library.Local;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Controls.Local;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// One-stop helper that drill-in pages call from their <c>RightTapped</c>
/// handler to bring up the local-item context menu. Resolves the facade,
/// builds the <see cref="LocalItemMenuContext"/> with the standard set of
/// callbacks (Play / Like / Mark watched / Set kind / Add to collection /
/// Edit details / Delete from disk / etc.), and forwards to
/// <see cref="ContextMenuHost.Show"/>.
///
/// <para>Pages just pass the row's effective metadata (URI / file path /
/// kind / liked-state / watched-at) plus the <c>RightTappedRoutedEventArgs</c>
/// from the handler.</para>
/// </summary>
public static class LocalItemContextMenuPresenter
{
    public static void Show(
        FrameworkElement source,
        RightTappedRoutedEventArgs args,
        string trackUri,
        string filePath,
        LocalContentKind kind,
        long lastPositionMs = 0,
        long? watchedAt = null,
        bool isLiked = false,
        string? linkedSpotifyTrackUri = null)
    {
        if (source is null || string.IsNullOrEmpty(trackUri)) return;
        var facade = Ioc.Default.GetService<ILocalLibraryFacade>();
        var xamlRoot = source.XamlRoot;

        var ctx = new LocalItemMenuContext
        {
            TrackUri = trackUri,
            FilePath = filePath,
            Kind = kind,
            LastPositionMs = lastPositionMs,
            WatchedAt = watchedAt,
            IsLiked = isLiked,
            LinkedSpotifyTrackUri = linkedSpotifyTrackUri,
            Facade = facade,

            OnPlay = () => LocalPlaybackLauncher.PlayOne(trackUri),
            OnToggleWatched = () =>
            {
                if (facade is null) return;
                _ = facade.MarkWatchedAsync(trackUri, !watchedAt.HasValue);
            },
            OnToggleLike = () =>
            {
                if (facade is null) return;
                _ = facade.SetLikedAsync(trackUri, !isLiked);
            },
            OnLinkSpotifyTrack = () => ShowLinkSpotifyTrackFlyout(
                source,
                facade,
                trackUri,
                filePath,
                linkedSpotifyTrackUri),
            OnUnlinkSpotifyTrack = () => _ = UnlinkSpotifyTrackAsync(
                facade,
                trackUri,
                filePath,
                linkedSpotifyTrackUri),
            OnSetKind = newKind =>
            {
                if (facade is null) return;
                _ = facade.SetKindAsync(filePath, newKind);
            },
            OnAddToCollection = collectionId =>
            {
                if (facade is null) return;
                _ = facade.AddToCollectionAsync(collectionId, filePath);
            },
            OnNewCollection = () =>
            {
                if (facade is null) return;
                _ = Task.Run(async () =>
                {
                    var id = await facade.CreateCollectionAsync("New collection");
                    await facade.AddToCollectionAsync(id, filePath);
                });
            },
            OnEditDetails = () => ShowEditFlyout(source, trackUri),
            OnRefreshMetadata = () =>
            {
                if (facade is null) return;
                _ = facade.RefreshMetadataAsync(trackUri);
            },
            OnShowInExplorer = () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "/select,\"" + filePath + "\"",
                        UseShellExecute = true,
                    });
                }
                catch { /* swallow — non-critical */ }
            },
            OnRemoveFromLibrary = () =>
            {
                if (facade is null) return;
                _ = facade.RemoveFromLibraryAsync(filePath);
            },
            OnDeleteFromDisk = () => _ = ConfirmDeleteFromDiskAsync(xamlRoot, facade, filePath),
        };

        var items = LocalItemContextMenuBuilder.Build(ctx);
        ContextMenuHost.Show(source, items, args.GetPosition(source));
    }

    /// <summary>Opens the editable flyout against the item.</summary>
    public static void ShowEditFlyout(FrameworkElement anchor, string trackUri)
    {
        var content = new LocalItemDetailFlyout();
        _ = content.LoadAsync(trackUri);
        var flyout = new Flyout
        {
            Content = content,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop,
        };
        flyout.ShowAt(anchor);
    }

    private static async Task ConfirmDeleteFromDiskAsync(XamlRoot? xamlRoot, ILocalLibraryFacade? facade, string filePath)
    {
        if (facade is null || xamlRoot is null) return;
        var dlg = new ContentDialog
        {
            Title = "Delete from disk?",
            Content = "This permanently removes the file from your disk. This can't be undone.\n\n" + filePath,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await facade.DeleteFromDiskAsync(filePath);
    }

    private static void ShowLinkSpotifyTrackFlyout(
        FrameworkElement anchor,
        ILocalLibraryFacade? facade,
        string localMusicVideoTrackUri,
        string localFilePath,
        string? currentSpotifyTrackUri)
    {
        if (facade is null) return;

        var picker = new LinkSpotifyTrackFlyout();
        picker.Initialize(facade, localMusicVideoTrackUri, localFilePath, currentSpotifyTrackUri);

        var flyout = new Flyout
        {
            Content = picker,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop,
            ShouldConstrainToRootBounds = true,
        };

        flyout.FlyoutPresenterStyle = BuildLinkPickerPresenterStyle();

        picker.RequestClose += (_, _) => flyout.Hide();
        flyout.Opened += (_, _) => picker.OnFlyoutOpened();
        flyout.Closed += (_, _) => picker.OnFlyoutClosed();

        flyout.ShowAt(anchor);
    }

    private static Style BuildLinkPickerPresenterStyle()
    {
        Style? baseStyle = null;
        if (Application.Current.Resources.TryGetValue("DefaultFlyoutPresenterStyle", out var value)
            && value is Style defaultStyle)
        {
            baseStyle = defaultStyle;
        }

        var style = baseStyle is null
            ? new Style(typeof(FlyoutPresenter))
            : new Style(typeof(FlyoutPresenter)) { BasedOn = baseStyle };
        style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(8)));
        style.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, 440d));
        style.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, 440d));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, double.PositiveInfinity));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        return style;
    }

    private static async Task UnlinkSpotifyTrackAsync(
        ILocalLibraryFacade? facade,
        string localMusicVideoTrackUri,
        string localFilePath,
        string? spotifyTrackUri)
    {
        if (facade is null) return;

        await facade.UnlinkMusicVideoFromSpotifyTrackAsync(localMusicVideoTrackUri);

        // Drop the Spotify-side overlay so the card falls back to the original
        // local file metadata / extracted thumbnail. Both calls are best-effort:
        // failures here shouldn't undo the unlink.
        if (!string.IsNullOrEmpty(localFilePath))
        {
            try { await facade.PatchMetadataAsync(localFilePath, new Wavee.Local.Models.MetadataPatch()); }
            catch { /* swallow */ }
        }
        try { await facade.ClearArtworkOverrideAsync(localMusicVideoTrackUri); }
        catch { /* swallow */ }

        if (TryNormalizeSpotifyTrackUri(spotifyTrackUri, out var normalized))
        {
            var metadata = Ioc.Default.GetService<IMusicVideoMetadataService>();
            metadata?.ForgetVideoAssociation(normalized);
            Ioc.Default.GetService<IMusicVideoDiscoveryService>()
                ?.BeginBackgroundDiscovery(normalized);
        }
    }

    internal static bool TryNormalizeSpotifyTrackUri(string? input, out string spotifyTrackUri)
    {
        spotifyTrackUri = string.Empty;
        var value = input?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return false;

        const string spotifyPrefix = "spotify:track:";
        if (value.StartsWith(spotifyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = value[spotifyPrefix.Length..].Split('?', '#')[0].Trim();
            if (!IsSpotifyBase62Id(id)) return false;
            spotifyTrackUri = spotifyPrefix + id;
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Host.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!string.Equals(segments[i], "track", StringComparison.OrdinalIgnoreCase)) continue;
                var id = segments[i + 1];
                if (!IsSpotifyBase62Id(id)) return false;
                spotifyTrackUri = spotifyPrefix + id;
                return true;
            }
        }

        if (IsSpotifyBase62Id(value))
        {
            spotifyTrackUri = spotifyPrefix + value;
            return true;
        }

        return false;
    }

    internal static bool IsSpotifyBase62Id(string? id)
    {
        if (id is null || id.Length != 22) return false;
        foreach (var ch in id)
        {
            var isAsciiLetter = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
            var isDigit = ch >= '0' && ch <= '9';
            if (!isAsciiLetter && !isDigit) return false;
        }
        return true;
    }
}
