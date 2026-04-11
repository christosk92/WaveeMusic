# Wavee Performance Optimization — Full Audit

**Date:** 2026-04-11  
**Scope:** Memory, CPU/Async, UI Rendering  
**Methodology:** Static code analysis via parallel codebase exploration  
**Status:** Reference document — findings catalogued, implementation priority TBD

---

## Executive Summary

The codebase has solid infrastructure (LRU image cache, CachingProfilePresets, PageCache, MemoryReleaseHelper, tabbed IDisposable lifecycle) but carries a pattern gap: **ViewModels that subscribe to events in constructors are not uniformly cleaned up**, accumulating memory over extended sessions. On the CPU/async side, **fire-and-forget and timer proliferation during playback** create dispatcher queue pressure. On the rendering side, **a non-virtualizing layout on the home page** is the single largest user-visible bottleneck.

The findings below are organized by category and severity (HIGH / MEDIUM / LOW).

---

## 1. Memory Management

### 1.1 HomeViewModel Event Handler Leak — HIGH

**File:** `Wavee.UI.WinUI/ViewModels/HomeViewModel.cs:86`

**Root cause:** HomeViewModel subscribes to `_recentlyPlayedService.ItemsChanged` in its constructor but does not implement `IDisposable`. When the Home tab is closed, `TabBarItem.Dispose()` calls `IDisposable.Dispose()` on the page — but because neither HomePage nor HomeViewModel implement `IDisposable`, no unsubscription happens. Each Home tab ever opened in the session retains a live event handler reference, keeping the VM alive indefinitely.

**Fix:** Make `HomeViewModel : IDisposable`. Unsubscribe `_recentlyPlayedService.ItemsChanged -= OnRecentlyPlayedItemsChanged` in `Dispose()`. Make `HomePage` call `(ViewModel as IDisposable)?.Dispose()` on unload (following the existing AlbumPage pattern).

---

### 1.2 Inconsistent Page IDisposable Pattern — MEDIUM

**Files:** `AlbumPage.xaml.cs`, `ArtistPage.xaml.cs`, `LibraryPage.xaml.cs`, and others

**Root cause:** `AlbumPage` already calls `(ViewModel as IDisposable)?.Dispose()`, but other pages do not. ViewModels with event subscriptions or Rx chains on those pages leak subscriptions until app exit.

**Fix:** Audit all page `OnNavigatedFrom` / `Unloaded` handlers. Add `(ViewModel as IDisposable)?.Dispose()` to every page. Alternatively, establish a `BasePageView<TViewModel>` that does this automatically.

---

### 1.3 Lyrics Cache Upper Bound — MEDIUM

**File:** `Wavee.UI.WinUI/Data/Models/AppSettings.cs` (CachingProfilePresets)

**Root cause:** VeryAggressive profile allows 1,000 lyrics entries × ~60 KB average = up to 60 MB for the lyrics cache alone, held in-process. This is configured but not always warranted — heavy listeners accumulate lyrics over weeks and the cache has no session-based eviction.

**Fix:** Document the intended TTL and max session size. Consider lowering VeryAggressive lyrics max to 500 entries, or add a session-start prune of entries older than N days.

---

### 1.4 Image Cache — Well-Optimized (No Action Needed)

**File:** `Wavee.UI.WinUI/Services/ImageCacheService.cs`

LRU with 60-entry hard cap, bucketed decode sizes (64/128/256/512 px), `CleanupStale()` with TTL eviction. Recent reduction from 100→60. No action needed.

---

### 1.5 Static TabInstances Collection — LOW

**File:** `Wavee.UI.WinUI/ViewModels/ShellViewModel.cs:59`

Static `ObservableCollection<TabBarItem>` held for app lifetime. Items are properly disposed on removal. Risk is low provided no external code holds references to disposed items. No action unless profiling confirms retention.

---

## 2. CPU and Async Patterns

### 2.1 Fire-and-Forget Playback Commands — HIGH

**File:** `Wavee.UI.WinUI/Data/Contexts/PlaybackStateService.cs:649, 658, 660, 677-678, 713-714, 721-726, 728, 730-734, 738-739`

**Root cause:** Playback commands (TogglePlayPause, SkipNext, SeekTo, etc.) use `_ = someAsync().ContinueWith(t => ...)` pattern. This:
- Creates unobserved task exceptions that bypass error UI
- Prevents CancellationToken propagation
- Context-switches from thread pool back to UI thread for error recovery, wasting scheduler overhead

**Fix:** Convert to proper `async Task` command handlers with `try/catch`. Pass CancellationTokens. Surface errors via the existing error observable rather than inline `.ContinueWith`.

---

### 2.2 Missing ConfigureAwait(false) in LyricsService — HIGH

