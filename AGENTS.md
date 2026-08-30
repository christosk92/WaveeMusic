# AGENTS.md — Wavee

Guidance for AI agents working in this repo. The complete rules are in `CLAUDE.md` (read it first); this file is
the short form for agents that do not load `CLAUDE.md` automatically.

- **What this is:** the Wavee Spotify desktop client, built on the FluentGpu engine in the **sibling checkout**
  `..\fluent-gpu` (`$(EngineRoot)` in `Directory.Build.props`). Engine rules, the UI programming model and the
  `fluentgpu` skill live there — `..\fluent-gpu\CLAUDE.md`, `..\fluent-gpu\AGENTS.md`.
- **Build & test:** `dotnet build Wavee.slnx` (Debug **and** `-c Release`), `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj`,
  `Invoke-Pester -Path ops/release/tests`. No CI exists; releases are local (`ops/release/wavee-release.ps1`).
- **Out of scope (never read/edit):** `src/apps/.native/**`, `src/apps/Wavee.PlayPlay/**`, `private-runtimes/**`,
  `src/apps/tmp_*`, `ops/tools/playplay_*`, `ops/tools/x64_*`, `ops/*/pyghidra*`, `docs/plans/wavee/{wavee-playplay*,playplay-*,spotiload-offline-path}.md`,
  `**/playplay-runtime.json` — the private PlayPlay workspace, fenced by `.githooks/pre-commit`.
- **Rules:** component props freeze at mount (data flows through signals/context/keys); no source-text tests;
  no env-var switches; no legacy paths; plans carry real code; only the orchestrator builds/tests/launches.
