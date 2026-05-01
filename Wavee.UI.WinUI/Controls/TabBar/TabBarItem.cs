using System;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Diagnostics;

namespace Wavee.UI.WinUI.Controls.TabBar;

public sealed partial class TabBarItem : ObservableObject, ITabBarItem, IDisposable
{
    // 5 cached pages per tab — deliberate memory-vs-UX tradeoff. Back/forward
    // through a deep navigation stack stays instant: no page recreation, no
    // flicker, no rebound of virtualized item containers, no palette/hero
    // re-prefetch. Setting this to 0 made nav feel sluggish; find memory wins
    // elsewhere (image cache pin balance, unbounded subscriptions, idle render
    // loops) before touching this number again.
    private const int DefaultFrameCacheSize = 5;

    public Frame ContentFrame { get; }

    public event EventHandler<Microsoft.UI.Xaml.Navigation.NavigationEventArgs>? Navigated;
    public event EventHandler<TabItemParameter>? ContentChanged;

    [ObservableProperty]
    private IconSource? _iconSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeader))]
    [NotifyPropertyChangedFor(nameof(DisplayToolTipText))]
    private string? _header;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayToolTipText))]
    private string? _toolTipText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeader))]
    [NotifyPropertyChangedFor(nameof(PinIndicatorVisibility))]
    [NotifyPropertyChangedFor(nameof(CompactWidth))]
    private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeader))]
    [NotifyPropertyChangedFor(nameof(TabStyle))]
    [NotifyPropertyChangedFor(nameof(CompactWidth))]
    private bool _isCompact;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SleepIndicatorVisibility))]
    [NotifyPropertyChangedFor(nameof(DisplayToolTipText))]
    [NotifyPropertyChangedFor(nameof(TabOpacity))]
    private bool _isSleeping;

    /// <summary>
    /// Returns empty string when compact (icon-only), otherwise the header text
    /// </summary>
    public string? DisplayHeader => IsCompact ? "" : Header;

    /// <summary>
    /// Returns the appropriate style based on IsCompact state
    /// </summary>
    public Style? TabStyle => IsCompact
        ? Application.Current.Resources["TabBarItemCompactStyle"] as Style
        : Application.Current.Resources["TabBarItemStyle"] as Style;

    /// <summary>
    /// Returns a narrow max width when compact, otherwise no limit.
    /// Pinned compact tabs are slightly wider to show the pin indicator.
    /// </summary>
    public double CompactWidth => IsCompact ? (IsPinned ? 64 : 44) : double.PositiveInfinity;

    /// <summary>
    /// Returns Visible when pinned, Collapsed otherwise (for pin badge)
    /// </summary>
    public Visibility PinIndicatorVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SleepIndicatorVisibility => IsSleeping ? Visibility.Visible : Visibility.Collapsed;

    public string? DisplayToolTipText => IsSleeping
        ? $"{(ToolTipText ?? Header ?? "Tab")} (Sleeping)"
        : ToolTipText ?? Header;

    public double TabOpacity => IsSleeping ? 0.72 : 1.0;

    private ITabBarItemContent? _previousContent;
    private IDisposable? _contentPendingDisposal;
    private const int MaxBackStackSize = 20;
    private TabSleepSnapshot? _sleepSnapshot;
    private object? _pendingSleepRestoreState;
    private Type? _pendingSleepRestorePageType;

    // Correlates Start/Stop ETW pairs for a single navigation. Set inside Navigate()
    // (or the NavigationParameter setter) right before ContentFrame.Navigate, read
    // from ContentFrame_Navigated to emit the Stop event with the matching nav id.
    // A plain field is safe because all navigations on a given tab's UI thread are
    // sequential — no interleaving possible.
    private long _pendingNavId;
    private string? _pendingPageName;

    private TabItemParameter? _navigationParameter;
    public DateTimeOffset LastActivatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    public TabItemParameter? NavigationParameter
    {
        get => _navigationParameter;
        set
        {
            if (value != _navigationParameter)
            {
                _navigationParameter = value;
                if (_navigationParameter?.InitialPageType != null)
                {
                    var navId = WaveeNavigationEventSource.Log.NextNavId();
                    WaveeNavigationEventSource.Log.Navigating(navId, _navigationParameter.InitialPageType.Name, "Restore");
                    _pendingNavId = navId;
                    _pendingPageName = _navigationParameter.InitialPageType.Name;

                    TryNavigateFrame(
                        _navigationParameter.InitialPageType,
                        _navigationParameter.NavigationParameter,
                        new DrillInNavigationTransitionInfo());
                }
                else
                {
                    ContentFrame.Content = null;
                }
            }
        }
    }

    public ITabBarItemContent? TabItemContent => ContentFrame.Content as ITabBarItemContent;

    public TabBarItem()
    {
        ContentFrame = new Frame
        {
            CacheSize = DefaultFrameCacheSize,
            IsNavigationStackEnabled = true
        };
        ContentFrame.Navigating += ContentFrame_Navigating;
        ContentFrame.Navigated += ContentFrame_Navigated;
        ContentFrame.NavigationFailed += ContentFrame_NavigationFailed;
    }

    private void ContentFrame_NavigationFailed(object sender, Microsoft.UI.Xaml.Navigation.NavigationFailedEventArgs e)
    {
        e.Handled = true;
        ShowNavigationError(
            e.SourcePageType,
            _navigationParameter?.NavigationParameter,
            e.Exception);
    }

    private bool TryNavigateFrame(
        Type pageType,
        object? parameter,
        NavigationTransitionInfo transition)
    {
        try
        {
            var navigated = ContentFrame.Navigate(pageType, parameter, transition);
            if (!navigated)
            {
                ShowNavigationError(
                    pageType,
                    parameter,
                    new InvalidOperationException($"Frame rejected navigation to {pageType.Name}."));
            }

            return navigated;
        }
        catch (Exception ex)
        {
            ShowNavigationError(pageType, parameter, ex);
            return false;
        }
    }

    private void ShowNavigationError(Type? pageType, object? parameter, Exception? exception)
    {
        var pageName = pageType?.Name ?? _pendingPageName ?? "page";
        System.Diagnostics.Debug.WriteLine(
            $"NavigationFailed [{pageName}]: {exception?.Message}");

        if (_pendingNavId != 0)
        {
            WaveeNavigationEventSource.Log.Navigated(_pendingNavId, $"{pageName}.Failed");
            _pendingNavId = 0;
            _pendingPageName = null;
        }

        if (_previousContent != null)
        {
            _previousContent.ContentChanged -= TabItemContent_ContentChanged;
            _previousContent = null;
        }

        var oldContent = ContentFrame.Content;
        var pendingContent = _contentPendingDisposal;
        _contentPendingDisposal = null;

        ContentFrame.Content = CreateNavigationErrorContent(pageType, parameter, exception);

        if (pendingContent != null && !ReferenceEquals(pendingContent, ContentFrame.Content))
            pendingContent.Dispose();

        if (oldContent is IDisposable disposableOld
            && !ReferenceEquals(oldContent, pendingContent)
            && !ReferenceEquals(oldContent, ContentFrame.Content))
        {
            disposableOld.Dispose();
        }
    }

    private UIElement CreateNavigationErrorContent(Type? pageType, object? parameter, Exception? exception)
    {
        var title = pageType is null
            ? "Couldn't open this page"
            : $"Couldn't open {GetReadablePageName(pageType)}";
        var message = exception is null
            ? "Navigation failed before the page could be loaded."
            : $"{exception.GetType().Name}: {exception.Message}";

        var error = new Wavee.UI.WinUI.Controls.ErrorStateView
        {
            Title = title,
            Message = message,
            RetryButtonText = "Try again",
            RetryCommand = pageType is null
                ? null
                : new RelayCommand(() => Navigate(pageType, parameter))
        };

        return new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { error }
        };
    }

    private static string GetReadablePageName(Type pageType)
    {
        var name = pageType.Name;
        return name.EndsWith("Page", StringComparison.Ordinal)
            ? name[..^4]
            : name;
    }

    public void Navigate(Type pageType, object? parameter = null, bool suppressTransition = false)
    {
        if (IsSleeping)
        {
            DiscardSleepState();
            ResetFrameForWake();
        }

        // Open the ETW navigation pair for this hop. We emit Navigating unconditionally
        // so every user-perceived navigation gets a row in Navigation_Metrics.csv —
        // including the same-page no-op and the refresh-in-place cases, which are both
        // closed with an immediate Navigated below.
        var navId = WaveeNavigationEventSource.Log.NextNavId();
        WaveeNavigationEventSource.Log.Navigating(navId, pageType.Name, suppressTransition ? "Suppressed" : "DrillIn");

        var oldParameter = _navigationParameter;

        _navigationParameter = new TabItemParameter
        {
            InitialPageType = pageType,
            NavigationParameter = parameter
        };

        // If the current page is already the target type, reuse it instead of
        // destroying and recreating the entire visual tree (expensive XAML parsing).
        if (ContentFrame.Content?.GetType() == pageType)
        {
            var currentUri = GetParameterUri(oldParameter?.NavigationParameter);
            var newUri = GetParameterUri(parameter);

            if (string.Equals(currentUri, newUri, StringComparison.Ordinal))
            {
                // Same page, same parameter — no Frame.Navigate will fire, so close the pair now.
                WaveeNavigationEventSource.Log.Navigated(navId, pageType.Name);
                return;
            }

            // Different parameter — let the page refresh in-place
            if (ContentFrame.Content is ITabBarItemContent refreshable)
            {
                try
                {
                    refreshable.RefreshWithParameter(parameter);
                    // RefreshWithParameter is synchronous from our caller's perspective; close the pair.
                    WaveeNavigationEventSource.Log.Navigated(navId, pageType.Name);
                }
                catch (Exception ex)
                {
                    _pendingNavId = navId;
                    _pendingPageName = pageType.Name;
                    ShowNavigationError(pageType, parameter, ex);
                }

                return;
            }

            // Fallback: page doesn't support refresh, force re-creation
            ContentFrame.Content = null;
        }

        // Real Frame.Navigate path — stash the nav id so ContentFrame_Navigated can close the pair.
        _pendingNavId = navId;
        _pendingPageName = pageType.Name;

        // CONNECTED-ANIM (disabled): suppression branch was only used for content
        // pages running connected animations. With them disabled, every navigation
        // uses the default DrillIn transition.
        // var transition = suppressTransition
        //     ? (NavigationTransitionInfo)new SuppressNavigationTransitionInfo()
        //     : new DrillInNavigationTransitionInfo();
        var transition = (NavigationTransitionInfo)new DrillInNavigationTransitionInfo();
        TryNavigateFrame(pageType, parameter, transition);
        MarkActivated();
    }

    public void MarkActivated() => LastActivatedAtUtc = DateTimeOffset.UtcNow;

    public void TrimActiveContentForNavigationCache()
    {
        if (IsSleeping)
            return;

        if (ContentFrame.Content is INavigationCacheMemoryParticipant participant)
            participant.TrimForNavigationCache();
    }

    public void RestoreActiveContentFromNavigationCache()
    {
        if (IsSleeping)
            return;

        if (ContentFrame.Content is INavigationCacheMemoryParticipant participant)
            participant.RestoreFromNavigationCache();
    }

    public bool Sleep()
    {
        if (IsSleeping || ContentFrame.Content is null)
            return false;

        object? activePageState = null;
        var activePageType = ContentFrame.Content.GetType();
        if (ContentFrame.Content is ITabSleepParticipant sleepParticipant)
            activePageState = sleepParticipant.CaptureSleepState();

        string? navigationState = null;
        try
        {
            navigationState = ContentFrame.GetNavigationState();
        }
        catch
        {
            // Best effort: route fallback below still allows waking.
        }

        _sleepSnapshot = new TabSleepSnapshot(navigationState, activePageType, activePageState);
        ClearLiveContent();
        IsSleeping = true;
        return true;
    }

    public bool Wake()
    {
        if (!IsSleeping)
            return false;

        ResetFrameForWake();
        IsSleeping = false;

        var snapshot = _sleepSnapshot;
        _pendingSleepRestoreState = snapshot?.ActivePageState;
        _pendingSleepRestorePageType = _pendingSleepRestoreState != null ? snapshot?.ActivePageType : null;

        var restoredFromNavigationState = false;
        if (!string.IsNullOrWhiteSpace(snapshot?.NavigationState))
        {
            try
            {
                ContentFrame.SetNavigationState(snapshot.NavigationState);
                restoredFromNavigationState = true;
            }
            catch
            {
                restoredFromNavigationState = false;
            }
        }

        if (!restoredFromNavigationState && _navigationParameter?.InitialPageType != null)
        {
            _pendingNavId = WaveeNavigationEventSource.Log.NextNavId();
            _pendingPageName = _navigationParameter.InitialPageType.Name;
            WaveeNavigationEventSource.Log.Navigating(_pendingNavId, _pendingPageName, "WakeFallback");
            TryNavigateFrame(
                _navigationParameter.InitialPageType,
                _navigationParameter.NavigationParameter,
                new DrillInNavigationTransitionInfo());
        }

        _sleepSnapshot = null;
        MarkActivated();
        return true;
    }

    private static string? GetParameterUri(object? parameter) => parameter switch
    {
        ContentNavigationParameter nav => nav.Uri,
        string s => s,
        _ => null
    };

    private void ContentFrame_Navigating(object sender, Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
    {
        TrimActiveContentForNavigationCache();
        _contentPendingDisposal = ContentFrame.Content as IDisposable;
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // Close the ETW navigation pair opened in Navigate() / NavigationParameter setter.
        // _pendingNavId == 0 means this callback fired without a preceding Start (shouldn't
        // happen in practice, but guard so we never emit a Stop without a Start).
        if (_pendingNavId != 0)
        {
            WaveeNavigationEventSource.Log.Navigated(
                _pendingNavId,
                _pendingPageName ?? e.SourcePageType?.Name ?? "Unknown");
            _pendingNavId = 0;
            _pendingPageName = null;
        }

        // Forward navigation event for external subscribers
        Navigated?.Invoke(this, e);

        RestoreActiveContentFromNavigationCache();

        // Unsubscribe from previous page's ContentChanged to prevent leak
        if (_previousContent != null)
            _previousContent.ContentChanged -= TabItemContent_ContentChanged;

        var contentToDispose = _contentPendingDisposal;
        _contentPendingDisposal = null;
        if (contentToDispose != null && !ReferenceEquals(contentToDispose, ContentFrame.Content))
            contentToDispose.Dispose();

        _previousContent = TabItemContent;

        if (TabItemContent != null)
            TabItemContent.ContentChanged += TabItemContent_ContentChanged;

        if (_pendingSleepRestoreState != null
            && _pendingSleepRestorePageType != null
            && e.SourcePageType == _pendingSleepRestorePageType
            && ContentFrame.Content is ITabSleepParticipant sleepParticipant)
        {
            sleepParticipant.RestoreSleepState(_pendingSleepRestoreState);
            _pendingSleepRestoreState = null;
            _pendingSleepRestorePageType = null;
        }

        // Cap BackStack to prevent unbounded growth
        while (ContentFrame.BackStack.Count > MaxBackStackSize)
            ContentFrame.BackStack.RemoveAt(0);
    }

    private void TabItemContent_ContentChanged(object? sender, TabItemParameter e)
    {
        _navigationParameter = e;
        ContentChanged?.Invoke(this, e);
    }

    public void Dispose()
    {
        ContentFrame.Navigating -= ContentFrame_Navigating;
        ContentFrame.Navigated -= ContentFrame_Navigated;
        ContentFrame.NavigationFailed -= ContentFrame_NavigationFailed;
        DiscardSleepState();

        ClearLiveContent();
        ContentFrame.CacheSize = 0;
    }

    private void ClearLiveContent()
    {
        var activeContent = ContentFrame.Content;
        var previousContent = _previousContent;
        var pendingContent = _contentPendingDisposal;
        _previousContent = null;
        _contentPendingDisposal = null;

        if (previousContent != null)
        {
            previousContent.ContentChanged -= TabItemContent_ContentChanged;
            if (!ReferenceEquals(previousContent, activeContent) && previousContent is IDisposable previousDisposable)
                previousDisposable.Dispose();
        }

        if (pendingContent != null && !ReferenceEquals(pendingContent, activeContent))
            pendingContent.Dispose();

        if (activeContent is IDisposable disposable)
            disposable.Dispose();

        ContentFrame.BackStack.Clear();
        ContentFrame.ForwardStack.Clear();
        ContentFrame.Content = null;
        ContentFrame.CacheSize = 0;
        _navigationParameter = null;
    }

    private void ResetFrameForWake()
    {
        ContentFrame.CacheSize = DefaultFrameCacheSize;
    }

    private void DiscardSleepState()
    {
        _sleepSnapshot = null;
        _pendingSleepRestoreState = null;
        _pendingSleepRestorePageType = null;
        IsSleeping = false;
    }

    private sealed record TabSleepSnapshot(
        string? NavigationState,
        Type? ActivePageType,
        object? ActivePageState);
}
