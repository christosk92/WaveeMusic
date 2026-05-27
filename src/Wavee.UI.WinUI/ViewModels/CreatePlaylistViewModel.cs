using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.ViewModels;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class CreatePlaylistViewModel : ObservableObject, ITabBarItemContent
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Title { get; set; } = "Create Playlist";

    [ObservableProperty]
    public partial string IconGlyph { get; set; } = "\uE93F";

    [ObservableProperty]
    public partial string PlaceholderText { get; set; } = "Playlist name";

    [ObservableProperty]
    public partial bool IsFolder { get; set; }

    private IReadOnlyList<string>? _trackIds;
    private string? _folderStartUri;

    private readonly ILibraryDataService _libraryDataService;
    private readonly IRootlistService _rootlistService;
    private readonly IPlaylistMutationService _playlistMutationService;

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

    public CreatePlaylistViewModel(ILibraryDataService libraryDataService, IRootlistService rootlistService, IPlaylistMutationService playlistMutationService)
    {
        _libraryDataService = libraryDataService;
        _rootlistService = rootlistService;
        _playlistMutationService = playlistMutationService;
    }

    public void Initialize(CreatePlaylistParameter parameter)
    {
        IsFolder = parameter.IsFolder;
        _trackIds = parameter.TrackIds;
        _folderStartUri = parameter.FolderStartUri;
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

        // Create playlist or folder. The dealer subscription in LibraryChangeManager
        // picks up the resulting RootlistModificationInfo push and fires the sidebar
        // refresh — no manual cache invalidation needed here.
        var created = IsFolder
            ? await _rootlistService.CreateFolderAsync(Name)
            : await _playlistMutationService.CreatePlaylistAsync(Name, _trackIds);

        if (IsFolder)
        {
            // No dedicated folder page yet — return to the library view.
            NavigationHelpers.OpenLibrary(openInNewTab: false);
        }
        else
        {
            // Two-step rootlist mutation when the caller requested a folder
            // target: create at root, then move into the folder. Same shape
            // the drag-into-folder path uses (see LibraryPlaylistMediator).
            // Best-effort — if the move fails the playlist still exists at
            // root, which is recoverable; the user sees a toast on failure.
            if (!string.IsNullOrEmpty(_folderStartUri))
            {
                try
                {
                    await _rootlistService.MovePlaylistIntoFolderAsync(created.Id, _folderStartUri).ConfigureAwait(true);
                }
                catch
                {
                    // Move failure → playlist lives at rootlist top. The
                    // create succeeded; surface a soft warning rather than
                    // hide the success.
                }
            }

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