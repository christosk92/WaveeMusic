using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        bool isLiked = false)
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
}
