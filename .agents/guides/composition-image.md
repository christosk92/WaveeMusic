---
guide: composition-image
scope: WaveeMusic's composition-backed image control — GPU-resident cache, LoadedImageSurface lifecycle, and every place CompositionImage / CrossFadeImage are bound across the UI.
last_verified: 2026-05-26
verified_by: read+grep over src/Wavee.UI.WinUI/Controls/Imaging and src/Wavee.UI.WinUI/Services while making off-screen CompositionImage surfaces disposable
root_index: AGENTS.md (Codex) and CLAUDE.md (Claude Code)
---

# Wavee CompositionImage Inventory

`CompositionImage` is the primary card/row image primitive used across the
app. It hosts a `SpriteVisual` whose brush is the GPU-resident
`LoadedImageSurface` owned by `ImageCacheService`; decoded CPU pixels are
released after the GPU upload. Some WinUI-native `BitmapImage` /
`PersonPicture` surfaces still exist outside this guide and need their own
unload clearing.

Use this guide when changing image loading, image caching, the placeholder /
fade-in animation, or anything that touches the `OnLoaded` / `OnUnloaded`
contract. Use it as a directory when you need to find every place an image
URL flows into the visual tree.

## How To Use This Guide

1. Skim the **Quick-find table** for the surface you're touching.
2. Read **Core contracts** before changing the control itself — most
   gotchas live there.
3. The **Lifecycle invariants** section documents what *must* hold across
   `OnLoaded` / `OnUnloaded` for virtualized rows to render correctly. If
   you change `OnLoaded` or `OnUnloaded`, re-read it.

Re-verification commands:

```
rg -n "imaging:CompositionImage" src/Wavee.UI.WinUI -g "*.xaml"
rg -n "CompositionImage" src/Wavee.UI.WinUI -g "*.cs"
rg -n "ImageCacheService|CachedImage|LoadedImageSurface" src/Wavee.UI.WinUI
rg -n "TrackImageRetryBehavior|CardImageRetryBehavior|ImageLoadingSuspension" src/Wavee.UI.WinUI
```

Scope:
- Included: `CompositionImage`, `CrossFadeImage`, `ImageCacheService`,
  `CachedImage`, `ImageLoadingSuspension`, `SpotifyImageHelper`,
  `TrackImageRetryBehavior`, `CardImageRetryBehavior`, every XAML +
  code-behind consumer that pushes a URL into a `CompositionImage`.
- Not included: the visual chrome of cards (`ContentCard.xaml`) — see
  `.agents/guides/content-card.md`. Row-level binding flow
  (LazyTrackItem, BindCompactData) — see `track-and-episode-ui.md`.

## Quick-find Table

