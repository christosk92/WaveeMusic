using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ProfilePage : Page, ITabBarItemContent
{
    private readonly ProfileCache? _cache;

    public ProfileViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ProfilePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ProfileViewModel>();
        _cache = Ioc.Default.GetService<ProfileCache>();
        InitializeComponent();

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        if (_cache != null)
            _cache.DataRefreshed += OnCacheDataRefreshed;

        Unloaded += ProfilePage_Unloaded;
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProfileViewModel.ProfileImageUrl):
                UpdateProfileAvatar(ViewModel.ProfileImageUrl);
                break;
            case nameof(ProfileViewModel.HeroColorHex):
                UpdateHeroColor(ViewModel.HeroColorHex);
                break;
        }
    }

    private void UpdateProfileAvatar(string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            ProfileAvatar.ProfilePicture = null;
            return;
        }

        // Only load https:// URLs directly; spotify: URIs need conversion later
        if (imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ProfileAvatar.ProfilePicture = new BitmapImage(new Uri(imageUrl));
        }
    }

    private void UpdateHeroColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return;

        hex = hex.TrimStart('#');
        if (hex.Length < 6) return;

        var r = Convert.ToByte(hex[0..2], 16);
        var g = Convert.ToByte(hex[2..4], 16);
        var b = Convert.ToByte(hex[4..6], 16);

        HeroColorTop.Color = Windows.UI.Color.FromArgb(255, r, g, b);
        HeroColorMid.Color = Windows.UI.Color.FromArgb(120, r, g, b);
    }

    private void OnCacheDataRefreshed(ProfileSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() => ViewModel.ApplyBackgroundRefresh(snapshot));
    }

    private void ProfilePage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;

        if (_cache != null)
            _cache.DataRefreshed -= OnCacheDataRefreshed;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Initialize();
    }
}
