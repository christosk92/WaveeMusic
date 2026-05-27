using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    private int _cacheSize = 5;

    public PageHost()
    {
        DefaultStyleKey = typeof(ContentControl);
        base.Content = _container;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
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

    /// <summary>
    /// Maximum number of cached pages (active + collapsed-cached). Setting this
    /// runs eviction immediately. Matches today's <c>TabBarItem</c>
    /// adaptive 3/2 sizing.
    /// </summary>
    public int CacheSize
    {
        get => _cacheSize;
        set
        {
            if (_cacheSize == value) return;
            _cacheSize = Math.Max(0, value);
            EvictLruIfNeeded();
        }
    }

    /// <summary>Total count of cached pages (active + collapsed). For memory diagnostics.</summary>
    public int CachedPageCount => _container.Children.Count;

    /// <summary>
    /// Cached pages ordered oldest-first — the last entry is the active page,
    /// the second-last is the prime back-target. Drives the nav-cache
    /// surface-retention pass in <c>TabBarItem</c>.
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
    /// Evicts the oldest collapsed (non-active) cached page, disposing it when
    /// <see cref="IDisposable"/>. Returns false when only the active page
    /// remains. Drives the cross-tab cached-page ceiling.
    /// </summary>
    public bool EvictOldestCollapsed()
    {
        Type? victimType = null;
        foreach (var type in _lru)
        {
            if (type != _currentPageType)
            {
                victimType = type;
                break;
            }
        }

        if (victimType is null || !_cache.TryGetValue(victimType, out var victim))
            return false;

        _cache.Remove(victimType);
        _lru.Remove(victimType);
        _container.Children.Remove(victim);
        if (victim is IDisposable d)
            d.Dispose();
        return true;
    }

    /// <summary>
    /// Evicts collapsed pages from oldest to newest while preserving the active
    /// page and the requested number of newest collapsed pages.
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
        _backStack.Clear();
        _forwardStack.Clear();
        _activePage = null;
        _currentPageType = null;
        _currentParameter = null;
    }

    /// <summary>
    /// Pre-realises a page during idle so the first user navigation to it is a
    /// cache hit. Page is constructed, added to the panel with
    /// <see cref="Visibility.Collapsed"/>, registered in the cache, but NOT made
    /// active. <c>Loaded</c> fires on the page during the next layout pass.
    /// Subsequent calls for the same type are no-ops.
    /// </summary>
    public void Prewarm(Type pageType)
    {
        if (_cache.ContainsKey(pageType)) return;

        try
        {
            UserControl page;
            using (Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("pageHost.prewarm.ctor." + pageType.Name))
            {
                page = PageRegistry.Create(pageType);
            }
            page.Visibility = Visibility.Collapsed;
            AttachFirstLoadedMeter(page);
            _container.Children.Add(page);
            _cache[pageType] = page;
            _lru.AddFirst(pageType); // oldest end — first to evict if pressure rises
            EvictLruIfNeeded();
        }
        catch
        {
            // Prewarm is best-effort. Failure means no cache hit later; navigation still works.
        }
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
                    if (outgoing is IDisposable d) d.Dispose();
                }
                else
                {
                    outgoing.Visibility = Visibility.Collapsed;
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

            if (incoming is IPageHostAware entered)
                entered.OnEntered(parameter, mode);

            EvictLruIfNeeded();

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

    private void EvictLruIfNeeded()
    {
        while (_container.Children.Count > _cacheSize && _lru.Count > 0)
        {
            // Never evict the currently-active page.
            var victimType = _lru.First!.Value;
            if (victimType == _currentPageType)
            {
                if (_lru.Count == 1) break;
                // Walk forward looking for a non-active entry.
                var node = _lru.First.Next;
                while (node is not null && node.Value == _currentPageType)
                    node = node.Next;
                if (node is null) break;
                victimType = node.Value;
                _lru.Remove(node);
            }
            else
            {
                _lru.RemoveFirst();
            }

            EvictPage(victimType);
        }
    }

    private bool EvictPage(Type pageType)
    {
        if (!_cache.TryGetValue(pageType, out var victim))
            return false;

        _cache.Remove(pageType);
        _lru.Remove(pageType);
        _container.Children.Remove(victim);
        if (victim is IDisposable d)
            d.Dispose();
        return true;
    }
}
