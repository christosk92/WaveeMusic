# Concert UI Component and Engine Plan

Status: UI Phase 0 implemented; awaiting verification.

Companion technical plan: [Concert Navigation, Hub, and Generic Geolocation](concert-navigation-hub-geolocation-plan.md)

## Engine rules

- Constructor props freeze after mount. Dynamic data uses signals/context or a keyed remount.
- Hooks run in stable order before conditional returns.
- Cards and rows use canonical URI keys, never indexes.
- Each page owns one outer vertical `ScrollEl` with `Grow = 1`, `MinHeight = 0`, stable `ScrollKey`, and
  `PlayerDock.Reserve` bottom padding.
- Same-axis nested vertical scrollers are prohibited. `ContentSized` is limited to bounded popup results.
- `Responsive.Of` is used at section boundaries and lives in a column wrapper without explicit page width.
- Structural breakpoint changes use `ConcertLayout` hysteresis.
- Shelf data/count/mode is stable at mount or deliberately rekeyed.
- Initial/route loading uses `Skel.Region` derived from the real seeded subtree, never a hand-authored skeleton.
- Async `Loadable` writes return through `UsePost`; stale query generations cannot publish.
- Shelf/card roots and rounded images are not scaled because viewport/scissor clipping can expose broken corners.
- Motion uses existing recipes; append does not replay layout projection across every card.

## Component allocation

| Responsibility | Existing/custom composition | Constraint |
| --- | --- | --- |
| Page viewport | one vertical `ScrollEl` | no nested vertical viewport |
| Responsive sections | `Responsive.Of` + pure hysteresis | same tree for numeric changes |
| Nearby/Recommended | `PagedShelf` | measured only for bounded <=16 items; otherwise fixed `cardHeight` |
| All Events | `Ui.AutoGrid(300, gap, 112, ...)` | loaded sequential pages; no `LazyGrid` without total/range API |
| Concepts | `SelectorBar` inside horizontal `ScrollEl` | selector does not scroll/wrap itself |
| Loading | `Skel.Region` | representative seed uses real subtree |
| Empty/error | `EmptyState.Build`, `ErrorState.Build` | retry is a real action |
| Location picker | anchored `Overlay.Service` panel | fresh mount per open; inline bounded results |
| Ticket action | standard/accent/hyperlink button | external URI path only |

`AutoSuggestBox` is not nested inside the first location overlay. The picker follows the proven PlaylistPicker pattern:
`EditableText`, live signals, inline result rows, a bounded internal result scroll, focus trap, and light dismiss.

## Shared components

### `ConcertUi.DateBlock`

Fixed 64-DIP or compact 52-DIP date identity using current-culture day/month labels and semantic accent colors.

### `ConcertUi.ScheduleRow`, `GridCard`, and `ShelfCard`

One visual family with explicit dimensions. All variants:

- are keyed by concert URI;
- have role, focusability, focus visuals, pointer feedback, and one internal-navigation callback;
- use date/art, title, venue/city, optional Near you status, and chevron;
- never expose a playback callback;
- avoid root scale and image zoom.

### `ConcertUi.LocationButton` and `ConcertLocationPickerPanel`

The button displays current location. The panel consumes live query/results/loading/error signals, renders search results
inline, and delegates selection/geolocation actions to the controller. Network and permission behavior do not live in
the visual component.

### `MediaCard.WideEditorialDestination`

Uses one responsive layered composition:

- >=900 DIP: 288 high, 28 padding, art around 38 percent;
- 600-899 DIP: 240 high, 24 padding, art around 42 percent;
- <600 DIP: 220 high, 20 padding, art around 55 percent and tighter text limits.

The portrait artwork is cover-cropped in an explicit right pane. The card uses no acrylic, self-blur, root scale, or
image zoom.

## Surface layouts

### Artist schedule

- header with artist identity and location control;
- optional bounded nearby shelf;
- chronological `Flow.For` schedule rows;
- wide mode enters at 760 DIP and leaves below 720 DIP;
- one seeded `Skel.Region` and one page scroll.

### Concert detail

- event identity/facts;
- ticket panel, optional lineup/venue, related shelf;
- two-column mode enters at 920 DIP and leaves below 860 DIP;
- 320-DIP ticket panel in wide mode, inline ticket section in narrow mode;
- lineup names without canonical artist URIs remain plain text.

### Concert Hub

- accent header and location control;
- `SelectorBar` in horizontal scrolling edge-fade container;
- Nearby and Recommended shelves;
- playlist promos use existing playlist cards/routes;
- All Events uses keyed `AutoGrid` children and separate load-more state;
- new location/concept clears accumulated pages; pagination append preserves current cards.

### Home

The complete wide card opens `concerts`. It is inserted below the greeting and before dynamic shelves without changing
unrelated Home sections.

## UI phases

### UI Phase 0 - Component proofs

Implemented:

- shared cards/date block/location controls/editorial destination;
- signal-driven location panel shape;
- pure schedule/detail hysteresis and editorial metrics;
- long-lived provider-neutral domain inputs;
- compile and metric/contract tests.

Gate: code/test review. These components are intentionally not routed into production pages yet.

### UI Phase 1 - Clickability baseline

Wire existing tour/concert surfaces to minimal schedule/detail routes and verify mouse/keyboard behavior with no player
queue changes.

### UI Phase 2 - Artist schedule iteration

Implement and visually review loaded, pending, empty, failed, long-text, 1280, 760, 720, and 420-DIP states.

### UI Phase 3 - Concert detail iteration

Implement and review two-column/single-column, no/one/multiple offer, missing optional section, invalid URL, and keyboard
traversal states.

### UI Phase 4 - Hub and location iteration

Implement and review concept overflow, shelves, grid, pagination, manual search, permission denial, rapid filter changes,
and 1440/900/600/420-DIP states.

### UI Phase 5 - Home destination iteration

Review crop, gradient, typography, light/dark themes, missing art, focus, 200 percent text scaling, and all target widths.

### UI Phase 6 - Cross-surface hardening

Run visual consistency, one-scroll ownership, KeepAlive, late async completion, repeated popup, keyboard, theme, and
reduced-motion checks.

## UI definition of complete

- every internal concert surface is mouse and keyboard navigable;
- no concert action calls playback;
- one vertical scroll owner per page;
- shelves/grids/selectors/overlays obey lifetime and keying constraints;
- pending/empty/failure/retry/late-response states are verified;
- light/dark, long localization, narrow width, and 200 percent text scaling remain usable;
- each phase is explicitly accepted before the next begins.
