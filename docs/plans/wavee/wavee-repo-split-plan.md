# Wavee → `christosk92/WaveeMusic` repo split

**Decided 2026-08-29.** Wavee (app, core, SDK, modules, tests, release tool, release tooling, docs, skills) moves
out of `fluent-gpu` into the existing `WaveeMusic` repository. The old WaveeMusic code is deleted (history kept —
the repo is not recreated). WaveeMusic references the engine as a **sibling checkout by relative path**
(`C:\wavee\fluent-gpu` next to `C:\wavee\WaveeMusic`); there is **no GitHub build** for Wavee anywhere — releases
are cut locally with `ops/release/wavee-release.ps1`. Executed only after the auto-update work is green in
`fluent-gpu` (build Debug+Release, `Wavee.Tests`, VerticalSlice, Pester, `-DryRun` rehearsal).

## 1. Target layout (WaveeMusic)

```
C:\wavee\
├── fluent-gpu\                       engine repo (unchanged layout; Wavee removed from it)
│   └── src\FluentGpu.{Engine,Controls,Windows,WindowsApi,SourceGen}\
└── WaveeMusic\                       ← everything below is copied 1:1 from fluent-gpu, same relative shape
    ├── Wavee.slnx                    Wavee + Wavee.Core + Wavee.Sdk + Wavee.Tests + Wavee.ReleaseTool + modules + NVorbis
    │                                 + the FIVE engine projects by path ..\fluent-gpu\src\FluentGpu.*.csproj
    ├── Directory.Build.props         = today's src/apps/Directory.Build.props (already engine-independent)
    ├── global.json                   pins the SDK the engine builds with (11.0.100-preview.4, rollForward latestFeature)
    ├── NuGet.config                  as fluent-gpu's (TerraFX feed etc.)
    ├── src\apps\{Wavee,Wavee.Core,Wavee.Sdk,Wavee.Tests,Wavee.ReleaseTool,modules,vendor}\   (path kept: `..\..\FluentGpu.*` → `..\..\..\fluent-gpu\src\FluentGpu.*`)
    ├── src\apps\Wavee.PlayPlay       junction → C:\WAVEE\wavee-playplay-private\app\Wavee.PlayPlay (gitignored, per checkout)
    ├── ops\build\{pack-wavee-msix.ps1, publish-wavee-aot.ps1, publish-wavee-modules.ps1, generate-third-party-notices.ps1,
    │             Wavee.Build.psm1 (copy), Wavee.AppxManifest.xml, Wavee.AppInstaller.template.xml, notices-extra.json,
    │             pack-module.ps1, bench-wavee.*, publish-wavee-aot*.cmd, signing\metadata.template.json, README.md (Wavee part)}
    ├── ops\release\**                orchestrator, module, tests, feed body, wavee\<semver>\whatsnew.json
    ├── ops\tools\playready-native    FluentGpu.PlayReady.Native (Wavee-only C++; Wavee.csproj builds it)
    ├── CHANGELOG.md  PRIVACY.md  THIRD-PARTY-NOTICES.txt  link-playplay.ps1 (untracked helper — copy by hand)
    ├── docs\guide\{releasing-wavee, playback-modules, playplay-private-split, sidebar-extension-platform}.md
    ├── docs\plans\wavee\**           (incl. the auto-update plan/dossier/prototype/implementation)
    ├── .claude\skills\{wavee, wavee-sidebar, wavee-native?*, releasing (Wavee half)}   *wavee-native documents FluentGpu.WindowsApi → stays in fluent-gpu
    ├── .githooks\pre-commit          the PlayPlay guard (unchanged); `git config core.hooksPath .githooks`
    ├── .gitignore                    fluent-gpu's + `!ops/release/`
    └── CLAUDE.md / AGENTS.md         rewritten for the app repo (engine rules by reference to ..\fluent-gpu)
```

