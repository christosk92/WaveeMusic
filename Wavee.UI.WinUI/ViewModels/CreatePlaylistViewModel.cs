using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class CreatePlaylistViewModel : ObservableObject, ITabBarItemContent
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _title = "Create Playlist";

    [ObservableProperty]
    private string _iconGlyph = "\uE93F";

    [ObservableProperty]
    private string _placeholderText = "Playlist name";

    [ObservableProperty]
    private bool _isFolder;

    private IReadOnlyList<string>? _trackIds;

    private readonly ILibraryDataService _libraryDataService;

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    /// <summary>
    /// Number of tracks to be added after creation.
    /// </summary>
    public int TrackCount => _trackIds?.Count ?? 0;

    /// <summary>
    /// Whether there are tracks to add.
    /// </summary>
    public bool HasTracks => TrackCount > 0;

    /// <summary>
    /// Display text for track count info.
    /// </summary>
    public string TracksInfoText => HasTracks
        ? $"Adding {TrackCount} track{(TrackCount == 1 ? "" : "s")}"
        : "";

    public CreatePlaylistViewModel(ILibraryDataService libraryDataService)
    {
        _libraryDataService = libraryDataService;
    }

    public void Initialize(CreatePlaylistParameter parameter)
    {
        IsFolder = parameter.IsFolder;
        _trackIds = parameter.TrackIds;
        Name = "";
        Title = parameter.IsFolder ? "Create Folder" : "Create Playlist";
        IconGlyph = parameter.IsFolder ? "\uE8F4" : "\uE93F";
        PlaceholderText = parameter.IsFolder ? "Folder name" : "Playlist name";

        TabItemParameter = new TabItemParameter
        {
            Title = Title,
            PageType = NavigationPageType.CreatePlaylist
        };

        OnPropertyChanged(nameof(TrackCount));
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(TracksInfoText));

        ContentChanged?.Invoke(this, TabItemParameter);
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return;

        // Create playlist via service (will trigger PlaylistsChanged event for sidebar update)
        var created = await _libraryDataService.CreatePlaylistAsync(Name, _trackIds);

        // Navigate to the created playlist/folder in the current tab
        if (IsFolder)
        {
            // For folders, navigate to library
            NavigationHelpers.OpenLibrary(openInNewTab: false);
        }
        else
        {
            // Navigate to the playlist page
            NavigationHelpers.OpenPlaylist(created.Id, created.Name, openInNewTab: false);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseCurrentTab();
    }

    private void CloseCurrentTab()
    {
        // Find and close the current tab
        var currentIndex = App.AppModel.TabStripSelectedIndex;
        if (currentIndex >= 0 && currentIndex < ShellViewModel.TabInstances.Count)
        {
            var tab = ShellViewModel.TabInstances[currentIndex];
            ShellViewModel.TabInstances.RemoveAt(currentIndex);
            tab.Dispose();

            // If no tabs left, open home
            if (ShellViewModel.TabInstances.Count == 0)
            {
                Helpers.Navigation.NavigationHelpers.OpenHome();
            }
            else if (currentIndex >= ShellViewModel.TabInstances.Count)
            {
                App.AppModel.TabStripSelectedIndex = ShellViewModel.TabInstances.Count - 1;
            }
        }
    }
}
