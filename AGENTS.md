# Wavee Agent Docs

Component-specific agent docs live in `.agents/guides/`. Keep this file as a
small **index** so agents can find the right focused guide without loading every
component note by default. The detailed inventories belong in `.agents/guides/`,
not here.

## Index

Read the relevant guide before changing that area:

- **Track and episode UI** — every track/episode row, list, card, search cell,
  omnibar suggestion, queue row, home episode card, and now-playing surface.
  `.agents/guides/track-and-episode-ui.md`

## How To Add A New Component Guide

1. Create `.agents/guides/<component-name>.md`.
2. Open the file with this frontmatter so every guide is uniformly machine-readable:

   ```
   ---
   guide: <component-name>
   scope: <one sentence — what this guide covers>
   last_verified: <YYYY-MM-DD>
   verified_by: <how, e.g. "read+grep over src/...">
   root_index: AGENTS.md (Codex) and CLAUDE.md (Claude Code)
   ---
   ```

3. Keep the guide scoped to one subsystem. Include, in order:
   - Scope (included / excluded surfaces).
   - A **Quick-find table** keyed by user-visible surface, with `file:line` host
     references, DTOs, and source bindings.
   - Shared controls / contracts (the things you'd edit to change every surface
     at once).
   - Per-surface notes for the non-obvious sites.
   - Change guidance — "if you want X, edit Y".
   - A short "keeping this guide current" section.

4. Add the guide to the **Index** in this file.
5. Add the same line to the *Agent component docs* section in `CLAUDE.md` so
   Claude Code picks it up too.
6. Re-verify and update `last_verified` whenever you touch the inventoried area.

## How This Pairs With CLAUDE.md

- `AGENTS.md` (this file) is the entry point Codex reads automatically.
- `CLAUDE.md` is the entry point Claude Code reads, and it mirrors the same
  index pointing at the same `.agents/guides/` files.
- General repository guidance (build commands, architecture, conventions) lives
  in `CLAUDE.md` — don't duplicate it here. This file stays a small index.
