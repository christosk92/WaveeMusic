using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PlaylistPage : Page
{
    private readonly ILogger? _logger;
    private readonly ISettingsService _settings;
    private bool _isNarrowMode;

    public PlaylistViewModel ViewModel { get; }

    public PlaylistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlaylistViewModel>();
        _logger = Ioc.Default.GetService<ILogger<PlaylistPage>>();
        _settings = Ioc.Default.GetRequiredService<ISettingsService>();
        InitializeComponent();

        Func<object, string> addedFormatter = item =>
        {
            if (item is PlaylistTrackDto track)
                return track.AddedAtFormatted;
            if (item is LazyTrackItem lazy && lazy.Data is PlaylistTrackDto inner)
                return inner.AddedAtFormatted;
            return "";
        };
        TrackGrid.DateAddedFormatter = addedFormatter;

        // Editorial / radio playlists don't carry added-at timestamps — hide the whole
        // Date Added column when the loaded tracks have none.
        ViewModel.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(PlaylistViewModel.HasAnyAddedAt))
                ApplyDateAddedColumnVisibility();
        };
        ApplyDateAddedColumnVisibility();
    }

    private void ApplyDateAddedColumnVisibility()
    {
        if (TrackGrid.Columns is null) return;
        var dateCol = TrackGrid.Columns.FirstOrDefault(c => c.Key == "DateAdded");
        if (dateCol is null) return;
        dateCol.IsVisible = ViewModel.HasAnyAddedAt;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _logger?.LogInformation(
            "PlaylistPage.OnNavigatedTo: parameter type={Type}, value={Value}",
            e.Parameter?.GetType().FullName ?? "<null>", e.Parameter);

        string? playlistId = null;

        if (e.Parameter is Data.Parameters.ContentNavigationParameter nav)
        {
            _logger?.LogInformation(
                "PlaylistPage.OnNavigatedTo: ContentNavigationParameter Uri='{Uri}', Title='{Title}', Subtitle='{Subtitle}', ImageUrl='{ImageUrl}'",
                nav.Uri, nav.Title, nav.Subtitle, nav.ImageUrl);
            playlistId = nav.Uri;
            ViewModel.PrefillFrom(nav);
            ViewModel.Activate(nav.Uri);
        }
        else if (e.Parameter is string rawId && !string.IsNullOrWhiteSpace(rawId))
        {
            _logger?.LogInformation("PlaylistPage.OnNavigatedTo: string parameter '{RawId}'", rawId);
            playlistId = rawId;
            ViewModel.Activate(rawId);
        }
        else
        {
            _logger?.LogWarning("PlaylistPage.OnNavigatedTo: unrecognized parameter shape — no load triggered");
        }

        if (!string.IsNullOrEmpty(playlistId))
            RestorePlaylistPanelWidth(playlistId);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Drop the store subscription — refcount hits zero and any inflight
        // fetch/CTS is cancelled cleanly (kills TaskCanceledException spam
        // that comes from leaving fetches running after navigation).
        ViewModel.Deactivate();
    }

    private void RestorePlaylistPanelWidth(string playlistId)
    {
        const double defaultWidth = 280;
        var key = $"playlist:{playlistId}";

        var width = _settings.Settings.PanelWidths.TryGetValue(key, out var saved)
            ? saved
            : defaultWidth;

        width = Math.Clamp(width, 200, 500);
        LeftPanelColumn.Width = new GridLength(width, GridUnitType.Pixel);
    }

    private void PlaylistArtContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border border && e.NewSize.Width > 0)
            border.Height = e.NewSize.Width;
    }

    private void PlaylistSplitter_ResizeCompleted(object? sender, GridSplitterResizeCompletedEventArgs e)
    {
        var playlistId = ViewModel.PlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;

        _settings.Update(s => s.PanelWidths[$"playlist:{playlistId}"] = e.NewWidth);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var shouldBeNarrow = e.NewSize.Width < 600;

        if (shouldBeNarrow && !_isNarrowMode)
        {
            _isNarrowMode = true;
            LeftPanelColumn.MinWidth = 0;
            LeftPanelColumn.Width = new GridLength(0);
            VisualStateManager.GoToState(this, "NarrowState", true);
        }
        else if (!shouldBeNarrow && _isNarrowMode)
        {
            _isNarrowMode = false;
            LeftPanelColumn.MinWidth = 200;
            var playlistId = ViewModel.PlaylistId;
            if (!string.IsNullOrEmpty(playlistId))
                RestorePlaylistPanelWidth(playlistId);
            else
                LeftPanelColumn.Width = new GridLength(280, GridUnitType.Pixel);

            VisualStateManager.GoToState(this, "WideState", true);
        }
        else if (!shouldBeNarrow && !_isNarrowMode)
        {
            VisualStateManager.GoToState(this, "WideState", true);
        }
    }

}
