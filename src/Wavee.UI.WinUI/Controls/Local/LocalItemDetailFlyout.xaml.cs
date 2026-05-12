using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels.Local;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Wavee.UI.WinUI.Controls.Local;

/// <summary>
/// Editable detail pane for one local item. Drag-drop an image file onto the
/// root to replace the cover artwork; commit button persists every edited field
/// through the facade.
/// </summary>
public sealed partial class LocalItemDetailFlyout : UserControl
{
    public LocalItemDetailFlyoutViewModel ViewModel { get; }

    public LocalItemDetailFlyout()
    {
        ViewModel = Ioc.Default.GetService<LocalItemDetailFlyoutViewModel>() ?? new LocalItemDetailFlyoutViewModel();
        InitializeComponent();
    }

    /// <summary>One-line entry point — pass the URI of the item to edit.</summary>
    public Task LoadAsync(string trackUri) => ViewModel.LoadAsync(trackUri);

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveAsync();
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Replace cover art";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0 || items[0] is not StorageFile file) return;

        var ext = file.FileType?.ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp")) return;

        using var stream = await file.OpenReadAsync();
        var bytes = new byte[stream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);

        var mime = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".webp"           => "image/webp",
            ".gif"            => "image/gif",
            ".bmp"            => "image/bmp",
            _                 => "image/jpeg",
        };
        await ViewModel.ApplyArtworkOverrideAsync(bytes, mime);
    }
}
