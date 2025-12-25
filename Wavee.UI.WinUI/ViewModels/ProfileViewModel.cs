using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ProfileViewModel : ObservableObject, ITabBarItemContent
{
    [ObservableProperty]
    private string _userName = "User";

    [ObservableProperty]
    private string? _profileImageUrl;

    [ObservableProperty]
    private string _displayName = "Music Lover";

    [ObservableProperty]
    private int _followersCount;

    [ObservableProperty]
    private int _followingCount;

    [ObservableProperty]
    private int _playlistCount;

    public TabItemParameter? TabItemParameter { get; private set; }

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ProfileViewModel()
    {
        // Initialize with placeholder data
        TabItemParameter = new TabItemParameter
        {
            Title = "Profile",
            PageType = NavigationPageType.Profile
        };
    }

    public void Initialize()
    {
        // TODO: Load actual user profile data
        UserName = "spotify_user";
        DisplayName = "Music Lover";
        FollowersCount = 42;
        FollowingCount = 128;
        PlaylistCount = 15;

        ContentChanged?.Invoke(this, TabItemParameter!);
    }

    [RelayCommand]
    private void EditProfile()
    {
        // TODO: Open profile edit dialog
    }

    [RelayCommand]
    private void OpenSettings()
    {
        // TODO: Navigate to settings
    }
}
