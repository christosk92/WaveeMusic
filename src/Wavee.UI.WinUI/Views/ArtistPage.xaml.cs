using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Wavee.Core.Http;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Controls.AlbumDetailPanel;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.ViewModels;
using ColorAnimation = Microsoft.UI.Xaml.Media.Animation.ColorAnimation;

namespace Wavee.UI.WinUI.Views;

public sealed partial class ArtistPage : Page, ITabBarItemContent, INavigationCacheMemoryParticipant, IDisposable
{
    private const int ShimmerCollapseDelayMs = 250;
    private const int ResizeDebounceDelayMs = 150;
    private const int ScrollRestoreMaxAttempts = 12;
    private const int ScrollRestoreRetryDelayMs = 16;
    private const int NavigationCacheTrimDelaySeconds = 45;
    private static readonly TimeSpan PageTintTransitionDuration = TimeSpan.FromMilliseconds(420);
    private const double ShyHeaderPinThresholdPx = 24;

    // Avatar collapse — when the artist has a header image but no watch-feed
    // video, the 120px circular avatar is redundant with the hero and collapses
    // to reclaim horizontal space for the name/stats block.
    private const double AvatarExpandedWidth = 136; // 120 avatar + 16 gap
    private const double AvatarCollapseDurationMs = 320;
    private bool _avatarCollapsed;
    private int _avatarAnimGen;

    private readonly ILogger? _logger;
    private readonly ISettingsService _settings;
    private bool _showingContent;
    private bool _isNavigatingAway;
    private LinearGradientBrush? _pageTintBrush;
    private GradientStop? _pageTintStartStop;
    private GradientStop? _pageTintHeroStop;
    private GradientStop? _pageTintFadeStop;
    private GradientStop? _pageTintEndStop;
    // Cached storyboard + animation per stop. Reused across palette/theme changes
    // (stop + retarget + begin) instead of allocating a fresh Storyboard +
    // ColorAnimation each call — palette + theme changes can fire 4× per
    // event, and ApplyTheme cascades on every Palette change.
    private readonly System.Collections.Generic.Dictionary<GradientStop, (Storyboard Sb, ColorAnimation Anim)> _pageTintAnims = new();
    private TransitionHelper? _shyHeaderTransition;
    private bool _isShyHeaderPinned;
    private bool _isShyHeaderTransitionRunning;
    private bool _shyHeaderRecheckPending;
    private bool _suppressShyHeaderEvaluation;
    private bool _suppressContentReveal;
    private bool _heroRevealed;
    private bool _crossfadeScheduled;
    private bool _isDisposed;
    private bool _trimmedForNavigationCache;
    private string? _lastRestoredArtistId;
    private bool _discographyRepeatersDetached;
    private double? _pendingNavigationScrollOffset;
    private string? _pendingNavigationScrollArtistId;
    private int _scrollRestoreGeneration;
    private DispatcherQueueTimer? _navigationTrimTimer;

    public ArtistViewModel ViewModel { get; }

    public ShimmerLoadGate ShimmerGate { get; } = new();

    public TabItemParameter? TabItemParameter => ViewModel.TabItemParameter;

    public bool ReuseForParameterNavigation => false;

    public event EventHandler<TabItemParameter>? ContentChanged;

    public ArtistPage()
    {
        _topTrackTappedHandler = TopTrackItem_Tapped;
        ViewModel = Ioc.Default.GetRequiredService<ArtistViewModel>();
        _logger = Ioc.Default.GetService<ILogger<ArtistPage>>();
        _settings = Ioc.Default.GetRequiredService<ISettingsService>();
        InitializeComponent();

        ViewModel.ContentChanged += ViewModel_ContentChanged;
        // Subscribe to IsLoading transitions in the ctor (not Loaded) so we don't
        // miss a fast load that completes before the page is added to the visual
        // tree — that race left the shimmer on forever after a tab restore.
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        // Refresh the palette-driven page tint + VM brushes when the user
        // toggles app theme; tier selection (HighContrast vs HigherContrast)
        // depends on it.
        ActualThemeChanged += (_, _) =>
        {
            ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
            UpdatePageTint();
        };
        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);

        // Start hero overlay + content invisible at composition level so they
        // can be faded in independently as their data arrives — prevents the
        // hard pop where the avatar/name/buttons render blank, then suddenly
        // fill while shimmer is still on screen below.
        SetHeroOverlayOpacity(0);
        ElementCompositionPreview.GetElementVisual(ContentContainer).Opacity = 0;
        ContentContainer.Visibility = Visibility.Collapsed;

