# Wavee theme planning handoff

Planning snapshot: 2026-10-11. Implementation has not started. The owner requested a draft PR containing the final plans, reviews, decisions and boards, and explicitly asked to stop after opening it.

Start with [the technical plan](plan/technical-plan.md). Revision 6 schedules 176 PRs across the app and engine. Four review rounds and the targeted revision-6 check found it ready for owner approval. Publishing this documentation draft does not approve implementation.

## Package

- `plan/technical-plan.md`: architecture, phase catalogue, dependencies, tests, decisions and review dispositions.
- `plan/owner-decisions-2026-10-11.md`: binding decisions, including the accepted follow-up defaults.
- `plan/plan-review*.md` and `plan/plan-check-r6.md`: review evidence. Findings describe the revisions reviewed; consult the final plan for their disposition.
- Other top-level `plan/*.md`: brief, architecture maps, Classic specifications and research summaries.
- `boards/SIZES-BRIEF.md`: size-tier design brief.
- `boards/{classic,winui,zune,panorama}/project/`: one consolidated snapshot per theme, including the short-window board, shared parts and `canvas.json`.

Historical plan revisions, downloaded third-party source, helper scripts and superseded canvas snapshots are excluded. References to those files in the plan and reviews are historical provenance, not files shipped in this package.

## Canvas status and provenance

| Theme | Final canvas | Indexed boards |
|---|---|---|
| Classic | https://claude.ai/artifact/Dkyq1kj5KGeZ3F5zdUyGqM | 166 |
| WinUI | https://claude.ai/artifact/3RQwch7Vo6QgPqFzzgX2dD | 159 |
| Zune | https://claude.ai/artifact/DJ6eBgpGoeDivZ3TgVkoi1 | 180 |
| Panorama | https://claude.ai/artifact/EyUvqRXxDLWaEj7rZ83MrP | 143 |

The prior session reports that all final canvases reside in the original owner account. WinUI migration completed with 160 files (159 indexed boards plus `canvas.json`). The duplicate WinUI canvas, https://claude.ai/artifact/GeUyyTo6KLKfWHyzKqLkAt, remains because deletion was interrupted. No deletion is part of this PR.

These snapshots combine the durable handoff with the original session's surviving final local files. Classic includes the short board and the final sidebar default; Zune includes the followed-playlist variants and all Small/Huge dialogs. Short boards are consolidated into their theme project rather than accompanied by older partial indexes. Every indexed board exists, and every local `dc-import` resolves. Live canvas contents were not independently rechecked during packaging. Verify the live canvas before implementing each tier family.

The canvas wins over older written design decisions where specified by NQ8. Later owner decisions override superseded canvas text; record each conflict in the relevant implementation PR.

## Decisions accepted for the draft

The owner accepts all four follow-up defaults:

| Decision | Accepted behavior |
|---|---|
| NQ9 | Minimum height 330 DIP. TC1 checks quarter-Snap on a real 1080p/150% display; use 328 if the bottom border counts. |
| NQ10 | Zune and Panorama retain the 3200 Huge 4K edge. Revised Auto zoom reaches it only on sufficiently wide displays; ordinary 16:9 4K monitors require Manual zoom. |
| NQ12 | Classic follows directional holds at its edges. A fresh launch resolves by the edge value. |
| NQ13 | Revised zoom and resize rules override the older Classic board text. TC1 lists both conflicts. |

D1 is now specified in plan section 3.3 and the EP22 test cases: storing a pending tier must wake a frame and count as work in the modal-loop idle decision. A `RunFrame` test verifies scheduling rather than bypassing it with direct `Paint` calls. D2 accepts two old-tier frames on a zoom change. D3 requires TC2 to model Classic Huge's two docked panels.

## Next implementation phase, after explicit approval

Start P0 only, then assess the cost per PR before continuing:

1. H0: headless shell host in a dedicated `Wavee.ShellTests` process; settings test seams are folded in.
2. LP1: pin Live-option behavior on the mounted shell before any views move; depends on H0.
3. G2: theme analyzers.

P1 follows with the engine work, including EP22's pending-tier commit. App and engine implementation belongs in their respective repositories, in branches on existing worktrees. Keep implementation PRs within the plan's 3,000-line limit; this archival documentation package is larger and changes no production behavior.

Run Debug and Release builds and tests for implementation changes; engine changes also require VerticalSlice. Add the new shell-test commands to the workspace instructions once that project exists, respecting the owner's instruction-file rules. Keep private companion code out of public repositories, avoid live-profile probes and stop only processes started for the task.

## Known design limits

- Some Classic Huge copy uses bracketed placeholders.
- WinUI's prior switch audit covered all Huge boards, but other boards may still need checking.
- Zune's followed Huge playlist omits the owner-only recommended column.
- Panorama's short-window ruler still names a 340 minimum; implementation uses the accepted 330/328 rule. One followed-playlist board invents song counts, and Video/Setup Standard use 1440 x 960 rather than Zune's 1440 x 900.
- Earlier review inventories can differ from the consolidated counts above. Use the packaged indexes when planning a tier PR, then compare with the live canvas.

No application build or test run is required to validate this documentation snapshot. Packaging checks cover JSON parsing, indexed-file coverage, local component imports and the final documentation readback; they do not establish visual correctness or implement the plan.
