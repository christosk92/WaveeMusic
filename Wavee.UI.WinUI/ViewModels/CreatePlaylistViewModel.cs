using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;

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

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public CreatePlaylistViewModel()
    {
    }

    public void Initialize(bool isFolder)
    {
        IsFolder = isFolder;
        Name = "";
        Title = isFolder ? "Create Folder" : "Create Playlist";
        IconGlyph = isFolder ? "\uE8F4" : "\uE93F";
        PlaceholderText = isFolder ? "Folder name" : "Playlist name";

        TabItemParameter = new TabItemParameter
        {
            Title = Title,
            PageType = NavigationPageType.CreatePlaylist
        };

        ContentChanged?.Invoke(this, TabItemParameter);
    }

    [RelayCommand]
    private void Create()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return;

        // TODO: Create playlist/folder via Wavee core and get the actual ID
        var createdId = System.Guid.NewGuid().ToString(); // Placeholder ID

        // Navigate to the created playlist/folder in the current tab
        if (IsFolder)
        {
            // For folders, navigate to library
            Helpers.Navigation.NavigationHelpers.OpenLibrary(openInNewTab: false);
        }
        else
        {
            // Navigate to the playlist page
            Helpers.Navigation.NavigationHelpers.OpenPlaylist(createdId, Name, openInNewTab: false);
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