| Surface | Host file:line | Source binding | Notes |
| --- | --- | --- | --- |
| Track row compact album art | `Controls/Track/TrackItem.xaml:100` (`CompactAlbumArt`) | `track.ImageSmallUrl ?? track.ImageUrl` via `ApplyCompactAlbumArt` | `DecodePixelSize=64`. `CornerRadius=4`. x:Load-deferred behind `IsCompactMode`. `TrackImageRetryBehavior` attached imperatively from code-behind. **Only consumer of compact mode**. |
| Track row detailed album art | `Controls/Track/TrackItem.xaml:373` (`RowAlbumArt`) | `track.ImageSmallUrl ?? track.ImageUrl` via `ApplyRowAlbumArt` | `DecodePixelSize=64`. `CornerRadius=4`. x:Load-deferred behind `IsRowMode` (default true). `TrackImageRetryBehavior` attached. |
| ContentCard square image | `Controls/Cards/ContentCard.xaml:73` (`SquareImage`) | `ContentCard.ImageUrl` DP | `Stretch=UniformToFill`, `Opacity=0` (fade-in on `ImageOpened`). `CardImageRetryBehavior.Enable=True`. `DecodePixelSize` chosen by `ContentCard.LoadImage` from `CardImageDecodeSize=200`. |
| ContentCard circle image | `Controls/Cards/ContentCard.xaml:178` (`CircleImage`) | `ContentCard.ImageUrl` DP, `IsCircle=True` | x:Load-deferred behind `CircleImageContainer`. `Opacity=0.85`. `CardImageRetryBehavior` not attached on this layer (square is the retry target). |
| Album hero artwork | `Views/AlbumPage.xaml:159` | `ViewModel.AlbumImageUrl` | `DecodePixelSize=280`. `CornerRadius=0`. |
| Album hero (banner cover, large) | `Views/AlbumPage.xaml:847` | `ViewModel.AlbumImageUrl` | Inside hero layout. |
| EpisodeCard cover | `Controls/Cards/EpisodeCard.xaml:54` (`CoverImage`) | `HomeSectionItem` / episode DTO | `DecodePixelSize=200`. |
| SpotlightReleaseCard cover | `Controls/Cards/SpotlightReleaseCard.xaml:78` (`CoverImage`) | Release DTO | `DecodePixelSize=144`. |
| PopularReleaseRow cover | `Controls/Cards/PopularReleaseRow.xaml:42` (`CoverImage`) | `ArtistReleaseVm` | `DecodePixelSize=112`. Bound from `ArtistPage.xaml.cs:PopularReleasesRepeater_ElementPrepared`. |
| EditorialHeroCard hero | `Controls/Cards/EditorialHeroCard.xaml:103` (`HeroImage`) | Editorial DTO | `DecodePixelSize=256`. |
| BaselineHomeCard hero | `Controls/Cards/BaselineHomeCard.xaml:80` (`HeroImage`) | `HomeSectionItem` | Inside `HeroMotionHost` for parallax / hover motion. |
| BaselineHomeCard cover thumb | `Controls/Cards/BaselineHomeCard.xaml:171` (`CoverThumbImage`) | `HomeSectionItem` | Small inline cover overlay. |
| ShortsPill thumbnail | `Controls/Cards/ShortsPill.xaml:46` | Short-form DTO | `Width=56 Height=54`. `DecodePixelSize=64`. |
| ArtistCircleCard image | `Controls/Cards/ArtistCircleCard.xaml:20` (`CardImage`) | Artist DTO | `DecodePixelSize=100`. Wrapped in circle clip by the card. |
| MediaCard image | `Controls/Cards/MediaCard.xaml:22` (`CardImage`) | Generic media DTO | `DecodePixelSize=200`. |
| AddToPlaylistBar source image | `Controls/AddToPlaylist/AddToPlaylistBar.xaml:48` | `ImageUrl` DP | `DecodePixelSize=72`. `CornerRadius=4`. |
| AddToPlaylistBar target image | `Controls/AddToPlaylist/AddToPlaylistBar.xaml:139` (`TargetTileImage`) | `TargetImageUrl` DP | `DecodePixelSize=68`. |
| CrossFadeImage layer A | `Controls/Imaging/CrossFadeImage.xaml:21` (`LayerA`) | Parent `CrossFadeImage.ImageUrl` | Opacity-animated pair (sibling layer fades out as this fades in). |
| CrossFadeImage layer B | `Controls/Imaging/CrossFadeImage.xaml:26` (`LayerB`) | Parent `CrossFadeImage.ImageUrl` | See sibling. |

Note: every shelf usage of `ContentCard` (Home, Search, Browse, Library,
Artist, Album, Show, Concert, Profile, Local-media) ultimately drives a
`CompositionImage` through `ContentCard.SquareImage` or `CircleImage`.
The full shelf inventory lives in `content-card.md`.

## Core Contracts

### `Controls/Imaging/CompositionImage.xaml(.cs)`

Dependency properties:

