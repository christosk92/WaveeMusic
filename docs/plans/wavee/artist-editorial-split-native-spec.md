# Wavee artist page — Editorial Split native specification

**Status:** implemented, pending canonical verification.  
**Scope:** FluentGpu Wavee artist page. The HTML study is reference material only; this document defines the native
control, token, motion, reactivity, and virtualization contracts.

## 1. Composition

The artist page is one native scroll surface in the real Wavee shell. It has four ordered bands:

1. Editorial Split identity hero.
2. Two-column paged Top Tracks with a supporting Artist Pick / latest-release rail.
3. Complete inline discography facets: Albums, Singles and EPs, Compilations.
4. Existing secondary content: appears-on, tour, videos, playlists, concerts, merch, biography, gallery, related.

No section is wrapped in a decorative card. Cards remain object surfaces only.

## 2. Native control map

| Experience | FluentGpu implementation | Contract |
| --- | --- | --- |
| Artist hero | Existing `ArtistPage` composition, responsive `BoxEl` layout, `ImageEl` through `Ui.Image` | Text and controls live on the semantic copy surface; photography is a separate field and never receives text, scrims, masks, or acrylic. |
| Verified state | `InfoBadge.Icon` + `Ui.Caption` | One compact status mark, not a custom pill. |
| Watch-feed portrait | Existing `ArtistWatchFeedPicture` | Preserved when the payload provides it. |
| Primary actions | `WaveeCta.Play`, `Button`, `IconButton`, `ToolTip`, existing `FollowButton` | One accent-filled primary action; secondary commands use stock control appearances. |
| Artist Pick | `MediaCard.ArtistPick`, `PersonPicture`, `Button`, `Surfaces.Artwork` | `BackgroundImage` selects the rich image-backed shape; absent background selects compact. Both carry the artist comment and pinned object. |
| Top Tracks | Existing `ArtistPopular` → `PagedShelf` → `ChartRow`/`TrackRow`, existing pager | Five rows per column, at most two columns, up to the existing 50-track extended source. Rank/play/equalizer, heart, duration, focus, and paging remain canonical. |
| Discography disclosure | Existing `Expander` with `TemplateParts` | Each facet starts expanded and can collapse. The header remains the sticky context surface. |
| Discography collection | Existing `LazyGrid` + `VirtualCollection<Album>` | The entire facet stays inline and data-virtualized. No 50-item preview cap and no ordinary drill-in barrier. |
| Release object | Existing `MediaCard.GridCard` | Responsive wrapping grid; album activation opens the existing inline drawer. |
| Album detail | Existing `AlbumDrawerPanel` | Full-width, in-place track drawer; stock selection/menu/row behavior remains intact. |
| Loading | `Skel.Region` derived from the real `Body(seed)` | No hand-authored artist skeleton tree. |

The only control-surface extension is an optional exact visible-range callback and sticky-header inset on `LazyGrid`.
It does not add an element type, render opcode, scene column, or new layout primitive.

## 3. Hero geometry and responsive policy

`ArtistHeroLayout` is the single pure policy source. It uses four pressure tiers with the detail system's 24-DIP
hysteresis:

| Tier | Width policy | Layout | Height | Copy/photo |
| --- | --- | --- | --- | --- |
| Wide | ≥1040 after widening recovery | asymmetric horizontal split | 440 | 54% / 46% |
| Medium | ≥760 after widening recovery | asymmetric horizontal split | 384 | 58% / 42% |
| Compact | ≥480 after widening recovery | photo above identity | 540 | 224 photo + elastic identity |
| Narrow | below compact | photo above identity | 516 | 176 photo + elastic identity |

Narrowing crosses a pressure threshold immediately; widening must clear the recovery band. Page gutters use only
`Spacing.PageNarrow`, `Spacing.XXXL`, `Spacing.XXL`, and the existing semantic spacing ladder.

The hero name maps to the Fluent type ramp:

- Wide: `Ui.Display` through `WaveeType.ArtistDisplay`.
- Medium: `Ui.TitleLarge` through `WaveeType.ArtistTitle`.
- Compact/Narrow: existing `WaveeType.PageHero` (`Ui.Title`).

