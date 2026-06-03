using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.PageHost;

/// <summary>
/// Replacement for <see cref="Microsoft.UI.Xaml.Controls.Frame"/> tuned for snappy
/// navigation. Keeps cached pages rooted in an internal panel with
/// <see cref="Visibility.Collapsed"/> instead of detaching them — back/forward
/// nav becomes a Visibility flip on a page whose visual tree, compiled bindings,
/// and scroll position are already live. No <c>Loaded</c> re-fire, no layout
/// pass, no shimmer rebuild, no x:Bind re-eval.
///
/// Cache is keyed by <see cref="Type"/> alone (matching Frame semantics):
/// same-type-different-parameter nav reuses the cached instance and re-invokes
/// <see cref="IPageHostAware.OnEntered"/> with the new parameter.
///
/// Pages are constructed through <see cref="PageRegistry"/>, not
/// <c>Activator.CreateInstance</c> — explicit factory lambdas registered at
/// startup, no reflection.
/// </summary>
public sealed partial class PageHost : ContentControl
{
    private readonly Grid _container = new();
    private readonly Dictionary<Type, UserControl> _cache = new();
    private readonly LinkedList<Type> _lru = new(); // head = oldest, tail = most recent
    private readonly List<PageStackEntry> _backStack = new();
    private readonly List<PageStackEntry> _forwardStack = new();

    private UserControl? _activePage;
    private Type? _currentPageType;
    private object? _currentParameter;

    /// <summary>
    /// Page types currently hibernated — resident in the cache but idled
    /// (disconnected from live sources) because they fell outside the hot window.
    /// See <see cref="IHibernatingPage"/> and <see cref="ApplyResidencyTiers"/>.
    /// </summary>
    private readonly HashSet<Type> _hibernated = new();

    private readonly ILogger? _logger;

    /// <summary>
    /// Number of most-recently-used <em>collapsed</em> pages kept fully live ("hot")
    /// in addition to the active page, so back/forward to them stays instant. Any
    /// resident page older than this is hibernated via <see cref="IHibernatingPage"/>
    /// so it stops doing per-tick UI-thread work while off-screen.
    /// </summary>
    public const int HotCollapsedPageBudget = 2;

    public PageHost()
    {
        DefaultStyleKey = typeof(ContentControl);
        base.Content = _container;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        try { _logger = Ioc.Default.GetService<ILogger<PageHost>>(); }
        catch { /* Ioc not configured yet (designer / very early construction) — skip logging */ }
    }

    // ── Public API ──────────────────────────────────────────────────────

    public UserControl? ActivePage => _activePage;
    public Type? SourcePageType => _currentPageType;
    public object? CurrentParameter => _currentParameter;

    public IList<PageStackEntry> BackStack => _backStack;
    public IList<PageStackEntry> ForwardStack => _forwardStack;

    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

    /// <summary>
    /// When false, <see cref="Navigate"/> does not push to <see cref="BackStack"/>
    /// and <see cref="GoBack"/> / <see cref="GoForward"/> are no-ops. Use for
    /// single-slot hosts (root window, debug window, theatre overlay) that only
    /// ever show one page type.
    /// </summary>
    public bool IsNavigationStackEnabled { get; set; } = true;

    /// <summary>Total count of cached pages (active + collapsed). For memory diagnostics.</summary>
    public int CachedPageCount => _container.Children.Count;

    /// <summary>
    /// Cached pages ordered oldest-first — the last entry is the active page,
    /// the second-last is the prime back-target. Used by memory diagnostics and
    /// the pressure-driven cache cleanup.
    /// </summary>
    public IReadOnlyList<UserControl> CachedPagesByRecency()
    {
        var list = new List<UserControl>(_lru.Count);
        foreach (var type in _lru)
        {
            if (_cache.TryGetValue(type, out var page))
                list.Add(page);
        }
        return list;
    }

    /// <summary>
    /// Evicts collapsed pages from oldest to newest while preserving the active
    /// page and the requested number of newest collapsed pages. Only reached
    /// under memory-budget pressure (<c>PageHostCacheCleanupAdapter</c>) or the
    /// debug "drop caches" action — never on a tab switch.
    /// </summary>
    public int EvictCollapsedPages(int keepNewestCollapsed = 0)
    {
        keepNewestCollapsed = Math.Max(0, keepNewestCollapsed);

        var collapsed = new List<Type>();
        foreach (var type in _lru)
        {
            if (type != _currentPageType && _cache.ContainsKey(type))
                collapsed.Add(type);
        }

        var evictCount = Math.Max(0, collapsed.Count - keepNewestCollapsed);
        var removed = 0;
        for (var i = 0; i < evictCount; i++)
        {
            if (EvictPage(collapsed[i]))
                removed++;
        }

        return removed;
    }

    /// <summary>
    /// Evicts every collapsed cached page, preserving only the active page.
    /// Returns the number of page trees dropped.
    /// </summary>
    public int EvictAllCollapsed() => EvictCollapsedPages(keepNewestCollapsed: 0);

