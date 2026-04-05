using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
    private bool _showingContent;

    public ProfileViewModel ViewModel { get; }

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ProfilePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ProfileViewModel>();
        _cache = Ioc.Default.GetService<ProfileCache>();
        InitializeComponent();

        // Hide content initially via composition visual (not XAML Opacity — they multiply)
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;

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

        // Transition: loading started while showing content → show shimmer
        if (e.PropertyName == nameof(ProfileViewModel.IsLoading))
        {
            if (ViewModel.IsLoading && _showingContent)
                ShowShimmer();
        }

        // Transition: loading finished with data → crossfade to content
        if (e.PropertyName is nameof(ProfileViewModel.IsLoading) or nameof(ProfileViewModel.HasData))
        {
            if (!ViewModel.IsLoading && ViewModel.HasData && !_showingContent)
                CrossfadeToContent();
        }
    }

    // ── Crossfade transitions (same pattern as HomePage) ──

    private void CrossfadeToContent()
    {
        _showingContent = true;

        // Fade out shimmer
        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
            .Start(ShimmerContainer);

        // Fade in content
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                     delay: TimeSpan.FromMilliseconds(100))
            .Start(ContentContainer);

        // Collapse shimmer after animation to prevent hit testing
        _ = CollapseShimmerAfterDelay();
    }

    private void ShowShimmer()
    {
        _showingContent = false;
        ShimmerContainer.Visibility = Visibility.Visible;

        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(200))
            .Start(ShimmerContainer);

        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
            .Start(ContentContainer);
    }

    private const int ShimmerCollapseDelayMs = 500;

    private async Task CollapseShimmerAfterDelay()
    {
        await Task.Delay(ShimmerCollapseDelayMs);
        if (_showingContent)
            ShimmerContainer.Visibility = Visibility.Collapsed;
    }

    // ── Existing helpers ──

    private void UpdateProfileAvatar(string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            ProfileAvatar.ProfilePicture = null;
            return;
        }

        if (imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var cache = Ioc.Default.GetService<ImageCacheService>();
            ProfileAvatar.ProfilePicture = cache?.GetOrCreate(imageUrl, 100);
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

    private void ProfilePage_Unloaded(object sender, RoutedEventArgs e)
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