The bio is reduced to its first complete sentence and clamps to two lines. World rank has one home in the identity
metadata line, beside monthly listeners and followers.

## 4. Surface and color semantics

- Copy field: `Surfaces.ArtistEditorialWash`, a restrained artwork hue mixed into `Tok.FillLayerDefault`.
- Page identity/text: `Tok.TextPrimary`, `Tok.TextSecondary`, `Tok.TextTertiary`.
- Rank and active text: `Tok.AccentTextPrimary`.
- Object surfaces: `Tok.FillCardDefault` or opaque `Tok.FillSolidBase` where content overlays photography.
- Borders: `Tok.StrokeCardDefault`, `Tok.StrokeSurfaceDefault`, `Tok.StrokeControlDefault`.
- Interaction: declared `HoverFill` / `PressedFill` using the semantic subtle/control fill hierarchy.
- Corners: `Radii.Control`, `Radii.Card`, `Radii.Pill`; no free-hand radius system.
- Elevation: existing `Elevation.Card` / `Elevation.CardHover` only on discrete objects.

The photograph is not contrast-bearing. Theme switching therefore requires no per-artist scrim tuning.

## 5. Motion semantics

| Event | Native motion |
| --- | --- |
| Hero identity/photo arrival | `EnterExit` + `MotionTok.EmphasizedEnter` |
| Hero scroll collapse | compositor `ScrollBinds` for presented height, 12% photo parallax, and opacity handoff |
| Card hover/press | declarative `WhileHover` / `WhilePressed` + `MotionTok.ControlNormal`; hover plate uses `MotionTok.ControlFast` |
| Discography header brush | `MotionTok.ControlFaster` |
| Visible range/year status replacement | `MotionTok.ScrollFade` |
| Inline drawer open/close | `MotionTok.StandardEnter` / `StandardExit` |
| Inline drawer height reflow | `MotionTok.ContentResize` with `SizeMode.Reflow` |

Reduced motion is resolved by the motion system's token policy. The new paths contain no runtime
`if (Motion.ReducedMotion)` branch, custom timer, or hand-authored easing.

## 6. Catalogue wayfinding without a preview cap

Removing “50 + See all” restores continuous browsing but needs stronger location cues. Each facet therefore provides:

- a pinned `Expander` header below `ArtistShyPill.Clearance`;
- facet title and total count;
- exact visible range (`25–48 of 87`);
- visible release-year context (`2024–2022 · 25–48 of 87`);
- year landmarks on the first card of each newly encountered year;
- an edge fade only while content is clipped below the sticky header.

The visible range is computed from `LazyGrid`'s exact, non-overscanned viewport intersection. While an inline drawer
crosses the viewport, its owning row remains the contextual row. This avoids lying with the larger realization band.

## 7. Data and persistence

`PinnedItem` gains a trailing nullable `BackgroundImage`. `SpotifyExportMapper.MapPinned` maps
`profile.pinnedItem.backgroundImageV2.data.sources`. The nullable trailing default preserves positional-record JSON
compatibility with existing cached documents; null selects the compact Artist Pick.

## 8. Reactive and performance contracts

- Width changes reach the hero through `_heroWidth`; no signal is written during render.
- Tier selection is pure and hysteretic.
- Scroll collapse uses compositor bindings, not per-scroll component state.
- `ArtistPopular` retains its existing extended-track resource and keyed `PagedShelf` realization.
- Discography items page through `VirtualCollection`; `LazyGrid` keeps only the viewport plus overscan realized.
- The visible-range callback runs from an effect, never from render, and updates only when the exact range changes.
- The inline drawer continues to fetch one full album for the selected card.
- `Skel.Region` derives pending paint from the real artist body and its representative seed.

## 9. Verification gates

1. `dotnet build src/FluentGpu.slnx`
2. `dotnet run --project src/FluentGpu.VerticalSlice` → `ALL CHECKS PASSED`
3. `dotnet build src/FluentGpu.WindowsApp`

Focused tests cover hero tier/hysteresis policy, Artist Pick background mapping and persistence compatibility, exact
`LazyGrid` visible ranges including inline drawers, and sticky-header bring-into-view inset behavior.
