using System;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.Imaging;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Diagnostics;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.TabBar;

public sealed partial class TabBarItem : ObservableObject, ITabBarItem, IDisposable
{
    // Single GC critical window per user-initiated navigation. Covers the
    // entire nav lifecycle (Navigate → Navigating → Navigated → deferredTrim
    // 1 s later → trim work). One window per nav — no longer stacks three.
    // Previously: 4 s nav + 4 s page-host-navigating + 2 s page-host-navigated
    // opened three separate windows; with rapid back-and-forth nav (2-3 s
    // cadence) the refcount never returned to 0, so the post-window Gen2
    // drain in NavigationGcCoordinator.EndCriticalWindow never fired.
    private static readonly TimeSpan NavigationGcWindow = TimeSpan.FromSeconds(4);

    // Per-tab page cache. "Comfortable" = 3 keeps back/forward through a
    // deep nav stack instant (no recreation, no flicker, no item-container
    // rebind, no palette / hero re-prefetch). Setting this too low makes nav
    // feel sluggish.
    //
    // The catch is cross-tab: 5 × N tabs is unbounded growth, and the
    // composition tree footprint scales linearly with the total cached pages.
    // So we adapt: stay at 3 while tabs are few, drop to 2 when the user has
    // more tabs open. The threshold is set so a typical "1-3 tabs" workflow
    // keeps the full back/forward cache, while a power user with many tabs
    // trades a bit of cache depth for headroom.
    //
    // Both values are tuned numbers — change them in tandem with
    // AdaptiveTabCountThreshold.
    private const int ComfortableFrameCacheSize = 3;
    private const int ReducedFrameCacheSize = 2;
    private const int AdaptiveTabCountThreshold = 3;

    /// <summary>
    /// Picks the right CacheSize for the current total tab count. Called
    /// from the ctor of a freshly-built tab and from
    /// <see cref="OnTabInstancesChanged"/> whenever tabs are added or removed.
    /// </summary>
    private static int ComputeAdaptiveCacheSize(int tabCount)
        => tabCount > AdaptiveTabCountThreshold ? ReducedFrameCacheSize : ComfortableFrameCacheSize;

    private static bool _tabInstancesSubscribed;
    private static void EnsureTabInstancesSubscription()
    {
        if (_tabInstancesSubscribed) return;
        _tabInstancesSubscribed = true;
        ShellViewModel.TabInstances.CollectionChanged += OnTabInstancesChanged;
    }

