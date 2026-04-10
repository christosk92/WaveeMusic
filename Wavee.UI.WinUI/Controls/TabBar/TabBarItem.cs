using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Diagnostics;

namespace Wavee.UI.WinUI.Controls.TabBar;

public sealed partial class TabBarItem : ObservableObject, ITabBarItem, IDisposable
{
    public Frame ContentFrame { get; }

    public event EventHandler<Microsoft.UI.Xaml.Navigation.NavigationEventArgs>? Navigated;
    public event EventHandler<TabItemParameter>? ContentChanged;

    [ObservableProperty]
    private IconSource? _iconSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeader))]
    private string? _header;

    [ObservableProperty]
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

    private ITabBarItemContent? _previousContent;
    private const int MaxBackStackSize = 20;

    // Correlates Start/Stop ETW pairs for a single navigation. Set inside Navigate()
    // (or the NavigationParameter setter) right before ContentFrame.Navigate, read
    // from ContentFrame_Navigated to emit the Stop event with the matching nav id.
    // A plain field is safe because all navigations on a given tab's UI thread are
    // sequential — no interleaving possible.
    private long _pendingNavId;
    private string? _pendingPageName;

    private TabItemParameter? _navigationParameter;
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

                    ContentFrame.Navigate(
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
            CacheSize = 5,
            IsNavigationStackEnabled = true
        };
        ContentFrame.Navigated += ContentFrame_Navigated;
        ContentFrame.NavigationFailed += (_, e) =>
        {
            e.Handled = true;
            System.Diagnostics.Debug.WriteLine(
                $"NavigationFailed [{e.SourcePageType?.Name}]: {e.Exception?.Message}");
        };
    }

    public void Navigate(Type pageType, object? parameter = null, bool suppressTransition = false)
    {
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
                refreshable.RefreshWithParameter(parameter);
                // RefreshWithParameter is synchronous from our caller's perspective; close the pair.
                WaveeNavigationEventSource.Log.Navigated(navId, pageType.Name);
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
        ContentFrame.Navigate(pageType, parameter, transition);
    }

    private static string? GetParameterUri(object? parameter) => parameter switch
    {
        ContentNavigationParameter nav => nav.Uri,
        string s => s,
        _ => null
    };

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

        // Unsubscribe from previous page's ContentChanged to prevent leak
        if (_previousContent != null)
            _previousContent.ContentChanged -= TabItemContent_ContentChanged;

        _previousContent = TabItemContent;

        if (TabItemContent != null)
            TabItemContent.ContentChanged += TabItemContent_ContentChanged;

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
        ContentFrame.Navigated -= ContentFrame_Navigated;

        // Dispose current page
        if (TabItemContent is IDisposable disposable)
            disposable.Dispose();

        // Clear back stack and frame cache so cached pages can be GC'd
        ContentFrame.BackStack.Clear();
        ContentFrame.Content = null;
        ContentFrame.CacheSize = 0;
    }
}
