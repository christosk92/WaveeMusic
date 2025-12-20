using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.WinUI.Helpers;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for a baseline section card that combines section metadata with its single item.
/// Inherits from ContentItemViewModel to work with existing template infrastructure.
/// </summary>
public partial class BaselineSectionCardViewModel : ContentItemViewModel, IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    [ObservableProperty]
    public partial string SectionTitle { get; set; }

    /// <summary>
    /// Indicates if preview items are currently being loaded
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>
    /// Indicates if an error occurred while loading preview items
    /// </summary>
    [ObservableProperty]
    public partial bool HasError { get; set; }

    /// <summary>
    /// Error message to display if loading failed
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// Collection of preview images (album covers from track items)
    /// Populated by feedBaselineLookup API call
    /// </summary>
    [ObservableProperty]
    public partial ObservableCollection<BitmapImage> PreviewImages { get; set; }

    public BaselineSectionCardViewModel(string sectionTitle, ContentItemViewModel item)
    {
        SectionTitle = sectionTitle;

        // Copy all properties from the source item
        Uri = item.Uri;
        Name = item.Name;
        Subtitle = item.Subtitle;
        Image = item.Image;
        DominantColor = item.DominantColor;
        IsCircularImage = item.IsCircularImage;
        IsPlaying = item.IsPlaying;

        // Initialize preview items collection and loading state
        PreviewImages = new();
        IsLoading = true;
        HasError = false;

        // Trigger background load of preview items
        LoadPreviewItemsAsync(_cancellationTokenSource.Token).SafeFireAndForget();
    }

    /// <summary>
    /// Loads preview items (track album covers) from feedBaselineLookup API.
    /// This is a stub method that will be connected to the actual service later.
    /// Expected API: https://api-partner.spotify.com/pathfinder/v2/query
    /// with feedBaselineLookup query returning previewItems with track album covers.
    /// </summary>
    private async Task LoadPreviewItemsAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = null;

            // TODO: Connect to actual feedBaselineLookup API service
            // For now, simulate async operation with short delay
            await Task.Delay(100, cancellationToken);

            // TODO: Parse response and populate PreviewImages
            // Expected structure:
            // {
            //   data: {
            //     feedBaselineLookup: {
            //       previewItems: [
            //         { trackMetadata: { displayImageUri: "..." } }
            //       ]
            //     }
            //   }
            // }

            IsLoading = false;
        }
        catch (OperationCanceledException)
        {
            // Task was cancelled - this is expected during disposal
            IsLoading = false;
        }
        catch (System.Exception ex)
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = $"Failed to load preview items: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Error loading preview items for {SectionTitle}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }
}