**File:** `Wavee.UI.WinUI/Services/LyricsService.cs:358`

**Root cause:** `Task.Run(async () => { await _db.SetLyricsCacheAsync(...); })` without `ConfigureAwait(false)` on the inner await unnecessarily marshals the continuation back to the UI synchronization context (if one is captured). Called on every track play event.

**Fix:** Add `.ConfigureAwait(false)` to all `await` calls inside `Task.Run` lambdas in LyricsService.

---

### 2.3 Async Void Event Handlers — HIGH

**Files:** `BaselineHomeCard.xaml.cs`, `ContentCard.xaml.cs`, `ShortsPill.xaml.cs`, `RightPanelView.xaml.cs`

**Root cause:** `async void TrackPlayButton_Click(...)` handlers silently swallow exceptions. Combined with `Task.Run()` inside, they double-context-switch (UI → ThreadPool → UI). Failures are invisible to users and crash the event handler silently.

**Fix:** Wrap `async void` handlers in a `SafeFireAndForget` helper or use a relay command pattern (`AsyncRelayCommand`) that surfaces exceptions. The core logic should remain `async Task`.

---

### 2.4 Timer Proliferation in RightPanelView — MEDIUM-HIGH

**File:** `Wavee.UI.WinUI/Controls/RightPanel/RightPanelView.xaml.cs:264, 562-565, 1598-1606`

**Root cause:** Three separate `DispatcherQueueTimer` instances active during lyrics playback:
- `_positionTimer` at 50ms (lyrics sync)
- `_scrollResetTimer` at 3,000ms (on-demand)
- `_detailsLyricsTimer` at 250ms (snippet updates)

Plus `CompositionTarget.Rendering` (~16.67ms per frame). During playback this generates 4+ dispatcher events per frame.

**Fix:** Consolidate `_positionTimer` and `_detailsLyricsTimer` into a single timer. Throttle updates: sync lyrics position at 50ms but only update details snippet every 5th tick (250ms effectively). Use a flag to suppress redundant updates when lyrics haven't changed line.

---

### 2.5 Unthrottled CompositionTarget.Rendering in Visualizer — MEDIUM

**File:** `Wavee.UI.WinUI/Controls/Cards/PreviewAudioVisualizer.xaml.cs:312, 321`

**Root cause:** `CompositionTarget.Rendering` fires every frame (~60 Hz). Handler decays 24 bars, updates envelope arrays, calls `Invalidate()` — for every frame regardless of card visibility. Off-screen preview cards that haven't been GC'd still process full spectrum logic.

**Fix:** Check `IsLoaded && Visibility == Visible` at the top of the rendering handler and early-return if invisible. Unsubscribe from `CompositionTarget.Rendering` in `Unloaded`, resubscribe in `Loaded`.

---

### 2.6 LINQ Allocations in Queue Sync — MEDIUM

**File:** `Wavee.UI.WinUI/Data/Contexts/PlaybackStateService.cs:80, 568-571`

**Root cause:**
```csharp
// Line 80 — every property access
public IReadOnlyList<QueueItem> UserQueue => _queue.Where(q => q.IsUserQueued).ToList();

// Lines 568-571 — every queue state change
var sparseUris = _queue.Where(q => !q.HasMetadata && ...).Select(q => q.TrackId).ToList();
```
Two `List<T>` allocations + enumeration on every queue sync.

**Fix:** For the property, cache `UserQueue` and invalidate on queue mutation. For the sparse query, use `List<T>` with `AddRange` and clear/reuse, or LINQ2Objects with `ArrayPool`-backed results for hot paths.

---

### 2.7 Float Array Allocation Per Audio Frame — MEDIUM-LOW

**File:** `Wavee.UI.WinUI/Services/PreviewAudioGraphService.cs:421`

**Root cause:** `var payload = amplitudes.ToArray()` creates a new `float[24]` on every audio quantum (~50-100/sec during preview playback). Each allocation is tiny but cumulative GC pressure.

**Fix:** Use a pre-allocated double-buffer (`float[2][24]`) and alternate. Pass the buffer index rather than a copy. Or use `ArrayPool<float>.Shared` and return after frame consumption.

---

### 2.8 Unnecessary UI Thread Marshaling in OnRemoteStateChanged — LOW

**File:** `Wavee.UI.WinUI/Data/Contexts/PlaybackStateService.cs:148-329`

**Root cause:** Entire `OnRemoteStateChanged` handler is wrapped in `_dispatcherQueue.TryEnqueue(...)`. Since the callback already arrives on the dispatcher thread in most cases, this is defensive double-marshaling.

**Fix:** Check `_dispatcherQueue.HasThreadAccess` before enqueuing. Only marshal when actually needed.

---

## 3. UI Rendering

