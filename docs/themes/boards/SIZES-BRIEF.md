# Wavee themes: window sizes brief (read this first)

## The owner's request
"For each page, also design smaller modes and huuuge modes. For example I am missing a small mode (like we resize
the app to mobile phone compact) or in between (800×600?) and huge mode, like on a 32 inch screen."

Wavee is a Spotify desktop client for Windows (NativeAOT, GPU-rendered by the FluentGpu engine). It ships four
themes, each a complete look with its own pages, shell, player and navigation:
- Wavee Classic (tuned)
- WinUI
- Zune
- Panorama (Zune's structure with WinUI's look)

Every theme has a design canvas with ~30-58 boards, almost all drawn at the standard 1440×960 window. Your job is to
add size tiers to ONE theme's canvas.

## The size tiers (DIP = effective pixels after Windows scaling)

| Tier | Board size | What it stands for |
|---|---|---|
| Compact | 400 × 860 | window dragged down to phone width; one column, everything reachable |
| Small | 800 × 600 | the classic small window / a laptop split screen |
| Standard | 1440 × 960 | the existing boards; leave them as they are |
| Huge | 2560 × 1440 | a 32-inch 1440p screen at 100 %, or a 4K screen at 150 % |
| Huge 4K | 3840 × 2160 | a 32-inch 4K screen at 100 %; only the key surfaces (see below) |

## Design rules
- **Every page and the shell get Compact, Small and Huge boards.**
  - "Page" = every page board on the canvas: Home, Browse, Browse category, Search, Album, Artist, Discography,
    Playlist (owned and followed), Liked Songs, Library views, Recents, Profile, Show, Episode, Concert, Queue,
    Lyrics, Now Playing / full-screen stage, Video, Settings, Setup, Release notes, Feedback, and the theme's options
    board.
  - "Shell" = nav, title bar, player bar and side panels.
  - State boards (loading, empty, error, row states) need Compact only, plus Huge where layout really changes.
- **Huge 4K** boards for: the frame with Home, Album, Artist, and Now Playing full screen.
- **Each tier is a real design, never a scaled copy.**
  - **Compact:**
    - The nav collapses (hamburger, bottom pivots or a minimal rail, whatever fits the theme's language).
    - The player bar becomes a slim mini bar that expands to a sheet.
    - Side panels become overlays.
    - Heroes shrink and stack.
    - Track rows drop columns (album, date, duration go into a second line or away).
    - Grids go to 2 columns.
    - Touch-size targets stay ≥ 40 px.
  - **Small (800×600):**
    - Short height is the real constraint: reserve the player bar, keep heroes short, and decide what collapses
      first.
    - The nav is compact.
    - Panels overlay or narrow.
  - **Huge:**
    - Use the space on purpose. Wider grids (more columns, larger cards), multi-pane layouts (e.g. album tracks
      + artist info side by side, queue and lyrics docked at once), larger heroes and type steps where the theme's
      ramp allows.
    - A max content width with deliberate margins only where reading line length demands it.
    - Never a tiny layout floating in a huge empty window.
    - The 4K boards show how far it scales: more columns, docked panels, now-playing that fills the screen
      gracefully.
- **Stay inside the theme.** Use its existing tokens, type ramp, components and parts exactly as the Standard
  boards do. Reuse the canvas's shared parts (title bar, nav, player, panel parts) via `<dc-import>`. Give a shared
  part a `tier` prop where its layout changes by tier, rather than copying it.
- **No layout jumps** (owner rule): Wavee must be butter smooth. For each page, the canvas needs one compact
  "breakpoints" sticky note, or a small spec board per theme, listing the thresholds between tiers and what changes
  at each. Changes animate or reserve space; they never snap.
  - Suggested thresholds: < 640 Compact, 640-1007 Small/medium, 1008-1919 Standard, ≥ 1920 Huge.
  - Check the theme's existing narrow boards and the app code, and adjust if the theme already defines tiers.
- **Minimum window size.** If Wavee's real minimum window size (look in `C:\wavee\fgpu\WaveeMusic\src\apps\Wavee`
  and `C:\wavee\fgpu\fluent-gpu\src`, read-only) is larger than 400 wide, still design Compact. Note in your report
  that the minimum must drop, with the file:line.
- Real copy only (song titles and artists consistent with the existing boards); no lorem ipsum.

## Canvas layout
- Keep every existing board where it is.
- Add one canvas page per tier group, e.g. "Sizes: Compact", "Sizes: Small", "Sizes: Huge", "Sizes: Huge 4K",
  plus the breakpoint spec. Rows by page, with a `title1` note per row group.
- Board names: `<prefix>-<Page>-Compact.dc.html`, `-Small`, `-Huge`, `-Huge4K` (e.g. `W-Album-Compact.dc.html`).

## Process (canvas type rules, binding)
- Read the canvas's `SKILL.md` first (Artifact `read`, `path: "SKILL.md"`) and follow it. Also read
  `artifact-type/reference/format.md` and `craft.md`.
- **Step 1: copy.** Your root folder already holds the theme's existing `project/` files, a copy of the old canvas.
  Read every file before publishing it (you are distributing it). Then publish:
  - first `project/canvas.json` + `project/Main.dc.html`;
  - then the rest of the existing boards, in a few batches.
  Keep `canvas.json`'s existing `title`, `pages`, `notes`, `launch` and `createdOnFiles`.
- **Step 2: add the tier boards.** Update `canvas.json` (new pages, boards, order, notes), then write and publish
  boards one at a time (write one, send it, then the next), as SKILL.md says.
- Never verify, render, screenshot or open the canvas. Never touch the old canvases. Never use the `fable` model;
  helpers, if any, use `opus`.
- Read-only on the repos: no edits, builds, runs, git writes.

## Report (≤ 30 lines)
- Board count per tier.
- The breakpoints you chose and why.
- The biggest per-tier decisions (nav, player, panels, rows, grids).
- Anything you couldn't draw.
- The minimum-window finding.
