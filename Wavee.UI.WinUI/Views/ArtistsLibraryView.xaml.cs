using System;
using System.Collections;
using System.ComponentModel;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ArtistsLibraryView : UserControl, IDisposable
{
    private const double NarrowLayoutBreakpoint = 800;
    private bool _hasInitializedLayoutMode;
    private bool _disposed;

    public ArtistsLibraryViewModel ViewModel { get; }

    public ArtistsLibraryView(ArtistsLibraryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Subscribed once for the lifetime of this long-lived UserControl.
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;

        // Load is idempotent (guarded in the VM); called once on first creation.
        _ = ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutMode(preserveContext: false);

        // Sync selection when loaded into the visual tree
        SyncSelectionToItemsView();

        // Initialize tracks panel state
        UpdateTracksPanelVisibility(ViewModel.IsTracksPanelVisible, animate: false);

        ApplyArtistsViewMode();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutMode(preserveContext: _hasInitializedLayoutMode);
    }

    private void UpdateLayoutMode(bool preserveContext)
    {
        var isNarrow = ActualWidth <= NarrowLayoutBreakpoint;
        ViewModel.SetNarrowLayout(isNarrow, preserveContext);
        _hasInitializedLayoutMode = true;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.IsTracksPanelVisible))
        {
            UpdateTracksPanelVisibility(ViewModel.IsTracksPanelVisible, animate: true);
        }
        else if (e.PropertyName == nameof(ViewModel.ViewMode))
        {
            ApplyArtistsViewMode();
        }
    }

    /// <summary>
    /// Swaps the artists <see cref="ItemsView.Layout"/> and <see cref="ItemsView.ItemTemplate"/>
    /// based on the selected <see cref="LibraryViewMode"/>. The inline template defined in XAML
    /// is the DefaultList variant; the other three live in <c>UserControl.Resources</c>.
    /// </summary>
    private void ApplyArtistsViewMode()
    {
        if (ArtistsView is null) return;

        var resources = Resources;
        switch (ViewModel.ViewMode)
        {
            case LibraryViewMode.CompactList:
                ArtistsView.Layout = new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 };
                ApplyTemplateFromResources("ArtistCompactListItemTemplate");
                break;

            case LibraryViewMode.CompactGrid:
                ArtistsView.Layout = new UniformGridLayout
                {
                    MinItemWidth = 104,
                    MinItemHeight = 104,
                    MinRowSpacing = 8,
                    MinColumnSpacing = 8,
                    ItemsStretch = UniformGridLayoutItemsStretch.None
                };
                ApplyTemplateFromResources("ArtistCompactGridItemTemplate");
                break;

            case LibraryViewMode.DefaultGrid:
                ArtistsView.Layout = new UniformGridLayout
                {
                    MinItemWidth = 112,
                    MinItemHeight = 150,
                    MinRowSpacing = 4,
                    MinColumnSpacing = 4,
                    ItemsStretch = UniformGridLayoutItemsStretch.None
                };
                ApplyTemplateFromResources("ArtistDefaultGridItemTemplate");
                break;

            case LibraryViewMode.DefaultList:
            default:
                ArtistsView.Layout = new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 };
                ApplyTemplateFromResources("ArtistDefaultListItemTemplate");
                break;
        }
    }

    private void ApplyTemplateFromResources(string resourceKey)
    {
        if (ArtistsView is null) return;
        if (Resources.TryGetValue(resourceKey, out var tpl) && tpl is Microsoft.UI.Xaml.IElementFactory factory)
            ArtistsView.ItemTemplate = factory;
    }

    private void UpdateTracksPanelVisibility(bool isVisible, bool animate)
    {
        if (TracksPanelBorder == null || TracksPanelColumn == null) return;

        // Use star sizing when visible so panel fills available space (respecting MinWidth on Border)
        var visibleWidth = new GridLength(1, GridUnitType.Star);
        var hiddenWidth = new GridLength(0);

        if (!animate)
        {
            TracksPanelColumn.Width = isVisible ? visibleWidth : hiddenWidth;
            TracksPanelBorder.Opacity = isVisible ? 1 : 0;
            return;
        }

        if (isVisible)
        {
            // Expand column first, then animate content in
            TracksPanelColumn.Width = visibleWidth;

            AnimationBuilder.Create()
                .Translation(Axis.X, from: 100, to: 0, duration: TimeSpan.FromMilliseconds(300))
                .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300))
                .Start(TracksPanelBorder);
        }
        else
        {
            // Animate content out, then collapse column
            AnimationBuilder.Create()
                .Translation(Axis.X, from: 0, to: 100, duration: TimeSpan.FromMilliseconds(200))
                .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
                .Start(TracksPanelBorder, () =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        TracksPanelColumn.Width = hiddenWidth;
                    });
                });
        }
    }

    private void ArtistsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem != ViewModel.SelectedArtist)
        {
            ViewModel.SelectedArtist = sender.SelectedItem as Data.DTOs.LibraryArtistDto;
        }
    }

    private void ArtistsView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedArtist is not { } artist) return;

        // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph
        // // Prepare connected animation from the detail panel artist image
        // Helpers.ConnectedAnimationHelper.PrepareAnimation(
        //     Helpers.ConnectedAnimationHelper.ArtistImage, DetailArtistImageContainer);

        var param = new ContentNavigationParameter
        {
            Uri = artist.Id,
            Title = artist.Name,
            ImageUrl = artist.ImageUrl
        };
        NavigationHelpers.OpenArtist(param, artist.Name, NavigationHelpers.IsCtrlPressed());
    }

    private void ViewArtistButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedArtist is not { } artist) return;

        // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph
        // Helpers.ConnectedAnimationHelper.PrepareAnimation(
        //     Helpers.ConnectedAnimationHelper.ArtistImage, DetailArtistImageContainer);

        var param = new ContentNavigationParameter
        {
            Uri = artist.Id,
            Title = artist.Name,
            ImageUrl = artist.ImageUrl
        };
        NavigationHelpers.OpenArtist(param, artist.Name, NavigationHelpers.IsCtrlPressed());
    }

    private void ViewAlbumButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAlbumForTracks == null) return;
        var album = ViewModel.SelectedAlbumForTracks.Album;

        // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph
        // Helpers.ConnectedAnimationHelper.PrepareAnimation(
        //     Helpers.ConnectedAnimationHelper.AlbumArt, DetailAlbumImageContainer);

        var param = new ContentNavigationParameter
        {
            Uri = album.Id,
            Title = album.Name,
            ImageUrl = album.ImageUrl
        };
        NavigationHelpers.OpenAlbum(param, album.Name, NavigationHelpers.IsCtrlPressed());
    }

    private void NarrowArtistsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is Data.DTOs.LibraryArtistDto artist)
        {
            ViewModel.ShowSelectedArtistDetails(artist);
        }
    }

    private void NarrowArtistItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Data.DTOs.LibraryArtistDto artist)
        {
            ViewModel.ShowSelectedArtistDetails(artist);
        }
    }

    private void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        switch (args.Index)
        {
            case 0:
                ViewModel.ShowArtistsRoot();
                break;
            case 1:
                ViewModel.ShowSelectedArtistDetails();
                break;
        }
    }

    private void SyncSelectionToItemsView()
    {
        if (ArtistsView is null) return;

        if (ViewModel.SelectedArtist is null)
        {
            ArtistsView.DeselectAll();
        }
        else if (ViewModel.FilteredArtists is IList list)
        {
            var index = list.IndexOf(ViewModel.SelectedArtist);
            if (index >= 0 && ArtistsView.SelectedItem != ViewModel.SelectedArtist)
            {
                ArtistsView.Select(index);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
    }
}