    private static void OnTabInstancesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var target = ComputeAdaptiveCacheSize(ShellViewModel.TabInstances.Count);
        foreach (var tab in ShellViewModel.TabInstances)
        {
            // Never raise above the just-computed adaptive ceiling, but don't
            // touch sleeping tabs (their host is detached / CacheSize=0 by
            // design — DiscardSleepState restores it on wake).
            if (tab.IsSleeping) continue;
            if (tab.ContentHost.CacheSize != target)
                tab.ContentHost.CacheSize = target;
        }
    }

    public PageHost.PageHost ContentHost { get; }

    public event EventHandler<PageHostNavigatedEventArgs>? Navigated;
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
    private const int MaxBackStackSize = 20;
    private TabSleepSnapshot? _sleepSnapshot;
    private object? _pendingSleepRestoreState;
    private Type? _pendingSleepRestorePageType;

    // Correlates Start/Stop ETW pairs for a single navigation. Set inside Navigate()
    // (or the NavigationParameter setter) right before ContentHost.Navigate, read
    // from ContentHost_Navigated to emit the Stop event with the matching nav id.
    // A plain field is safe because all navigations on a given tab's UI thread are
    // sequential — no interleaving possible.
    private long _pendingNavId;
    private string? _pendingPageName;
    private bool _skipNextNavigationCacheTrim;

    // Deferred trim: when the user navigates AWAY from a page, we don't tear
    // down its bindings synchronously on the critical-path. Instead we schedule
    // `participant.TrimForNavigationCache()` ~1 s later via a DispatcherQueueTimer
    // and cancel if the user returns to that page first. Holding the
    // participant reference (not just ContentHost.ActivePage) means the timer
    // fires for the right page even if it's evicted from the cache mid-wait.
    // UI-thread-only field; no locking needed.
    private static readonly TimeSpan DeferredTrimDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeferredTrimRetryDelay = TimeSpan.FromMilliseconds(750);
    private const int MaxDeferredTrimRetries = 8;
    private (INavigationCacheMemoryParticipant Participant, DispatcherQueueTimer Timer)? _pendingTrim;

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
        EnsureTabInstancesSubscription();
        // The new tab isn't in TabInstances yet — count is the existing
        // tabs, +1 for ourselves. OnTabInstancesChanged will fire after the
        // ShellViewModel/NavigationHelpers code adds us and re-tune every
        // tab if the threshold has been crossed.
        ContentHost = new PageHost.PageHost
        {
            CacheSize = ComputeAdaptiveCacheSize(ShellViewModel.TabInstances.Count + 1),
            IsNavigationStackEnabled = true
        };
        ContentHost.Navigating += ContentHost_Navigating;
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

        if (IsSleeping)
        {
            DiscardSleepState();
            ResetHostForWake();
        }

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

        // `suppressTransition` parameter preserved for signature stability
        // (NavigationHelpers.Navigate still computes it for connected-animation
        // paths) — it now only drives `_skipNextNavigationCacheTrim`.
        _skipNextNavigationCacheTrim = suppressTransition;
        using (NavigationDiagnostics.Instance?.Stage(diagNavId, "pageHostNavigate"))
        {
            if (!TryNavigatePageHost(pageType, parameter))
                _skipNextNavigationCacheTrim = false;
        }
        MarkActivated();
        // Close the diagnostics nav AFTER the Stage scope above has disposed.
        // ContentHost_Navigated ran synchronously inside TryNavigatePageHost and
        // already added its own stages; we now stamp the final summary line.
        if (diagNavId != 0)
            NavigationDiagnostics.Instance?.EndNav(diagNavId);
    }

    public void MarkActivated() => LastActivatedAtUtc = DateTimeOffset.UtcNow;

    public void TrimActiveContentForNavigationCache()
    {
        if (IsSleeping)
            return;

        if (ContentHost.ActivePage is not INavigationCacheMemoryParticipant participant)
            return;

        // Defer the actual trim by ~1 s. Two reasons:
        //   1. The 96-listener Bindings.StopTracking + Hibernate work for a
        //      typical detail page (Album / Playlist / Artist) is ~100 ms
        //      synchronous CPU; running it inline blocks the new page's first
        //      frame from rendering.
        //   2. If the user navigates back to the same page (e.g. tap-through
        //      album → home → album), the trim is cancelled and bindings stay
        //      live — no re-bind cost, no flicker.
        // CancelPendingTrim drops any prior schedule (for any participant) so
        // we never end up with multiple competing timers on the same tab.
        CancelPendingTrim();

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            // No dispatcher (shouldn't happen on the UI thread, but be safe) —
            // fall back to synchronous trim.
            try { participant.TrimForNavigationCache(); }
            catch { /* best-effort */ }
            return;
        }

        var timer = dispatcher.CreateTimer();
        timer.Interval = DeferredTrimDelay;
        timer.IsRepeating = false;
        var capturedParticipant = participant;
        timer.Tick += (s, _) =>
        {
            // Clear field BEFORE invoking so a re-schedule inside the trim
            // doesn't trip over a stale entry.
            if (_pendingTrim?.Timer == s)
                _pendingTrim = null;

            // NOTE: the indiscriminate CompositionImage tree-walk release that
            // used to live here was removed. It released every cover surface on
            // every cached page (TrackItem rows, ContentCard shelves, popular-
            // release rows, etc.). Restore relied on the LRU still holding the
            // entry, but ReleasePin unpinned each entry first — under any
            // memory pressure the LRU evicted the unpinned cache entry between
            // release and the user's return, the post-Back tree-walk peek
            // missed, TryLoadCurrent fell into the cold-load path that nulls
            // the brush surface, and rows flickered on then stayed on the
            // placeholder glyph. Page-level micro-steps below still release
            // the heavy GPU consumers (HeroHeader surfaces, backdrop blur
            // graphs) per-page where the page actually knows what's safe to
            // drop. Broader GPU-surface release across cached pages now exists
            // in ApplySurfaceRetention; hidden pages restore via a clean reload,
            // not the fragile cache peek that failed here.

            // Enqueue each micro-step on its own low-priority dispatcher pump
            // so rendering and input frames can interleave between them. For
            // heavy pages (AlbumPage / PlaylistPage / ArtistPage) this turns
            // a ~120 ms blocking burst into several ~30-50 ms chunks; pages
            // that didn't override GetTrimMicroSteps still get a single chunk
            // matching the old behaviour (default impl yields the legacy
            // TrimForNavigationCache as one step).
            foreach (var step in capturedParticipant.GetTrimMicroSteps())
            {
                var capturedStep = step;
                ScheduleDeferredTrimStep(dispatcher, capturedParticipant, capturedStep, attempt: 0);
            }
        };
        _pendingTrim = (participant, timer);
        timer.Start();
    }

    private void ScheduleDeferredTrimStep(
        DispatcherQueue dispatcher,
        INavigationCacheMemoryParticipant participant,
        Action step,
        int attempt)
    {
        dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(participant, ContentHost.ActivePage))
                return;

            if (NavigationGcCoordinator.IsNavigationCritical)
            {
                if (attempt >= MaxDeferredTrimRetries)
                    return;

                var retryTimer = dispatcher.CreateTimer();
                retryTimer.Interval = DeferredTrimRetryDelay;
                retryTimer.IsRepeating = false;
                retryTimer.Tick += (s, _) =>
                {
                    s.Stop();
                    ScheduleDeferredTrimStep(dispatcher, participant, step, attempt + 1);
                };
                retryTimer.Start();
                return;
            }

            using (NavigationDiagnostics.Instance?.StageCurrent("deferredTrim.step"))
            {
                try { step(); }
                catch { /* best-effort */ }
            }
        });
    }

    /// <summary>
    /// Drops any pending deferred trim for this tab. Called when the user
    /// re-enters the page whose trim was scheduled (so its bindings stay
    /// alive), and on <see cref="Dispose"/> so timers don't leak.
    /// </summary>
    private void CancelPendingTrim()
    {
        if (_pendingTrim is { } pending)
        {
            pending.Timer.Stop();
            _pendingTrim = null;
        }
    }

    public void RestoreActiveContentFromNavigationCache()
    {
        if (IsSleeping)
            return;

        // Page-level binding restore only. GPU-surface restore is handled
        // separately by ApplySurfaceRetention — one mechanism, recency-scoped.
        if (ContentHost.ActivePage is INavigationCacheMemoryParticipant participant)
            participant.RestoreFromNavigationCache();
    }

    // ── Nav-cache GPU-surface retention ──────────────────────────────────
    //
    // A page keeps its GPU surfaces (image surfaces, Win2D swap chains, baked
    // backdrops) only while it is the active page of the active tab. Collapsed
    // cached pages keep their full XAML tree and state, but shed native image
    // resources via the NavCacheSurfaces walk and re-hydrate on return. Visual
    // trees are never touched, so Back stays a Visibility flip; covers can fade
    // back in after the page is already visible.

    /// <summary>
    /// Re-classifies this tab's cached pages and releases / restores their
    /// surface participants accordingly. Each page's tree-walk is pumped on a
    /// low-priority dispatcher tick so it never blocks a frame.
    /// </summary>
    public void ApplySurfaceRetention(bool isActiveTab)
    {
        if (IsSleeping)
            return;

        var pages = ContentHost.CachedPagesByRecency();
        var count = pages.Count;
        if (count == 0)
            return;

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        for (var i = 0; i < count; i++)
        {
            // Only the visible active page keeps native image/composition surfaces.
            var page = pages[i];
            var retained = isActiveTab && ReferenceEquals(page, ContentHost.ActivePage);
            void Apply()
            {
                if (retained)
                    NavCacheSurfaces.RestoreAll(page);
                else
                    NavCacheSurfaces.ReleaseAll(page);
            }

            if (dispatcher is not null)
                dispatcher.TryEnqueue(DispatcherQueuePriority.Low, Apply);
            else
                Apply();
        }
    }

    /// <summary>
    /// Releases the surfaces of every cached page in this tab — used when the
    /// tab is deactivated and the whole tab goes off-screen.
    /// </summary>
    public void ReleaseAllSurfaces()
    {
        if (IsSleeping)
            return;

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        foreach (var page in ContentHost.CachedPagesByRecency())
        {
            var captured = page;
            if (dispatcher is not null)
                dispatcher.TryEnqueue(DispatcherQueuePriority.Low,
                    () => NavCacheSurfaces.ReleaseAll(captured));
            else
                NavCacheSurfaces.ReleaseAll(captured);
        }
    }

    // Cross-tab cached-page ceiling. Per-tab CacheSize already adapts 3 to 2, but
    // 2 × N tabs is still unbounded as tabs accumulate. This caps the *total*
    // realised page count across all non-sleeping tabs, evicting the oldest
    // collapsed page from the least-recently-activated tab.
    private const int GlobalCachedPageLimit = 8;

    private static void EnforceGlobalCachedPageLimit()
    {
        var tabs = ShellViewModel.TabInstances;
        // Bounded loop — each pass evicts exactly one page or stops.
        for (var guard = 0; guard < 64; guard++)
        {
            var total = 0;
            TabBarItem? victim = null;
            var victimActivatedAt = DateTimeOffset.MaxValue;
            foreach (var tab in tabs)
            {
                if (tab.IsSleeping)
                    continue;
                total += tab.ContentHost.CachedPageCount;
                // Drop from the least-recently-activated tab that has a
                // collapsed page to spare (CachedPageCount > 1 keeps its
                // active page). The active tab has the newest
                // LastActivatedAtUtc, so this naturally favours background tabs.
                if (tab.ContentHost.CachedPageCount > 1
                    && tab.LastActivatedAtUtc < victimActivatedAt)
                {
                    victimActivatedAt = tab.LastActivatedAtUtc;
                    victim = tab;
                }
            }

            if (total <= GlobalCachedPageLimit || victim is null)
                break;
            if (!victim.ContentHost.EvictOldestCollapsed())
                break;
        }
    }

    /// <summary>
    /// Releases the GPU surfaces of every cached page across all tabs except
    /// each tab's currently-active page (the active tab's active page is
    /// on-screen; an inactive tab's active page is kept as a cheap safety
    /// margin). Drives the memory-budget service's pressure re-sweep.
    /// </summary>
    public static void ReleaseDormantSurfacesAllTabs()
    {
        foreach (var tab in ShellViewModel.TabInstances)
        {
            if (tab.IsSleeping)
                continue;
            var active = tab.ContentHost.ActivePage;
            foreach (var page in tab.ContentHost.CachedPagesByRecency())
            {
                if (!ReferenceEquals(page, active))
                    NavCacheSurfaces.ReleaseAll(page);
            }
        }
    }

    public bool Sleep()
    {
        if (IsSleeping || ContentHost.ActivePage is null)
            return false;

        object? activePageState = null;
        var activePageType = ContentHost.ActivePage.GetType();
        if (ContentHost.ActivePage is ITabSleepParticipant sleepParticipant)
            activePageState = sleepParticipant.CaptureSleepState();

        _sleepSnapshot = new TabSleepSnapshot(activePageType, activePageState);
        ClearLiveContent();
        IsSleeping = true;
        return true;
    }

    public bool Wake()
    {
        if (!IsSleeping)
            return false;

        ResetHostForWake();
        IsSleeping = false;

        var snapshot = _sleepSnapshot;
        _pendingSleepRestoreState = snapshot?.ActivePageState;
        _pendingSleepRestorePageType = _pendingSleepRestoreState != null ? snapshot?.ActivePageType : null;

        if (_navigationParameter?.InitialPageType != null)
        {
            _pendingNavId = WaveeNavigationEventSource.Log.NextNavId();
            _pendingPageName = _navigationParameter.InitialPageType.Name;
            WaveeNavigationEventSource.Log.Navigating(_pendingNavId, _pendingPageName, "WakeFallback");
            var wakeDiagNavId = NavigationDiagnostics.Instance?.BeginNav(
                _pendingPageName, "WakeFallback") ?? 0;
            _pendingDiagNavId = wakeDiagNavId;
            using (NavigationDiagnostics.Instance?.Stage(wakeDiagNavId, "pageHostNavigate"))
            {
                TryNavigatePageHost(
                    _navigationParameter.InitialPageType,
                    _navigationParameter.NavigationParameter);
            }
            if (wakeDiagNavId != 0)
                NavigationDiagnostics.Instance?.EndNav(wakeDiagNavId);
        }

        _sleepSnapshot = null;
        MarkActivated();
        return true;
    }

    private static string? GetParameterUri(object? parameter) => parameter switch
    {
        ContentNavigationParameter nav => nav.Uri,
        EpisodeNavigationParameter nav => nav.EpisodeUri,
        string s => s,
        _ => null
    };

    private void ContentHost_Navigating(object? sender, PageHostNavigatingEventArgs e)
    {
        // GC critical window opened once in Navigate() / NavigationParameter setter
        // / Wake fallback — covers the entire nav including this event. Don't open
        // another window here (it would stack, preventing the post-nav Gen2 drain
        // in NavigationGcCoordinator from ever firing under rapid back-and-forth).

        // Runs synchronously inside PageHost.Navigate, BEFORE the new page is
        // realised / made visible. The Trim call below invokes Hibernate +
        // Bindings.StopTracking on the outgoing page — large pages with many
        // bound properties spend real time here. Bracket so it shows up on the
        // [nav] line.
        using (NavigationDiagnostics.Instance?.Stage(_pendingDiagNavId, "pageHostNavigating"))
        {
            if (_skipNextNavigationCacheTrim)
                _skipNextNavigationCacheTrim = false;
            else
                TrimActiveContentForNavigationCache();
        }
    }

    private void ContentHost_Navigated(object? sender, PageHostNavigatedEventArgs e)
    {
        // GC critical window already covers this stage — see ContentHost_Navigating comment.

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

            using (NavigationDiagnostics.Instance?.Stage(navIdForDiag, "restoreFromNavCache"))
            {
                RestoreActiveContentFromNavigationCache();
            }

            // If a deferred trim was scheduled for the page the user is now
            // returning to, drop it — its bindings should stay live. Same
            // applies when a cached page is reused with a different parameter
            // (Album1 → Album2 reuses the same AlbumPage instance).
            if (_pendingTrim?.Participant is INavigationCacheMemoryParticipant pending
                && ReferenceEquals(pending, ContentHost.ActivePage))
            {
                CancelPendingTrim();
            }

            // Unsubscribe from previous page's ContentChanged to prevent leak
            if (_previousContent != null)
                _previousContent.ContentChanged -= TabItemContent_ContentChanged;

            _previousContent = TabItemContent;

            if (TabItemContent != null)
                TabItemContent.ContentChanged += TabItemContent_ContentChanged;

            if (_pendingSleepRestoreState != null
                && _pendingSleepRestorePageType != null
                && e.PageType == _pendingSleepRestorePageType
                && ContentHost.ActivePage is ITabSleepParticipant sleepParticipant)
            {
                sleepParticipant.RestoreSleepState(_pendingSleepRestoreState);
                _pendingSleepRestoreState = null;
                _pendingSleepRestorePageType = null;
            }

            // Cap BackStack to prevent unbounded growth
            while (ContentHost.BackStack.Count > MaxBackStackSize)
                ContentHost.BackStack.RemoveAt(0);
        }

        // Bound the cross-tab page count, then re-classify this tab's cached
        // pages so hidden pages shed GPU surfaces (low-priority pumps - never
        // blocks this frame).
        EnforceGlobalCachedPageLimit();
        ApplySurfaceRetention(isActiveTab: true);

        // EndNav is NOT called here. ContentHost_Navigated runs synchronously
        // inside the originating Navigate call; the entry-point method
        // (Navigate / NavigationParameter setter / Wake fallback) calls EndNav
        // after its own surrounding Stage scopes have closed.
    }

    private void TabItemContent_ContentChanged(object? sender, TabItemParameter e)
    {
        _navigationParameter = e;
        ContentChanged?.Invoke(this, e);
    }

    public void Dispose()
    {
        ContentHost.Navigating -= ContentHost_Navigating;
        ContentHost.Navigated -= ContentHost_Navigated;
        ContentHost.NavigationFailed -= ContentHost_NavigationFailed;
        DiscardSleepState();
        CancelPendingTrim();

        ClearLiveContent();
        ContentHost.CacheSize = 0;
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

    private void ResetHostForWake()
    {
        ContentHost.CacheSize = ComputeAdaptiveCacheSize(ShellViewModel.TabInstances.Count);
    }

    private void DiscardSleepState()
    {
        _sleepSnapshot = null;
        _pendingSleepRestoreState = null;
        _pendingSleepRestorePageType = null;
        IsSleeping = false;
    }

    private sealed record TabSleepSnapshot(
        Type? ActivePageType,
        object? ActivePageState);
}
