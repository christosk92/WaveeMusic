---
guide: content-card
scope: ContentCard — the reusable shelf / grid card used across every shelf and grid surface (Home, Search, Browse, Library, Artist, Album, Show, Concert, Profile, Local-media).
last_verified: 2026-05-18
verified_by: read+grep over src/Wavee.UI.WinUI/Controls/Cards, src/Wavee.UI.WinUI/Behaviors/Card, and every XAML page that hosts a ContentCard
root_index: AGENTS.md (Codex) and CLAUDE.md (Claude Code)
---

# Wavee ContentCard Inventory

`ContentCard` is the single shelf / grid card used everywhere in the app:
every Home shelf entry, every search result tile, every library album
tile, every artist-page related-albums shelf, every local-media row. If
you want to change the look or behavior of "the card" across the entire
app, this is the control.

It composes:
- A colored placeholder surface (`SquareImageContainer` /
  `CircleImageContainer`) with a Fluent glyph fallback.
- A `CompositionImage` for the actual artwork (see
  `.agents/guides/composition-image.md` for the image primitive).
- Title / subtitle / badge text, configurable per shelf.
- Hover and press chrome (scale + reveal-stroke style).
- Play-overlay button bound to `PlayRequested` / `IsPlaying` /
  `IsContextPaused` via `NowPlayingHighlightService`.
- An "external action" overlay used by IsExternal cards (merch).
- A `SecondaryAction` slot.
- Connected-animation hand-off support for grid → detail page nav.

## How To Use This Guide

1. Skim the **Quick-find table** below to find your surface.
2. Read **Core structure** before touching the control itself — the class
   is split across four partial files, each with a defined ownership.
3. Read **Lifecycle and viewport gating** if you're changing how images
   load or how cards behave during scroll.
4. Behaviors (image retry, passive pointer, effective-viewport prefetch)
   live in `Behaviors/Card`. See **Attached behaviors**.

Re-verification commands:

```
rg -n ":ContentCard\b" src/Wavee.UI.WinUI -g "*.xaml"
rg -n "new ContentCard\b|ContentCard\." src/Wavee.UI.WinUI -g "*.cs"
rg -n "CardImageRetryBehavior|CardPassivePointerBehavior|CardEffectiveViewportBehavior" src/Wavee.UI.WinUI
rg -n "NowPlayingHighlightService" src/Wavee.UI.WinUI
```

Scope:
- Included: `ContentCard` + its four partials, the three attached
  behaviors in `Behaviors/Card`, `NowPlayingHighlightService`,
  `ThemeColorService` interactions for placeholder color hints, every
  XAML usage of `<cards:ContentCard>`.
- Not included: bespoke card-like surfaces that do NOT use ContentCard
  (`EpisodeCard`, `SpotlightReleaseCard`, `PopularReleaseRow`,
  `BaselineHomeCard`, `EditorialHeroCard`, `ArtistCircleCard`,
  `MediaCard`, `ShortsPill`) — those are listed in the
  `composition-image.md` Quick-find table since they each host their own
  `CompositionImage`. Track / episode rows have their own guide
  (`track-and-episode-ui.md`).

## Quick-find Table

