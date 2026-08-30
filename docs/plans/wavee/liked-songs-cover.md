# Liked Songs cover — what shipped

Status: **landed** (2026-08). The Liked Songs collection cover is no longer one bundled purple heart per account.
This note is the record of what was built, what it rides on, and what was deliberately NOT built. The prototype it
implements is `docs/plans/wavee/liked-songs-cover-mica.html`; the plan and its edge-case ledger (E1–E22) are
`.claude-work/plans/drifting-spinning-possum.md`.

## What shipped

**Nine treatments**, all composed from the user's own newest likes, all authored on ONE 304-DIP canvas and dropped
into a `size / 304` scale wrapper (`src/apps/Wavee/Features/Detail/LikedCoverTreatments.cs`):

| Style | What it is | Min tiles |
| --- | --- | --- |
| **Lens** (default) | 3×3 mosaic blurred + dimmed + palette-veiled as the ground, the identical mosaic crisp inside a heart-shaped stencil clip, thin rim, sheen | 4 |
| Wall | tilted 6×6 wall under a 92 s ambient drift, two-layer vignette | 8 |
| Rainbow | 4×4 ordered by dominant hue, serpentine rows | 8 |
| Marquee | two diagonal strips crossing over a palette ground | 6 |
| Feature | newest like at 2×2 with six followers | 4 |
| Mosaic | flat 3×3, bottom scrim, name chip | 4 |
| Tone | five radial washes graded from the newest covers, vector heart, track count | 1 |
| Stack | last five fanned in the hub-card language, hover fan-out | 3 |
| Stock | the bundled PNG | 0 |

**Picker** — hover the rail cover → "Cover style" pill → `PopupChrome.Flyout` overlay with a 3×3 radio strip of LIVE
miniatures (the same builders at `mini: true`, so a thumbnail cannot drift from the cover behind it). Selection
persists to `WaveeSettings.LikedCoverStyle`, broadcast by `AppearancePrefs.Bump()`.

**Rail facts** — likes-per-week sparkline, most-liked artists face pile, tag blend bar, a this-week-last-year row and
a since-line, all pure functions of `AddedAt`/`Tags`/artists.

**Default = Lens**, and the **degrade ladder** is what makes that safe: `LikedCoverRules.Effective(style, tiles)`
returns Stock whenever the library cannot honestly feed the requested style. A fresh install therefore sees exactly
today's cover until it owns four distinct covers, with no setting written and no special-casing.

## The engine tier it rides on