### 3.1 SafeUniformGridLayout Uses NonVirtualizingLayout — HIGH

**File:** `Wavee.UI.WinUI/Controls/Layouts/SafeUniformGridLayout.cs` (and `Layouts/` usages)

**Root cause:** Home page Baseline sections use `SafeUniformGridLayout : NonVirtualizingLayout`. This forces WinUI to instantiate, measure, and arrange **all** card templates upfront — even those far below the fold. With ~5-6 sections × ~4-6 cards each and each `BaselineHomeCard` containing 25+ visual elements (radial gradient, linear gradient, canvas, visualizer, lottie, progress controls), this is the single largest startup bottleneck: estimated 500ms–2s of extra layout work on home page load.

**Fix:** Replace `NonVirtualizingLayout` base with `VirtualizingLayout` (following the pattern in `SingleRowLayout.cs`). Implement `MeasureOverride(VirtualizingLayoutContext, Size)` to only realize visible items. This is a medium-effort change but the highest-ROI optimization in the codebase.

---

### 3.2 MediaPlayerElement Reparenting on Card Hover — MEDIUM-HIGH

**File:** `Wavee.UI.WinUI/Services/SharedCardCanvasPreviewService.cs:145-147`

**Root cause:** One shared `MediaPlayerElement` is inserted into and removed from different Panel hosts as users hover over cards. `MediaPlayerElement` owns an internal `SwapChainPanel` for composition rendering — reparenting it causes the composition surface to be recreated each time, causing frame drops and memory spikes during rapid hover transitions.

**Fix:** Anchor `MediaPlayerElement` to a fixed overlay `Canvas` at the root of the window (Z-order above cards). On hover, translate/resize the overlay to match the card's screen position using `GetElementVisual().Offset` or `TransformToVisual`. Card hover shows the overlay; unhover hides it. Never reparent.

---

### 3.3 Dependent Animation on ProgressBar in BaselineHomeCard — MEDIUM

**File:** `Wavee.UI.WinUI/Controls/Cards/BaselineHomeCard.xaml.cs:948-982`

**Root cause:** `StartPreviewPendingProgressBarAnimation()` uses a `Storyboard` with `EnableDependentAnimation = true`, animating `ProgressBar.Value`. Dependent animations run on the UI thread every frame. With 10+ visible cards potentially in preview-pending state simultaneously, this adds significant per-frame UI thread cost.

**Fix:** Replace with a Composition animation via `AnimationBuilder` targeting the `ScaleX` of a Rectangle overlay that visually mimics progress. Composition animations run off the UI thread entirely.

---

### 3.4 60 Hardcoded Shimmer Elements in HomePage.xaml — MEDIUM

**File:** `Wavee.UI.WinUI/Views/HomePage.xaml:281-504`

**Root cause:** 60 `<controls:Shimmer>` elements are hardcoded in XAML for the loading skeleton. Even though the container starts hidden, all 60 are parsed and instantiated at page load. Each Shimmer runs an animated gradient brush. Estimated 300-500ms additional initialization cost.

**Fix:** Use `x:Load="False"` on the shimmer container so it's not instantiated until data load is actually requested. Or generate the shimmer grid programmatically in `OnNavigatedTo` only when needed, and remove it after data arrives.

---

### 3.5 Staggered ItemContainerTransitions on Track Lists — MEDIUM

**File:** `Wavee.UI.WinUI/Controls/TrackList/TrackListView.xaml:34-39`

**Root cause:**
```xml
<EntranceThemeTransition IsStaggeringEnabled="True"/>
```
Staggering delays each item by ~50ms. On pages with 50+ tracks (album page, playlist page), this creates 2.5+ seconds of cascading entrance animations that block perceived content load.

**Fix:** Set `IsStaggeringEnabled="False"`. If entrance animations are desired, use a single fade-in on the entire list container via a Composition animation rather than per-item staggering.

---

### 3.6 ColumnsFirstGridLayout Measures All Children — MEDIUM

**File:** `Wavee.UI.WinUI/Controls/Layouts/ColumnsFirstGridLayout.cs:122-123`

**Root cause:**
```csharp
// Measures ALL children even beyond visible grid rows
for (int i = 0; i < count; i++)
    children[i].Measure(measureSize);
```
Items beyond `MaxRows × columns` are measured but arranged off-screen. Comment justifies this for "smooth pagination transitions" — but measure cost for large collections is O(n) and layout is called frequently.

**Fix:** Only measure items with index `< MaxRows * _columns`. If pagination smoothness is visually affected, use a fade/slide transition on the container rather than pre-measuring all children.

---

### 3.7 GC Compaction Timing Fragility — LOW

**File:** `Wavee.UI.WinUI/App.xaml.cs:117-139`