| Surface | Host file:line | Data source | Notes |
| --- | --- | --- | --- |
| Home — playlist / album shelf entry | `Views/HomePage.xaml:68` | `HomeSectionItem` (`BestMediumImageUrl`, `Title`, `Subtitle`) | Default square card. Click → `HomePage.ContentCard_Click` routes via `AlbumNavigationHelper` / `PlaylistNavigationHelper`. |
| Home — artist circle card | `Views/HomePage.xaml:80` | `HomeSectionItem` (artist variant) | Template selected by `HomeItemTemplateSelector`. `IsCircularImage=True`. |
| Recently played — default card | `Controls/RecentlyPlayedSection.xaml:15` | `HomeSectionItem` | Reusable square template. |
| Recently played — artist card | `Controls/RecentlyPlayedSection.xaml:27` | `HomeSectionItem` (artist variant) | `IsCircularImage=True`. Template key `ArtistCardTemplate`. |
| Browse — editorial tile | `Views/BrowsePage.xaml:34` | `HomeSectionItem` | Browse editorial item template. |
| Section shelves — generic shelf entry | `Views/Shared/SectionShelvesView.xaml:21` | `HomeSectionItem` | The shared shelf template — used by HomePage, SearchPage section shelves, ProfilePage shelves, AlbumPage related shelves. Square default. |
| Search — result tile | `Views/SearchPage.xaml:173` | `Wavee.Core.Http.Pathfinder.SearchResultItem` | `ImageUrl="{x:Bind ImageUrl}"`, `Title="{x:Bind Name}"`, `Subtitle="{x:Bind DisplaySubtitle}"`. |
| Artist page — related albums shelf | `Views/ArtistPage.xaml:734` | `LazyReleaseItem` (release) | `ImageUrl="{x:Bind Data.ImageUrl}"`. Goes via `LazyReleaseItem.Data`. |
| Artist page — music videos shelf | `Views/ArtistPage.xaml:918` | `MusicVideoVm` | `AspectMode=Wide`, `PlaceholderGlyph="\uE714"` (video icon). |
| Artist page — related playlists shelf | `Views/ArtistPage.xaml:938` | `ArtistPlaylistVm` | Square. |
| Artist page — related artists shelf | `Views/ArtistPage.xaml:1224` | `RelatedArtistVm` | `IsCircularImage=True`. `Subtitle="Artist"` hardcoded. |
| Artist discography page — release grid | `Views/ArtistDiscographyPage.xaml:73` | `LazyReleaseItem` | Square grid. |
| Album page — related albums shelf | `Views/AlbumPage.xaml:1001` | `AlbumRelatedResult` | `Tag="{x:Bind}"`. |
| Album page — related playlists shelf | `Views/AlbumPage.xaml:1051` | `PlaylistDetailDto` | `ImageUrl` is `OneTime` binding for perf. |
| Show / Podcast page — recommendations | `Views/ShowPage.xaml:606` | `ShowRecommendationDto` | Square. |
| Concert page — album shelf | `Views/ConcertPage.xaml:737` | `ConcertAlbumVm` | Square. |
| Concert page — featured playlists shelf | `Views/ConcertPage.xaml:789` | `ConcertFeaturedPlaylistVm` | Square. |
| Profile page — followed playlists shelf | `Views/ProfilePage.xaml:287` | `SpotifyProfilePlaylist` | Square. |
| Profile page — followed artists shelf | `Views/ProfilePage.xaml:322` | `SpotifyProfileArtist` | `IsCircularImage=True`. `Subtitle="Artist"` hardcoded. |
| Library — saved albums grid (wide) | `Views/AlbumsLibraryView.xaml:48` | `LibraryAlbumDto` | Square grid. |
| Library — saved albums narrow display | `Views/AlbumsLibraryView.xaml:349` | `LibraryAlbumDto` | `Tapped="NarrowAlbumCard_Tapped"` for the narrow detail-pane handoff. |
| Local library — continue watching | `Views/LocalLibraryPage.xaml:104` | `LocalContinueItem` | `AspectMode=Backdrop` (16:9). `PlaceholderGlyph="\uE714"` (video). |
| Local library — shows | `Views/LocalLibraryPage.xaml:130` | `LocalShow` | `AspectMode=Tall` (2:3). `PlaceholderGlyph="\uE7F4"`. |
| Local library — movies | `Views/LocalLibraryPage.xaml:157` | `LocalMovie` | `AspectMode=Tall`. `PlaceholderGlyph="\uE714"`. |
| Local library — music videos | `Views/LocalLibraryPage.xaml:184` | `LocalMusicVideo` | `AspectMode=Wide`. `PlaceholderGlyph="\uE714"`. |
| Local shows page — show grid | `Views/Local/LocalShowsPage.xaml:116` | `LocalShow` | `AspectMode=Tall`. |
| Local show detail — cast members | `Views/Local/LocalShowDetailPage.xaml:240` | `LocalCastMember` | `IsCircularImage=True`. `CenterText=True`. `ShowPlaybackOverlay=False`. |
| Local movies page — movie grid | `Views/Local/LocalMoviesPage.xaml:114` | `LocalMovie` | `AspectMode=Tall`. |
| Local movie detail — cast members | `Views/Local/LocalMovieDetailPage.xaml:194` | `LocalCastMember` | `IsCircularImage=True`. `CenterText=True`. `PlaceholderGlyph="\uE77B"` (person). |
| Local music videos page | `Views/Local/LocalMusicVideosPage.xaml:26` | `LocalMusicVideo` | `AspectMode=Wide`. |
| Local person detail — shows | `Views/Local/LocalPersonDetailPage.xaml:129` | `LocalShow` | `AspectMode=Tall`. |
| Local person detail — movies | `Views/Local/LocalPersonDetailPage.xaml:159` | `LocalMovie` | `AspectMode=Tall`. |

## Core Structure

The class is split across four partial files. Stick to the ownership when
adding code so the file boundaries stay meaningful:

| File | Owns |
| --- | --- |
| `Controls/Cards/ContentCard.xaml` | Layout: `CardRoot` Grid, `SquareImageContainer` + `SquareImage` + `SquarePlaceholderIcon`, `CircleImageContainer` + `CircleImage` + `CirclePlaceholderIcon` (both x:Load-deferred behind their respective realize gates), play / external-action overlays, title / subtitle / badges, lazy shimmer overlay (x:Load=IsLoading). Attached behaviors wired here. |
| `Controls/Cards/ContentCard.xaml.cs` | Construction, `Loaded`/`Unloaded` lifecycle, image load state machine (`LoadImage` / `ReleaseImage` / `ReloadImageIfNeeded` / `HasImage`), placeholder color/glyph application, aspect-ratio resizing, the helpers shared by the other partials. |
| `Controls/Cards/ContentCard.DependencyProperties.cs` | Every DP (24+ as of `last_verified`) and its `PropertyChanged` callback — `ImageUrl`, `Title`, `Subtitle`, `Badge`, `AspectMode`, `IsCircularImage`, `CenterText`, `PlaceholderColorHex`, `PlaceholderGlyph`, `ImageSize`, `NavigationUri`, `NavigationTitle`, `NavigationTotalTracks`, `IsExternal`, `ShowPlaybackOverlay`, `UseConnectedAnimation`, `AutoNavigateOnTap`, `SecondaryActionVisible`, `SecondaryActionGlyph`, `SecondaryActionTooltip`, `IsPassive`, `IsLoading`, `IsPlaying`, `IsContextPaused`, `IsImageLoadingSuspended`. |
| `Controls/Cards/ContentCard.Navigation.cs` | Click routing (`CardClick`, `CardMiddleClick`, `CardRightTapped`), `NavigateToUri()` helper (palette + count prefill + connected-animation prep), drag-source payload construction, `SecondaryAction` click. |
| `Controls/Cards/ContentCard.PlaybackHighlight.cs` | `IsPlaying` / `IsContextPaused` visual state, subscription to `NowPlayingHighlightService.CurrentChanged`, play-button click, pending-beam (border glow during play-pending), now-playing equalizer overlay. |

## Modes and Variants

Four user-facing knobs combine to produce every shelf style:

- **Square (default)** vs **Circular** (`IsCircularImage="True"`). Square
  shows `SquareImageContainer` (rounded rect, 4 px corners). Circular
  shows `CircleImageContainer` (centered ellipse with 16 px inset).
- **AspectMode** (`Square` default / `Tall` / `Wide` / `Backdrop`). Used
  by the local-media shelves to host portrait posters (`Tall` 2:3),
  16:9 thumbnails (`Wide`), and backdrop hero (`Backdrop`). Applied via
  `SquareImageContainer_SizeChanged` → height = width × ratio.
- **CenterText** — used by cast cards to center the name under a circle
  portrait. Default left-aligned.
- **ShowPlaybackOverlay** — disabled by cast / non-playable cards so the
  hover play button doesn't render.

## Lifecycle and Viewport Gating

`ContentCard` participates in three lifecycle pipelines:

1. **Standard WinUI `Loaded` / `Unloaded`.**
   - `OnLoaded` subscribes to `NowPlayingHighlightService.CurrentChanged`
     and `ImageLoadingSuspension.Changed`, registers the
     `CardImageRetryBehavior` retry callback, and triggers `LoadImage` if
     either no viewport behavior has fired yet or the card is currently
     inside the viewport.
   - `OnUnloaded` releases the `SquareImageContainer`'s composition clip,
     unsubscribes from the highlight service / suspension event, removes
     the retry handler, detaches the `CircleImageContainer.SizeChanged`
     handler. `CompositionImage.OnUnloaded` handles its own pin release —
     this control does not null `SquareImage.Source` (the
     `feedback_contentcard_unload_nulls_image` memory captures the
     reasoning; recycle bugs are fixed by re-triggering `LoadImage` on
     re-realization, not by removing the cleanup).

2. **Viewport gating via `CardEffectiveViewportBehavior`.**
   - The attached behavior subscribes the card's
     `EffectiveViewportChanged` event on `Loaded` (cleared on `Unloaded`
     so handler entries don't accumulate in the WinRT event-source table
     across ItemsRepeater recycles).
   - `ContentCard.HandleViewportIntersectionChanged(hasViewport, isInside)`
     is the call from the behavior. When the card moves OUT of the
     viewport the behavior is the gate, not this control — `OnUnloaded`
     is NOT called for off-screen virtualized cards in
     `NonVirtualizingLayout`; the gate exists so cold image loads don't
     fire for cards that haven't intersected the viewport.
   - `ContentCard.HandleViewportPrefetch(uri, kind)` is invoked once per
     (realization, kind) when the card enters prefetch range. Pushes the
     URI to `IAlbumPrefetcher` or `IPlaylistMetadataPrefetcher`.
   - `ContentCard.HandleViewportReset()` resets the local mirror on
     detach so the next attach re-samples.