| Property | Default | Notes |
| --- | --- | --- |
| `ImageUrl` | `null` | Raw URL — `https://`, `spotify:image:<hash>`, `spotify:mosaic:...` (resolves to null — single-image only), or `wavee-artwork://<hash>`. `SpotifyImageHelper.ToHttpsUrl` does the resolution. |
| `DecodePixelSize` | `0` (native) | Snapped to a bucket: 64 / 128 / 256 / 512 by `ImageCacheService.SnapToBucket`. Distinct cache key per bucket — same URL at different sizes lives in separate entries. |
| `Stretch` | `UniformToFill` | Mapped to `CompositionStretch` and applied to the `_surfaceBrush`. |
| `IsCircle` | `false` | Switches between `CompositionRoundedRectangleGeometry` clip and `CompositionEllipseGeometry`. Both are sized by `ExpressionAnimation` bound to the host visual's `Size` — they auto-track layout. |
| `PlaceholderBrush` | `null` | Painted behind the surface. **Almost every consumer leaves this null** — the parent card / row supplies its own colored placeholder Border + glyph below the `CompositionImage`. |
| `PlaceholderOpacity` | `1.0` | Initial opacity of the internal `PlaceholderHost` Border. Reset on every failed load and after `OnUnloaded`. |
| `FadeInDurationMs` | `220` | Composition animation duration when the placeholder fades out after a successful load (`FadeOutPlaceholder`). |
| `IsImageLoaded` | `false` (read-only out) | One-way out — true once `LoadedImageSurface.LoadCompleted` reports success. Useful for `x:Bind` triggers. |
| `CornerRadius` | inherited | Driven by `OnClipShapeChanged` → `UpdateClip`. |

Events:

- `ImageOpened` — surface load succeeded. Consumers (e.g. `ContentCard`)
  fade in the image and hide their placeholder glyph.
- `ImageFailed` — surface load failed OR the URL was invalidated. Wired by
  `TrackImageRetryBehavior` / `CardImageRetryBehavior` for the single-retry
  recovery path.

Internal structure (`CompositionImage.xaml`):

```
RootGrid
├── PlaceholderHost (Border, optional PlaceholderBrush, opacity-faded on load)
└── SurfaceHost   (Grid — visual parent of the SpriteVisual via
                   ElementCompositionPreview.SetElementChildVisual)
```

The `SpriteVisual` uses `RelativeSizeAdjustment = Vector2.One` so it
auto-tracks `SurfaceHost.Size` from the layout pass — required because
ItemsRepeater virtualization realizes the element AFTER its parent's
layout, so `ActualWidth`/`ActualHeight` can be `0` on `Loaded`.

### Composition resource lifecycle

`EnsureCompositionResources()` is one-shot (`_initialized` guard) and runs
inside `OnLoaded`. It builds `_spriteVisual`, `_surfaceBrush`,
`_roundedRectGeometry`/`_roundedRectClip`,
`_ellipseGeometry`/`_ellipseClip`, and sets up the expression animations
that tie clip + sprite size to the host visual. Subsequent `OnLoaded`
calls (recycle / nav-cache restore) are no-ops for resource creation.

`ReleaseSurfaceReference(...)` is the surface teardown: clear the brush
surface, detach the visual from XAML, reset the placeholder, unsubscribe
from `LoadCompleted`, and release the cache pin. `ReleaseCompositionResources()`
is the full composition-object teardown layered on top of that, disposing
the brush/visual/clip objects after the surface reference has already been
released. Currently only navigation-cache trim and explicit memory-pressure
paths need that full object teardown.

## Lifecycle Invariants

This is the contract you must keep when changing `OnLoaded` / `OnUnloaded`
or any of the helpers in `CompositionImage`. Violating it breaks
virtualized rows that get recycled while their image load is in flight.

### Invariant 1 — `OnUnloaded` is a real off-screen release

`OnUnloaded` now treats the control as out of view: it clears
`_surfaceBrush.Surface`, detaches the sprite visual, unsubscribes any
`LoadCompleted` handler, drops `_currentCachedImage`, resets
`_resolvedUrl`, and unpins the cache entry with `evictIfUnpinned: true`.
If no other visible control still pins that `(url, decodeBucket)` entry,
`ImageCacheService` disposes the `LoadedImageSurface` immediately.

This intentionally means a re-realized row/page shows the placeholder first
and reloads through the normal image path. The OS HTTP/disk cache can still
serve the bytes quickly, but Wavee does not keep decoded GPU surfaces alive
for detached controls.

