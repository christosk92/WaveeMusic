# Library rework — visual prototype (approval gate)

This is the **browser approval gate** for the WaveeMusic Library rework. Open
`index.html` in a browser **on the Windows 11 dev machine** (it uses *Segoe
Fluent Icons* and *Segoe UI Variable*, the app's real font + icon set, so it
renders faithfully and maps 1:1 to the WinUI port). No XAML is written until the
look here is approved.

## What it mocks

All four library tabs in the proposed **unified** language:

- **Albums** — left master grid (square art) + the shared right detail panel.
  Source segmented: *Saved* / *From Liked Songs*. The Liked source shows the
  *Liked / Full album* inline sub-toggle.
- **Artists** — left master grid (circular avatars) + shared detail panel. The
  detail content slot holds the **discography** (collapsible album groups). One
  master *View as* governs list/grid (the old per-group toggles are gone).
- **Liked Songs** — full-width track table (correct for a flat list) that adopts
  the same toolbar: a real *Sort* control, the **genre/mood chip row**, and a
  free-text filter pill.
- **Podcasts** — collapsed from 3 columns to the same 2-pane master/detail:
  shows list + shared detail panel (grouped episode list, *Saved / Latest*
  sub-toggle). **This 3→2 collapse is the main UX change to sign off on.**

## The unified grammar (what to evaluate)

1. **One toolbar** everywhere: `[ source segmented? ] [ Sort & view ▾ ] [ filter pill ]`.
2. **One detail panel** with a config-driven hero: art *shape* is a flag —
   square (albums, shows) vs circle (artists) at the same size.
3. **One declarative action row** (Play / Shuffle / View / Unheart / Follow /
   Saved-only / Open / scope toggle) — same control, per-tab visibility.
4. Reveal uses a **cross-fade** (consistent with the app's no-connected-animations
   policy), and respects `prefers-reduced-motion`.

## Files

- `index.html` — app chrome (rail, top bar, tabs, player) + mount points.
- `styles.css` — the entire unified visual language (design tokens + components).
- `app.js` — tab switch, selection → shared detail panel, source toggle,
  sort/view popover, view-mode swap, chip + free-text filtering, cross-fade.
- `mock-data.js` — realistic placeholder data for all four tabs.

## Decisions this prototype is asking you to approve

- Podcasts 3-pane → 2-pane (fallback: keep 3 columns, adopt toolbar/panel styling only).
- One hero template handling square vs circle art via a shape flag.
- Unified source labels *Saved* / *From Liked Songs* (replacing *Hearted* / *Following*).
- Liked Songs gaining a free-text filter pill + a real sort control.