3. **Image-loading suspension via `ImageLoadingSuspension.Changed`.**
   - `HomePage.BeginScrollRestore` flips the gate `true` during scroll
     restoration. Suspended cards' cold loads bail; on release,
     `ContentCard.OnImageLoadingSuspensionChanged` enqueues a
     `ReloadImageIfNeeded(ignoreViewportGate: true)` on the dispatcher.

The mirror fields `_hasEffectiveViewport` and `_isInsideEffectiveViewport`
let synchronous DP-changed callbacks read the gate without taking a
dependency on the behavior. Default is `true` so the first realize loads
the image even before the first `EffectiveViewportChanged` fires.

`_currentImageCacheUrl` is the card's own URL dedup — set by `LoadImage`
when a fresh URL is pushed to `SquareImage.ImageUrl`. The retry callback
uses this to bail when the failing URL no longer matches what the card
currently wants.

## Attached Behaviors

Attached behaviors live in `Wavee.UI.WinUI/Behaviors/Card/`:

### `CardImageRetryBehavior`

Wired declaratively on `SquareImage` in `ContentCard.xaml:76`. Subscribes
to `CompositionImage.ImageFailed`. Records the failing URL; calls the
registered retry callback ONCE per URL. Gated on
`!ImageLoadingSuspension.IsSuspended` so it doesn't retry during scroll
restore. The retry callback is registered in `ContentCard.OnLoaded` via
`AddRetryHandler(SquareImage, OnImageRetryRequested)` and removed in
`OnUnloaded`. `OnImageRetryRequested` re-runs `LoadImage(ImageUrl)`
after sanity-checking that the card is still loaded, in the viewport,
the slot is still empty, and the URL still matches `_currentImageCacheUrl`.

### `CardPassivePointerBehavior`

Wired declaratively on the card root in `ContentCard.xaml:17`. Re-routes
pointer events via `AddHandler(handledEventsToo: true)` so hover chrome
still runs when a parent's selection chrome (e.g. `ItemsView`,
`ListView`) marks pointer events as handled. The behavior calls back
into `ContentCard.HandlePassivePointerEntered` / `Exited` / `Pressed` /
`Released`.

### `CardEffectiveViewportBehavior`

Wired declaratively on the card root in `ContentCard.xaml:18`.
Subscribes to `EffectiveViewportChanged` on `Loaded` and unsubscribes on
`Unloaded`. Computes whether the card is inside the visible viewport AND
whether it's within prefetch range, then calls
`HandleViewportIntersectionChanged` / `HandleViewportPrefetch` /
`HandleViewportReset` on the card. Prefetch-once latching lives in the
behavior.

## NowPlayingHighlightService

`Services/NowPlayingHighlightService.cs` is a shared singleton that
listens to `NowPlayingChangedMessage` once at startup and re-broadcasts
through a plain C# event (`CurrentChanged`). `ContentCard.OnLoaded`
subscribes; `OnUnloaded` unsubscribes (strong event, explicit
unsubscribe required). Without this indirection, every realized card
would `WeakReferenceMessenger.Default.Register<...>` directly — on
HomePage that's ~310 register calls per realize burst.

`ApplyHighlight(contextUri, albumUri, playing)` compares the broadcast
state against the card's `NavigationUri` to decide `IsPlaying` /
`IsContextPaused`. The card's overlay state machine in
`ContentCard.PlaybackHighlight.cs` reacts to those DPs.

## Image Loading Pipeline (per-card)

The sequence for a freshly-realized card:

1. ContentCard constructor: `InputSystemCursor.Hand` set;
   `EnsureManualDragAttached()`; `Loaded` / `Unloaded` subscribed.
2. DPs apply (`ImageUrl`, `Title`, `Subtitle`, `IsCircularImage`, etc.).
   `OnImageUrlChanged` records the URL but does NOT push to the image —
   it lets `OnLoaded` do the first push when viewport state is known.
3. `OnLoaded` fires:
   - Subscribe to `NowPlayingHighlightService.CurrentChanged`.
   - Subscribe to `ImageLoadingSuspension.Changed`.
   - `CardImageRetryBehavior.AddRetryHandler(SquareImage, OnImageRetryRequested)`.
   - If `!_hasEffectiveViewport || _isInsideEffectiveViewport` →
     `LoadImage(ImageUrl)`.