**Root cause:** 5-second delayed blocking GC compaction (`GCLargeObjectHeapCompactionMode.CompactOnce` + `GC.Collect(MaxGeneration, blocking: true)`) runs as fire-and-forget. On slow machines the user may be actively interacting when the pause hits.

**Fix:** Add a check: only run if the window has been in foreground for >5s AND no audio is playing AND `Environment.TickCount64 - _lastUserInteractionMs > 2000`. Alternatively, switch to non-blocking (`blocking: false`) and accept non-deterministic compaction timing.

---

## 4. Priority Matrix

| # | Finding | Category | Severity | Effort |
|---|---------|----------|----------|--------|
| 1 | SafeUniformGridLayout NonVirtualizing | UI Rendering | HIGH | Medium |
| 2 | HomeViewModel event handler leak | Memory | HIGH | Low |
| 3 | Fire-and-forget playback commands | CPU/Async | HIGH | Medium |
| 4 | Async void event handlers | CPU/Async | HIGH | Low |
| 5 | Missing ConfigureAwait(false) in LyricsService | CPU/Async | HIGH | Low |
| 6 | Timer proliferation in RightPanelView | CPU/Async | MEDIUM-HIGH | Low |
| 7 | MediaPlayerElement reparenting | UI Rendering | MEDIUM-HIGH | High |
| 8 | Inconsistent Page IDisposable pattern | Memory | MEDIUM | Low |
| 9 | Dependent animation in BaselineHomeCard | UI Rendering | MEDIUM | Low |
| 10 | 60 hardcoded shimmer elements | UI Rendering | MEDIUM | Low |
| 11 | Staggered ItemContainerTransitions | UI Rendering | MEDIUM | Low |
| 12 | LINQ allocations in queue sync | CPU/Async | MEDIUM | Low |
| 13 | ColumnsFirstGridLayout over-measurement | UI Rendering | MEDIUM | Low |
| 14 | Unthrottled CompositionTarget.Rendering | CPU/Async | MEDIUM | Low |
| 15 | Float array allocation per audio frame | CPU/Async | MEDIUM-LOW | Low |
| 16 | Unnecessary UI thread marshaling | CPU/Async | LOW | Low |
| 17 | Lyrics cache upper bound | Memory | MEDIUM | Low |
| 18 | GC compaction timing fragility | UI Rendering | LOW | Low |
| 19 | Static TabInstances collection | Memory | LOW | None |

---

## 5. Quick Wins (Low Effort, High Impact)

These can be done independently, in any order, with minimal risk:

1. **`HomeViewModel : IDisposable`** — unsubscribe `ItemsChanged`, call dispose from `HomePage`
2. **`ConfigureAwait(false)`** in `LyricsService.cs` Task.Run lambdas
3. **`IsStaggeringEnabled="False"`** in TrackListView.xaml
4. **`x:Load="False"` on shimmer container** in HomePage.xaml
5. **Early return in visualizer** when `!IsLoaded || Visibility != Visible`
6. **Consolidate RightPanelView timers** to a single 50ms timer with a counter
7. **`EnableDependentAnimation = false`** on ProgressBar storyboard; use Composition scale instead
8. **Audit all pages for `(ViewModel as IDisposable)?.Dispose()`** — apply AlbumPage pattern consistently

---

## 6. Architectural Changes (Higher Effort)

These require more careful design and testing:

1. **VirtualizingLayout for SafeUniformGridLayout** — highest user-visible impact, requires layout algorithm rewrite
2. **MediaPlayerElement overlay anchoring** — replace reparenting with fixed overlay + transform; requires SharedCardCanvasPreviewService refactor
3. **AsyncRelayCommand for playback actions** — replace fire-and-forget with proper command pattern throughout PlaybackStateService
4. **ArrayPool for audio frame buffers** — profile first to confirm GC pressure warrants it

---

## 7. What's Already Working Well

- `ImageCacheService` — LRU with hard cap, bucketed decode sizes, TTL eviction
- `PageCache<T>` — proper PeriodicTimer + CancellationTokenSource lifecycle
- `TabBarItem.Dispose()` — calls IDisposable on pages, clears frame/back stack, invokes MemoryReleaseHelper
- `AppLifecycleHelper._appSubscriptions` — disposes all app-level Rx subscriptions on shutdown
- `PreviewAudioGraphService.StopCurrentSession_NoLock()` — thorough AudioGraph cleanup
- `CardPreviewPlaybackCoordinator.Dispose()` — proper disposed flag, event unsubscription, CTS cancellation
- `TrackLikeService` — uses `.DisposeWith(_disposables)` pattern cleanly
- `HomeFeedCache` — incremental diff (ApplyDiff) rather than full rebuild
- `CachingProfilePresets` — well-documented per-profile estimates with memory reasoning
