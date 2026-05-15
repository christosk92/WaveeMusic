using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.HeroHeader;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ProfilePage : Page, ITabBarItemContent, INavigationCacheMemoryParticipant, IDisposable
{
    private readonly ProfileCache? _cache;
    private readonly ILogger<ProfilePage>? _logger;
    private bool _showingContent;
    private bool _isNavigatingAway;

    private ShyHeaderController? _shyHeader;
    private bool _identityCardRevealed;
    private bool _pageBleedRevealed;
    private bool _isDisposed;
    private bool _viewSubscriptionsAttached;
    private bool _dataSubscriptionsAttached;
    private bool _trimmedForNavigationCache;

    public ProfileViewModel ViewModel { get; }

    public ShimmerLoadGate ShimmerGate { get; } = new();

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ProfilePage()
    {
        ViewModel = Ioc.Default.GetRequiredService<ProfileViewModel>();
        _cache = Ioc.Default.GetService<ProfileCache>();
        _logger = Ioc.Default.GetService<ILogger<ProfilePage>>();
        InitializeComponent();

        // Start every "depends-on-data" surface invisible at composition level.
        // Each one is faded in independently as its data arrives — prevents the
        // hard pop where IdentityCard renders blank, then suddenly fills with
        // name/avatar/palette tint while shimmer is still on screen below.
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;
        ElementCompositionPreview.GetElementVisual(IdentityCardWrap).Opacity = 0;
        ElementCompositionPreview.GetElementVisual(PageBleedHost).Opacity = 0;

        AttachDataSubscriptions();

        Loaded += ProfilePage_Loaded;
        Unloaded += ProfilePage_Unloaded;
    }

    // ── Lifecycle ──

    private void ProfilePage_Loaded(object sender, RoutedEventArgs e)
    {
        AttachDataSubscriptions();
        AttachViewSubscriptions();
    }

    private void AttachViewSubscriptions()
    {
        if (_viewSubscriptionsAttached)
            return;

        _isNavigatingAway = false;
        EnsureShyHeader();
        ViewModel.IsDarkTheme = ActualTheme == ElementTheme.Dark;
        ActualThemeChanged += ProfilePage_ActualThemeChanged;
        UpdateIdentityCardBackground();
        _shyHeader?.Attach();
        IdentityCardWrap.SizeChanged += IdentityCardWrap_SizeChanged;
        _viewSubscriptionsAttached = true;
    }

    private void EnsureShyHeader()
    {
        if (_shyHeader is not null) return;
        _shyHeader = new ShyHeaderController(
            PageScrollView, IdentityCardWrap, HeroOverlayPanel, ShyHeaderCard,
            (TransitionHelper)Resources["ProfileShyHeaderTransition"],
            // Profile has no traditional hero — the bleed wash is the visual
            // that fades as the user scrolls past the identity card. Ignore
            // the controller's progress arg; UpdatePageBleedOpacity has its
            // own formula (Max(80, cardHeight)) the controller doesn't know.
            applyHeroFade: _ => UpdatePageBleedOpacity(),
            ShyHeaderPinOffset.BelowElement(IdentityCardWrap, 32),
            canEvaluate: () => !_isNavigatingAway,
            logger: _logger);
    }

    private void ProfilePage_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ViewModel.IsDarkTheme = ActualTheme == ElementTheme.Dark;
        UpdateIdentityCardBackground();
    }

    private void ProfilePage_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachViewSubscriptions();
    }

    private void CleanupSubscriptions()
    {
        DetachViewSubscriptions();
        DetachDataSubscriptions();
    }

    private void DetachViewSubscriptions()
    {
        _isNavigatingAway = true;
        if (!_viewSubscriptionsAttached)
            return;

        _shyHeader?.Detach();
        if (IdentityCardWrap != null)
            IdentityCardWrap.SizeChanged -= IdentityCardWrap_SizeChanged;
        ActualThemeChanged -= ProfilePage_ActualThemeChanged;
        _viewSubscriptionsAttached = false;
    }

    private void AttachDataSubscriptions()
    {
        if (_dataSubscriptionsAttached)
            return;

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        if (_cache != null)
            _cache.DataRefreshed += OnCacheDataRefreshed;

        _dataSubscriptionsAttached = true;
    }

    private void DetachDataSubscriptions()
    {
        if (!_dataSubscriptionsAttached)
            return;

        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;

        if (_cache != null)
            _cache.DataRefreshed -= OnCacheDataRefreshed;

        _dataSubscriptionsAttached = false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Loaded -= ProfilePage_Loaded;
        Unloaded -= ProfilePage_Unloaded;
        CleanupSubscriptions();
        _shyHeader?.Dispose();
        _shyHeader = null;
        if (ProfileAvatar != null)
            ProfileAvatar.ProfilePicture = null;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadProfileParameter(e.Parameter);
    }

    /// <summary>
    /// Same-tab profile→profile navigation reuses this Page instance and never
    /// fires <see cref="OnNavigatedTo"/> — TabBarItem.Navigate routes through
    /// this method instead. Without it, clicking a different profile from the
    /// player bar / a song's context menu while ProfilePage is the active tab
    /// content silently drops the new parameter.
    /// </summary>
    public void RefreshWithParameter(object? parameter)
        => LoadProfileParameter(parameter);

    private void LoadProfileParameter(object? parameter)
    {
        _isNavigatingAway = false;
        _trimmedForNavigationCache = false;
        AttachDataSubscriptions();
        ViewModel.Initialize(parameter as ContentNavigationParameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        TrimForNavigationCache();
    }

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = true;
        _isNavigatingAway = true;
        _shyHeader?.Stop();
        DetachDataSubscriptions();
        ViewModel.Hibernate();

        if (ProfileAvatar != null)
            ProfileAvatar.ProfilePicture = null;
        if (PageBleedHost != null)
            PageBleedHost.Opacity = 0;
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling is no longer rooted by the (singleton-store-subscribed) VM —
        // without this the entire page tree is pinned across navigations.
        Bindings?.StopTracking();
    }

    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = false;
        _isNavigatingAway = false;
        Bindings?.Update();
        AttachDataSubscriptions();
        ViewModel.ResumeFromHibernate();
        UpdateProfileAvatar(ViewModel.ProfileImageUrl);
        UpdateIdentityCardBackground();
        _shyHeader?.Reset();
    }

    // ── ViewModel events ──

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
                UpdateIdentityCardBackground();
                RevealPageBleedIfReady();
                break;
            case nameof(ProfileViewModel.DisplayName):
                RevealIdentityCardIfReady();
                break;
        }

        if (e.PropertyName == nameof(ProfileViewModel.IsLoading))
        {
            if (ViewModel.IsLoading && _showingContent)
                ShowShimmer();
        }

        if (e.PropertyName is nameof(ProfileViewModel.IsLoading) or nameof(ProfileViewModel.HasData))
        {
            if (!ViewModel.IsLoading && ViewModel.HasData && !_showingContent && !_crossfadeScheduled)
                ScheduleCrossfade();
        }
    }

    private void RevealIdentityCardIfReady()
    {
        if (_identityCardRevealed || string.IsNullOrEmpty(ViewModel.DisplayName)) return;
        _identityCardRevealed = true;
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(280))
            .Start(IdentityCardWrap);
    }

    private void RevealPageBleedIfReady()
    {
        if (_pageBleedRevealed || ViewModel.PageBleedBrush is null) return;
        _pageBleedRevealed = true;
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(360))
            .Start(PageBleedHost);
    }

    // ── Crossfade ──
    // Shimmer and content occupy the same Grid cell in XAML so the visual
    // overlap is in place. The remaining hazard is layout churn: when the
    // ItemsRepeaters realize their cards mid-fade, the cell resizes and the
    // page bumps. We mitigate that by:
    //   1. Scheduling the crossfade on a low-priority dispatcher tick — gives
    //      the layout system at least one full pass to settle before we begin.
    //   2. Re-checking once more after a 16 ms tick (~one vsync) so any
    //      ItemsRepeater realization triggered by the first measure also lands.
    //   3. Using the slower HomePage durations (200 ms shimmer-out, 300 ms
    //      content-in @ +100 ms) — eased linear feels softer than abrupt.

    private bool _crossfadeScheduled;

    private async void ScheduleCrossfade()
    {
        _crossfadeScheduled = true;

        // Two dispatcher yields give XAML time to: (1) propagate the data
        // bindings, (2) measure ItemsRepeaters, (3) realize their cards.
        // Without this, content height grows by hundreds of pixels mid-fade
        // and the page visibly jumps.
        await Task.Yield();
        await Task.Delay(16);

        if (_isNavigatingAway || _showingContent) return;
        CrossfadeToContent();
    }

    private void CrossfadeToContent()
    {
        _showingContent = true;
        _ = CrossfadeShimmerOutAsync();
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                     delay: TimeSpan.FromMilliseconds(100))
            .Start(ContentContainer);
    }

    private async Task CrossfadeShimmerOutAsync()
    {
        if (ShimmerContainer == null) return;
        try
        {
            await AnimationBuilder.Create()
                .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
                .StartAsync(ShimmerContainer);
        }
        catch
        {
            // Animation cancelled (e.g. ShowShimmer fired during crossfade).
        }
        if (!_showingContent || ShimmerContainer == null) return;
        ShimmerContainer.Visibility = Visibility.Collapsed;
        ShimmerGate.IsLoaded = false;
    }

    private void ShowShimmer()
    {
        _showingContent = false;
        _crossfadeScheduled = false;
        ShimmerGate.IsLoaded = true;
        if (ShimmerContainer is null) return;
        ShimmerContainer.Visibility = Visibility.Visible;
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(200))
            .Start(ShimmerContainer);
        AnimationBuilder.Create()
            .Opacity(from: 1, to: 0, duration: TimeSpan.FromMilliseconds(200))
            .Start(ContentContainer);
    }

    // ── Avatar ──

    private void UpdateProfileAvatar(string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl))
        {
            ProfileAvatar.ProfilePicture = null;
            return;
        }
        if (imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Single-avatar surface — uncached one-off.
            ProfileAvatar.ProfilePicture = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(imageUrl))
            {
                DecodePixelWidth = 100,
                DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical,
            };
        }
    }

    // ── Page bleed (top-left radial wash, fades on scroll) ──

    private void UpdatePageBleedOpacity()
    {
        if (PageBleedHost == null || IdentityCardWrap == null) return;
        // Don't fight the reveal animation: until the bleed has been faded in
        // for the first time, leave its opacity alone (it's 0 at composition
        // level, and RevealPageBleedIfReady will animate it to 1).
        if (!_pageBleedRevealed) return;
        // Fully visible at top; fade to 0 by the time the identity card scrolls
        // mostly out of view. Mirrors HomePage.UpdatePageBleedOpacity.
        double cardH = IdentityCardWrap.ActualHeight > 0 ? IdentityCardWrap.ActualHeight : 140;
        double fadeDistance = Math.Max(80, cardH);
        double progress = Math.Clamp(PageScrollView.VerticalOffset / fadeDistance, 0.0, 1.0);
        PageBleedHost.Opacity = 1.0 - progress;
    }

    private void UpdateIdentityCardBackground()
    {
        if (!TryGetHeroColor(out var color))
        {
            IdentityCardWrap.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            return;
        }
        var alpha = (byte)(ActualTheme == ElementTheme.Dark ? 56 : 36);
        IdentityCardWrap.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private bool TryGetHeroColor(out Windows.UI.Color color)
    {
        color = default;
        var hex = ViewModel.HeroColorHex;
        if (string.IsNullOrEmpty(hex)) return false;
        hex = hex.TrimStart('#');
        if (hex.Length < 6) return false;
        try
        {
            var r = Convert.ToByte(hex[0..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);
            color = Windows.UI.Color.FromArgb(255, r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void IdentityCardWrap_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Card resize moves the pin threshold; the controller re-evaluates
        // pinOffset() each call so we just need to nudge it.
        UpdatePageBleedOpacity();
        _ = _shyHeader?.EvaluateAsync();
    }

    private void OnCacheDataRefreshed(ProfileSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() => ViewModel.ApplyBackgroundRefresh(snapshot));
    }

    // ── Header action buttons ──

    private void ShareProfile_Click(object sender, RoutedEventArgs e) => CopyProfileLinkInternal();

    private void CopyProfileLink_Click(object sender, RoutedEventArgs e) => CopyProfileLinkInternal();

    private void CopyProfileLinkInternal()
    {
        var uri = ViewModel.UserUri;
        if (string.IsNullOrEmpty(uri)) return;
        var url = uri.StartsWith("spotify:user:", StringComparison.Ordinal)
            ? $"https://open.spotify.com/user/{uri["spotify:user:".Length..]}"
            : uri;
        var package = new DataPackage();
        package.SetText(url);
        Clipboard.SetContent(package);
    }

    private async void OpenInSpotify_Click(object sender, RoutedEventArgs e)
    {
        var uri = ViewModel.UserUri;
        if (string.IsNullOrEmpty(uri)) return;
        var url = uri.StartsWith("spotify:user:", StringComparison.Ordinal)
            ? $"https://open.spotify.com/user/{uri["spotify:user:".Length..]}"
            : uri;
        try
        {
            await Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to launch profile URL");
        }
    }
}