4. `LoadImage`:
   - Resolves the URL via `SpotifyImageHelper.ToHttpsUrl`.
   - Picks the right `CompositionImage` (square vs circle).
   - Sets `DecodePixelSize = CardImageDecodeSize` (200).
   - Pushes `ImageUrl` to the `CompositionImage`.
   - `CompositionImage.TryLoadCurrent` does the actual cache pin / load
     subscription — see `composition-image.md` for the lifecycle.
5. On success, `SquareImage.ImageOpened` →
   `ContentCard.SquareImage_ImageOpened` fires; the placeholder glyph is
   hidden and the image fades from `Opacity=0` to `Opacity=1`.
6. On failure, `SquareImage.ImageFailed` → `CardImageRetryBehavior`
   schedules one retry via `OnImageRetryRequested`. Second failure for
   the same URL leaves the card on the placeholder.

The card's placeholder color is set by `ApplyPlaceholderColor` from
`ThemeColorService.ResolveCardPlaceholder(...)` when `PlaceholderColorHex`
isn't supplied — gives each card a stable tinted backdrop that doesn't
flash white before the image lands.

## Change Guidance

When changing cards:

- **Add a new shelf** → bind to `ContentCard` with `ImageUrl`, `Title`,
  `Subtitle`, `NavigationUri` (for auto-navigate), and either
  `IsCircularImage` or an `AspectMode`. Wire `CardClick` if you need a
  custom click handler (otherwise `AutoNavigateOnTap` + `NavigationUri`
  handles it).
- **Change the look of every card** → edit `ContentCard.xaml`.
  Hover/press chrome lives in the visual states applied from
  `Card_Pointer*` handlers in `xaml.cs`. Be mindful of the lazy x:Load
  gates on the play overlay and external-action button — they ship
  collapsed and only realize when needed.
- **Per-page customization** → prefer adding a new DP +
  `PropertyChanged` callback in
  `ContentCard.DependencyProperties.cs` over a parallel control.
  Convention: the DP defines the *what*; the `PropertyChanged` callback
  applies the visual change so it's consistent regardless of where the
  DP is set.
- **Click / nav behavior** → `ContentCard.Navigation.cs`. The control
  routes through `AlbumNavigationHelper` /
  `PlaylistNavigationHelper` when `NavigationUri` is a Spotify entity
  URI; otherwise raises `CardClick`.
- **Drag source** → `ContentCard.Navigation.cs:EnsureManualDragAttached`
  + `OnCardDragStarting`. The drag payload is built from
  `NavigationUri` + `Title` + drag-image.
- **Image loading / cache** → see `composition-image.md`. Do NOT touch
  `SquareImage.Source` directly — go through `LoadImage` /
  `ReleaseImage` / `ReloadImageIfNeeded`.
- **Now-playing visual state** → `ContentCard.PlaybackHighlight.cs`.
  Updates are driven by `NowPlayingHighlightService`; don't subscribe
  to `WeakReferenceMessenger` directly (the 310-cards-per-page register
  storm is the reason this service exists).
- **Viewport / prefetch** → `CardEffectiveViewportBehavior`. Add new
  prefetch kinds to `ContentCard.ViewportPrefetchKind` if needed.

When you find a card "stuck on placeholder":

1. Build with `WAVEE_IMAGE_DIAGNOSTICS` to enable the `[CompImg:NNNN]`
   trace from `CompositionImage`. See `composition-image.md`
   "Lifecycle invariants" → that guide owns the diagnostics workflow for
   image loading.
2. Check whether `LoadImage` was called at all — verify the viewport
   gate didn't suppress the first attempt
   (`_isInsideEffectiveViewport` should be true on first realize).
3. Verify `_currentImageCacheUrl` matches the URL the consumer expects
   — a stale latch from a previously-recycled URL can cause the retry
   path to bail.

## Keeping This Guide Current

If you add, remove, or rename a `ContentCard` consumer:
1. Update the **Quick-find table** with the new file:line + data source.
2. If you add a new DP, update **Core structure** (the
   `ContentCard.DependencyProperties.cs` row) and call out non-obvious
   defaults in **Modes and Variants**.
3. If you change the lifecycle / viewport pipeline, update
   **Lifecycle and viewport gating**.
4. Re-run the re-verification commands at the top to confirm nothing
   else moved.
5. Bump `last_verified` and `verified_by` in the frontmatter.
