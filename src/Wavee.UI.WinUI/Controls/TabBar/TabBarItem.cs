using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Diagnostics;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.TabBar;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class TabBarItem : ObservableObject, ITabBarItem, IDisposable
{
    // Single GC critical window per user-initiated navigation. Covers the
    // entire nav lifecycle (Navigate → Navigating → Navigated). One window per nav.
    private static readonly TimeSpan NavigationGcWindow = TimeSpan.FromSeconds(2);

    public PageHost.PageHost ContentHost { get; }

    public event EventHandler<PageHostNavigatedEventArgs>? Navigated;
    public event EventHandler<TabItemParameter>? ContentChanged;

    [ObservableProperty]
    public partial IconSource? IconSource { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeader))]
    [NotifyPropertyChangedFor(nameof(DisplayToolTipText))]
    public partial string? Header { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayToolTipText))]
    public partial string? ToolTipText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeader))]
    [NotifyPropertyChangedFor(nameof(PinIndicatorVisibility))]
    [NotifyPropertyChangedFor(nameof(CompactWidth))]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeader))]
    [NotifyPropertyChangedFor(nameof(TabStyle))]
    [NotifyPropertyChangedFor(nameof(CompactWidth))]
    public partial bool IsCompact { get; set; }

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

    public string? DisplayToolTipText => ToolTipText ?? Header;

    private ITabBarItemContent? _previousContent;
    private const int MaxBackStackSize = 20;

    // Correlates Start/Stop ETW pairs for a single navigation. Set inside Navigate()
    // (or the NavigationParameter setter) right before ContentHost.Navigate, read
    // from ContentHost_Navigated to emit the Stop event with the matching nav id.
    // A plain field is safe because all navigations on a given tab's UI thread are
    // sequential — no interleaving possible.
    private long _pendingNavId;
    private string? _pendingPageName;

    // Parallel correlation id for NavigationDiagnostics (per-stage timing + GC
    // / page-fault / memory-release correlation). Independent of the ETW navId
    // above so the two systems can be enabled/disabled independently.
    private long _pendingDiagNavId;

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
                    NavigationGcCoordinator.BeginCriticalWindow(NavigationGcWindow, "tab-restore-navigation");
                    var navId = WaveeNavigationEventSource.Log.NextNavId();
                    WaveeNavigationEventSource.Log.Navigating(navId, _navigationParameter.InitialPageType.Name, "Restore");
                    _pendingNavId = navId;
                    _pendingPageName = _navigationParameter.InitialPageType.Name;
                    var restoreDiagNavId = NavigationDiagnostics.Instance?.BeginNav(
                        _navigationParameter.InitialPageType.Name, "Restore") ?? 0;
                    _pendingDiagNavId = restoreDiagNavId;

                    using (NavigationDiagnostics.Instance?.Stage(restoreDiagNavId, "pageHostNavigate"))
                    {
                        TryNavigatePageHost(
                            _navigationParameter.InitialPageType,
                            _navigationParameter.NavigationParameter);
                    }
                    if (restoreDiagNavId != 0)
                        NavigationDiagnostics.Instance?.EndNav(restoreDiagNavId);
                }
                else
                {
                    ContentHost.Clear();
                }
            }
        }
    }

    public ITabBarItemContent? TabItemContent => ContentHost.ActivePage as ITabBarItemContent;

    public TabBarItem()
    {
        // Files-app model: every page a tab visits stays fully resident (visual
        // tree + GPU surfaces) until the tab closes. PageHost caches by Type, so
        // a tab holds at most one instance per page type — no proactive eviction,
        // no sleeping, no per-switch surface shedding. Memory is reclaimed only on
        // tab close (Dispose → Clear) or under genuine pressure via
        // PageHostCacheCleanupAdapter.
        ContentHost = new PageHost.PageHost
        {
            IsNavigationStackEnabled = true
        };
        ContentHost.Navigated += ContentHost_Navigated;
        ContentHost.NavigationFailed += ContentHost_NavigationFailed;
    }

    private void ContentHost_NavigationFailed(object? sender, PageHostNavigationFailedEventArgs e)
    {
        e.Handled = true;
        ShowNavigationError(
            e.PageType,
            _navigationParameter?.NavigationParameter,
            e.Exception);
    }

    private bool TryNavigatePageHost(Type pageType, object? parameter)
    {
        try
        {
            var navigated = ContentHost.Navigate(pageType, parameter);
            if (!navigated)
            {
                ShowNavigationError(
                    pageType,
                    parameter,
                    new InvalidOperationException($"PageHost rejected navigation to {pageType.Name}."));
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

        if (_pendingDiagNavId != 0)
        {
            NavigationDiagnostics.Instance?.EndNav(_pendingDiagNavId);
            _pendingDiagNavId = 0;
        }

        if (_previousContent != null)
        {
            _previousContent.ContentChanged -= TabItemContent_ContentChanged;
            _previousContent = null;
        }

        // PageHost can't host arbitrary UIElement content — the error
        // surface goes in the tab's chrome alongside ContentHost via the
        // template, not inside it. We just log; the active page stays as-is
        // (which may be a stale prior page or empty).
        // Future refinement: introduce an explicit "error overlay" surface
        // in the tab content template that observes a NavigationError property.
    }

    public void Navigate(Type pageType, object? parameter = null, bool suppressTransition = false)
    {
        NavigationGcCoordinator.BeginCriticalWindow(NavigationGcWindow, "tab-navigation");

        // Open the ETW navigation pair for this hop. We emit Navigating unconditionally
        // so every user-perceived navigation gets a row in the diagnostics log —
        // including the same-page no-op and the refresh-in-place cases, which are both
        // closed with an immediate Navigated below.
        var navId = WaveeNavigationEventSource.Log.NextNavId();
        WaveeNavigationEventSource.Log.Navigating(navId, pageType.Name, suppressTransition ? "Suppressed" : "DrillIn");

        // Per-stage timing correlator. Snapshots GC counts, working set, page faults
        // at click time so EndNav can report deltas. See NavigationDiagnostics.
        var diagSource = suppressTransition ? "Suppressed" : "DrillIn";
        var diagNavId = NavigationDiagnostics.Instance?.BeginNav(pageType.Name, diagSource) ?? 0;

        var oldParameter = _navigationParameter;

        _navigationParameter = new TabItemParameter
        {
            InitialPageType = pageType,
            NavigationParameter = parameter
        };

        // If the current page is already the target type, reuse it instead of
        // re-realising the visual tree.
        if (ContentHost.ActivePage?.GetType() == pageType)
        {
            var currentUri = GetParameterUri(oldParameter?.NavigationParameter);
            var newUri = GetParameterUri(parameter);

            if (string.Equals(currentUri, newUri, StringComparison.Ordinal))
            {
                // Same page, same parameter — no navigation will fire, so close the pair now.
                WaveeNavigationEventSource.Log.Navigated(navId, pageType.Name);
                NavigationDiagnostics.Instance?.EndNav(diagNavId);
                return;
            }

            // Different parameter — most pages can refresh in-place, but pages
            // with heavy scroll/transition state can opt into a real navigation
            // so the outgoing page is not visibly mutated.
            if (ContentHost.ActivePage is ITabBarItemContent refreshable)
            {
                if (refreshable.ReuseForParameterNavigation)
                {
                    try
                    {
                        using (NavigationDiagnostics.Instance?.Stage(diagNavId, "refreshWithParameter"))
                        {
                            refreshable.RefreshWithParameter(parameter);
                        }
                        // RefreshWithParameter is synchronous from our caller's perspective; close the pair.
                        WaveeNavigationEventSource.Log.Navigated(navId, pageType.Name);
                        NavigationDiagnostics.Instance?.EndNav(diagNavId);
                    }
                    catch (Exception ex)
                    {
                        _pendingNavId = navId;
                        _pendingPageName = pageType.Name;
                        _pendingDiagNavId = diagNavId;
                        ShowNavigationError(pageType, parameter, ex);
                    }

                    return;
                }
            }

            // Fall through to a normal Navigate. This preserves the old
            // page as a back-stack entry and gives the new parameter a fresh
            // visual tree.
        }

        // Real Navigate path — stash the nav id so ContentHost_Navigated can close the pair.
        _pendingNavId = navId;
        _pendingPageName = pageType.Name;
        _pendingDiagNavId = diagNavId;

        // `suppressTransition` only drives the ETW/diagnostics source label now
        // (NavigationHelpers still computes it for connected-animation paths).
        using (NavigationDiagnostics.Instance?.Stage(diagNavId, "pageHostNavigate"))
        {
            TryNavigatePageHost(pageType, parameter);
        }
        MarkActivated();
        // Close the diagnostics nav AFTER the Stage scope above has disposed.
        // ContentHost_Navigated ran synchronously inside TryNavigatePageHost and
        // already added its own stages; we now stamp the final summary line.
        if (diagNavId != 0)
            NavigationDiagnostics.Instance?.EndNav(diagNavId);
    }

    public void MarkActivated() => LastActivatedAtUtc = DateTimeOffset.UtcNow;

    private static string? GetParameterUri(object? parameter) => parameter switch
    {
        ContentNavigationParameter nav => nav.Uri,
        EpisodeNavigationParameter nav => nav.EpisodeUri,
        string s => s,
        _ => null
    };

    private void ContentHost_Navigated(object? sender, PageHostNavigatedEventArgs e)
    {
        // GC critical window already covers this stage — opened in Navigate() /
        // NavigationParameter setter.

        // Close the ETW navigation pair opened in Navigate() / NavigationParameter setter.
        if (_pendingNavId != 0)
        {
            WaveeNavigationEventSource.Log.Navigated(
                _pendingNavId,
                _pendingPageName ?? e.PageType.Name);
            _pendingNavId = 0;
            _pendingPageName = null;
        }

        var navIdForDiag = _pendingDiagNavId;
        _pendingDiagNavId = 0;

        using (NavigationDiagnostics.Instance?.Stage(navIdForDiag, "pageHostNavigated"))
        {
            // Forward navigation event for external subscribers
            Navigated?.Invoke(this, e);

            // Unsubscribe from previous page's ContentChanged to prevent leak
            if (_previousContent != null)
                _previousContent.ContentChanged -= TabItemContent_ContentChanged;

            _previousContent = TabItemContent;

            if (TabItemContent != null)
                TabItemContent.ContentChanged += TabItemContent_ContentChanged;

            // Cap BackStack to prevent unbounded growth
            while (ContentHost.BackStack.Count > MaxBackStackSize)
                ContentHost.BackStack.RemoveAt(0);
        }

        // EndNav is NOT called here. ContentHost_Navigated runs synchronously
        // inside the originating Navigate call; the entry-point method
        // (Navigate / NavigationParameter setter) calls EndNav after its own
        // surrounding Stage scopes have closed.
    }

    private void TabItemContent_ContentChanged(object? sender, TabItemParameter e)
    {
        _navigationParameter = e;
        ContentChanged?.Invoke(this, e);
    }

    public void Dispose()
    {
        ContentHost.Navigated -= ContentHost_Navigated;
        ContentHost.NavigationFailed -= ContentHost_NavigationFailed;

        ClearLiveContent();
    }

    private void ClearLiveContent()
    {
        // Unsubscribe from page's ContentChanged event first to prevent leaks.
        if (_previousContent != null)
        {
            _previousContent.ContentChanged -= TabItemContent_ContentChanged;
            _previousContent = null;
        }

        // PageHost.Clear disposes every cached page (active + collapsed) that
        // implements IDisposable, and tears down both stacks.
        ContentHost.Clear();
        _navigationParameter = null;
    }
}