Why the `src\apps\` shape is kept: every script computes `$root` from `$PSScriptRoot\..\..` and every csproj
path is relative to `src\apps\<proj>`; keeping the shape means the only edits are the four engine references
and the `$root`-relative engine path in nothing else (scripts never touch the engine tree).

## 2. Edits that are NOT a plain copy

| File | Change |
|---|---|
| `src/apps/Wavee/Wavee.csproj` L89-92, L198 | `..\..\FluentGpu.X\` → `..\..\..\fluent-gpu\src\FluentGpu.X\` (Engine, Controls, Windows, WindowsApi, SourceGen analyzer ref) |
| `src/apps/Wavee.Tests/Wavee.Tests.csproj` L89, L512 | same for WindowsApi + SourceGen |
| `Wavee.slnx` (new) | the Wavee projects + the five engine projects by relative path (so IDE + `dotnet build Wavee.slnx` work) |
| `Directory.Build.props` (root) | = `src/apps/Directory.Build.props`; add `<EngineRoot>$(MSBuildThisFileDirectory)..\fluent-gpu\</EngineRoot>` and use `$(EngineRoot)src\FluentGpu.X\` in the csprojs; fail fast with a clear `<Error>` if `$(EngineRoot)src\FluentGpu.Engine\FluentGpu.Engine.csproj` is missing ("clone fluent-gpu next to WaveeMusic") |
| `src/apps/Wavee/App/AppInstallerUpdateService.cs`, `Features/ReleaseNotes/ReleaseNotesText.cs` (`RepoUrl`), `Features/Shell/SettingsPage.About.cs` (FeedbackUrl/WebsiteUrl/PrivacyUrl), `ops/release/wavee-release.ps1` (`-Repo` default), `ops/release/wavee/README.md`, `docs/guide/releasing-wavee.md`, `ops/release/feed-release-body.md`, `PRIVACY.md` | `christosk92/WaveeMusic` → `christosk92/WaveeMusic` (the feed URL is baked into every package — do this BEFORE the first release) |
| `ops/build/Wavee.Build.psm1` | copied; the gallery keeps its own in fluent-gpu (two copies by design — different repos) |
| `.claude/skills/releasing/SKILL.md` | fluent-gpu keeps the gallery half; WaveeMusic gets the Wavee half |
| `CLAUDE.md` (WaveeMusic) | app-repo instructions: engine at `..\fluent-gpu` (read its CLAUDE.md for engine rules), no CI, local release runbook, out-of-scope PlayPlay paths (same fence), no source-text tests, no env switches |

## 3. Procedure (all local; nothing builds on GitHub)

```
A. fluent-gpu, on a branch `split/wavee-out`:
   1. git mv nothing — COPY (robocopy /MIR of the paths in §1 into C:\wavee\WaveeMusic\, excluding bin/obj/.msix-build/artifacts and the junction)
   2. git rm -r src/apps ops/release ops/tools/playready-native ops/build/{Wavee.*,pack-wavee-msix.ps1,publish-wavee-*,generate-third-party-notices.ps1,notices-extra.json,pack-module.ps1,bench-wavee.*}
              CHANGELOG.md PRIVACY.md THIRD-PARTY-NOTICES.txt docs/plans/wavee docs/guide/{releasing-wavee,playback-modules,playplay-private-split,sidebar-extension-platform}.md .claude/skills/{wavee,wavee-sidebar}
   3. src/FluentGpu.slnx: drop the 8 Wavee entries; .github/workflows/build.yml: drop the Wavee build/test/smoke/AOT steps (L35-115) and every -p:WaveeSkipPrivateSources;
      CLAUDE.md: remove the Wavee sections (keep the out-of-scope fence for safety), releasing skill: gallery only; ops/build/README.md: gallery only; docs/guide/README.md index.
   4. dotnet build src/FluentGpu.slnx (Debug+Release) + VerticalSlice → green; commit "split: move Wavee to christosk92/WaveeMusic".
B. WaveeMusic (C:\WAVEE\WaveeMusic — NOTE the old clone is dirty: 86 paths; stash nothing, `git status` first and decide):
   1. git checkout -b main (or reuse; the current default branch is release/0.1.2-alpha — set `main` as default on GitHub afterwards)
   2. commit 1 "wipe: retire the WinUI-era codebase" — `git rm -r --cached . && del everything except .git` (history stays)
   3. commit 2 "import: Wavee from fluent-gpu@<sha>" — the robocopy from A.1 + the §2 edits; recreate the PlayPlay junction (link-playplay.ps1 copied by hand, or the CLAUDE.md recipe)
   4. git config core.hooksPath .githooks
   5. Verify: dotnet build Wavee.slnx -c Debug and -c Release; dotnet test src/apps/Wavee.Tests; powershell -File ops\release\wavee-release.ps1 -DryRun -SkipTests
      (both arches, Trusted-Signed, feed docs pointing at github.com/christosk92/WaveeMusic/releases/download/wavee-stable/…); install the arm64 msix.
   6. push main; set default branch = main; delete stale branches only if the user says so.
C. First real release from WaveeMusic: `wavee-release.ps1` (creates wavee-v0.2.0 + the wavee-stable feed release there).
```

## 4. Things that deliberately stay in fluent-gpu
`FluentGpu.WindowsApi/Packaging/*` (the `PackageUpdater` interop is engine-side OS service), the gallery release
pipeline (`msix.yml`, `pack-msix.ps1`, `AppxManifest.xml`, `AppInstaller.template.xml`), `wavee-native` skill,
`ops/diag`, the VerticalSlice. WaveeMusic consumes them through the relative ProjectReferences.

## 5. Open points to confirm at execution time
- The dirty state of `C:\WAVEE\WaveeMusic` (86 paths): anything worth keeping before the wipe?
- SDK pin: fluent-gpu has no `global.json`; the pack scripts picked SDK 11.0.100-preview.4 from PowerShell and 10.0.300 from bash — WaveeMusic should pin one (`global.json`, `rollForward: latestFeature`) so a local `dotnet build` and the release script agree.
- The `.claude/worktrees/agent-*` folder inside fluent-gpu is stale scratch from an earlier session and is not part of the move.