The older mid-load recycle workaround kept `_currentCachedImage` and the
handler alive across unload. Do not reintroduce that retention path unless
you also prove it cannot keep off-screen `LoadedImageSurface` instances
alive after LRU eviction.

### Invariant 2 — `OnLoaded` re-attaches the visual eagerly

Because `OnUnloaded` detaches the sprite (`DetachVisualFromHost`),
`OnLoaded` must call `AttachVisualToHost()` BEFORE the same-URL bail-out
in `TryLoadCurrent`. The same-URL path returns without attaching, so
without an eager attach a retained same-URL surface could stay detached.
Most unloads now reset `_resolvedUrl`, but the eager attach remains the
defensive rule for non-unload re-entry paths.

`OnLoaded` also re-applies the brush surface for non-unload re-entry paths
where a retained cache reference is already loaded but the brush is empty:

```csharp
if (_currentCachedImage is { IsLoaded: true, Surface: not null } cached
    && _surfaceBrush is not null
    && _surfaceBrush.Surface is null)
{
    _surfaceBrush.Surface = cached.Surface;
    IsImageLoaded = true;
    FadeOutPlaceholder();
}
```

The `[CompImg:NNNN] OnLoaded:reAssignSurfaceFromInFlightLoad` diagnostic
fires when this defensive path is exercised.

### Invariant 3 — Two layers of dedup, both required

Consumers (TrackItem, ContentCard) maintain their own
`_boundCompactImageUrl` / `_currentImageCacheUrl` latch so that re-binds
on the same URL skip pushing a fresh DP write. `CompositionImage`
maintains `_resolvedUrl` + `_pinnedDecode` so a same-URL `TryLoadCurrent`
short-circuits before re-pinning.

Both dedups are correct, but they interact: the consumer dedup must be
cleared whenever the inner state can drift — concretely, when a TrackItem
container is re-attached after an unload in Compact mode the
`_boundCompactImageUrl` is cleared in `TrackItem.OnLoaded`, forcing a
fresh push down. See `track-and-episode-ui.md` and the comment in
`TrackItem.OnLoaded` for the recycle-mid-load reasoning.

### Invariant 4 — `_isAttached` gates `TryLoadCurrent`

`TryLoadCurrent` bails when `!_isAttached`. This protects the cold-load
path from kicking before composition resources are built. The URL DP
setter (`OnImageUrlChanged`) fires regardless of attach state — the bail
is correct; the retry happens from `OnLoaded` once attached.

Setting `ImageUrl` on a detached `CompositionImage` is the normal flow
for rows realized via `FindName` on an x:Load-deferred parent. The DP
holds the URL until `OnLoaded` fires, at which point `TryLoadCurrent`
reads `ImageUrl` and proceeds.

## ImageCacheService + CachedImage

`Services/ImageCacheService.cs`

- LRU keyed by `(uri, decodeBucket)`. Capacity 200.
- Decoded pixels live in `LoadedImageSurface` (GPU). No CPU bitmap retained.
- `GetOrCreate(uri, decode, pin: true/false)` — atomic pin BEFORE trim, so
  a freshly-added entry can't self-evict when the cache is full of pinned
  rows.
- `TryGet(uri, decode)` — peek without creating. `CompositionImage` uses
  this for the fast-path peek hit in `TryLoadCurrent`.
- `Pin` / `Unpin` — reference-counted; multiple visible cards on the same
  URL stack pins.
- `Invalidate(uri, decode)` — drops a failed entry so the next GetOrCreate
  starts a fresh load. Called from `OnCachedLoaded(success: false)`.
- `ClearUnpinned` — soft cleanup for memory pressure (Tier-1).
- `Clear` — hard reset.

`Services/CachedImage.cs`

- Holds the `LoadedImageSurface`, the source URL, the decode bucket.
- Subscribes to `Surface.LoadCompleted` once at construction; flips
  `IsLoaded = true` or `LoadFailed = true` on completion.