    public event EventHandler<PageHostNavigatingEventArgs>? Navigating;
    public event EventHandler<PageHostNavigatedEventArgs>? Navigated;
    public event EventHandler<PageHostNavigationFailedEventArgs>? NavigationFailed;

    public bool Navigate(Type pageType, object? parameter = null)
        => NavigateInternal(pageType, parameter, PageHostNavigationMode.New);

    public bool GoBack()
    {
        if (!IsNavigationStackEnabled || _backStack.Count == 0) return false;

        var entry = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);

        if (_currentPageType is not null)
            _forwardStack.Add(new PageStackEntry(_currentPageType, _currentParameter));

        return NavigateCore(entry.PageType, entry.Parameter, PageHostNavigationMode.Back);
    }

    public bool GoForward()
    {
        if (!IsNavigationStackEnabled || _forwardStack.Count == 0) return false;

        var entry = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);

        if (_currentPageType is not null)
            _backStack.Add(new PageStackEntry(_currentPageType, _currentParameter));

        return NavigateCore(entry.PageType, entry.Parameter, PageHostNavigationMode.Forward);
    }

    public void ClearBackStack() => _backStack.Clear();
    public void ClearForwardStack() => _forwardStack.Clear();

    /// <summary>
    /// Drop everything — active page, cache, both stacks. Disposes any
    /// <see cref="IDisposable"/> pages.
    /// </summary>
    public void Clear()
    {
        foreach (var child in _container.Children.OfType<UserControl>().ToArray())
        {
            if (child is IPageHostAware aware) aware.OnLeaving();
            if (child is IDisposable d) d.Dispose();
        }

        _container.Children.Clear();
        _cache.Clear();
        _lru.Clear();
        _hibernated.Clear();
        _backStack.Clear();
        _forwardStack.Clear();
        _activePage = null;
        _currentPageType = null;
        _currentParameter = null;
    }

    // ── Internal navigation flow ────────────────────────────────────────

    private bool NavigateInternal(Type pageType, object? parameter, PageHostNavigationMode mode)
    {
        var navigatingArgs = new PageHostNavigatingEventArgs(pageType, parameter, mode);
        Navigating?.Invoke(this, navigatingArgs);
        if (navigatingArgs.Cancel) return false;

        if (mode == PageHostNavigationMode.New && _currentPageType is not null && IsNavigationStackEnabled)
        {
            _backStack.Add(new PageStackEntry(_currentPageType, _currentParameter));
            _forwardStack.Clear();
        }

        return NavigateCore(pageType, parameter, mode);
    }

    private bool NavigateCore(Type pageType, object? parameter, PageHostNavigationMode mode)
    {
        try
        {
            // ── Outgoing ──
            if (_activePage is { } outgoing)
            {
                if (outgoing is IPageHostAware leaving) leaving.OnLeaving();

                // Cache opt-out: pages implementing IPageHostAware with
                // ShouldCacheInHost = false are disposed on leave instead of
                // staying in the LRU. (VideoPlayerPage is the canonical case —
                // releases the MediaPlayer surface.)
                var shouldCache = outgoing is not IPageHostAware aware || aware.ShouldCacheInHost;
                if (!shouldCache)
                {
                    _container.Children.Remove(outgoing);
                    _cache.Remove(outgoing.GetType());
                    _lru.Remove(outgoing.GetType());
                    _hibernated.Remove(outgoing.GetType());
                    if (outgoing is IDisposable d) d.Dispose();
                }
                else
                {
                    outgoing.Visibility = Visibility.Collapsed;
                    // Page stays resident, but stop any continuous GPU work (Win2D
                    // render loops) while it's off-screen — see IHostVisibilityAware.
                    NotifyHostVisibility(outgoing, isVisible: false);
                    TouchLru(outgoing.GetType());
                }
            }

            // ── Incoming ──
            UserControl incoming;
            if (_cache.TryGetValue(pageType, out var cached))
            {
                incoming = cached;
                incoming.Visibility = Visibility.Visible;
                TouchLru(pageType);
            }
            else
            {
                using (Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("pageHost.ctor"))
                {
                    incoming = PageRegistry.Create(pageType);
                }
                incoming.Visibility = Visibility.Visible;
                // First-Loaded instrumentation — measures the wall-clock delta
                // between Content attachment and the page firing Loaded
                // (i.e. first-measure + arrange + composition commit).
                AttachFirstLoadedMeter(incoming);
                _container.Children.Add(incoming);
                _cache[pageType] = incoming;
                _lru.AddLast(pageType);
            }

            _activePage = incoming;
            _currentPageType = pageType;
            _currentParameter = parameter;

            // A page only ever leaves the idle tier by being navigated to. Re-arm it
            // BEFORE OnEntered so the reload path runs against a rehydrated page.
            if (_hibernated.Remove(pageType) && incoming is IHibernatingPage reviving)
                reviving.Rehydrate();

            if (incoming is IPageHostAware entered)
                entered.OnEntered(parameter, mode);

            // Resume continuous GPU work on the now-visible page. For a freshly
            // created page the tree isn't realized yet (this walk finds nothing)
            // and the control re-checks its own ancestor visibility on Loaded; for
            // a cached page (Visibility flip, no Loaded) this is what resumes it.
            NotifyHostVisibility(incoming, isVisible: true);

            // Idle every resident page that has fallen outside the hot window so it
            // stops doing per-tick UI-thread work while off-screen.
            ApplyResidencyTiers();

            Navigated?.Invoke(this, new PageHostNavigatedEventArgs(pageType, parameter, mode));
            return true;
        }
        catch (Exception ex)
        {
            var failed = new PageHostNavigationFailedEventArgs(pageType, parameter, ex);
            NavigationFailed?.Invoke(this, failed);
            if (!failed.Handled) throw;
            return false;
        }
    }

    private void TouchLru(Type pageType)
    {
        _lru.Remove(pageType);
        _lru.AddLast(pageType);
    }

    /// <summary>
    /// Keeps the active page + the <see cref="HotCollapsedPageBudget"/> most-recently
    /// used collapsed pages fully live, and hibernates every resident
    /// <see cref="IHibernatingPage"/> older than that. Idempotent and cheap — in
    /// steady state exactly one page transitions live→idle per navigation (the one
    /// that just fell off the hot window).
    /// </summary>
    private void ApplyResidencyTiers()
    {
        if (_lru.Count == 0)
            return;

        // _lru tail = active page (most recent); walk backward toward oldest.
        var keptHotCollapsed = 0;
        var hibernatedNow = 0;
        var rehydratedNow = 0;
        var isActive = true;

        for (var node = _lru.Last; node is not null; node = node.Previous)
        {
            var type = node.Value;

            // The active page is always live.
            if (isActive)
            {
                isActive = false;
                continue;
            }

            var withinHotWindow = keptHotCollapsed < HotCollapsedPageBudget;
            if (withinHotWindow)
            {
                keptHotCollapsed++;
                // Defensive: a page re-entering the hot window must be live again.
                if (_hibernated.Remove(type) &&
                    _cache.TryGetValue(type, out var hot) && hot is IHibernatingPage reviving)
                {
                    reviving.Rehydrate();
                    rehydratedNow++;
                }
                continue;
            }

            // Beyond the hot window — idle it if it isn't already.
            if (!_hibernated.Contains(type) &&
                _cache.TryGetValue(type, out var victim) && victim is IHibernatingPage hibernating)
            {
                hibernating.Hibernate();
                _hibernated.Add(type);
                hibernatedNow++;
            }
        }

        if (hibernatedNow > 0 || rehydratedNow > 0)
        {
            _logger?.LogDebug(
                "[pagehost-residency] resident={Resident} hot={Hot} idle={Idle} (+{Hib} hibernated, +{Reh} rehydrated)",
                _cache.Count,
                _cache.Count - _hibernated.Count,
                _hibernated.Count,
                hibernatedNow,
                rehydratedNow);
        }
    }

    /// <summary>
    /// Walks the realized visual tree under <paramref name="root"/> and notifies
    /// every <see cref="IHostVisibilityAware"/> that its host page just became
    /// visible / collapsed, so it can pause/resume continuous GPU work. Cheap —
    /// only toggles a flag; no surfaces or bindings are touched.
    /// </summary>
    private static void NotifyHostVisibility(DependencyObject? root, bool isVisible)
    {
        if (root is null)
            return;

        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is IHostVisibilityAware aware)
                aware.OnHostVisibilityChanged(isVisible);

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(current); }
            catch { continue; }
            for (var i = 0; i < count; i++)
            {
                try { stack.Push(VisualTreeHelper.GetChild(current, i)); }
                catch { /* tree mutated mid-walk — skip */ }
            }
        }
    }

    private static void AttachFirstLoadedMeter(UserControl page)
    {
        var profiler = Wavee.UI.WinUI.Services.UiOperationProfiler.Instance;
        if (profiler is null) return;
        var ts = System.Diagnostics.Stopwatch.GetTimestamp();
        Microsoft.UI.Xaml.RoutedEventHandler? handler = null;
        handler = (_, _) =>
        {
            page.Loaded -= handler;
            var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000.0 /
                     System.Diagnostics.Stopwatch.Frequency;
            profiler.RecordOperation("nav.pageHost.loaded." + page.GetType().Name, ms);
        };
        page.Loaded += handler;
    }

    private bool EvictPage(Type pageType)
    {
        if (!_cache.TryGetValue(pageType, out var victim))
            return false;

        _cache.Remove(pageType);
        _lru.Remove(pageType);
        _hibernated.Remove(pageType);
        _container.Children.Remove(victim);
        if (victim is IDisposable d)
            d.Dispose();
        return true;
    }
}
