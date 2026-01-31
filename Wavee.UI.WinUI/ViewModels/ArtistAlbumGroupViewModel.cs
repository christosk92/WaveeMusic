using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for a group of albums (Albums, Singles & EPs, Compilations).
/// Each group has its own Grid/List toggle.
/// </summary>
public sealed partial class ArtistAlbumGroupViewModel : ObservableObject
{
    private readonly ICatalogService _catalogService;
    private readonly Action<ArtistAlbumItemViewModel>? _onAlbumSelected;

    public string GroupName { get; }
    public string GroupType { get; }

    [ObservableProperty]
    private ObservableCollection<ArtistAlbumItemViewModel> _albums = [];

    [ObservableProperty]
    private bool _isListView;

    [ObservableProperty]
    private ArtistAlbumItemViewModel? _selectedAlbum;

    public ArtistAlbumGroupViewModel(
        string groupName,
        string groupType,
        IEnumerable<LibraryArtistAlbumDto> albums,
        ICatalogService catalogService,
        Action<ArtistAlbumItemViewModel>? onAlbumSelected = null)
    {
        GroupName = groupName;
        GroupType = groupType;
        _catalogService = catalogService;
        _onAlbumSelected = onAlbumSelected;

        foreach (var album in albums)
        {
            Albums.Add(new ArtistAlbumItemViewModel(album, OnAlbumItemSelected));
        }
    }

    private void OnAlbumItemSelected(ArtistAlbumItemViewModel album)
    {
        // In grid view with callback, show tracks in third column
        // In list view, tracks are shown inline so navigate to album page
        if (!IsListView && _onAlbumSelected != null)
        {
            _onAlbumSelected(album);
        }
        else
        {
            // Fallback: navigate to album page
            Helpers.Navigation.NavigationHelpers.OpenAlbum(album.Album.Id, album.Album.Name);
        }
    }

    partial void OnSelectedAlbumChanged(ArtistAlbumItemViewModel? value)
    {
        // When selection changes via ItemsView, notify parent
        if (value != null && !IsListView && _onAlbumSelected != null)
        {
            _onAlbumSelected(value);
        }
    }

    [RelayCommand]
    private void ToggleViewMode()
    {
        IsListView = !IsListView;
    }

    partial void OnIsListViewChanged(bool value)
    {
        if (value)
        {
            _ = LoadTracksForAlbumsAsync();
        }
    }

    private async Task LoadTracksForAlbumsAsync()
    {
        // Load tracks for all albums that haven't been loaded yet
        var loadTasks = Albums
            .Where(a => !a.HasLoadedTracks && !a.IsLoadingTracks)
            .Select(a => a.LoadTracksAsync(_catalogService));

        await Task.WhenAll(loadTasks);
    }

    [RelayCommand]
    private void PlayAlbum(ArtistAlbumItemViewModel? album)
    {
        if (album == null) return;
        // TODO: Play album via Wavee core
    }

    [RelayCommand]
    private void OpenAlbum(ArtistAlbumItemViewModel? album)
    {
        if (album == null) return;

        // In grid view with callback, show tracks in third column
        // In list view, tracks are shown inline so don't trigger callback
        if (!IsListView && _onAlbumSelected != null)
        {
            _onAlbumSelected(album);
        }
        else
        {
            // Fallback: navigate to album page
            Helpers.Navigation.NavigationHelpers.OpenAlbum(album.Album.Id, album.Album.Name);
        }
    }

    [RelayCommand]
    private void PlayTrack(AlbumTrackDto? track)
    {
        if (track == null) return;
        // TODO: Play track via Wavee core
    }
}
