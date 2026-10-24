# Owner decisions on the technical plan's open questions (§10), 2026-10-11

Answers to `technical-plan.md` revision 3, §10. These override the plan's defaults where they differ.

## Changes that reshape the plan

- **PR #205 is discarded (Q3).** It is an old branch; the themes work starts from scratch, cleanly. Drop the R205-E/R205-1..5
  split, `themes/zune-seed` and the 95-commit table (§2.1). Zune is built from its canvas, not from #205 commits.
  The approved Classic designs that lived in #205 (Liked Songs, Play/Shuffle split, chips, Filter flyout, breadcrumb B)
  are assumed to be rebuilt fresh as tuned-Classic items (not confirmed by the owner).
- **No pixel-identity / golden-frame work (Q23).** Drop the P0 golden harness (G1a-e, ~460 scenarios, baselines,
  `--golden*` CLI) and the "Classic pixel-identical to main" gate. Q31's "Classic reference frames" piece goes with it.
- **Q17, Q24 are moot** (they were about #205 commits `8002d28f`, `d1af8eb2`).
- **Q36 resolved:** the Liked/Detail-Bar concept board https://claude.ai/artifact/NDBcNd34ZF7AuzCnsWwH2r now opens;
  a copy is in `..\live\calm-discover-concept\`.

## Answers

| Q | Decision |
|---|---|
| Q1 | One `Wavee.csproj` + analyzer fences (default) |
| Q2 | Answered by the Panorama canvas (default) |
| Q3 | **Discard PR #205; start from scratch** |
| Q4 | WinUI "Tabs in the title bar" Off: hide the strip, keep the tabs for when it's turned On again (default) |
| Q5 | Zune keeps both: right-click Back history menu + Friends as a right-panel pivot (default) |
| Q6 | Pane/panel sizes saved per theme (default) |
| Q7 | Accept the split: content prefs in every theme, the listed knobs Classic-only (default) |
| Q8 | **Design WinUI narrow-window + player tiers first** (a tier board before the WinUI player PRs) |
| Q9 | Recovery dialog OS-native everywhere + "Reset appearance" (default) |
| Q10 | Theme switch: freeze, settle, 250 ms dissolve (default) |
| Q11 | High contrast out of scope, slot reserved (default) |
| Q12 | **Keep the startup migration until the next minor release** (not R+1) |
| Q13 | Settings › Appearance without a community-themes tile (default) |
| Q14 | Utility pages (diagnostics, logs, history, not found, customize, module pages): shared plain views (default) |
| Q15 | No alternative (default) |
| Q16 | **All full-screen layouts in every theme**: Card, Large art, Centered, Artist restyled per theme (WinUI + Zune) |
| Q17 | Moot (#205 discarded) |
| Q18 | Undesigned content pages built from the theme's parts, reviewed at phase exit (default) |
| Q19 | Zune shows the core App mode in its options (default) |
| Q20 | Panorama shows Background + Now Playing options, defaults None / Spotlight (default) |
| Q21 | **Mosaic / Type / Spotlight become faces available in every theme** (not Zune-only) |
| Q22 | Panorama art rows 52 px, one counted override (default) |
| Q23 | Moot (#205 discarded; no pixel identity) |
| Q24 | Moot (#205 discarded) |
| Q25 | Theme switch shows the new theme settled; entrances not replayed (default) |
| Q26 | **Carry nothing across a theme switch**: page UI state resets like a fresh navigation |
| Q27 | Marquee + now-playing presentation work in every theme (default) |
| Q28 | Panorama may declare its own options as counted exceptions (default) |
| Q29 | **No download states; show cached tracks with the "downloaded"-style indicator instead** (there is no true download) |
| Q30 | **Zune Browse row 2 by discovery: top · for you · genres · charts · concerts** |
| Q31 | Temporary pieces allowed with expiries (default; Classic reference frames dropped with the golden harness) |
| Q32 | Translucent→translucent switches: one backdrop step at the start of the dissolve (default) |
| Q33 | Classic "Surfaces" defaults to Subtle (default) |
| Q34 | Sidebar default width stays 320 (default) |
| Q35 | Build the D-10 list bar (default) |
| Q36 | Resolved (board readable) |
| Q37 | **Remove the lyrics blur on/off setting entirely; keep only the blur intensity setting** (assumed: honoured by every theme) |
| Q38 | **Build all six**: F4, D20, D21, Y1, L2, T2 |

## Plan sections to revise

- §2.1 (PR 205 split), §2.1.2 (commit table), §6.6.3 attributions: remove; re-derive Classic items from the tuned canvas.
- §3 (golden harness) and every golden/rebaseline gate in §2.5, §4, §6: remove.
- §5.6 / E17 transaction-scoped host-state carry-over (Q26): remove; a switch resets page UI state. Settle still applies.
- §6.2 W4b, §6.3 Z4b: add Centered + Artist stage layouts (Q16); add Mosaic/Type/Spotlight as core faces (Q21).
- WinUI phase: add a tier/narrow design board before W4a (Q8).
- §7 bootstrap expiry → next minor (Q12); lyrics blur toggle removal migration (Q37); Zune Browse row-2 data (Q30);
  cached-as-downloaded indicator in Zune offline/error states (Q29).

## Revision 4 follow-ups (answered 2026-10-11, apply in revision 5)

| Q | Decision |
|---|---|
| Q37 | Confirmed: remove Auto (-1) from the lyrics blur setting; keep the 0-100 intensity (0 = off); stored -1 reads as 100 |
| NQ1 | Clamp Auto zoom so a window keeps its unzoomed size tier; Manual zoom stops before the Compact floor (default) |
| NQ2 | Minimum window height 340 DIP (default) |
| NQ3 | **Same face everywhere**: Bloom in every theme until the user picks one (NOT per-theme starting faces) |
| NQ4 | "On this PC" indicator only; no offline playback (default) |
| NQ5 | **WinUI full-screen lyrics may blur as drawn** (using the blur intensity); other themes keep no blur |
| NQ6 | Accept all four Classic defaults: show covers On; followed playlists show Insights only as the closed sheet; second sidebar toggle reverses mid-way; washes-off users keep Accent from artwork On |

## Round-3 review follow-up (answered 2026-10-11, apply in revision 5)

| Q | Decision |
|---|---|
| NQ2 (revised) | Minimum window height **330 DIP** (supersedes 340): R3-M7 showed that 340 plus the window frame blocks the 1080p/150% quarter-snap it was chosen for |

## Revision 5 follow-ups (answered 2026-10-11, apply in revision 6)

| Q | Decision |
|---|---|
| NQ7 | Accept: apply NQ1 as answered; big screens get the smaller Auto zoom (4K @100 % → 1.25, ~3072 DIP, Huge 4K layouts) |
| NQ8 | Canvas wins over older #205-era written decisions; the plan lists each conflict it finds so the owner can overrule individually |
| Panorama pages | Draw the ~13 brief pages Panorama never had (Browse category, Discography, Playlist followed, Recents, Profile, Show, Episode, Concert, Video, Setup, Release notes, Feedback, Library Playlists/Podcasts) at Standard + Compact/Small/Huge |

## Round-4 review follow-ups (answered 2026-10-11, apply in revision 6)

| Q | Decision |
|---|---|
| NQ7 / NQ1 (revised) | **Clamp only out of Standard**: Auto zoom keeps today's smooth curve, except it never pushes a Huge-sized window back down into Standard. Zoom never jumps during a resize (monotonic, per `ZoomAutoPolicy`'s contract). Supersedes the earlier "keep the unzoomed tier" clamp and the NQ7 acceptance |
| Short boards | One short-window board per theme (shell at 800×330) is being drawn; keep it minimal |
| Classic sidebar covers | Still open with the owner: whether the shared C-Sidebar part defaults to covers On |

## Documentation draft follow-ups (answered 2026-10-11)

| Question | Decision |
|---|---|
| Package location | Publish final plans, reviews, decisions and all design boards in WaveeMusic `docs/themes` |
| Package scope | Exclude historical revisions, downloaded research source, temporary helpers and superseded canvas copies |
| NQ9 | Keep minimum height 330 DIP; TC1 checks Snap on a real 1080p/150% display and uses 328 if the border counts |
| NQ10 | Accept Huge 4K reachability under the revised zoom rule; Zune and Panorama keep their 3200 edge |
| NQ12 | Follow Classic's directional holds; a fresh launch resolves by the edge value |
| NQ13 | Revised NQ1 and plan ?3.3 rule 4 override superseded board text; TC1 records both conflicts |
| Classic sidebar covers | On by default, including the shared sidebar part; published in the previous session |
| After opening the draft | Stop. Implementation and P0 remain unstarted |