Lens is the only treatment that needed new engine surface: the **tier-3 stencil path clip** —
`BoxEl.ClipPath` / `ClipPathRule` / `ClipPathViewBoxW/H`, recorded as `PushStencilClip`/`PopStencilClip`. It is
specified as-built in `docs/design/subsystems/gpu-renderer.md` §6.1 (with §5's known-gap line now retired), gated by
`PathSuite.StencilClipChecks` in the VerticalSlice and pictured by the `stencilclip` / `stencilclip-fallback`
screenshot scenes. The app consumes it and owns none of it.

Everything else the treatments needed already existed: `ImageEl.BakedBlur` / `ColorOverlay` / `Saturation`,
`BoxEl.Gradient`, the slab's looping `Keyframes`, `MotionTarget` hover deltas, `PathEl` + `PathGeometryTable`.

## The E1–E22 resilience decisions, in brief

- **Cold and empty** (E1–E4): zero tiles is Stock for every style, so the first frame is the pre-feature app. The
  facts panel is not mounted at all below its evidence floor — no empty cards. `EnsureLiked()` is charged only when a
  non-Stock style will actually paint, or when the picker opens.
- **Network** (E5–E7): every tile is an ordinary `ImageEl` on the app's placeholder ladder, so a cell that cannot
  decode shows that record's graded tint. Palette grounds paint immediately from a neutral fallback and upgrade in
  place on `Watch`. Lens's ZStack puts the crisp window ABOVE the ground so the ground's late blur derivative fades
  UNDER a fixed shape instead of smearing the whole cover.
- **Live mutation** (E8–E10): `LibraryStore.Liked` refreshes in place, so a like/unlike recomposes the cover with no
  skeleton flash; cells are url-keyed, so only genuinely changed art re-decodes. Dropping below a floor mid-view
  cross-fades to Stock and re-liking restores it — the same one path, not a special case.
- **Hostile data** (E11–E14): `FromSetting` clamps an unknown int to Stock. Tiles are deduped by album uri AND by
  url, and artwork-less tracks are skipped rather than contributing empty cells. Facts exclude null/epoch `AddedAt`
  and clamp future dates; the blend card needs a minimum evidence count or it is not mounted.
- **Lifecycle** (E17–E20): ambient loops are slab keyframe rows, so they quiesce under a parked page by themselves.
  Reduced motion is a VALUE (a zero-amplitude keyframe array), never an `if` in render code. Decode sizes bucket to
  64/128/256 so a rail drag re-scales a composited transform instead of re-decoding.

## Deliberate departures — stated, not hidden

1. **The path clip is a HARD EDGE.** The v1 stencil tier keeps or discards a pixel; it does not feather one. Lens's
   heart therefore has no anti-aliasing of its own, and the white rim stroke drawn OVER the boundary is what dresses
   it. An anti-aliased path clip is the offscreen-layer route (gpu-renderer.md §7.1) and was not built.
2. **Lens's blurred ground is COVER-SIZED.** `CoverPaletteLeaves.cs:59-70` is a tombstone for a deliberately deleted
   PAGE-scale blurred-art plane, and the page's own ground still clamps to hue only. A 304-DIP blurred mosaic inside
   the cover is a different thing at a different scale, and it is a stated departure from that tombstone rather than
   a quiet re-litigation of it.
3. **Lens's ground blur is baked PER TILE**, not over the layer. `BakedBlur` derives one persistent image per source,
   so tile boundaries stay faintly legible as colour transitions where the prototype's single-layer CSS blur bleeds
   across them. The trade buys a cover that costs nothing per frame on a page that also scrolls a 10k list.
4. **No multiply blend, no additive blend, no grain, no perspective.** The renderer blends premultiplied source-over.
   Lens's veil and Tone's spots are source-over at reduced alphas; Tone's film grain has no engine equivalent and is
   absent; Wall drops the prototype's `rotateX` (the transform block is 2D affine) and carries the tilt with its roll.
5. **No play-recency facts.** `PlayLogStore` is a 200-entry FIFO ring, so "you haven't played this in a year" cannot
   be answered honestly. The rediscover fact is reframed onto `AddedAt` alone ("this week last year you liked N").
6. **Picker cards below their floor stay SELECTABLE** (dimmed, showing the stock stand-in). `RadioButtons` has no
   per-item enabled state, and forging one would park the selection ring on a value that never persisted. Choosing an
   unfed style is legitimate: it stores, it paints Stock, and it lights up by itself when the library reaches the floor.

## Where the code lives

- Rules (engine-free, fully unit-tested): `Features/Detail/LikedCoverRules.cs`, `Features/Detail/LikedFactsRules.cs`
- Composition: `Features/Detail/LikedCoverTreatments.cs` (all nine), `Features/Detail/LikedHeart.cs` (the interned
  contour), `Features/Detail/LikedCoverLeaves.cs` (every `CoverColorPlane.Watch` subscriber)
- The one component: `Features/Detail/LikedCoverArt.cs`; entry point `Components/LikedSongsArtwork.cs` → `Dynamic`
- Picker: `Features/Detail/LikedCoverPicker.cs`; facts: `Features/Detail/LikedFactsPanel.cs`
- Tests: `src/apps/Wavee.Tests/LikedCoverRulesTests.cs`, `LikedFactsRulesTests.cs`