        Unloaded += ArtistPage_Unloaded;
        Loaded += ArtistPage_Loaded;
    }

    private void ArtistPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ArtistPage_Loaded;

        SizeChanged += OnSizeChanged;
        HeroGrid.SizeChanged += HeroGrid_SizeChanged;

        HeroGrid.RightTapped += (_, e) =>
        {
            if (string.IsNullOrEmpty(ViewModel.ArtistId)) return;
            var items = ArtistContextMenuBuilder.Build(new ArtistMenuContext
            {
                ArtistId = ViewModel.ArtistId!,
                ArtistName = ViewModel.ArtistName ?? string.Empty,
                IsFollowing = ViewModel.IsFollowing,
                PlayCommand = ViewModel.PlayTopTracksCommand,
                ToggleFollowCommand = ViewModel.ToggleFollowCommand
            });
            ContextMenuHost.Show(HeroGrid, items, e.GetPosition(HeroGrid));
            e.Handled = true;
        };
        PageScrollView.ViewChanged += PageScrollView_ViewChanged;
        EnsureShyHeaderTransition();
        ResetShyHeaderState();

        // PointerWheelChanged via AddHandler with handledEventsToo=true so we
        // still see horizontal-pan events even when a child TrackItem (or any
        // other inner element) marks the wheel event handled before bubbling.
        TopTracksSection.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(TopTracksSection_PointerWheelChanged),
            handledEventsToo: true);

        // If data already arrived before we attached, the IsLoading transition
        // already fired and our handler missed it. Force the crossfade now.
        TryShowContentNow();
    }

    private void TryShowContentNow()
    {
        if (_suppressContentReveal
            || _showingContent
            || _crossfadeScheduled
            || ViewModel.IsLoading
            || string.IsNullOrEmpty(ViewModel.ArtistName))
            return;
        // Reveal the hero immediately if we already have a name (data was
        // pre-fetched / cache-served before the page attached).
        RevealHeroIfReady();
        ScheduleCrossfade();
    }

    // ── Scheduled crossfade ──
    // Yield twice so XAML has time to: (1) propagate the data bindings,
    // (2) measure ItemsRepeaters, (3) realize their cards. Without this,
    // content height grows by hundreds of pixels mid-fade and the page
    // visibly jumps. Mirrors ProfilePage.ScheduleCrossfade.

    private async void ScheduleCrossfade()
    {
        _crossfadeScheduled = true;
        await Task.Yield();
        await Task.Delay(16);
        if (_isNavigatingAway || _showingContent) return;
        CrossfadeToContent();
    }

    // Debounce timer that coalesces hero-resize ticks (window drags fire
    // dozens per second; UpdatePageTint re-targets gradient brushes which
    // is non-trivial on every tick).
    private Microsoft.UI.Xaml.DispatcherTimer? _heroResizeDebounce;

    private void HeroGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Window-driven hero resize: keep the page tint aligned so the fade
        // always starts at the hero's bottom edge. Coalesce the high-frequency
        // ticks of a window drag through a 50 ms debounce — UpdatePageTint and
        // the shy-header evaluation only need to settle once the resize stops.
        if (_heroResizeDebounce is null)
        {
            _heroResizeDebounce = new Microsoft.UI.Xaml.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50),
            };
            _heroResizeDebounce.Tick += OnHeroResizeDebounceTick;
        }
        _heroResizeDebounce.Stop();
        _heroResizeDebounce.Start();
    }

    private void OnHeroResizeDebounceTick(object? sender, object e)
    {
        _heroResizeDebounce?.Stop();
        UpdatePageTint();
        if (_suppressShyHeaderEvaluation)
            return;
        _ = EvaluateShyHeaderAsync();
    }

    private void EnsureShyHeaderTransition()
    {
        if (_shyHeaderTransition != null)
            return;

        // Helper is declared in Page.Resources so it survives navigation cache.
        // Source/Target are wired here because XAML resources can't ElementName-bind.
        if (Resources.TryGetValue("ArtistShyHeaderTransition", out var resource)
            && resource is TransitionHelper helper)
        {
            helper.Source = HeroOverlayPanel;
            helper.Target = ShyHeaderCard;
            _shyHeaderTransition = helper;
        }
    }

    private void ResetShyHeaderState()
    {
        _isShyHeaderPinned = false;
        _isShyHeaderTransitionRunning = false;
        _shyHeaderRecheckPending = false;
        ForceHeroHeaderSourceState();
        // Reset to source state: hero overlay visible, floating card collapsed.
        _shyHeaderTransition?.Reset(toInitialState: true);
        ForceHeroHeaderSourceState();
    }

    private void SuppressShyHeaderForContentReset()
    {
        _suppressShyHeaderEvaluation = true;
        try { _shyHeaderTransition?.Stop(); } catch { }
        ResetShyHeaderState();
        if (HeroGrid != null)
            HeroGrid.ScrollFadeProgress = 0;
    }

    private void ResumeShyHeaderAfterContentReset()
    {
        _suppressShyHeaderEvaluation = false;
        ResetShyHeaderState();
        UpdateHeroScrollFade();
    }

    private void PageScrollView_ViewChanged(ScrollView sender, object args)
    {
        UpdateHeroScrollFade();
        if (_suppressShyHeaderEvaluation)
            return;

        _ = EvaluateShyHeaderAsync();
    }

    private void UpdateHeroScrollFade()
    {
        if (HeroGrid == null) return;
        var heroH = HeroGrid.ActualHeight;
        if (heroH <= 0)
        {
            HeroGrid.ScrollFadeProgress = 0;
            return;
        }
        HeroGrid.ScrollFadeProgress = Math.Clamp(PageScrollView.VerticalOffset / heroH, 0.0, 1.0);
    }

    private async Task EvaluateShyHeaderAsync()
    {
        if (_suppressShyHeaderEvaluation)
            return;

        if (_shyHeaderTransition == null || HeroOverlayPanel == null || ShyHeaderCard == null || HeroGrid == null)
            return;

        if (_isShyHeaderTransitionRunning)
        {
            // Coalesce: re-check once the in-flight transition lands.
            _shyHeaderRecheckPending = true;
            return;
        }

        while (true)
        {
            if (_suppressShyHeaderEvaluation)
                return;

            if (_isNavigatingAway || !HeroGrid.IsLoaded || !ShyHeaderHost.IsLoaded)
                return;

            double pinOffset = Math.Max(0, HeroGrid.ActualHeight - 120);
            bool shouldPin = PageScrollView.VerticalOffset >= pinOffset;

            if (shouldPin == _isShyHeaderPinned)
                return;

            _isShyHeaderTransitionRunning = true;
            _shyHeaderRecheckPending = false;

            try
            {
                if (shouldPin)
                    await _shyHeaderTransition.StartAsync();
                else
                    await _shyHeaderTransition.ReverseAsync();

                _isShyHeaderPinned = shouldPin;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Shy header transition skipped.");
                return;
            }
            finally
            {
                _isShyHeaderTransitionRunning = false;
            }

            // Loop only if a scroll event arrived during the transition.
            if (!_shyHeaderRecheckPending)
                return;
        }
    }

    private void ViewModel_ContentChanged(object? sender, TabItemParameter e)
        => ContentChanged?.Invoke(this, e);

    private Windows.Media.Playback.MediaPlayer? _watchFeedMediaPlayer;
    private Microsoft.UI.Xaml.Controls.MediaPlayerElement? _watchFeedElement;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArtistViewModel.IsLoading))
        {
            if (!ViewModel.IsLoading
                && !_showingContent
                && !_crossfadeScheduled
                && !_suppressContentReveal
                && !string.IsNullOrEmpty(ViewModel.ArtistName))
            {
                ScheduleCrossfade();
            }
        }
        else if (e.PropertyName is nameof(ArtistViewModel.ArtistName))
        {
            Bindings?.Update();
            if (!_suppressContentReveal)
            {
                TryShowContentNow();
            }
        }
        else if (e.PropertyName is nameof(ArtistViewModel.ArtistImageUrl)
                              or nameof(ArtistViewModel.HeaderImageUrl)
                              or nameof(ArtistViewModel.MonthlyListeners)
                              or nameof(ArtistViewModel.WorldRank)
                              or nameof(ArtistViewModel.HasWorldRank)
                              or nameof(ArtistViewModel.WorldRankNumberText)
                              or nameof(ArtistViewModel.IsVerified)
                              or nameof(ArtistViewModel.IsFollowing)
                              or nameof(ArtistViewModel.IsPlayPending)
                              or nameof(ArtistViewModel.IsArtistContextPlaying)
                              or nameof(ArtistViewModel.IsArtistContextPaused)
                              or nameof(ArtistViewModel.ArtistPlayButtonText)
                              or nameof(ArtistViewModel.PaletteAccentPillBrush)
                              or nameof(ArtistViewModel.PaletteAccentPillForegroundBrush))
        {
            Bindings?.Update();

            if (e.PropertyName is nameof(ArtistViewModel.HeaderImageUrl) && _showingContent)
                UpdateAvatarLayout(animate: true);
        }
        else if (e.PropertyName is nameof(ArtistViewModel.WatchFeed))
        {
            // Re-evaluate avatar collapse state after the initial crossfade.
            // SetupWatchFeedVideo is called from CrossfadeToContent — no need here.
            if (_showingContent)
                UpdateAvatarLayout(animate: true);
        }
        else if (e.PropertyName is nameof(ArtistViewModel.HeaderHeroColorHex)
                                 or nameof(ArtistViewModel.Palette))
        {
            Bindings?.Update();
            UpdatePageTint();
        }
        else if (e.PropertyName is nameof(ArtistViewModel.CurrentPage))
        {
            AnimateTopTracksPageChange();
        }
        else if (e.PropertyName is nameof(ArtistViewModel.LatestReleaseName)
                              or nameof(ArtistViewModel.LatestReleaseImageUrl)
                              or nameof(ArtistViewModel.LatestReleaseUri)
                              or nameof(ArtistViewModel.LatestReleaseDate)
                              or nameof(ArtistViewModel.LatestReleaseTrackCount)
                              or nameof(ArtistViewModel.LatestReleaseType)
                              or nameof(ArtistViewModel.HasLatestRelease)
                              or nameof(ArtistViewModel.LatestReleaseSubtitle))
        {
            // The latest-release card is nested inside a section that can be
            // collapsed during artist swaps. WinUI occasionally misses child
            // x:Bind updates in that path, leaving subtitle current but
            // image/title stale. A full page binding refresh is cheap here and
            // keeps the card coherent.
            Bindings?.Update();
        }
        else if (e.PropertyName is nameof(ArtistViewModel.AlbumsGridView)
                              or nameof(ArtistViewModel.SinglesGridView)
                              or nameof(ArtistViewModel.CompilationsGridView)
                              or nameof(ArtistViewModel.Albums)
                              or nameof(ArtistViewModel.Singles)
                              or nameof(ArtistViewModel.Compilations)
                              or nameof(ArtistViewModel.AlbumsTotalCount)
                              or nameof(ArtistViewModel.SinglesTotalCount)
                              or nameof(ArtistViewModel.CompilationsTotalCount))
        {
            UpdateDiscographyRepeaterBindings();
        }
    }

    private void RevealHeroIfReady()
    {
        if (_heroRevealed || string.IsNullOrEmpty(ViewModel.ArtistName)) return;
        _heroRevealed = true;
        ForceHeroHeaderSourceState();
        ElementCompositionPreview.GetElementVisual(HeroOverlayPanel).Opacity = 1;
        HeroOverlayPanel.Opacity = 0;
        AnimationBuilder.Create()
            .Opacity(from: 0, to: 1,
                     duration: TimeSpan.FromMilliseconds(280),
                     layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(HeroOverlayPanel);
    }

    private void ForceHeroHeaderSourceState()
    {
        if (HeroOverlayPanel is not null)
            HeroOverlayPanel.Visibility = Visibility.Visible;

        if (ShyHeaderCard is not null)
            ShyHeaderCard.Visibility = Visibility.Collapsed;
    }

    private void SetHeroOverlayOpacity(float opacity)
    {
        if (HeroOverlayPanel == null)
            return;

        HeroOverlayPanel.Visibility = Visibility.Visible;
        HeroOverlayPanel.Opacity = opacity;
        ElementCompositionPreview.GetElementVisual(HeroOverlayPanel).Opacity = opacity;
    }

    // Pixels of tint spill past the hero's bottom edge before the wash fully fades.
    private const double PageTintSpillPx = 320;

    private void UpdatePageTint()
    {
        if (PageTintFill == null) return;

        EnsurePageTintBrush();

        // Size the rectangle to the current hero height plus a fixed spill tail.
        // This keeps the fade anchored to the hero's bottom edge as the window resizes.
        double heroH = HeroGrid.ActualHeight > 0 ? HeroGrid.ActualHeight : HeroRow.MinHeight;
        double totalH = heroH + PageTintSpillPx;
        PageTintFill.Height = totalH;

        // Relative offsets: full tint through the hero region (hidden behind image),
        // start fading right at the hero's bottom edge, fully transparent at the tail.
        double heroBottomOffset = heroH / totalH;
        double fadeOffset = Math.Min(1.0, heroBottomOffset + (1.0 - heroBottomOffset) * 0.7);

        if (_pageTintStartStop != null) _pageTintStartStop.Offset = 0.0;
        if (_pageTintHeroStop != null) _pageTintHeroStop.Offset = heroBottomOffset;
        if (_pageTintFadeStop != null) _pageTintFadeStop.Offset = fadeOffset;
        if (_pageTintEndStop != null) _pageTintEndStop.Offset = 1.0;

        // Prefer the visualIdentity palette (richer, two-tone — BackgroundTinted
        // top → Background bottom — and theme-aware) over the single-hex
        // HeaderHeroColorHex. Same tier policy as ConcertViewModel and
        // SearchResultHeroCard: dark theme → HigherContrast; light → HighContrast.
        // MinContrast is intentionally skipped; too pastel for white-on-tint text.
        // Monochromatic alpha fade — only the lighter BackgroundTinted shade is
        // used across all four stops. Mixing BackgroundTinted with the more
        // saturated Background mid-gradient produced a visible hue seam.
        var (tintTop, _) = ResolveTintColors();
        if (tintTop is null)
        {
            AnimatePageTintColor(_pageTintStartStop, Windows.UI.Color.FromArgb(0, 0, 0, 0));
            AnimatePageTintColor(_pageTintHeroStop, Windows.UI.Color.FromArgb(0, 0, 0, 0));
            AnimatePageTintColor(_pageTintFadeStop, Windows.UI.Color.FromArgb(0, 0, 0, 0));
            AnimatePageTintColor(_pageTintEndStop, Windows.UI.Color.FromArgb(0, 0, 0, 0));
            return;
        }

        var top = tintTop.Value;
        AnimatePageTintColor(_pageTintStartStop, Windows.UI.Color.FromArgb(110, top.R, top.G, top.B));
        AnimatePageTintColor(_pageTintHeroStop,  Windows.UI.Color.FromArgb(70,  top.R, top.G, top.B));
        AnimatePageTintColor(_pageTintFadeStop,  Windows.UI.Color.FromArgb(20,  top.R, top.G, top.B));
        AnimatePageTintColor(_pageTintEndStop,   Windows.UI.Color.FromArgb(0,   top.R, top.G, top.B));
    }

    /// <summary>
    /// Resolves the top + bottom colours of the page tint gradient. Prefers the
    /// theme-appropriate ArtistPalette tier (BackgroundTinted top, Background
    /// bottom). Falls back to the single-hex HeaderHeroColorHex when no palette
    /// exists (older artists with no visualIdentity block).
    /// </summary>
    private (Windows.UI.Color? Top, Windows.UI.Color? Bottom) ResolveTintColors()
    {
        var palette = ViewModel.Palette;
        if (palette != null)
        {
            var tier = ActualTheme == ElementTheme.Dark
                ? (palette.HigherContrast ?? palette.HighContrast)
                : (palette.HighContrast ?? palette.HigherContrast);

            if (tier != null)
            {
                var top = Windows.UI.Color.FromArgb(255,
                    tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);
                var bottom = Windows.UI.Color.FromArgb(255,
                    tier.BackgroundR, tier.BackgroundG, tier.BackgroundB);
                return (top, bottom);
            }
        }

        if (TintColorHelper.TryParseHex(ViewModel.HeaderHeroColorHex, out var parsed))
            return (parsed, parsed);

        return (null, null);
    }

    private void EnsurePageTintBrush()
    {
        if (_pageTintBrush != null)
        {
            if (!ReferenceEquals(PageTintFill.Fill, _pageTintBrush))
                PageTintFill.Fill = _pageTintBrush;
            return;
        }

        _pageTintBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1)
        };
        _pageTintStartStop = new GradientStop { Offset = 0.0, Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) };
        _pageTintHeroStop = new GradientStop { Offset = 0.5, Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) };
        _pageTintFadeStop = new GradientStop { Offset = 0.85, Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) };
        _pageTintEndStop = new GradientStop { Offset = 1.0, Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) };

        _pageTintBrush.GradientStops.Add(_pageTintStartStop);
        _pageTintBrush.GradientStops.Add(_pageTintHeroStop);
        _pageTintBrush.GradientStops.Add(_pageTintFadeStop);
        _pageTintBrush.GradientStops.Add(_pageTintEndStop);
        PageTintFill.Fill = _pageTintBrush;
    }

    private void AnimatePageTintColor(GradientStop? stop, Windows.UI.Color targetColor)
    {
        if (stop == null)
            return;

        if (stop.Color == targetColor)
            return;

        if (!_pageTintAnims.TryGetValue(stop, out var entry))
        {
            var anim = new ColorAnimation
            {
                Duration = new Duration(PageTintTransitionDuration),
                EnableDependentAnimation = true
            };
            var sb = new Storyboard();
            Storyboard.SetTarget(anim, stop);
            Storyboard.SetTargetProperty(anim, nameof(GradientStop.Color));
            sb.Children.Add(anim);
            entry = (sb, anim);
            _pageTintAnims[stop] = entry;
        }
        else
        {
            // Stop the previous run before retargeting so the in-flight
            // animation releases its hold on the property and the new To
            // animates from the current value.
            entry.Sb.Stop();
        }

        entry.Anim.To = targetColor;
        entry.Sb.Begin();
    }

    /// <summary>
    /// Keeps the circular avatar fixed at the expanded width. The avatar is
    /// always visible — the hero header image is a banner, the avatar is the
    /// artist portrait, and they serve different purposes. Watch-feed videos
    /// fade in over the avatar circle when available.
    /// </summary>
    private void UpdateAvatarLayout(bool animate)
    {
        if (AvatarWrapper == null || ArtistImageContainer == null) return;

        const bool shouldCollapse = false;

        if (shouldCollapse == _avatarCollapsed) return;
        _avatarCollapsed = shouldCollapse;

        double targetWidth = shouldCollapse ? 0 : AvatarExpandedWidth;
        double targetOpacity = shouldCollapse ? 0 : 1;
        float targetScale = shouldCollapse ? 0.6f : 1f;

        // Anchor composition transforms around the avatar center
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ArtistImageContainer);
        visual.CenterPoint = new System.Numerics.Vector3(60, 60, 0);

        if (!animate)
        {
            AvatarWrapper.Width = targetWidth;
            ArtistImageContainer.Opacity = targetOpacity;
            visual.Scale = new System.Numerics.Vector3(targetScale, targetScale, 1f);
            _avatarAnimGen++; // cancel any in-flight width interpolation
            return;
        }

        // Composition-driven fade + scale (runs off the UI thread for buttery smoothness)
        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Opacity(to: targetOpacity,
                     duration: TimeSpan.FromMilliseconds(220),
                     easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .Scale(to: new System.Numerics.Vector3(targetScale, targetScale, 1f),
                   duration: TimeSpan.FromMilliseconds(AvatarCollapseDurationMs),
                   easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
            .Start(ArtistImageContainer);

        // Layout-affecting Width interpolation via CompositionTarget.Rendering
        // (DoubleAnimation on Width is a dependent animation and looks janky;
        //  per-frame manual interpolation gives a proper 60fps layout re-flow).
        double startWidth = double.IsNaN(AvatarWrapper.Width) ? AvatarExpandedWidth : AvatarWrapper.Width;
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(AvatarCollapseDurationMs);
        var myGen = ++_avatarAnimGen;

        EventHandler<object>? tick = null;
        tick = (_, _) =>
        {
            if (myGen != _avatarAnimGen || _isNavigatingAway)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= tick;
                return;
            }

            var elapsed = DateTime.UtcNow - startTime;
            double t = Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            // Cubic ease-out — snappy start, soft landing
            double eased = 1 - Math.Pow(1 - t, 3);
            AvatarWrapper.Width = startWidth + (targetWidth - startWidth) * eased;

            if (t >= 1)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= tick;
            }
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += tick;
    }

    private void SetupWatchFeedVideo()
    {
        if (_isNavigatingAway || !IsLoaded)
            return;

        if (ViewModel.WatchFeed?.VideoUrl == null) return;

        // Clean up previous
        TeardownWatchFeed();

        // Create MediaPlayer
        _watchFeedMediaPlayer = new Windows.Media.Playback.MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = true,
            AutoPlay = true
        };
        _watchFeedMediaPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(
            new Uri(ViewModel.WatchFeed.VideoUrl));

        // Create MediaPlayerElement programmatically (never in XAML — WinUI teardown bug)
        _watchFeedElement = new Microsoft.UI.Xaml.Controls.MediaPlayerElement
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            AreTransportControlsEnabled = false,
            AutoPlay = true
        };
        _watchFeedElement.SetMediaPlayer(_watchFeedMediaPlayer);

        WatchFeedGrid.Children.Insert(0, _watchFeedElement);

        // Constrain the MediaPlayerElement so the swap chain doesn't overflow
        _watchFeedElement.Width = 120;
        _watchFeedElement.Height = 120;

        // Clip the avatar host and media element; swap chains can ignore ancestor clips.
        ApplyWatchFeedCircleClip();

        // Crossfade: wait for video to start rendering, then fade in over the static image
        _watchFeedMediaPlayer.VideoFrameAvailable += OnFirstVideoFrame;
        _watchFeedMediaPlayer.IsVideoFrameServerEnabled = true;
    }

    private void ApplyWatchFeedCircleClip()
    {
        ApplyCompositionCircleClip(ArtistImageContainer);
        ApplyCompositionCircleClip(WatchFeedGrid);
        if (_watchFeedElement != null)
            ApplyCompositionCircleClip(_watchFeedElement);
    }

    private static void ApplyCompositionCircleClip(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ellipse = compositor.CreateEllipseGeometry();
        ellipse.Center = new Vector2(60, 60);
        ellipse.Radius = new Vector2(60, 60);
        visual.Clip = compositor.CreateGeometricClip(ellipse);
    }

    private void OnFirstVideoFrame(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        // Only need the first frame — unsubscribe immediately
        sender.VideoFrameAvailable -= OnFirstVideoFrame;
        sender.IsVideoFrameServerEnabled = false;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isNavigatingAway || _watchFeedMediaPlayer != sender || _watchFeedElement == null || WatchFeedGrid.XamlRoot == null)
                return;

            // Fade video in over the static image
            try
            {
                AnimationBuilder.Create()
                    .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(600),
                             easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
                    .Start(WatchFeedGrid);

                // Show hover overlay
                WatchFeedHoverOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Watch feed first-frame transition skipped during navigation teardown");
            }
        });
    }

    private void TeardownWatchFeed()
    {
        if (_watchFeedElement != null)
        {
            _watchFeedElement.SetMediaPlayer(null);
            WatchFeedGrid.Children.Remove(_watchFeedElement);
            _watchFeedElement = null;
        }
        if (_watchFeedMediaPlayer != null)
        {
            _watchFeedMediaPlayer.VideoFrameAvailable -= OnFirstVideoFrame;
            try { _watchFeedMediaPlayer.Pause(); } catch { }
            try { _watchFeedMediaPlayer.Dispose(); } catch { }
            _watchFeedMediaPlayer = null;
        }
    }

    private async void CrossfadeToContent()
    {
        if (_showingContent) return;
        _showingContent = true;
        ResumeShyHeaderAfterContentReset();
        RevealHeroIfReady();
        ContentContainer.Visibility = Visibility.Visible;
        ContentContainer.Opacity = 0;
        UpdateDiscographyRepeaterBindings();

        // 200 ms shimmer-out + 300 ms content-in @ +100 ms delay (Xaml layer
        // because the shimmer subtree drives layout that composition-only
        // opacity won't capture). Animation + finalization centralised in
        // ShimmerGate; sets IsLoaded=false at the end so x:Load unrealizes
        // the skeleton subtree.
        await ShimmerGate.RunCrossfadeAsync(ShimmerContainer, ContentContainer,
            CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml,
            () => _showingContent);

        // Set up watch feed video after the first content frame; MediaPlayer
        // creation owns a swap chain and should not compete with first paint.
        if (!_isNavigatingAway)
            SetupWatchFeedVideo();

        // Decide whether to collapse the redundant circular avatar now that
        // we know the final HeaderImageUrl + WatchFeed state.
        UpdateAvatarLayout(animate: true);

        await EvaluateShyHeaderAsync();
    }

    private void ArtistPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // Under NavigationCacheMode=Required, the Page is reused across N
        // navigations and the (singleton-via-cache) ViewModel lives with it,
        // so the constructor's `ViewModel.PropertyChanged += ...` registration
        // is correct for the page's lifetime — do NOT unhook here. Earlier
        // we mirrored AlbumPage_Unloaded's unhook to break a leak chain that
        // assumed Disabled cache (one VM per nav); under Required cache that
        // unhook ran on X→Y nav and ArtistPage_Loaded (self-removed after the
        // first Loaded) never re-attached on back-to-X, leaving the page deaf
        // to IsLoading=false transitions and stuck on shimmer for cold cache.
        _isNavigatingAway = true;
        CancelResizeDebounce();
        CollapseExpandedAlbum();
        TeardownWatchFeed();
    }

    private void CancelResizeDebounce()
    {
        if (_resizeDebounceCts == null)
            return;

        try { _resizeDebounceCts.Cancel(); } catch (ObjectDisposedException) { }
        _resizeDebounceCts.Dispose();
        _resizeDebounceCts = null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Hero height = 45% of page height (min 200).
        if (HeroRow != null)
            HeroRow.Height = new GridLength(Math.Max(200, e.NewSize.Height * 0.45), GridUnitType.Pixel);

        // Debounced recompute of expanded panel position
        if (_activeDetailPanel != null && _expandedItem != null)
        {
            CancelResizeDebounce();
            _resizeDebounceCts = new CancellationTokenSource();
            var token = _resizeDebounceCts.Token;
            _ = RecomputeExpandedPanelAsync(token);
        }
    }

    private async Task RecomputeExpandedPanelAsync(CancellationToken ct)
    {
        try { await Task.Delay(ResizeDebounceDelayMs, ct); }
        catch (OperationCanceledException) { return; }

        if (_activeDetailPanel == null || _expandedItem == null ||
            _originalRepeater == null || _splitParent == null || _originalItemsSource == null)
            return;

        ApplySplitLayout();
    }

    /// <summary>
    /// Shared logic: computes columns from available width, splits items before/after
    /// the expanded item's row, updates both repeaters, and positions the notch.
    /// Called on initial expand and on debounced resize.
    /// </summary>
    private void ApplySplitLayout()
    {
        if (_originalRepeater == null || _splitParent == null || _originalItemsSource == null ||
            _activeDetailPanel == null)
            return;

        var layout = _originalRepeater.Layout as UniformGridLayout;
        var allItems = _originalItemsSource as System.Collections.IList;
        if (allItems == null) return;

        var availableWidth = _splitParent.ActualWidth;
        var minWidth = layout?.MinItemWidth ?? 160;
        var spacing = layout?.MinColumnSpacing ?? 12;
        var columns = Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (minWidth + spacing)));

        // Split point: end of the expanded item's row
        var rowOfItem = _expandedItemIndex / columns;
        var splitAfterIndex = Math.Min((rowOfItem + 1) * columns, allItems.Count);

        var itemsBefore = new System.Collections.Generic.List<object>();
        var itemsAfter = new System.Collections.Generic.List<object>();
        for (int i = 0; i < allItems.Count; i++)
        {
            if (i < splitAfterIndex)
                itemsBefore.Add(allItems[i]!);
            else
                itemsAfter.Add(allItems[i]!);
        }

        // Update the first repeater
        _originalRepeater.ItemsSource = itemsBefore;

        // Update or create/remove the second repeater
        if (_splitRepeaterAfter != null)
        {
            if (itemsAfter.Count > 0)
                _splitRepeaterAfter.ItemsSource = itemsAfter;
            else
            {
                _splitRepeaterAfter.ElementClearing -= DiscographyRepeater_ElementClearing;
                ReleaseImagesInSubtree(_splitRepeaterAfter);
                _splitParent.Children.Remove(_splitRepeaterAfter);
                _splitRepeaterAfter = null;
            }
        }
        else if (itemsAfter.Count > 0)
        {
            _splitRepeaterAfter = new ItemsRepeater
            {
                Layout = new UniformGridLayout
                {
                    MinItemWidth = minWidth,
                    MinItemHeight = layout?.MinItemHeight ?? 240,
                    MinRowSpacing = layout?.MinRowSpacing ?? 12,
                    MinColumnSpacing = spacing,
                    ItemsStretch = Microsoft.UI.Xaml.Controls.UniformGridLayoutItemsStretch.Uniform
                },
                ItemTemplate = _originalRepeater.ItemTemplate,
                ItemsSource = itemsAfter
            };
            _splitRepeaterAfter.ElementClearing += DiscographyRepeater_ElementClearing;
            var panelIndex = _splitParent.Children.IndexOf(_activeDetailPanel);
            if (panelIndex >= 0)
                _splitParent.Children.Insert(panelIndex + 1, _splitRepeaterAfter);
        }

        // Position notch at the expanded item's center
        var columnIndex = _expandedItemIndex % columns;
        var cellWidth = (availableWidth - (columns - 1) * spacing) / columns;
        _activeDetailPanel.NotchOffsetX = columnIndex * (cellWidth + spacing) + cellWidth / 2;
    }

    private void TopTracksLayout_ColumnCountChanged(object? sender, int columns)
    {
        ViewModel.ColumnCount = columns;
    }

    // ── Top tracks page transition animation ──
    // Slide+fade carousel feel. _prevTopTracksPage starts at -1 so the very
    // first render skips the out-tween; cancellation token bumps on each call
    // so a rapid pip click aborts the in-flight phase 1 instead of stacking.
    private int _prevTopTracksPage = -1;
    private long _topTracksAnimGen;
    private bool _topTracksTranslationEnabled;
    private const float TopTracksSlideOffsetPx = 40f;
    private static readonly TimeSpan TopTracksOutDuration = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan TopTracksInDuration = TimeSpan.FromMilliseconds(120);

    private async void AnimateTopTracksPageChange()
    {
        if (ViewModel == null || TopTracksRepeater == null) return;

        int newPage = ViewModel.CurrentPage;
        int prevPage = _prevTopTracksPage;
        _prevTopTracksPage = newPage;

        // First render — no animation.
        if (prevPage < 0) return;

        int dir = Math.Sign(newPage - prevPage);
        if (dir == 0) return;

        if (!_topTracksTranslationEnabled)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(TopTracksRepeater, true);
            _topTracksTranslationEnabled = true;
        }

        long gen = System.Threading.Interlocked.Increment(ref _topTracksAnimGen);

        var visual = ElementCompositionPreview.GetElementVisual(TopTracksRepeater);
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.68f, 1f));

        // Phase 1 — slide out + fade.
        var outBatch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        var outOffset = compositor.CreateVector3KeyFrameAnimation();
        outOffset.InsertKeyFrame(1f, new Vector3(-TopTracksSlideOffsetPx * dir, 0f, 0f), easing);
        outOffset.Duration = TopTracksOutDuration;
        var outOpacity = compositor.CreateScalarKeyFrameAnimation();
        outOpacity.InsertKeyFrame(1f, 0f, easing);
        outOpacity.Duration = TopTracksOutDuration;
        visual.StartAnimation("Translation", outOffset);
        visual.StartAnimation("Opacity", outOpacity);
        outBatch.End();

        var tcs = new TaskCompletionSource();
        outBatch.Completed += (_, _) => tcs.TrySetResult();
        await tcs.Task;

        // Newer page change superseded us — abandon.
        if (gen != System.Threading.Interlocked.Read(ref _topTracksAnimGen)) return;

        // Let the binding rebuild PagedTopTracks before phase 2 starts.
        await DispatcherQueue.EnqueueAsync(() => { });

        if (gen != System.Threading.Interlocked.Read(ref _topTracksAnimGen)) return;

        // Phase 2 — snap to opposite side, slide back in + fade.
        visual.Properties.InsertVector3("Translation", new Vector3(TopTracksSlideOffsetPx * dir, 0f, 0f));
        visual.Opacity = 0f;
        var inOffset = compositor.CreateVector3KeyFrameAnimation();
        inOffset.InsertKeyFrame(1f, Vector3.Zero, easing);
        inOffset.Duration = TopTracksInDuration;
        var inOpacity = compositor.CreateScalarKeyFrameAnimation();
        inOpacity.InsertKeyFrame(1f, 1f, easing);
        inOpacity.Duration = TopTracksInDuration;
        visual.StartAnimation("Translation", inOffset);
        visual.StartAnimation("Opacity", inOpacity);
    }

    // ── Trackpad two-finger horizontal swipe → paginate top tracks ──
    // Precision touchpad horizontal pans surface as PointerWheelChanged with
    // IsHorizontalMouseWheel=true. Vertical-only wheel events are left
    // unhandled so the parent ScrollViewer keeps scrolling the page.
    private double _wheelAccumulator;
    private DateTimeOffset _wheelCooldownUntil = DateTimeOffset.MinValue;
    private const double WheelPageThreshold = 80.0;
    private static readonly TimeSpan WheelCooldown = TimeSpan.FromMilliseconds(250);

    private void TopTracksSection_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel == null || sender is not UIElement panel) return;

        var props = e.GetCurrentPoint(panel).Properties;
        if (!props.IsHorizontalMouseWheel) return;

        if (DateTimeOffset.UtcNow < _wheelCooldownUntil)
        {
            e.Handled = true;
            return;
        }

        _wheelAccumulator += props.MouseWheelDelta;

        if (_wheelAccumulator >= WheelPageThreshold)
        {
            if (ViewModel.CurrentPage < ViewModel.TotalPages - 1)
                ViewModel.NextPageCommand.Execute(null);
            _wheelAccumulator = 0;
            _wheelCooldownUntil = DateTimeOffset.UtcNow + WheelCooldown;
            e.Handled = true;
        }
        else if (_wheelAccumulator <= -WheelPageThreshold)
        {
            if (ViewModel.CurrentPage > 0)
                ViewModel.PreviousPageCommand.Execute(null);
            _wheelAccumulator = 0;
            _wheelCooldownUntil = DateTimeOffset.UtcNow + WheelCooldown;
            e.Handled = true;
        }
    }

    private int? _topTrackSelectionAnchor;
    private readonly TappedEventHandler _topTrackTappedHandler;

    private void TopTracksRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is Controls.Track.TrackItem trackItem)
        {
            trackItem.PlayCommand = ViewModel.PlayTrackCommand;

            var lazy = ViewModel?.PagedTopTracks.ElementAtOrDefault(args.Index);
            trackItem.Tag = lazy;

            // handledEventsToo=true: TrackItem may consume Tapped for single-tap play.
            // Selection still needs to run on the same gesture.
            trackItem.RemoveHandler(UIElement.TappedEvent, _topTrackTappedHandler);
            trackItem.AddHandler(UIElement.TappedEvent, _topTrackTappedHandler, true);

            trackItem.DataContextChanged -= TopTrackItem_DataContextChanged;
            trackItem.DataContextChanged += TopTrackItem_DataContextChanged;

            if (lazy is ViewModels.LazyTrackItem preparedLazy && ViewModel != null)
            {
                preparedLazy.IsSelected = ViewModel.IsTopTrackSelected(preparedLazy);
                trackItem.IsSelected = preparedLazy.IsSelected;
            }
        }
    }

    private void TopTrackItem_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Controls.Track.TrackItem ti &&
            args.NewValue is ViewModels.LazyTrackItem lazy &&
            ViewModel != null)
        {
            lazy.IsSelected = ViewModel.IsTopTrackSelected(lazy);
            ti.IsSelected = lazy.IsSelected;
        }
    }

    private void TopTrackItem_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Controls.Track.TrackItem ti ||
            ti.Tag is not ViewModels.LazyTrackItem lazy ||
            ViewModel == null)
            return;

        var (ctrl, shift) = GetCtrlShiftState();

        var paged = ViewModel.PagedTopTracks.ToList();
        int index = paged.IndexOf(lazy);
        if (index < 0) return;

        if (shift && _topTrackSelectionAnchor.HasValue)
        {
            int start = Math.Min(_topTrackSelectionAnchor.Value, index);
            int end = Math.Max(_topTrackSelectionAnchor.Value, index);
            ViewModel.SelectedTopTracks.Clear();
            for (int i = start; i <= end; i++)
                ViewModel.SelectedTopTracks.Add(paged[i]);
        }
        else if (ctrl)
        {
            if (ViewModel.SelectedTopTracks.Contains(lazy))
                ViewModel.SelectedTopTracks.Remove(lazy);
            else
                ViewModel.SelectedTopTracks.Add(lazy);
            _topTrackSelectionAnchor = index;
        }
        else
        {
            // Plain click toggles: re-clicking an already-selected item deselects it.
            if (ViewModel.SelectedTopTracks.Contains(lazy))
            {
                ViewModel.SelectedTopTracks.Remove(lazy);
                if (ViewModel.SelectedTopTracks.Count == 0)
                    _topTrackSelectionAnchor = null;
            }
            else
            {
                ViewModel.SelectedTopTracks.Clear();
                ViewModel.SelectedTopTracks.Add(lazy);
                _topTrackSelectionAnchor = index;
            }
        }

        RefreshTopTrackSelectionVisuals();
    }

    private static (bool ctrl, bool shift) GetCtrlShiftState()
    {
        try
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            return (
                (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down,
                (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down);
        }
        catch
        {
            return (false, false);
        }
    }

    private void RefreshTopTrackSelectionVisuals()
    {
        if (ViewModel == null) return;
        var paged = ViewModel.PagedTopTracks.ToList();
        for (int i = 0; i < paged.Count; i++)
        {
            paged[i].IsSelected = ViewModel.IsTopTrackSelected(paged[i]);
            if (TopTracksRepeater.TryGetElement(i) is Controls.Track.TrackItem ti)
                ti.IsSelected = paged[i].IsSelected;
        }
    }

    private void TopTracksSection_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel == null) return;
        if (IsWithinTrackItem(e.OriginalSource as DependencyObject)) return;

        ViewModel.ClearTopTracksSelection();
        _topTrackSelectionAnchor = null;
        RefreshTopTrackSelectionVisuals();
    }

    private static bool IsWithinTrackItem(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Controls.Track.TrackItem) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    public void RefreshWithParameter(object? parameter)
    {
        CancelNavigationCacheTrim();
        _isNavigatingAway = false;
        LoadNewContent(parameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.artist.onNavigatedFrom");
        base.OnNavigatedFrom(e);
        TrimForNavigationCache();
    }

    public void TrimForNavigationCache()
    {
        ScheduleNavigationCacheTrim();
    }

    private void TrimForNavigationCacheNow()
    {
        if (_trimmedForNavigationCache)
            return;

        CaptureNavigationScrollPosition();
        _trimmedForNavigationCache = true;
        _lastRestoredArtistId = ViewModel.ArtistId;
        _isNavigatingAway = true;
        CancelResizeDebounce();
        CollapseExpandedAlbum();
        TeardownWatchFeed();
        try { _shyHeaderTransition?.Stop(); } catch { }

        // Hibernate releases the store subscription and heavy bound collections
        // before the page is hidden. Revisit speed comes from warm data caches.
        ViewModel.Hibernate();
        ReleaseNavigationCachedImages();
        HeroGrid?.ReleaseSurface();
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling is no longer rooted by the (singleton-store-subscribed) VM —
        // without this the entire page tree is pinned across navigations.
        Bindings?.StopTracking();
    }

    private void ScheduleNavigationCacheTrim()
    {
        if (_isDisposed || _trimmedForNavigationCache)
            return;

        var timer = _navigationTrimTimer;
        if (timer is null)
        {
            timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Tick += NavigationTrimTimer_Tick;
            _navigationTrimTimer = timer;
        }

        timer.Stop();
        timer.Interval = TimeSpan.FromSeconds(NavigationCacheTrimDelaySeconds);
        timer.Start();
    }

    private void CancelNavigationCacheTrim()
    {
        _navigationTrimTimer?.Stop();
    }

    private void NavigationTrimTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_isDisposed || _trimmedForNavigationCache)
            return;

        TrimForNavigationCacheNow();
    }

    public void RestoreFromNavigationCache()
    {
        CancelNavigationCacheTrim();
        if (!_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = false;
        _isNavigatingAway = false;
        _suppressShyHeaderEvaluation = false;
        _suppressContentReveal = false;
        ResetShyHeaderState();
        // Defer the hero-surface rehydration to a low-priority dispatch
        // (same rationale as OnNavigatedTo above).
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => HeroGrid?.RestoreSurface());
        // Skip the page-wide x:Bind re-evaluation when returning to the same
        // artist we just left — every binding still points at the same data.
        var sameArtist = !string.IsNullOrEmpty(_lastRestoredArtistId)
            && string.Equals(_lastRestoredArtistId, ViewModel.ArtistId, StringComparison.Ordinal);
        if (!sameArtist)
        {
            using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.artist.bindingsUpdate"))
            {
                Bindings?.Update();
            }
        }

        if (!string.IsNullOrEmpty(ViewModel.ArtistId))
        {
            using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.artist.initialize"))
            {
                ViewModel.Initialize(ViewModel.ArtistId);
            }
            RestoreDiscographyRepeaters();
            SetupWatchFeedVideo();
            TryShowContentNow();
            TryRestorePendingNavigationScroll();
        }
    }

    private void CaptureNavigationScrollPosition()
    {
        _pendingNavigationScrollArtistId = ViewModel.ArtistId;
        _pendingNavigationScrollOffset = PageScrollView?.VerticalOffset ?? 0;
        _scrollRestoreGeneration++;
    }

    private void ClearPendingNavigationScrollPosition()
    {
        _pendingNavigationScrollArtistId = null;
        _pendingNavigationScrollOffset = null;
        _scrollRestoreGeneration++;
    }

    private void TryRestorePendingNavigationScroll()
    {
        if (_pendingNavigationScrollOffset is not { } offset
            || string.IsNullOrEmpty(_pendingNavigationScrollArtistId)
            || !string.Equals(_pendingNavigationScrollArtistId, ViewModel.ArtistId, StringComparison.Ordinal))
        {
            return;
        }

        var generation = _scrollRestoreGeneration;
        _ = RestorePendingNavigationScrollAsync(offset, generation);
    }

    private async Task RestorePendingNavigationScrollAsync(double offset, int generation)
    {
        if (offset <= 0 || PageScrollView is null)
        {
            ClearPendingNavigationScrollPosition();
            return;
        }

        for (var attempt = 0; attempt < ScrollRestoreMaxAttempts; attempt++)
        {
            await Task.Yield();
            if (attempt > 0)
                await Task.Delay(ScrollRestoreRetryDelayMs);

            if (_isDisposed || _isNavigatingAway || generation != _scrollRestoreGeneration || PageScrollView is null)
                return;

            var maxOffset = Math.Max(0, PageScrollView.ExtentHeight - PageScrollView.ViewportHeight);
            if (maxOffset <= 0 && attempt + 1 < ScrollRestoreMaxAttempts)
                continue;

            var target = Math.Clamp(offset, 0, maxOffset);
            PageScrollView.ScrollToImmediate(0, target);
            UpdateHeroScrollFade();
            _ = EvaluateShyHeaderAsync();
            ClearPendingNavigationScrollPosition();
            return;
        }
    }

    private void ReleaseNavigationCachedImages()
    {
        DetachDiscographyRepeaters();
        ReleaseImagesInSubtree(ArtistImageContainer);
        ReleaseImagesInSubtree(PinnedTopTracksCard);
        ReleaseImagesInSubtree(LatestReleaseCard);
        ReleaseImagesInSubtree(ShyHeaderHost);
    }

    private void DetachDiscographyRepeaters()
    {
        if (_discographyRepeatersDetached)
            return;

        ReleaseImagesInSubtree(AlbumsSection);
        ReleaseImagesInSubtree(SinglesSection);
        ReleaseImagesInSubtree(CompilationsSection);

        if (AlbumsGridRepeater is not null) AlbumsGridRepeater.ItemsSource = null;
        if (AlbumsListRepeater is not null) AlbumsListRepeater.ItemsSource = null;
        if (SinglesGridRepeater is not null) SinglesGridRepeater.ItemsSource = null;
        if (SinglesListRepeater is not null) SinglesListRepeater.ItemsSource = null;
        if (CompilationsShelf is not null) CompilationsShelf.ItemsSource = null;

        _discographyRepeatersDetached = true;
    }

    private void RestoreDiscographyRepeaters()
    {
        if (!_discographyRepeatersDetached)
        {
            UpdateDiscographyRepeaterBindings();
            return;
        }

        _discographyRepeatersDetached = false;
        UpdateDiscographyRepeaterBindings();
    }

    private void UpdateDiscographyRepeaterBindings()
    {
        if (_discographyRepeatersDetached)
            return;

        if (AlbumsGridRepeater is not null)
            AlbumsGridRepeater.ItemsSource = ViewModel.AlbumsGridView ? ViewModel.Albums : null;
        if (AlbumsListRepeater is not null)
            AlbumsListRepeater.ItemsSource = ViewModel.AlbumsGridView ? null : ViewModel.Albums;

        if (SinglesGridRepeater is not null)
            SinglesGridRepeater.ItemsSource = ViewModel.SinglesGridView ? ViewModel.Singles : null;
        if (SinglesListRepeater is not null)
            SinglesListRepeater.ItemsSource = ViewModel.SinglesGridView ? null : ViewModel.Singles;

        if (CompilationsShelf is not null)
            CompilationsShelf.ItemsSource = ViewModel.Compilations;
    }

    private void DiscographyRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        => ReleaseImagesInSubtree(args.Element);

    private static void ReleaseImagesInSubtree(DependencyObject? root)
    {
        if (root is null)
            return;

        switch (root)
        {
            case ContentCard card:
                card.ReleaseImage();
                break;
            case Image image:
                image.Source = null;
                image.Opacity = 1;
                image.Visibility = Visibility.Visible;
                break;
            case Microsoft.UI.Xaml.Shapes.Shape { Fill: ImageBrush shapeBrush }:
                shapeBrush.ImageSource = null;
                break;
            case Border { Background: ImageBrush borderBrush }:
                borderBrush.ImageSource = null;
                break;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
            ReleaseImagesInSubtree(VisualTreeHelper.GetChild(root, i));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.artist.onNavigatedTo");
        base.OnNavigatedTo(e);
        CancelNavigationCacheTrim();
        // Re-attach compiled x:Bind to VM.PropertyChanged BEFORE LoadNewContent /
        // ViewModel.Initialize fires PropertyChanged events for the new artist.
        // Earlier the flow was: TabBarItem.TrimActive → Bindings.StopTracking →
        // OnNavigatedTo set `_trimmedForNavigationCache = false` (preempting
        // RestoreFromNavigationCache from running its Bindings.Update) → LoadNewContent
        // pushed ResetForNewArtist + LoadAsync's scalar writes into deaf bindings →
        // ContentFrame_Navigated fired RestoreFromNavigationCache, which early-returned
        // because the flag was already cleared. Bindings stayed stopped, the VM's
        // Adam-Lambert data was correct underneath, but the page's scalar TextBlocks
        // (ArtistName, MonthlyListeners) and OneWay-bound collections (TopTracks)
        // kept showing the previous artist's values. Calling Update() here, before
        // the flag reset, fixes that — Update() is idempotent and re-attaches the
        // PropertyChanged listeners stopped by an earlier StopTracking.
        Bindings?.Update();
        _trimmedForNavigationCache = false;
        _isNavigatingAway = false;
        _suppressShyHeaderEvaluation = false;
        _suppressContentReveal = false;
        ResetShyHeaderState();

        // Rehydrate the hero surface if it was released on prior navigate-away.
        // No-op on first visit (LoadImage's URL-equality guard short-circuits).
        // Deferred to a low-priority dispatch so it runs AFTER first paint —
        // the palette gradient already paints behind it, so the user sees the
        // hero land via fade-in once the surface is ready (5-8 ms saved off
        // OnNavigatedTo synchronous cost).
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => HeroGrid?.RestoreSurface());

        // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph
        // ConnectedAnimationHelper.TryStartAnimation(ConnectedAnimationHelper.ArtistImage, ArtistImageContainer);

        // Extract the incoming artist URI
        var incomingUri = e.Parameter is ContentNavigationParameter nav ? nav.Uri
                        : e.Parameter as string;

        // Back navigation or re-entering the same artist: re-subscribe to the
        // ArtistStore so the warm BehaviorSubject re-emits the cached overview
        // and re-populates the collections that Hibernate cleared on the prior
        // OnNavigatedFrom. No network re-fetch — Initialize is idempotent for
        // the same artistId and the store value is in memory.
        if (e.NavigationMode == Microsoft.UI.Xaml.Navigation.NavigationMode.Back
            || (incomingUri != null && incomingUri == ViewModel.ArtistId))
        {
            if (!string.IsNullOrEmpty(ViewModel.ArtistId))
                ViewModel.Initialize(ViewModel.ArtistId);
            RestoreDiscographyRepeaters();
            SetupWatchFeedVideo();
            TryShowContentNow();
            TryRestorePendingNavigationScroll();
            return;
        }

        LoadNewContent(e.Parameter);
    }

    private async void LoadNewContent(object? parameter)
    {
        _trimmedForNavigationCache = false;
        CollapseExpandedAlbum();
        TeardownWatchFeed();
        ClearPendingNavigationScrollPosition();

        // Hide the reused page before jumping from the previous artist's scroll
        // position. Otherwise the old bottom-state page crosses the shy-header
        // threshold during navigation and flashes the floating header.
        SuppressShyHeaderForContentReset();
        _suppressContentReveal = true;
        _showingContent = false;
        _crossfadeScheduled = false;
        _heroRevealed = false;
        ContentContainer.Opacity = 0;
        ContentContainer.Visibility = Visibility.Collapsed;
        // Hero overlay also resets to invisible so the new artist's chrome
        // fades in on its own beat instead of inheriting the previous one.
        SetHeroOverlayOpacity(0);
        ShimmerGate.Reset(() => ShimmerContainer, () => ContentContainer);
        PageScrollView.ScrollToImmediate(0, 0);
        UpdateHeroScrollFade();
        RestoreDiscographyRepeaters();

        if (parameter is ContentNavigationParameter navParam)
        {
            ViewModel.Initialize(navParam.Uri);
            ViewModel.PrefillFrom(navParam);
        }
        else if (parameter is string artistIxd)
        {
            ViewModel.Initialize(artistIxd);
        }

        // Initialize() above already subscribed to the ArtistStore — the store
        // emits Ready once the overview lands and the VM drives its cascade from
        // there. No explicit load command needed.
        // SetupWatchFeedVideo is called from CrossfadeToContent after data loads.

        // Warm-cache trigger. ArtistStore is a BehaviorSubject — Initialize's
        // subscribe queues ApplyOverviewState via the dispatcher, which runs
        // after this method returns. After one yield it has landed (ArtistName
        // populated, IsLoading stayed false), so TryShowContentNow can fire
        // ScheduleCrossfade for the warm-cache case where the IsLoading=false
        // write was a no-op (PropertyChanged didn't fire). Without this, with
        // NavigationCacheMode=Required the same VM is reused across nav and the
        // shimmer stays stuck on cache hits. Mirrors AlbumPage / PlaylistPage.
        await Task.Yield();
        _suppressContentReveal = false;
        if (_isNavigatingAway) return;
        TryShowContentNow();
    }

    private void Release_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ArtistReleaseVm release)
        {
            var param = new ContentNavigationParameter
            {
                Uri = release.Uri ?? release.Id,
                Title = release.Name,
                ImageUrl = release.ImageUrl
            };
            NavigationHelpers.OpenAlbum(param, release.Name ?? "Album", NavigationHelpers.IsCtrlPressed());
        }
    }

    // ── Inline album expand (DOM-style visual tree manipulation) ──

    private AlbumDetailPanel? _activeDetailPanel;
    private EventHandler? _closeRequestedHandler;
    private ItemsRepeater? _splitRepeaterAfter;
    private StackPanel? _splitParent;
    private int _splitInsertIndex;
    private ItemsRepeater? _originalRepeater;
    private object? _originalItemsSource;

    // State needed to recompute split on resize
    private LazyReleaseItem? _expandedItem;
    private int _expandedItemIndex;
    private CancellationTokenSource? _resizeDebounceCts;

    private readonly IColorService _colorService = Ioc.Default.GetRequiredService<IColorService>();

    private void AlbumCard_Click(object sender, EventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        var repeater = FindParent<ItemsRepeater>(fe);
        if (repeater == null) return;

        // Walk up to find the direct child of the repeater (the template root)
        DependencyObject? current = fe;
        DependencyObject? parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        while (parent != null && parent != repeater)
        {
            current = parent;
            parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        if (current is not UIElement templateRoot) return;

        var index = repeater.GetElementIndex(templateRoot);
        if (index < 0) return;

        var items = repeater.ItemsSource as System.Collections.IList;
        if (items == null || index >= items.Count) return;

        var item = items[index] as LazyReleaseItem;
        if (item == null || !item.IsLoaded || item.Data == null) return;

        // If clicking the same album that was expanded, just collapse (toggle)
        if (ViewModel.ExpandedAlbum?.Id == item.Id)
        {
            CollapseExpandedAlbum();
            return;
        }

        // Capture the true original repeater/items before collapsing
        // (clicking in _splitRepeaterAfter means the real repeater is _originalRepeater)
        var trueRepeater = _originalRepeater ?? repeater;
        var trueItemsSource = (_originalItemsSource ?? repeater.ItemsSource) as System.Collections.IList;

        // Collapse any existing expansion first (restores original state)
        CollapseExpandedAlbum();

        // Now trueRepeater has its full ItemsSource restored
        if (trueItemsSource == null) return;

        var itemIndex = trueItemsSource.IndexOf(item);
        if (itemIndex < 0) return;

        // Find the parent StackPanel and the repeater's index in it
        var parentPanel = trueRepeater.Parent as StackPanel;
        if (parentPanel == null) return;

        var repeaterIndex = parentPanel.Children.IndexOf(trueRepeater);
        if (repeaterIndex < 0) return;

        // Save original state for restore + resize recompute
        _originalRepeater = trueRepeater;
        _originalItemsSource = trueItemsSource;
        _splitParent = parentPanel;
        _expandedItem = item;
        _expandedItemIndex = itemIndex;

        // Create the detail panel
        _activeDetailPanel = new AlbumDetailPanel();
        _activeDetailPanel.Album = item.Data;
        _activeDetailPanel.Tracks = ViewModel.ExpandedAlbumTracks;
        _closeRequestedHandler = (_, _) => CollapseExpandedAlbum();
        _activeDetailPanel.CloseRequested += _closeRequestedHandler;

        // Fetch extracted color for album art gradient (uses cache if available)
        _ = FetchAlbumColorAsync(item.Data, _activeDetailPanel);

        // Insert detail panel after the repeater
        _splitInsertIndex = repeaterIndex + 1;
        parentPanel.Children.Insert(_splitInsertIndex, _activeDetailPanel);

        // Compute split, notch, and second repeater
        ApplySplitLayout();

        // Auto-scroll so the clicked album card row is visible at the top,
        // with the detail panel below it
        _activeDetailPanel.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0.5, // center the panel, which puts the card row above it
            VerticalOffset = -200          // nudge up so the card row is also visible
        });

        // Update ViewModel state
        ViewModel.ExpandAlbumCommand.Execute(item);
    }

    private void CollapseExpandedAlbum()
    {
        if (_splitParent == null || _originalRepeater == null) return;

        // Unsubscribe event handler to prevent memory leak
        if (_activeDetailPanel != null && _closeRequestedHandler != null)
            _activeDetailPanel.CloseRequested -= _closeRequestedHandler;
        _closeRequestedHandler = null;

        // Detach tracks BEFORE removing from visual tree to prevent COMException
        // (ItemsRepeater can't process CollectionChanged while disconnected)
        if (_activeDetailPanel != null)
        {
            ReleaseImagesInSubtree(_activeDetailPanel);
            _activeDetailPanel.Tracks = null;
            _splitParent.Children.Remove(_activeDetailPanel);
        }
        if (_splitRepeaterAfter != null)
        {
            _splitRepeaterAfter.ElementClearing -= DiscographyRepeater_ElementClearing;
            ReleaseImagesInSubtree(_splitRepeaterAfter);
            _splitParent.Children.Remove(_splitRepeaterAfter);
        }

        // Restore original items source
        _originalRepeater.ItemsSource = _originalItemsSource;

        // Clean up
        _activeDetailPanel = null;
        _splitRepeaterAfter = null;
        _splitParent = null;
        _originalRepeater = null;
        _originalItemsSource = null;
        _expandedItem = null;
        _expandedItemIndex = -1;

        ViewModel.CollapseAlbumCommand.Execute(null);
    }

    private static T? FindParent<T>(DependencyObject child, DependencyObject? stopAt = null) where T : DependencyObject
    {
        var current = child;
        var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        while (parent != null && parent != stopAt)
        {
            if (parent is T found) return found;
            current = parent;
            parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        // If we stopped at stopAt, return the last child before it
        if (stopAt != null && parent == stopAt)
            return current as T;
        return null;
    }

    private void RelatedArtist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is RelatedArtistVm artist)
        {
            var param = new ContentNavigationParameter
            {
                Uri = artist.Uri ?? artist.Id ?? "",
                Title = artist.Name,
                ImageUrl = artist.ImageUrl
            };
            NavigationHelpers.OpenArtist(param, artist.Name ?? "Artist", NavigationHelpers.IsCtrlPressed());
        }
    }

    /// <summary>
    /// Detach the MediaPlayer from the visual tree BEFORE the page is removed.
    /// This prevents COM E_ABORT when WinUI tears down the MediaPlayerElement.
    /// </summary>
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        _isNavigatingAway = true;
        CancelResizeDebounce();
        CollapseExpandedAlbum();
        TeardownWatchFeed();
        try { _shyHeaderTransition?.Stop(); } catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Loaded -= ArtistPage_Loaded;
        Unloaded -= ArtistPage_Unloaded;
        SizeChanged -= OnSizeChanged;
        if (HeroGrid != null)
            HeroGrid.SizeChanged -= HeroGrid_SizeChanged;
        if (PageScrollView != null)
            PageScrollView.ViewChanged -= PageScrollView_ViewChanged;
        ViewModel.ContentChanged -= ViewModel_ContentChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_navigationTrimTimer is not null)
        {
            _navigationTrimTimer.Stop();
            _navigationTrimTimer.Tick -= NavigationTrimTimer_Tick;
            _navigationTrimTimer = null;
        }
        CancelResizeDebounce();
        CollapseExpandedAlbum();
        TeardownWatchFeed();
        try { _shyHeaderTransition?.Stop(); } catch { }
        ReleaseNavigationCachedImages();
        (ViewModel as IDisposable)?.Dispose();
    }


    private void WatchFeedOverlay_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        AnimationBuilder.Create()
            .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(150))
            .Start(WatchFeedHoverOverlay);
    }

    private void WatchFeedOverlay_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        AnimationBuilder.Create()
            .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(150))
            .Start(WatchFeedHoverOverlay);
    }

    private void WatchFeedOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Toggle mute on tap
        if (_watchFeedMediaPlayer != null)
            _watchFeedMediaPlayer.IsMuted = !_watchFeedMediaPlayer.IsMuted;
    }

    private void PinnedItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PinnedItem?.Uri == null) return;

        var type = ViewModel.PinnedItem.Type?.ToUpperInvariant();

        // TRACK type: play the track in the artist context
        if (type == "TRACK")
        {
            var playback = Ioc.Default.GetService<IPlaybackService>();
            if (playback != null && !string.IsNullOrEmpty(ViewModel.ArtistId))
                _ = playback.PlayTrackInContextAsync(ViewModel.PinnedItem.Uri, ViewModel.ArtistId);
            return;
        }

        // CONNECTED-ANIM (disabled): nothing to cancel once nothing is being prepared
        // ConnectedAnimationHelper.CancelPending();

        var param = new ContentNavigationParameter
        {
            Uri = ViewModel.PinnedItem.Uri,
            Title = ViewModel.PinnedItem.Title,
            ImageUrl = ViewModel.PinnedItem.ImageUrl
        };

        if (type is "ALBUM" or "SINGLE" or "EP")
            NavigationHelpers.OpenAlbum(param, ViewModel.PinnedItem.Title ?? "Album", NavigationHelpers.IsCtrlPressed());
        else
            NavigationHelpers.OpenAlbum(param, ViewModel.PinnedItem.Title ?? "Release", NavigationHelpers.IsCtrlPressed());
    }

    private async Task FetchAlbumColorAsync(ArtistReleaseVm album, AlbumDetailPanel panel)
    {
        if (!string.IsNullOrEmpty(album.ColorHex))
        {
            panel.ColorHex = album.ColorHex;
            return;
        }

        if (string.IsNullOrEmpty(album.ImageUrl)) return;

        var imageUrl = SpotifyImageHelper.ToHttpsUrl(album.ImageUrl);
        if (string.IsNullOrEmpty(imageUrl)) return;

        try
        {
            var color = await _colorService.GetColorAsync(imageUrl);
            if (color == null) return;

            // Use Spotify's pre-computed theme-appropriate color
            var isDark = ActualTheme == ElementTheme.Dark;
            var hex = isDark
                ? color.DarkHex ?? color.RawHex
                : color.LightHex ?? color.RawHex;

            if (!string.IsNullOrEmpty(hex))
                panel.ColorHex = hex;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch album color");
        }
    }

    private void AlbumCard_Hover(object sender, EventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        var repeater = FindParent<ItemsRepeater>(fe);
        if (repeater == null) return;

        DependencyObject? current = fe;
        DependencyObject? parentObj = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        while (parentObj != null && parentObj != repeater)
        {
            current = parentObj;
            parentObj = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        if (current is not UIElement templateRoot) return;

        var index = repeater.GetElementIndex(templateRoot);
        if (index < 0) return;

        var items = repeater.ItemsSource as System.Collections.IList;
        if (items == null || index >= items.Count) return;

        var item = items[index] as LazyReleaseItem;
        if (item?.Data == null || !string.IsNullOrEmpty(item.Data.ColorHex) || item.Data.ImageUrl == null) return;

        var imageUrl = SpotifyImageHelper.ToHttpsUrl(item.Data.ImageUrl);
        if (string.IsNullOrEmpty(imageUrl)) return;

        // Fire-and-forget prefetch via service (hot + SQLite + API)
        _ = _colorService.GetColorAsync(imageUrl);
    }

    private void LocationButton_LocationChanged(object? sender, string city)
    {
        ViewModel.UserLocationName = city;
        ViewModel.RefreshNearUserFlags();
    }

    private void ConcertCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string uri && !string.IsNullOrEmpty(uri))
        {
            var title = (btn.DataContext as ConcertVm)?.Title;
            var param = new ContentNavigationParameter
            {
                Uri = uri,
                Title = title
            };
            NavigationHelpers.OpenConcert(param, title ?? "Concert", NavigationHelpers.IsCtrlPressed());
        }
    }

    // ── Hero share / open menu ──────────────────────────────────────────
    // ShareArtist + CopyArtistLink put the open.spotify.com URL on the
    // clipboard; OpenInSpotify launches the spotify: URI which the OS
    // routes to the desktop app if installed (else web fallback).

    private void ShareArtist_Click(object sender, RoutedEventArgs e)
        => CopyArtistShareUrlToClipboard();

    private void CopyArtistLink_Click(object sender, RoutedEventArgs e)
        => CopyArtistShareUrlToClipboard();

    private void CopyArtistShareUrlToClipboard()
    {
        var artistId = ViewModel.ArtistId;
        if (string.IsNullOrEmpty(artistId)) return;

        var url = $"https://open.spotify.com/artist/{artistId}";
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(url);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    // ── About bio expand/collapse ───────────────────────────────────────

    private bool _biographyExpanded;

    private void BiographyShowMore_Click(object sender, RoutedEventArgs e)
    {
        _biographyExpanded = !_biographyExpanded;
        // MaxLines=0 on HtmlTextBlock means "no limit" — same convention TextBlock uses.
        BiographyBlock.MaxLines = _biographyExpanded ? 0 : 4;
        BiographyShowMoreButton.Content = _biographyExpanded ? "Show less" : "Show more";
    }

    // ── Connect & Markets / Gallery handlers ────────────────────────────

    private async void SocialLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            try { await Windows.System.Launcher.LaunchUriAsync(uri); }
            catch { }
        }
    }

    // ── Gallery wrapping grid + lightbox ────────────────────────────────
    // Tile invoke → cache the originating element for focus-restore on close,
    // jump the FlipView to the right index, open the Popup. Close paths:
    // explicit X button, click on the dim scrim (filter by OriginalSource),
    // Escape KeyboardAccelerator on the lightbox root.

    private Control? _galleryReturnFocus;

    private void Gallery_ItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not string url) return;

        // Restore-focus target on close — falls back to the ItemsView itself,
        // which brings keyboard focus back into the gallery section so the
        // user can keep arrow-navigating without a jarring focus jump.
        _galleryReturnFocus = sender;

        var idx = ViewModel.GalleryPhotos is IList<string> photos
            ? photos.IndexOf(url)
            : ViewModel.GalleryPhotos.ToList().IndexOf(url);
        GalleryFlipView.SelectedIndex = Math.Max(0, idx);
        UpdateGalleryCounterText();

        GalleryLightbox.IsOpen = true;
        // Defer focus until the popup has actually opened so it sticks.
        DispatcherQueue.TryEnqueue(() => GalleryLightboxCloseButton.Focus(FocusState.Programmatic));
    }

    private void GalleryLightboxClose_Click(object sender, RoutedEventArgs e)
        => CloseGalleryLightbox();

    private void GalleryLightboxScrim_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only close on clicks that actually land on the scrim itself —
        // bubbled events from the FlipView, image, buttons must not dismiss.
        if (ReferenceEquals(e.OriginalSource, GalleryLightboxRoot))
            CloseGalleryLightbox();
    }

    private void GalleryLightboxEscape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        CloseGalleryLightbox();
        args.Handled = true;
    }

    private void CloseGalleryLightbox()
    {
        GalleryLightbox.IsOpen = false;
        _galleryReturnFocus?.Focus(FocusState.Programmatic);
        _galleryReturnFocus = null;
    }

    private void GalleryFlipView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateGalleryCounterText();

    private void UpdateGalleryCounterText()
    {
        if (GalleryCounterText == null) return;
        var total = ViewModel.GalleryPhotos.Count;
        var idx = Math.Max(0, GalleryFlipView.SelectedIndex);
        GalleryCounterText.Text = total > 0 ? $"{idx + 1} / {total}" : string.Empty;
    }

    // ── Latest release card ─────────────────────────────────────────────

    private void LatestRelease_Click(object sender, RoutedEventArgs e)
    {
        var uri = ViewModel.LatestReleaseUri;
        var name = ViewModel.LatestReleaseName;
        if (string.IsNullOrEmpty(uri)) return;

        var param = new ContentNavigationParameter
        {
            Uri = uri,
            Title = name,
            ImageUrl = ViewModel.LatestReleaseImageUrl
        };
        NavigationHelpers.OpenAlbum(param, name ?? "Album", NavigationHelpers.IsCtrlPressed());
    }

    private void PlayLatestRelease_Click(object sender, RoutedEventArgs e)
    {
        // Same navigation for now — playback context-resolves on the album page.
        // Could be replaced with a direct play command on the VM if/when one exists.
        LatestRelease_Click(sender, e);
    }

    private async void OpenInSpotify_Click(object sender, RoutedEventArgs e)
    {
        var artistId = ViewModel.ArtistId;
        if (string.IsNullOrEmpty(artistId)) return;

        try
        {
            var uri = new Uri($"spotify:artist:{artistId}");
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // Best-effort — silently fail rather than throw on a user-initiated launch.
        }
    }
}
