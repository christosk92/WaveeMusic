# AGENTS.md — Wavee

Guidance for AI agents working in this repo. The complete rules are in `CLAUDE.md` (read it first); this file is
the short form for agents that do not load `CLAUDE.md` automatically.

- **What this is:** the Wavee Spotify desktop client, built on the FluentGpu engine in the **sibling checkout**
  `..\fluent-gpu` (`$(EngineRoot)` in `Directory.Build.props`). Engine rules, the UI programming model and the
  `fluentgpu` skill live there — `..\fluent-gpu\CLAUDE.md`, `..\fluent-gpu\AGENTS.md`.
- **Build & verify:** `dotnet build Wavee.slnx` (Debug), then the headless inspector
  (`docs/guide/agent-inspect.md`). No tests exist and none may be added. No CI exists; releases are local (`ops/release/release.ps1 -Version <v>`: a dry run without `-Go`).
- **PlayPlay (private, never committed here):** `src/apps/.native/**`, `src/apps/Wavee.PlayPlay/**`, `private-runtimes/**`,
  `src/apps/tmp_*`, `ops/tools/playplay_*`, `ops/tools/x64_*`, `ops/*/pyghidra*`, `docs/plans/wavee/{wavee-playplay*,playplay-*,spotiload-offline-path}.md`,
  `**/playplay-runtime.json`. Read and edit freely; commit/push only in the private `wavee-playplay-private` repo.
  Gitignored here and blocked by `.githooks/pre-commit` — never `git add -f` or `--no-verify` them.
- **Flow:** branch from and open PRs into the open release branch (the newest `release/*`), never `main` (what shipped,
  protected); one change file in `changes/unreleased/` per user-facing change instead of editing `CHANGELOG.md`; never
  edit `ContractVersion`. The whole flow: `.claude/skills/wavee-contributing/SKILL.md`.
- **Rules:** component props freeze at mount (data flows through signals/context/keys); no tests;
  no env-var switches; no legacy paths; plans carry real code; only the orchestrator builds/launches.
