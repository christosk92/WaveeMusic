# Wavee themes: shared brief (read this first)

## The owner's request (verbatim intent)

Wavee (a Spotify desktop client, `C:\wavee\fgpu\WaveeMusic`) runs on FluentGpu (`C:\wavee\fgpu\fluent-gpu`), a NativeAOT,
GPU-rendered UI engine for .NET 10. Today customization is spread out: different list/row styles, navigation styles,
sidebar versions etc. The owner calls it "messy and confusing" with "hardly any user value".

They want **themes**, where a theme changes *everything* that is presentation: pages look different, the player looks
different, navigation differs. "A theme on top of the core logic."

1. **Wavee Classic**: what production has now. Stays as-is visually.
2. **WinUI**: strict Windows 11 / WinUI 3 / Fluent 2. Starting point canvas: https://claude.ai/artifact/Na29w2SNpdMKiHHiRxCUru
   (an audit of where Wavee breaks Fluent + ground-up Home/Album/Artist/Library boards).
3. **Zune**: the Zune desktop software look (Metro: lowercase pivots, big light Segoe type, panorama, accent pink).
   Starting point canvas: https://claude.ai/artifact/YZ7exWb1x2K9vPsxhZn2jf (rows, album/artist/liked/playlist/queue/search).
4. **Community themes** people can install.

Each theme has **its own options, defined by the theme** (e.g. Classic keeps "row style classic/modern" and
"sidebar v3/classic"). The source code must NOT be tied to specific themes, because custom themes must be supported.

Hard requirements from the owner:
- Fast, smooth, clean. Engine changes are allowed. **No hacks: no `if (theme == Zune)` switches sprinkled in components.**
- **No flash/flicker**: never paint one frame in the wrong theme (startup, theme switch, page navigation).
- No legacy code left behind, no unhandled edge cases.
- Wavee UI must never jump/relayout suddenly (reserve heights or animate). Production motion choreography is deliberate.

## Repos (read-only for this phase: do NOT edit, build, commit or run anything that changes state)

- App: `C:\wavee\fgpu\WaveeMusic` (main, up to date). App source: `src/apps/Wavee/` (Shell/, Screens/, Entities/, Home/, Playback/ ...).
- Engine: `C:\wavee\fgpu\fluent-gpu` (main). Source: `src/FluentGpu.Engine`, `src/FluentGpu.Controls`, `src/FluentGpu.SourceGen`, ...
- In-flight, NOT on main: PR #205 `feat/shell-cohesion` in worktree `C:\WAVEE\sidebar\WaveeMusic` (+ engine `C:\WAVEE\sidebar\fluent-gpu`)
  reworks the shell (sidebar, Zune nav band/pivots/row 2, breadcrumbs, Liked Songs, detail-page bars). Note where it matters,
  but map main as the baseline.
- Never touch `wavee-playplay-private*` / `Wavee.PlayPlay` (private DRM code); it is irrelevant here.
- Never run Wavee or any `--spotify-*` probe. Never kill processes.

## Output

Write your report as Markdown to the path given in your prompt (scratchpad `themes\` folder). Be concrete: file paths with
line numbers, type and member names. Facts over prose. Your final message: a 10-20 line summary plus the report path.
