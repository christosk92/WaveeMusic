# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

**Wavee** — a Spotify desktop client for Windows, built on the **FluentGpu** engine (a from-scratch NativeAOT,
GPU-rendered UI engine for .NET 10). This repo holds the app only: `src/apps/{Wavee, Wavee.Core, Wavee.Sdk,
Wavee.ReleaseTool, modules/*, vendor/*}`, the release tooling (`ops/`), the app docs
(`docs/guide`, `docs/plans`) and the app skills (`.claude/skills`). It was split out of the engine repo on
2026-08-30 (`docs/plans/wavee/wavee-repo-split-plan.md`); history before that commit is the retired WinUI-era app.

**The engine is a sibling checkout, not a package.** `Directory.Build.props` resolves it as
`$(EngineRoot)` = `..\fluent-gpu\` (`C:\wavee\fluent-gpu` next to `C:\wavee\WaveeMusic`), and every engine
`ProjectReference` goes through that property. Clone `https://github.com/christosk92/fluent-gpu` beside this repo
first; the build fails with one clear sentence otherwise. **Engine rules live in the engine repo** — read
`..\fluent-gpu\CLAUDE.md` and use its `fluentgpu` skill (`..\fluent-gpu\.claude\skills\fluentgpu\SKILL.md`) for
how to build UI (signals, hooks, `Element` records, motion, the zero-alloc discipline). Engine changes are made
in `..\fluent-gpu` and verified there (`dotnet build src/FluentGpu.slnx`).

**There is no CI.** Nothing builds on GitHub: releases are cut locally by `ops/release/wavee-release.ps1`
(both arches, Azure Trusted Signing, uploaded with `gh`). The `wavee-stable` feed and `wavee-v*` tags live on
**this** repository (`github.com/christosk92/WaveeMusic`).

## PlayPlay: in scope to work on, committed only to its private repo

The PlayPlay (Spotify DRM) derivation lives in the separate **private** repo `christosk92/wavee-playplay-private`
(`C:\WAVEE\wavee-playplay-private`, `docs/guide/playplay-private-split.md`). Agents may read, search, and edit it
like any other code. The one rule: **it never enters this public repo** — commit and push PlayPlay work only in
`wavee-playplay-private` (`git -C C:\WAVEE\wavee-playplay-private …`). These paths are private material:

- `src/apps/.native/**`, `src/apps/Wavee.PlayPlay/**`, `private-runtimes/**`
- `src/apps/tmp_*`, `ops/scripts/pyghidra*`, `ops/tools/pyghidra*`, `ops/tools/playplay_*`, `ops/tools/x64_*`
- `docs/plans/wavee/wavee-playplay*.md`, `docs/plans/wavee/playplay-*.md`, `docs/plans/wavee/spotiload-offline-path.md`
- `**/playplay-runtime.json`

They are gitignored here and `.githooks/pre-commit` blocks them from entering the tree
(`git config core.hooksPath .githooks` once per clone) — never `git add -f` them or bypass the hook with
`--no-verify`. `src\apps\Wavee.PlayPlay` is a **per-checkout junction**
to `C:\WAVEE\wavee-playplay-private\app\Wavee.PlayPlay` (`.\link-playplay.ps1`, untracked helper); a checkout
without it still builds — the public-only variant — which is why the release script asserts the junction unless
`-PublicOnly` is passed.

## Commands

```powershell
dotnet build Wavee.slnx                                   # Debug — the app, pulling in the engine via $(EngineRoot)
dotnet run --project src/apps/Wavee -- --fake             # offline FakeData demo (no login / network)
powershell -File ops/release/tests/local-update-e2e.ps1 -Scenario inapp   # elevated; the real update path over a loopback feed
powershell -File ops/release/tests/local-update-e2e.ps1 -Scenario os      # elevated; App Installer's silent on-launch update
powershell -File ops/release/wavee-release.ps1 -DryRun -SkipTests         # release rehearsal; the real thing without -DryRun
```

There are no tests (banned 2026-10-20; the build and the pre-commit hook reject new ones). Verify UI with the headless
inspector (`docs/guide/agent-inspect.md`: `render`, `trace`, `crossing`, `lint`).

Before claiming done: a clean Debug build (`TreatWarningsAsErrors`) and the change rendered and traced in the
inspector with the screenshots looked at (for engine-touching work, a clean Debug build of `..\fluent-gpu` too). The
Release build happens only when releasing. Packaged runs write into the package's
`LocalCache` — never leave a real `%LOCALAPPDATA%\Wavee` (an unpackaged `dotnet run` creates it) around when
testing a packaged build; the E2E harness wipes it and the startup log line prints `logResolved=` for exactly
this reason.

## Working rules

- **Component props freeze at mount.** `Embed.Comp(() => new T { Field = value })` runs once; changing data
  reaches a child only via a `Signal`/`Func`, `Ctx.Provide`+`UseContext`, or a remount through `Key`
  (`..\fluent-gpu\docs\design\subsystems\component-props-contract.md`; `ReuseGuard` catches the mistake).
- **No tests, ever.** No test projects or files, no `dotnet test`; the build and the pre-commit hook reject them.
- **No environment-variable switches** for behaviour or verification: always-on logs, the diagnostics page, gates.
- **No legacy paths.** Replace outright; delete obsolete code. Breaking is acceptable.
- **Every fix references its issue.** The CHANGELOG bullet ends with ` (#n)`, the commit body carries `Fixes #n`,
  and the release script's `issue refs` gate refuses a mismatch between the two (→ `github-triage` skill).
- **Plans with real code.** Multi-part work gets a plan in `docs/plans/wavee/*-implementation.md` with the actual
  code, component trees and ASCII wireframes; implementation is parallel subagents on disjoint files, and only the
  orchestrator builds/launches.
- App skills: `.claude/skills/wavee` (deep links, backend, diagnostics), `wavee-sidebar` (the sidebar: layouts, modes,
  Edit mode, pins), `releasing` (the release runbook + gotchas), `github-triage` (labels, milestones, the project board,
  issue forms — every modifying `gh` call is approved by the user first). Sidebar: `docs/guide/sidebar.md`; playback
  modules: `docs/guide/playback-modules.md`; releasing: `docs/guide/releasing-wavee.md`; scrolling:
  `docs/guide/scrolling.md`; the full-screen stage and its visualizers: `docs/guide/fullscreen-visualizers.md`.