- Re-raises its own `LoadCompleted` event so multiple subscribers can
  share a single underlying surface load.
- `AddLoadCompletedHandler(handler)` — adds the handler AND fires
  synchronously if the surface is already loaded. Use this from
  `CompositionImage` so a peek-miss-then-subscribe race doesn't leave us
  hanging on placeholder.
- `Dispose` is called by the cache on eviction. Disposes the
  `LoadedImageSurface`.

## CrossFadeImage

`Controls/Imaging/CrossFadeImage.xaml(.cs)` is a two-layer
`CompositionImage` wrapper that cross-fades between the previous and new
URL when `Source` or `FallbackSource` changes. Used by the sidebar /
expanded now-playing layout for the large album art so URL changes don't
flash through the placeholder. Each layer is a regular `CompositionImage`
— see the Quick-find rows for `LayerA` / `LayerB`. Animation is driven by
opacity. Both layers share the cache.

## Helpers and Behaviors

### `Wavee.UI/Helpers/SpotifyImageHelper.cs`

- `ToHttpsUrl(spotifyUri)` — the canonical URL resolver. Returns:
  - `https://...` URLs unchanged.
  - `spotify:image:<hash>` → `https://i.scdn.co/image/<hash>`.
  - `spotify:mosaic:...` → `null` (single-image surfaces can't render
    mosaics; the 2×2 path lives in `TryParseMosaicTileUrls` +
    `PlaylistMosaicService`).
  - `wavee-artwork://<hash>` → `file:///<LocalArtworkRoot>/<hh>/<hash>.<ext>`.
  - Anything else → `null`.
- `BucketSources` / `PickByDecodeSize` — when a payload reports multiple
  resolutions per image, this picks the right one for the slot's decode
  bucket.

### `Wavee.UI.WinUI/Services/ImageLoadingSuspension.cs`

A process-wide gate. `IsSuspended = true` makes every `CompositionImage`
that's about to start a cold load bail out (the `TryLoad:bail:suspended`
diagnostic). Currently flipped by `HomePage.BeginScrollRestore` so that
ScrollViewer.ScrollTo + layout pass during scroll-state restoration
doesn't kick off a wave of network fetches before the right cards have
settled in the viewport. A 3 s watchdog (`WatchdogClearSuspensionAsync`)
guarantees the gate releases even if the restore path errors out.

`ImageLoadingSuspension.Changed` is subscribed by:
- `CompositionImage.OnSuspensionChanged` — retries `TryLoadCurrent` when
  the gate releases.
- `ContentCard.OnImageLoadingSuspensionChanged` — calls
  `ReloadImageIfNeeded(ignoreViewportGate: true)` on the dispatcher.

### `Wavee.UI.WinUI/Behaviors/Track/TrackImageRetryBehavior.cs`

- Attached on `CompactAlbumArt` / `RowAlbumArt` imperatively from
  `TrackItem`'s `EnsureCompactAlbumArtRealized` /
  `EnsureRowAlbumArtRealized`.
- Subscribes to `ImageFailed` and invokes the owner's retry callback
  ONCE per URL. Records the failed URL; subsequent fails for the same URL
  are no-ops (no infinite retry loops).
- Reset by `Reset(image)` whenever the bound URL changes — gives the new
  URL its own retry budget.
- Per-element state lives in a `ConditionalWeakTable<CompositionImage, State>`
  so it collects with the host control.

### `Wavee.UI.WinUI/Behaviors/Card/CardImageRetryBehavior.cs`

Same shape as `TrackImageRetryBehavior` but attached declaratively in
`ContentCard.xaml` (`cardb:CardImageRetryBehavior.Enable=True`). Wires
`ImageFailed` to the registered card retry callback. Gates retries on
`!ImageLoadingSuspension.IsSuspended`.

## `ReleaseForNavigationCache` / `RestoreAfterNavigationCache`

When a tab is trimmed for the navigation cache (see
`Controls/TabBar/TabBarItem.cs` and the
`perf(nav-cache): release image surfaces + backdrop blur graphs on trim`
commit), `CompositionImage.ReleaseSurfacesForNavigationCache(root)` walks
the cached page tree and calls `ReleaseForNavigationCache` on every image:

- Calls `ReleaseSurfaceReference(resetResolvedUrl: true, evictIfUnpinned: true)`
  so the brush drops its surface, the pin is released, and the cache entry
  is immediately disposed if no visible control still owns it.
- Calls `ReleaseCompositionResources()` to dispose the visual/brush/clip
  graph for the dormant cached page.
- Sets `_releasedForNavigationCache = true`.

On `Wake`, `RestoreSurfacesAfterNavigationCache(activePage)` walks the
tree and calls `RestoreAfterNavigationCache` which clears the released
flag and runs `TryLoadCurrent`. Composition resources are rebuilt on the
next `OnLoaded`; the normal placeholder-first load path runs, with the
fast-path peek still available if another visible image kept the cache
entry alive.

This remains the full composition-object teardown path. `OnUnloaded` is a
lighter off-screen release: it drops the surface, unpins, unsubscribes, and
detaches the visual, but keeps the reusable composition objects around for
the next realization.

## Change Guidance

When changing the image control:

- **Cache / pin semantics** → `ImageCacheService.cs` (LRU + pin counts +
  capacity). Tests live in `test/Wavee.UI.Tests` (search for
  `ImageCacheService` tests).
- **Per-control rendering** (clip shape, stretch, placeholder fade) →
  `CompositionImage.xaml.cs`. Update `MapStretch`, `UpdateClip`, or
  `FadeOutPlaceholder` accordingly.
- **Load lifecycle / attach / detach** → re-read **Lifecycle Invariants**
  first. Any change to `OnLoaded`, `OnUnloaded`, `TryLoadCurrent`, or
  `ReleasePin` should preserve the four invariants.
- **URL resolution** (new Spotify URI scheme, new local CDN format) →
  `Wavee.UI/Helpers/SpotifyImageHelper.cs`. Update `ToHttpsUrl` and add a
  test in `Wavee.UI.Tests`.
- **Retry policy** → `TrackImageRetryBehavior.cs` or
  `CardImageRetryBehavior.cs`. The "one retry per URL" rule lives there.
- **Add a new consumer** → see if you can use `ContentCard` first (see
  `content-card.md`). If you need a bespoke surface, host a
  `CompositionImage`, set `DecodePixelSize` for your slot's pixel size
  (snap to 64 / 128 / 256 / 512), and add an entry to the Quick-find
  table above.

When investigating a "stuck on placeholder" bug:

1. Build with `WAVEE_IMAGE_DIAGNOSTICS` to enable the `[CompImg:NNNN]`
   trace output (the `DiagLog` is `[Conditional("WAVEE_IMAGE_DIAGNOSTICS")]`
   so it's no-op in normal builds).
2. Look for `[CachedImg] LoadCompleted status=Success subscribers=0`.
   With the current unload contract this should not leave a control stuck:
   the detached consumer unsubscribes and reloads on the next `OnLoaded`.
   If it still sticks, check that the retry path uses
   `AddLoadCompletedHandler` and that `OnLoaded` is actually firing.
3. Look for `TryLoad:bail:notAttached` followed by no later `TryLoad:enter`
   — that means the URL was set on a detached control and `OnLoaded`
   never fired (likely the parent was never added to the live tree).
4. Look for `OnUnloaded:enter` showing `att|...` immediately followed by
   the same control's `OnLoaded:enter` (`det|...` → `att|...`). That's
   the recycle path; the new code re-attaches eagerly.

## Keeping This Guide Current

If you add, remove, or rename a `CompositionImage` consumer:
1. Update the **Quick-find table** with the new file:line + binding.
2. If the change touches `OnLoaded` / `OnUnloaded` / `TryLoadCurrent` /
   `ReleasePin`, re-read **Lifecycle Invariants** and update the section
   if the contract has shifted.
3. Re-run the re-verification commands at the top to confirm nothing else
   moved.
4. Bump `last_verified` and `verified_by` in the frontmatter.
