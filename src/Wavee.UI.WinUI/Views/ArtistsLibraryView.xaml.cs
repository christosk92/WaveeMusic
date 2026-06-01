using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.InPageFilter;
using Wavee.UI.WinUI.Controls.Layouts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ArtistsLibraryView : UserControl, IDisposable, IInPageFilterable
{
    // ── IInPageFilterable ───────────────────────────────────────────────
    string IInPageFilterable.FilterQuery
    {
        get => ViewModel?.SearchQuery ?? string.Empty;
        set { if (ViewModel is { } vm) vm.SearchQuery = value ?? string.Empty; }
    }
    string IInPageFilterable.FilterPlaceholder => AppLocalization.GetString("Filter_ArtistsPlaceholder");
    bool IInPageFilterable.CanFilter => ViewModel is not null;

    private const double NarrowLayoutBreakpoint = 680;
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
        SyncLikedSelectionToItemsView();

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
        else if (e.PropertyName == nameof(ViewModel.SourceMode))
        {
            ApplyArtistsViewMode();
            DispatcherQueue.TryEnqueue(ApplyArtistsViewMode);
        }
    }

    /// <summary>
    /// Swaps the artists <see cref="ItemsView.Layout"/> and <see cref="ItemsView.ItemTemplate"/>
    /// based on the selected <see cref="LibraryViewMode"/>. The inline template defined in XAML
    /// is the DefaultList variant; the other three live in <c>UserControl.Resources</c>.
    /// </summary>
    private void ApplyArtistsViewMode()
    {
        if (ArtistsView is null && LikedArtistsView is null) return;

        switch (ViewModel.ViewMode)
        {
            case LibraryViewMode.CompactList:
                ApplyArtistListLayout(new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 });
                ApplyTemplateFromResources("ArtistCompactListItemTemplate");
                ApplyLikedTemplateFromResources("LikedArtistCompactListItemTemplate");
                break;

            case LibraryViewMode.CompactGrid:
                ApplyArtistListLayout(new ResponsiveGridLayout
                {
                    MinItemWidth = 104,
                    ColumnSpacing = 8,
                    RowSpacing = 8,
                    AspectRatio = 1.0,
                    // Compact card shows the name only (~1 line) under the avatar.
                    TextBandHeight = 34
                });
                ApplyTemplateFromResources("ArtistCompactGridItemTemplate");
                ApplyLikedTemplateFromResources("LikedArtistCompactGridItemTemplate");
                break;

            case LibraryViewMode.DefaultGrid:
                ApplyArtistListLayout(new ResponsiveGridLayout
                {
                    // CSS auto-fill + 1fr: circular avatars grow with the column width,
                    // and the row height = avatar (square) + a fixed text band sized for
                    // the From-Liked-Songs card's 3 lines (name + "N liked songs" +
                    // recents). No clip, no empty space at any width.
                    MinItemWidth = 150,
                    ColumnSpacing = 12,
                    RowSpacing = 12,
                    AspectRatio = 1.0,
                    TextBandHeight = 88
                });
                ApplyTemplateFromResources("ArtistDefaultGridItemTemplate");
                ApplyLikedTemplateFromResources("LikedArtistDefaultGridItemTemplate");
                break;

            case LibraryViewMode.DefaultList:
            default:
                ApplyArtistListLayout(new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 });
                ApplyTemplateFromResources("ArtistDefaultListItemTemplate");
                ApplyLikedTemplateFromResources("LikedArtistDefaultListItemTemplate");
                break;
        }
    }

    private void ApplyArtistListLayout(Layout layout)
    {
        if (ArtistsView is not null)
            ArtistsView.Layout = layout;
        if (LikedArtistsView is not null)
            LikedArtistsView.Layout = CloneLayout(layout);
    }

    private static Layout CloneLayout(Layout layout) => layout switch
    {
        ResponsiveGridLayout r => new ResponsiveGridLayout
        {
            MinItemWidth = r.MinItemWidth,
            ColumnSpacing = r.ColumnSpacing,
            RowSpacing = r.RowSpacing,
            AspectRatio = r.AspectRatio,
            TextBandHeight = r.TextBandHeight
        },
        UniformGridLayout u => new UniformGridLayout
        {
            MinItemWidth = u.MinItemWidth,
            MinItemHeight = u.MinItemHeight,
            MinRowSpacing = u.MinRowSpacing,
            MinColumnSpacing = u.MinColumnSpacing,
            ItemsStretch = u.ItemsStretch
        },
        _ => new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 }
    };

    private void ApplyTemplateFromResources(string resourceKey)
    {
        if (ArtistsView is null) return;
        if (Resources.TryGetValue(resourceKey, out var tpl) && tpl is Microsoft.UI.Xaml.IElementFactory factory)
            ArtistsView.ItemTemplate = factory;
    }

    private void ApplyLikedTemplateFromResources(string resourceKey)
    {
        if (LikedArtistsView is null) return;
        if (Resources.TryGetValue(resourceKey, out var tpl) && tpl is Microsoft.UI.Xaml.IElementFactory factory)
            LikedArtistsView.ItemTemplate = factory;
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
        if (sender.SelectedItem is null && ViewModel.SelectedArtist is { } current)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed)
                    return;

                if (ReferenceEquals(ViewModel.SelectedArtist, current)
                    && ViewModel.FilteredArtists.Any(a => string.Equals(a.Id, current.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    SyncSelectionToItemsView();
                }
            });
            return;
        }

        if (sender.SelectedItem != ViewModel.SelectedArtist)
        {
            ViewModel.SelectedArtist = sender.SelectedItem as Wavee.UI.Models.LibraryArtistDto;
        }
    }

    private void ArtistsView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedArtist is not { } artist) return;

        var param = new ContentNavigationParameter
        {
            Uri = artist.Id,
            Title = artist.Name,
            ImageUrl = artist.ImageUrl
        };
        NavigationHelpers.OpenArtist(param, artist.Name, NavigationHelpers.IsCtrlPressed());
    }

    private void LikedArtistsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null && ViewModel.SelectedLikedArtist is { } current)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed)
                    return;

                if (ReferenceEquals(ViewModel.SelectedLikedArtist, current)
                    && ViewModel.FilteredLikedArtists.Any(a => string.Equals(a.Id, current.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    SyncLikedSelectionToItemsView();
                }
            });
            return;
        }

        if (sender.SelectedItem != ViewModel.SelectedLikedArtist)
        {
            ViewModel.SelectedLikedArtist = sender.SelectedItem as LikedArtistDto;
        }
    }

    private void LikedArtistsView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedLikedArtist is not { } artist || !artist.CanOpenArtist) return;

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
        if (ViewModel.SelectedLikedArtist is { } likedArtist)
        {
            if (!likedArtist.CanOpenArtist) return;

            var likedParam = new ContentNavigationParameter
            {
                Uri = likedArtist.Id,
                Title = likedArtist.Name,
                ImageUrl = likedArtist.ImageUrl
            };
            NavigationHelpers.OpenArtist(likedParam, likedArtist.Name, NavigationHelpers.IsCtrlPressed());
            return;
        }

        if (ViewModel.SelectedArtist is not { } artist) return;

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
        if (sender.SelectedItem is Wavee.UI.Models.LibraryArtistDto artist)
        {
            ViewModel.ShowSelectedArtistDetails(artist);
        }
    }

    private void NarrowArtistItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Wavee.UI.Models.LibraryArtistDto artist)
        {
            ViewModel.ShowSelectedArtistDetails(artist);
        }
    }

    private void NarrowLikedArtistsView_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is LikedArtistDto artist)
        {
            ViewModel.ShowSelectedLikedArtistDetails(artist);
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
                if (ViewModel.SourceMode == LibrarySource.FromLikedSongs)
                    ViewModel.ShowSelectedLikedArtistDetails();
                else
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

    private void SyncLikedSelectionToItemsView()
    {
        if (LikedArtistsView is null) return;

        if (ViewModel.SelectedLikedArtist is null)
        {
            LikedArtistsView.DeselectAll();
        }
        else if (ViewModel.FilteredLikedArtists is IList list)
        {
            var index = list.IndexOf(ViewModel.SelectedLikedArtist);
            if (index >= 0 && LikedArtistsView.SelectedItem != ViewModel.SelectedLikedArtist)
            {
                LikedArtistsView.Select(index);
            }
        }
    }

    private void LikedArtistItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not LikedArtistDto artist) return;

        var flyout = new MenuFlyout();
        var item = new MenuFlyoutItem
        {
            Text = $"Unlike all songs from this artist ({artist.LikedSongCount})",
            Icon = new FontIcon { Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.HeartFilled }
        };
        item.Click += async (_, _) => await ConfirmAndUnlikeAllAsync(artist);
        flyout.Items.Add(item);

        flyout.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private async System.Threading.Tasks.Task ConfirmAndUnlikeAllAsync(LikedArtistDto artist)
    {
        var dialog = new ContentDialog
        {
            Title = AppLocalization.GetString("Library_UnlikeAllFromArtistTitle"),
            Content = artist.LikedSongCount == 1
                ? AppLocalization.Format("Library_UnlikeAllFromArtist_OneContent", artist.Name)
                : AppLocalization.Format("Library_UnlikeAllFromArtist_ManyContent", artist.LikedSongCount, artist.Name),
            PrimaryButtonText = AppLocalization.GetString("Dialog_UnlikeAll"),
            CloseButtonText = AppLocalization.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            ViewModel.UnlikeAllSongsFromLikedArtist(artist);
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
