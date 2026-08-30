# Wavee three-mode sidebar prototype

A local, browser-based interaction prototype derived from the complete Wavee sidebar specification.

## Run it

```powershell
npm install
npm run dev
```

Open `http://127.0.0.1:4173`.

## Main flows

- The first-run chooser previews and applies Classic, Library, or Wavee Curated.
- The sidebar overflow menu switches designs, collapses the active mode, resets its width, and opens the customizer.
- Settings → General provides the same three-card design picker and theme controls.
- Pins are shared across all three designs. Pin from a sidebar row or a main-content card; reorder pins by dragging them in Classic or Curated.
- Library V3 includes library-only search, type and playlist-owner filters, five sorts, four views, folders, and local custom-order drag reordering.
- Wavee Curated renders a persistent section document. Its full-page editor supports five templates, adding/removing/duplicating/reordering sections, live property editing, item picking, a rail preview, and 50-step in-memory undo/redo.
- Each design remembers its own width and collapsed state in browser local storage.
- At widths below 780 px, the sidebar becomes an overlay drawer.

Use Settings → General → Reset prototype to clear local state and replay onboarding.

## Artist page study

Open the isolated artist-page comparison without entering the sidebar prototype:

```text
http://127.0.0.1:4173/?study=artist-hero
```

Three variants share one corrected foundation, so the comparison isolates the photo treatment:

| `variant` | Treatment |
| --- | --- |
| `band` (default) | Full-bleed 288 DIP band from the real 2660×1139 header; all identity text on the content layer below it. |
| `plate` | Framed 320² portrait beside the identity text over a 6% palette wash. |
| `current` | The shipping hero rebuilt against the real native constants — the honest control, and the only variant that sets text on the photograph. |

Variant and theme are written to the URL, so a specific view is directly shareable. `&chrome=0` hides
the study switcher for clean captures:

```text
http://127.0.0.1:4173/?study=artist-hero&variant=plate&theme=dark
http://127.0.0.1:4173/?study=artist-hero&variant=band&theme=light&chrome=0
```

The study renders in the real shell at full viewport (sidebar 280 + titlebar 48 + player 72), because
the earlier version rendered inside a fake inset window that cost 172 px of chrome and made every
proportion untransferable — its container query measured 1370 px for a pane that is really 1160 px.

Design tokens live in `src/components/artist-study/artistTokens.css` and are enforced by tests: six
type steps, weights 400/600, three radii, at most two elevation shadows, and one accent-filled object
per view. Regenerate the comparison screenshots into the gitignored `artifacts/` folder with:

```powershell
npm run capture:artist
```

The full design spec — diagnosis, geometry, responsive matrix and the FluentGpu mapping — is
`docs/plans/wavee/artist-page-v2-design.md`.

The Conan Gray campaign image is local to the research prototype so comparisons and screenshots stay
deterministic. It is reference material supplied for this study, not a production-licensed Wavee
asset; the album covers are muted local placeholders, deliberately quiet so a 40 px cover never
out-shouts a 14 px track title.

## Verification

```powershell
npm run build
npm test
```

The Playwright suite covers onboarding and live mode switching, Library V3 controls and shared pins, Settings and collapse behavior, and the Curated live editor.
