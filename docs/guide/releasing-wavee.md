# Releasing Wavee (signed MSIX)

The runbook for shipping the **Wavee** music client. Wavee and the FluentGpu gallery are two products in one tree and
release **independently** — and by two different mechanisms:

| | Gallery | Wavee |
|---|---|---|
| Trigger | push tag `v*` → `.github/workflows/msix.yml` | **run `ops/release/release.ps1 -Version <v> -Go` on this machine** |
| Tag prefix | `v*` | **`wavee-v*`** |
| Script | `ops/build/pack-msix.ps1` | `ops/release/release.ps1` + `ReleaseRunner.psm1` (which call `ops/build/pack-wavee-msix.ps1` per arch) |
| Manifest | `ops/build/AppxManifest.xml` | `ops/build/Wavee.AppxManifest.xml` |
| `.appinstaller` template | `ops/build/AppInstaller.template.xml` | `ops/build/Wavee.AppInstaller.template.xml` |
| Package identity | `MarTeco.FluentGpu` | **`cproducts.Wavee`** |
| Update feed | `releases/latest/download/FluentGpu.<arch>.appinstaller` | the rolling **`wavee-stable`** release |

**There is no CI release job for Wavee.** A CI checkout has no `src/apps/Wavee.PlayPlay` junction, so it would silently
publish the public-only variant of a build that is supposed to be PlayPlay-inclusive. Releases are cut by hand, from
this arm64 box, with both architectures built locally (x64 through the MSVC `HostArm64\x64` cross toolchain).

Wavee ships as a **NativeAOT packaged-Win32 full-trust MSIX**: no bundled .NET runtime, no WindowsAppSDK. The MSIX
manifest is what makes `wavee://` deep links, toast activation and `packageManagement` (the in-app updater) work
against a *registered* identity rather than the HKCU shim the unpackaged build falls back to — so the packaged build
is the one that must be smoke-tested, never just `dotnet run`.

Signing (Azure Trusted Signing) is shared with the gallery — see the `releasing` skill
(`.claude/skills/releasing/SKILL.md`) for the config table and every signing gotcha.

---

## 0. What a release actually is

Four artefacts, three of them permanent:

| Thing | Value | Lifetime |
|---|---|---|
| **Version tag** | `wavee-vX.Y.Z` (annotated, on the release commit of `release/X.Y.Z`, which then merges into `main`) | immutable, never deleted |
| **Version release** | the GitHub release on that tag — the `.msix` packages + the notes assets | immutable |
| **Rolling feed release** | **`wavee-stable`** — carries `Wavee.arm64.appinstaller`, `Wavee.x64.appinstaller` and `whatsnew-index.json`, replaced with `--clobber` on every release | **the anchor: created once, never renamed, never deleted** |
| **Codename** | one per **MINOR** (the sea series: Abyss 0.1, Breaker 0.2, Crest 0.3, Drift 0.4, Ebb, Fetch, …); patches inherit it | per minor |

Every installed copy of Wavee has the *feed* URL baked into its package by Windows:

```
https://github.com/christosk92/WaveeMusic/releases/download/wavee-stable/Wavee.<arch>.appinstaller
```

Deleting or re-tagging `wavee-stable` orphans every install permanently — read `ops/release/feed-release-body.md`
before touching that release. `releases/latest` is **not** the Wavee feed: that endpoint is repo-global and belongs to
the gallery, which versions independently under `v*`.

**The version numbers.** Windows compares exactly one thing: the MSIX `Identity/@Version` **quad**
`M.m.p.WaveeBuild`. `WaveeBuild` is a committed monotonic counter that only the release runner increments; the semver
and the codename are display and routing values.

```
semver    0.2.0                        <- release.ps1 -Version, written into Wavee.Version.props
codename  Breaker                      <- release.ps1 -Codename, one per minor
quad      0.2.0.7                      <- M.m.p + <WaveeBuild>, runner-owned
info      0.2.0+build.7.sha.d4227b3    <- stamped by the pack script
```

---

## 1. What you author (before you run anything)

Releases come from the open release branch, `release/<semver>`: every change reached it by PR, with a change file in
`changes/unreleased/` when a listener notices it (the flow: `docs/plans/wavee/release-flow-design.md`). You author one
thing:

- **`ops/release/wavee/<semver>/whatsnew.json`** (+ an optional `media/` beside it), merged into the release branch:
  the editorial layer, tagline, up to three hero highlights, notices. Authoring rules and the exact field-ownership
  table are in **`ops/release/wavee/README.md`**. Budgets the validator enforces: **≤150 KB** per still, **≤16 MB**
  per motion file, **≤17.5 MB** total, no GIFs, no duplicate basenames, and deep links must be
  `wavee://open?route=<known route>`. A hotfix needs no highlight (the page shows the fix list). A highlight's
  `community` credit fills itself from the CHANGELOG items it shares an issue with.

Leave `sections`, `links`, `packageVersion`, `date`, `generatedAt` and the `media` hash list empty — the tool writes
them. Everything in that folder ships **inside the MSIX** as well as as release assets.

Everything else is the runner's: `<WaveeVersion>` comes from `-Version`, `<WaveeCodename>` from `-Codename` on a new
minor, `<WaveeBuild>` goes up by one, the CHANGELOG section is assembled from the change files (you curate it after
`-Go -To changelog`), and the theme contract version is raised from the change files' `contract:` fields. Never
hand-edit `<WaveeBuild>`: the feed's monotonic gate depends on it.

---

## 2. Prerequisites (once per machine)

| Requirement | Check / fix |
|---|---|
| **Windows SDK** (`makeappx`, `makepri`, `signtool`) | preflight prints the kit version it picked |
| **VS Build Tools / MSVC** — NativeAOT's native link | the script prepends the VS Installer dir to `PATH` |
| **x64 cross tools on arm64** — `VC\Tools\MSVC\*\bin\HostArm64\x64\link.exe` plus the `runtime.win-x64.microsoft.dotnet.ilcompiler` NuGet | preflight `x64 cross toolchain`. Without them: `-SkipArch x64`, or `-X64Msix <prebuilt.msix>` to adopt a package built elsewhere |
| **Azure CLI signed in** — `az login` as an identity with **Artifact Signing Certificate Profile Signer** on the `Wavee` account | the active subscription is often REDLAB; the script runs `az account set --subscription "Azure subscription 1"` itself |
| **Trusted Signing client tools** — `Azure.CodeSigning.Dlib.dll` | `winget install -e --id Microsoft.Azure.ArtifactSigningClientTools` |
| **`ops/build/signing/metadata.json`** (gitignored) | copy `ops/build/signing/metadata.template.json` and fill in account / profile / endpoint |
| **`gh` authenticated** — `gh auth login` | used for the release, the feed, and (via `gh auth token`) the issue/PR lookups the notes tool makes |
| **`src/apps/Wavee.PlayPlay` junction** | a release build is **PlayPlay-inclusive**; preflight hard-fails without it. `-PublicOnly` opts out and builds the public variant deliberately |
| **The crash Worker's `wrangler`, logged in** — the `symbols` phase uploads with `ops\crash\worker\node_modules\.bin\wrangler.cmd`, never a global one | `npm --prefix ops/crash/worker ci`, then `npx wrangler login` inside `ops/crash/worker`; preflight `wrangler (crash symbols)` runs `wrangler whoami` |
| **1Password CLI (`op`), unlocked** — the crash ingest key (§5b) | `op read "op://Personal/Wavee crash ingest key/credential" --account my.1password.eu` must print the key (Windows Hello unlock); preflight checks `op whoami` first and prints `op signin --account my.1password.eu` |

The junction is per-checkout: a `-Go` links the release worktree's junction to the one in the checkout you run it from
(`CLAUDE.md` carries the `New-Item -ItemType Junction` recipe for a checkout that has none).

---

## 3. The dry run (the default)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Version 0.3.12
```

Without `-Go` the runner changes nothing: it reads `origin/release/<semver>`, prints the preflight table (each item
`ok`, `action` with the command that fixes it, or `note`) and then every step's commands, and exits 2 when an item
needs action, else 0. `-Preflight -Json` and `-Status -Json` give the same facts as JSON (for a UI); `-Status` says what
is done and what is next for a run in progress.

Before a minor, also rehearse the update path locally (§8): that is the only rehearsal of what installed clients do.

---

## 4. The real run

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Version 0.3.12 -Go -To changelog
#   curate CHANGELOG [0.3.12] and ops\release\wavee\0.3.12\whatsnew.json in C:\WAVEE\release\0.3.12\WaveeMusic, commit
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Version 0.3.12 -Go -PauseBefore feed
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Version 0.3.12 -Go
```

Run it from any checkout (the owner's is `C:\WAVEE\fgpu\WaveeMusic`). A `-Go` makes or reuses the **release worktree**
`C:\WAVEE\release\<semver>\`: `WaveeMusic` at `origin/release/<semver>`, `fluent-gpu` detached at the branch's
`engine.lock`, the PlayPlay junction linked to the invoking checkout's, `ops\build\signing\metadata.json` copied from
it, wrangler installed there; then it relaunches the worktree's own `release.ps1`, so the runner on the release branch
is the one that runs. State: `%LOCALAPPDATA%\WaveeRelease\<semver>\` (`state.json`, `runner.lock`, one log per step).
Staging: `<worktree>\artifacts\release\<semver>\`. Every `-Go` ends with a Windows toast naming the exit code and the
step.

**A step that reality reports as done is skipped**, so a second `-Go` continues where the first stopped; a new commit on
the release branch after the bump re-runs the notes and the packs (a local tag moves with it; a pushed tag never moves).

| # | Step | What it does | Done when |
|---|---|---|---|
| 0 | `preflight` | read-only; the list below | |
| 1 | `changelog` | `Wavee.ReleaseTool changelog assemble`: the CHANGELOG `[<semver>] - unreleased` section from the change files, the contract bump and `docs/themes/contract-history.md`, new contributors in `CONTRIBUTORS.md`; deletes the change files; commits | no change file left and the CHANGELOG has `[<semver>]` |
| 2 | `bump` | `<WaveeVersion>`, `<WaveeCodename>` (with `-Codename`), `<WaveeBuild>+1`; dates the CHANGELOG heading (UTC); commits `release: Wavee <semver> <codename> (build <quad>)` | that commit is on the branch since the previous tag |
| 3 | `notes` | `Wavee.ReleaseTool validate` into `<staging>\notes\` (reads the published `whatsnew-index.json` first: a 404 means first release, any other failure stops, because `feed` clobbers it); `commits.json` marks each commit `firstParent`, so the body's commit fold lists merge commits only; checks the body is under GitHub's 125,000 characters | recorded for the release commit, the outputs exist, `packageVersion` is the quad |
| 4 | `tag` | annotated `wavee-v<semver>` at the release commit, and in the engine worktree at the pinned engine commit (locally) | both tags at the right commits |
| 5 | `pack-arm64`, `pack-x64` | `pack-wavee-msix.ps1 -NoSign` with the crash ingest URL and the key (`op read`, once per run, never logged); asserts identity, publisher, crash stamp, PlayReady export, symbols zip | the staged package carries this quad and was built from this commit |
| 6 | `symbols` | per arch, `Wavee.ReleaseTool symbol-map`, then `wrangler r2 object put` of the `.symmap`, `Wavee.exe` and the symbols zip (4 tries each, backoff 5/15/45 s) | recorded for this quad with the staged hashes |
| 7 | `sign` | one Azure Trusted Signing `signtool` call over every `.msix` | `signtool verify /pa` passes on each |
| 8 | `stage` | one `.appinstaller` per arch, the flattened assets, `MANIFEST.txt` | the staged files match the manifest |
| 9 | `push` | the release branch (fast-forward only) and both tags; when origin moved meanwhile it stops: merge origin into the worktree branch and `-Go` again | origin has the release commit and both tags |
| 10 | `release` | `gh release`: draft, upload, publish, always `--latest=false` (`releases/latest` belongs to the gallery) | published, every asset byte-equal to the staged file |
| 11 | `feed` | repoints `wavee-stable` (and `wavee-beta` if it exists): **the moment installs see the release** | every feed serves this quad and this release's msix on both arches |
| 12 | `open-next` | creates `release/<next patch>` from the release head and moves every open PR off `release/<semver>` and `main`, one comment each | the branch exists and no open PR targets either |
| 13 | `merge-main` | opens and merges a PR `release/<semver>` -> `main` with a merge commit (never deleting the branch) | the release commit is in `main` |
| 14 | `forward-port` | a hotfix only (§6b): merges the hotfix branch into the renamed next branch through a PR | the hotfix head is in the next branch |
| 15 | `store-submit` | `wavee-store-submit.ps1` (the Store engine, §5c) packs, uploads and commits the Store submission | the Store ledger, or `msstore`, shows it committed |
| 16 | `notify` | comments on every issue the release closed (never closes or reopens one) | each carries the release comment |
| 17 | `store-poll` | waits up to `-PollMinutes` (15) for Published; not reaching it is no failure | Published (`WAITING` otherwise; `-Status` reports it) |

**Preflight** (1Password first, with `op signin --account my.1password.eu` as the fix): Windows SDK tools · x64 cross
toolchain · a Trusted Signing token and `metadata.json` · `gh` admin on both repos · wrangler logged in · the PlayPlay
junction (unless `-PublicOnly`) · `origin/release/<semver>` · `engine.lock` on that branch and the commit in fluent-gpu ·
the runner and `ops/release/wavee/<semver>/whatsnew.json` on the branch · valid change files · **feed monotonic** (the
new quad above both feed heads) · the tag free in both repos · `-Codename` on a new minor · **issue refs** (every commit
in `<prevTag>..` that closes an issue matches the change files' `issues:`, later the CHANGELOG entry's `(#n)`) ·
PRs into `main` (a note: `-RetargetPrs -Go` moves them). Items only some steps need turn `ok` once those steps are done,
so a `-Go` days later just to poll the Store is not refused by an expired token.

**Switches:** `-From`, `-To`, `-Only`, `-PauseBefore <step[,step]>` (a pause exits 4; the next `-Go` with the same
`-PauseBefore` continues and pauses at the next one) · `-Codename <name>` · `-SkipArch x64` / `-X64Msix <path>` (adopt
a prebuilt x64 package) · `-PublicOnly` (no PlayPlay; the Store still needs it) · `-PollMinutes <n>`. Exit codes: 0
done, 1 usage, 2 preflight refused (nothing changed), 3 stopped on a condition, 4 paused.

---

## 5. Verify

Phase 11 already checked the mechanical things; these are the by-hand confirmations.

```powershell
# the feed head, per architecture - this is what installed clients poll
Import-Module ops\release\Wavee.Release.psm1
Get-WaveeFeedVersion christosk92/WaveeMusic wavee-stable arm64
Get-WaveeFeedVersion christosk92/WaveeMusic wavee-stable x64

# the version release
gh release view wavee-vX.Y.Z --repo christosk92/WaveeMusic --json assets -q '.assets[].name'
```

Expect on the **version** release: `Wavee_<quad>_arm64.msix`, `Wavee_<quad>_x64.msix`,
`Wavee-<quad>-win-arm64-symbols.zip`, `Wavee-<quad>-win-x64-symbols.zip`, `whatsnew.json`, every media file,
`THIRD-PARTY-NOTICES.txt`, `MANIFEST.txt`. On **`wavee-stable`**: `Wavee.arm64.appinstaller`,
`Wavee.x64.appinstaller`, `whatsnew-index.json` — and nothing else.

Then, on a **clean VM** (a machine that has never had Wavee installed) — the only x64 observation we get:

1. Open `https://github.com/christosk92/WaveeMusic/releases/download/wavee-stable/Wavee.x64.appinstaller`. App
   Installer must show the publisher as trusted, with no "untrusted app" banner and no cert-import step.
2. Install, launch, and walk the smoke checklist from §3.
3. `Get-AppxPackage cproducts.Wavee` reports the quad you shipped, and `Get-AppxPackageAutoUpdateSettings` lists the
   feed URL — that entry is what proves the install joined the OS update channel.

**Once per minor**, also verify the update path end to end (§9): install the previous release, publish the next one,
and watch a real client move. An update that never arrives is indistinguishable from no release at all.

---

## 5b. Symbolicating a crash report

A shipped build is NativeAOT with `StackTraceSupport=false` (`src/apps/Wavee/Wavee.Publish.props`), so a crash report
(`%LOCALAPPDATA%\Wavee\logs\crash\<stamp>-<kind>\report.txt`, or the package's `LocalCache` equivalent; Settings ›
Privacy & diagnostics › Crash reports › a report's **…** › View opens it) prints every frame as an offset from the module base and nothing else:

```
   at Wavee!<BaseAddress>+0x7b1fc6
```

That is why **every package ships with its symbols**. `pack-wavee-msix.ps1` always publishes with
`/p:NativeDebugSymbols=true /p:DebugType=portable /p:IlcGenerateMapFile=true` (ILC `-g` + `link.exe /DEBUG`; the
managed portable PDB is what gives the native PDB its line numbers), moves the results out of the layout **before** the
package is staged, and zips them next to the `.msix`:

| File | Where it comes from | What it is for |
|---|---|---|
| `Wavee.pdb` | `publish\Wavee.pdb` (the ILC targets copy it there from `bin\Release\net11.0\<rid>\native\`) | the **only** thing that resolves an RVA; matches exactly one `Wavee.exe` |
| `Wavee.map.xml` | `obj\Release\net11.0\<rid>\native\Wavee.map.xml` (ILC `--map`, never in the publish folder) | names, sizes and content hashes of every emitted node — **no addresses**; use it to see which methods changed between two builds |
| `SYMBOLS.txt` | written by the pack script | the build stamp + the sha256/size of the exe the PDB belongs to |

Local: `<staging>\symbols\<quad>\win-<arch>\` (a ready-made WinDbg `.sympath`) and
`<staging>\Wavee-<quad>-win-<arch>-symbols.zip`. Published: the zip is a **version-release** asset beside the `.msix`
(phase 7 stages it, phase 9 uploads it, phase 11 verifies its size). The layout copy still purges `*.pdb`, so a
package can never carry one.

**Names, server-side, come from the symbol map at ingest — not from this zip.** The `symbols` phase (§4, between
`packX64` and `sign`) reads the same `<staging>\symbols\<quad>\win-<arch>\Wavee.pdb` plus the `Wavee.exe` extracted
from the staged `.msix`, calls `Wavee.ReleaseTool symbol-map` (DbgHelp `SymEnumSymbols` over every `SymTagFunction`
symbol) to write a binary `Wavee.symmap` (magic `WSYM`, sorted `{rva,size,nameOffset}` records + a UTF-8 string
table — the exact layout is the crash & diagnostics plan's §I contract), and uploads it with
`wrangler r2 object put wavee-crash/symbols/<quad>/win-<arch>.symmap --remote`. The Cloudflare Worker that ingests an opt-in
crash report resolves every frame's RVA against that map (binary search, no JSON parse) and stores the resolved
**names** — so a bundle shows up symbolicated on the crash dashboard with no manual step. It never carries **line
numbers**: that still means the `cdb` runbook below, run by hand against the archived symbols zip, exactly as before.
Both runbooks share one helper, `Publish-WaveeSymbolMap` (`Wavee.Release.psm1`: extract the exe, build the map,
upload, remove the temp exe); the Store leg (§5c) runs it in its own `symbols` phase under the **store** quad, so
`symbols/<storeQuad>/win-<arch>.symmap` exists for the Store packages too. A missing map also means no Discord alert:
the Worker only alerts for builds whose map it holds (`docs/guide/crash-diagnostics.md` §12).

**wrangler is the crash Worker's own.** The upload runs `ops\crash\worker\node_modules\.bin\wrangler.cmd`
(`Get-WranglerPath`), never a global `wrangler`: install it once with `npm --prefix ops/crash/worker ci` and log in
with `npx wrangler login` from inside `ops/crash/worker`. Preflight's `wrangler (crash symbols)` check runs
`wrangler whoami` against that binary, so an expired login fails before
anything is packed or signed.

**Every shipping build is stamped.** The ingest URL defaults to `https://crash.cproducts.dev`; the ingest key is read
from 1Password — `op read "op://Personal/Wavee crash ingest key/credential" --account my.1password.eu`, once per run
(`Resolve-CrashIngestKey`); it is **never logged, put on a command line or written to `state.json`**; a failing `op read` throws without echoing
its output. The `crash ingest` gate is **hard** for `stable`, `beta` and `store`: no https URL or no key, no release
— such a build's crashes could never reach the service. Only `dev` may ship unstamped. After packing, the **stamp
evidence** check (`Assert-CrashIngestStamp`, per arch, including an adopted `-X64Msix`) reads `Wavee.exe` out of the
`.msix` and proves it carries both the URL and the key (UTF-8 or UTF-16LE); its log line names the URL and the byte
offset, never the key.

The report header names the zip to take — `commit=`, `quad=`, `arch=` — and the `module=<path> base=0x… size=0x…`
line plus the `Frames (RVA)` section (one offset per line, innermost first, inner-exception frames included) are what
you feed to the debugger:

```powershell
# 1. the matching symbols + package (an .msix is a zip; Wavee.exe is at its root)
gh release download wavee-vX.Y.Z --repo christosk92/WaveeMusic -D sym `
    -p "Wavee-<quad>-win-<arch>-symbols.zip" -p "Wavee_<quad>_<arch>.msix"
Expand-Archive sym\Wavee-<quad>-win-<arch>-symbols.zip sym
Copy-Item sym\Wavee_<quad>_<arch>.msix sym\pkg.zip; Expand-Archive sym\pkg.zip sym\pkg
(Get-FileHash sym\pkg\Wavee.exe).Hash.ToLower()          # must equal the sha256 in sym\SYMBOLS.txt

# 2. resolve each RVA line of the report (cdb/WinDbg from the Windows SDK "Debugging Tools"; -lines for file:line)
cdb -lines -z sym\pkg\Wavee.exe -y sym -c "ln Wavee+0x7b1fc6; ln Wavee+0x1b616c; q"
#    -> Wavee!Wavee_Features_Detail_DetailPage__MapPlaylist+0x3e  (the nearest symbol at or below the offset)
#    for the source line:  cdb -lines -z sym\pkg\Wavee.exe -y sym -c "u Wavee+0x7b1fc6 L1; q"
```

Things to know:

- `ln Wavee+0x<rva>` needs the module name, not the base; the report's `base=` only matters when you hold an
  **absolute** address (a WER dump, a `!analyze` line): `rva = address - base`.
- `IlcFoldIdenticalMethodBodies=true` folds byte-identical bodies, so a frame can name a *sibling* method with the
  same code; the callers around it disambiguate. The ILC targets pass `/OPT:REF /OPT:ICF` with or without `/DEBUG`,
  so a symbols build has the same code layout as a symbol-less one — which is also why a **release that shipped
  without a zip** (0.2.0.1) can be resolved by rebuilding its tag with the same SDK and the same three properties.
- A JIT (`-NoAot`) package has only the managed PDB; its frames already carry names.

---

## 5c. The Microsoft Store submission

The Store is part of the run: step `store-submit` calls `ops\release\wavee-store-submit.ps1` from the release worktree
(which sits at the tag, as that script requires), and `store-poll` checks it. Nobody runs the Store script by hand any
more; its own ledger (`artifacts/store/<semver>`, schema 2) is what the runner reads for "done".

It builds one SDK `.msixbundle` containing **both** Store-channel MSIX packages, then wraps that single bundle in
`Wavee_<storeQuad>_arm64_x64_store.msixupload`. Store quad: `(M+1).m.(p*100+build).0`. The Store packages are
crash-stamped like the feed's (§5b), and a `symbols` phase uploads `symbols/<storeQuad>/win-<arch>.symmap`. The script
validates the nested manifests, unchanged package hashes and executable/symbol provenance before uploading.

Before commit it reconciles the **whole** package inventory: only the new upload remains active; known superseded
package IDs are marked `PendingDelete`. It preserves listing metadata and patches en-US notes. Every Store release goes
to everyone at once (no gradual rollout, decision D4). Ingestion starts after commit; certification is not publication:
`store-poll` reports `WAITING` until Published, and `-Status` shows it later.

Do not edit an API-created submission in Partner Center. The account setup and API ownership rule are in the
onboarding guide §6. Mutating runs hold a product-scoped machine mutex; do not create external/manual drafts while
automation runs.

| Symptom | What it means | Recovery |
|---|---|---|
| a pending submission exists | a draft is already open on the product; `msstore publish` would silently delete it, so the engine refuses | finish or delete the pending submission first (a hand-made one in Partner Center, an API-created one with `msstore submission delete`) |
| `msstore` authentication fails | the Entra client secret expired | new secret in Entra -> `msstore reconfigure` (onboarding guide §6) |
| certification failed | the Store rejected the submission | read the errors with `-Status`; fix the cause; `msstore submission delete`; move `artifacts\store\<semver>` aside (its ledger says committed) and `-Go` again |
| `store-poll` says `WAITING` | certification is still running | nothing: `-Status` later, or `-Go -Only store-poll` |

---

## 6. When something fails

Fix the cause and run the same `-Go` again: done steps are skipped.

| Where it stopped | State | What to do |
|---|---|---|
| preflight (exit 2) | nothing happened | fix each `action` item and run again |
| `changelog` | a change file is invalid, or the CHANGELOG already has `[<semver>]` | fix the file on the release branch by PR, or fold late change files into the curated section by hand |
| notes validation | the release is not shippable; every problem is listed | fix `whatsnew.json` or the CHANGELOG section in the release worktree, commit, `-Go` |
| a pack or `sign` | the tags are local only | fix, `-Go` (nothing done is redone); or `-Abort -Go` to delete the local tags and the runner's commits |
| `push`: origin moved | a PR merged into the release branch while the run built | merge `origin/release/<semver>` into the worktree branch, `-Go` (the notes and packs re-run for the new commit) |
| after `push`, before `feed` | the commit and tags are public; a draft release is invisible; installs see nothing | `-Go`. **Never delete a pushed tag** |
| `feed` did not come up | GitHub asset lag | `-Go` again |
| `merge-main` refused | the ruleset blocked the merge | read the message; `-Go` again once fixed (never `--admin`) |
| `notify` failed | the release is live | `-Go` retries just that step |
| a bad build shipped | | §6b (hotfix) or §7 (rollback) |

**Never delete a pushed release tag.** Installed clients, the feed's `MainPackage/@Uri` and `whatsnew-index.json` all
resolve through it (the tag ruleset refuses it anyway). Optionally mark the bad release a prerelease
(`gh release edit wavee-vX.Y.Z --prerelease`) while the fix builds.

## 6b. Hotfix

A hotfix is an ordinary release cut from the shipped tag. It takes the next patch, so the waiting work moves up one.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Hotfix 0.3.12        # dry run: what it renames and creates
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -Hotfix 0.3.12 -Go
```

It renames the open `release/0.3.12` to `release/0.3.13` (its open PRs follow: a base-branch rename retargets them;
the `release/**` ruleset may need the owner's bypass for the rename) and creates `release/0.3.12` from `wavee-v0.3.11`
(its `engine.lock` is the engine that shipped). The fix is a normal PR into `release/0.3.12` with a change file
(`kind: fixed`); an engine fix goes on `wavee-hotfix/0.3.12` in fluent-gpu from its `wavee-v0.3.11` tag, and the
hotfix's `engine.lock` points at it. Then `release.ps1 -Version 0.3.12 -Go` releases it (no highlight needed), and
`forward-port` merges it into `release/0.3.13`.

---

## 7. Rollback

Wavee's `.appinstaller` sets **`ForceUpdateFromAnyVersion="true"`**, so pointing the feed at an *older* release does
reach already-installed clients and moves them **back**. That is a real lever, and it is the only one that reaches
installs that are already out there.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -RepointFeed 0.3.11 -AllowDowngrade       # dry run
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\release.ps1 -RepointFeed 0.3.11 -AllowDowngrade -Go
```

It finds the release `wavee-v0.3.11`, reads the quad off its `.msix` asset names, regenerates both `.appinstaller`
documents pointing at that release, uploads them to `wavee-stable`, and waits for the feed to come up at that quad.
`whatsnew-index.json` is deliberately left alone — the index is cumulative history, not a pointer. Without
`-AllowDowngrade` a move backwards is refused.

**Fix forward is still the default.** A downgrade re-runs the installer on every client and cannot un-write user data a
bad build already wrote, so use it only when the shipped build is actively harmful; otherwise ship a hotfix (§6b).
The Store cannot downgrade: Store installs always roll forward with the hotfix.

**Downgrade safety.** Everything persistent must stay readable by the previous release: the library cache is safe by
design (fingerprint-named, never migrated); settings readers ignore keys they do not know and never rewrite them; a
format an older build cannot read ships in two releases (read both first, write the new one a release later).

A user who cannot wait: `Get-AppxPackage cproducts.Wavee | Remove-AppxPackage`, install the good `.msix` directly, then
open the feed `.appinstaller` once the fix ships to re-join the update channel.

---

## 8. Local end-to-end (no GitHub)

`ops\release\tests\local-update-e2e.ps1` runs the entire update story on this machine with nothing on GitHub touched:
no tag, no branch, no release, no `gh`. It is the rehearsal to run before a release, and the only thing to run when
the update path itself is what changed.

### The two paths, and why they cannot share a run

A packaged Wavee can be updated by **two different actors**, and they race. `-Scenario` picks which one the run
measures; there is no mode that measures both, because whichever wins makes the other unobservable.

| | `-Scenario inapp` (default) | `-Scenario os` |
|---|---|---|
| A is installed | from the bare `.msix` (`Add-AppxPackage -Path`) | through the feed (`Add-AppxPackage -AppInstallerFile`) |
| association after install | **none** — so Windows cannot preempt anything | created by the install |
| who applies B | **Wavee** — its checker offers B, install-on-quit hands the feed URI to `PackageUpdater` | **App Installer** — the `OnLaunch HoursBetweenUpdateChecks="0"` rule intercepts the next activation |
| the log says | `update available: B (running A)` → `install-on-quit: staging B` → `staged B; restarting` → `updated: A -> B` | `up to date: A` → *(nothing)* → `updated: A -> B` |
| association after the update | **created by the apply** — the app passing `FeedUrl` to the deployment engine is the only thing that could have made one | still there |
| what it proves | the app's own update code: the checker, the offer, install-on-quit, `PackageUpdater`, and that applying creates the association | the silent OS path — **what most users will actually experience**: they open the app and it is simply already new |

**The learning that forced the split** (run 2, 42 pass / 5 fail). The harness used to install A *through* the feed and
then expect the app to do the updating. It never got the chance: the association plus `HoursBetweenUpdateChecks="0"`
means that once the feed has moved, the **next activation is intercepted** — App Installer downloaded B over loopback
in 34 Range slices and registered it *before Wavee's entry point ran*. The log showed `up to date: A` →
`updated: A -> B` → `up to date: B`, with no `update available` and no `install-on-quit` line anywhere. Every OS-side
assertion passed and every in-app one failed, for the same reason. That run was a perfectly good `os` run under an
`inapp` label — hence two scenarios, and hence `inapp` installing bare so nothing can get in front of the app.

**What each run proves.** Both end with B installed, `updated: A -> B` in the log, and
`app.whatsnew.previousVersion` holding A's quad (the plate itself is out of reach here — see below). On `os` the
sequence is the SILENT one the shipped template asks for (`ShowPrompt="false" UpdateBlocksActivation="false"`):
the activation starts A immediately, App Installer checks the feed and **stages B in the background** while A runs
(its immediate register attempt fails with `0x80073D02`, package in use — recorded as an INFO row, expected), the
harness closes A, and the staged B is registered on exit or by the next activation, which then opens already
updated. The older "blank App Installer window blocks the launch and registers B first" behaviour was
`ShowPrompt="true"`, and is gone. The listener's request log is
what separates the two actors: the in-app feed GET carries `User-Agent: Wavee/...`, the package download does not,
and it arrives in `206` Range slices. On `os`, the run additionally asserts the **absence** of
`install-on-quit` after its log mark (the app's own checker may still say `update available` — it keeps running
while B is staged, and that line is recorded as an observation) — that absence is the evidence that the OS, not the app, did the work.

**The one command** — elevated, because it imports a certificate into `LocalMachine\TrustedPeople`, loads the
package's settings hive with `reg load`, and binds `http://127.0.0.1:8099/`:

```powershell
# the app's own update path (default)
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\tests\local-update-e2e.ps1

# App Installer's silent on-launch update - what most users get
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\tests\local-update-e2e.ps1 -Scenario os
```

Run it **without** redirecting its output: the harness writes its own transcript (`artifacts\local-e2e-<scenario>.log`,
or `-LogPath`), so the console shows the live rows. The P2 packs are two NativeAOT publishes (~8 minutes) — the
console prints the publish output, not a progress bar (`$ProgressPreference` is off for the whole run, because
`Remove-AppxPackage`'s bar used to stay painted for the whole publish and read as a hang).

The after-update plate itself is **not** verified by the harness: `AfterUpdateChrome` mounts inside the signed-in
`WaveeShell`, and the harness never signs in (the app sits on the device-code page), so P11 records the plate as
`SKIP` and instead asserts that `app.whatsnew.pendingFrom` **stays armed** (== A) for the next signed-in launch. P10
also no longer waits for the old process when Windows has already relaunched B (`A has already exited`).

Expected ending: `OVERALL: PASS`. The exit code is the FAIL count; WARN rows are soft observations (screenshot
luminance, the `206` count, the association) that are worth reading but never fail the run. The
last row of the table is an `INFO` summary — scenario, both quads, and the artefact paths — so a pasted table still
says what it measured.

### The evidence rows (why the table is longer than the assertions)

Run 3 failed three checks that were all, in the end, the *harness* being unable to see what had happened — the
package had flipped and the harness kept polling, the association existed and the cmdlet denied it, the app's data
had been reset underneath a mark and nothing said so. The fix in each case was to record the evidence rather than
infer it, so the run now emits `INFO` rows alongside its assertions:

| Row (`INFO` unless marked) | Phase | What it pins |
|---|---|---|
| `association probe: cmdlet` / `: feed log` / `: AppXDeploymentServer 603` | P6, P10 | the **three independent proofs** of the App Installer association (below) — each recorded whether or not it held |
| `package LocalCache logs at the mark` / `... now` | P9, P10 | the package's `LocalCache\...\Wavee\logs` directory, file names **and** sizes, on both sides of the update |
| `Helium User.dat at the mark` / `... now` | P9, P10 | the package container's settings hive — mtime + size, the same two moments |
| **`app data survived the update`** (hard) | P10 | every log file present at the P9 mark is still present **and not smaller**. An append-only log can only grow, so a shrink is a truncation or a re-create — which is exactly what run 3 saw (the whole directory replaced by one fresh 7 KB file) and which silently invalidates every mark-keyed wait |
| `launched Wavee` / `adopted the Wavee process already running` | every launch | pid, `StartTime`, parent pid **and parent name**, and the command line (`Win32_Process`). The parent is the story: `explorer.exe` = a user activation, `svchost.exe` = Windows relaunching after an update, `WerFault.exe` = a restart after a crash |
| `Application-log fault event` | P10, P11 | every `Application Hang` / `Application Error` / `Windows Error Reporting` record mentioning Wavee since the P9 mark (time + id + the first 160 chars) |
| `AppXDeploymentServer event` | P10, P11 | ids **603** (`UpdateUsingAppInstallerOperation`), **400/401/404** (deployment begin / end / failure) for this package family, same window |
| **`no Application Hang for Wavee during the apply`** (hard, `inapp` only) | P10 | the quit-time apply must not trip Windows' hang detector — see §9 |
| `app identity line` / `relaunched-by-Windows line` | P10 | the newest `identity: <package full name or 'unpackaged'>; lastRun='<x>'; appData=<root>` the app logged, and whether `[app] startup - relaunched by Windows after an update` appears |

**The association is proved three ways, and any one is enough.** `Get-AppxPackageAutoUpdateSettings` is asked by
`-PackageFullName` *and* by `-Name` (and reports whether the cmdlet exists at all — on a build where it does not,
"no association" and "cannot answer" used to be the same `$null`). Independently, the harness proves the association
from the **feed request log** — an `App Virt Client` `GET` of the `.appinstaller` after the P9 mark, which only the
deployment engine ever issues — and from **event 603** for the package family in the same window (the 603 alone is
only corroboration: it has been seen for a package that had no association and fetched nothing, so it counts only
alongside the cmdlet or the wire proof). The check passes on
any proof and its detail names which ones held. This is not belt-and-braces: run 3 had the cmdlet answer *"no
association"* for a package the AppXDeploymentServer log showed being updated through its `.appinstaller` on **every
single launch**.

**The apply poll re-queries everything, every iteration.** P9 waits for the package version to flip to B. It used to
ask `Get-AppxPackage -Name <name> | Select-Object -First 1` and compare that one package's raw `.Version` to the quad
string — and while a deferred update settles, Windows keeps the **outgoing** version registered until the last process
of the package exits, so the query answers with *two* packages and `-First 1` returns whichever the store enumerates
first: A. The loop watched A for the full 600 s while B was already registered; P10, running after the old process was
finally gone, saw B on its first try. It now re-queries fresh each iteration, looks at **every** version Windows can
see (per-user *and*, since the harness is elevated, `-AllUsers`, which additionally lists a staged-but-unregistered
package), and compares **normalized quad strings** rather than whatever `.Version` stringifies to (a three-part
`0.2.0` has `Revision` = -1, not 0).

**Log marks are keyed by full path.** Two directories hold identically named `wavee-<date>.log` files — the package
container's `LocalCache` and the real `%LOCALAPPDATA%` — so a name-keyed mark taken on one was satisfied by the other
sitting at a different length. Every mark, lookup and failure detail is now the **full path**, and a failed wait lists
*every* file it scanned with its size at the mark and its size now, including files that were marked and have since
vanished (`mark=… now=GONE`).

**Release notes are generated first, not copied.** Before packing, the harness runs `Wavee.ReleaseTool validate` for
A and for B (dating a *copy* of `CHANGELOG.md` into `artifacts\local-e2e\CHANGELOG.<semver>.md`, so `git status` stays
clean) and embeds and serves the **emitted** `notes-A\` / `notes-B\` folders. This matters: `ops\release\wavee\<semver>\whatsnew.json`
is an *input* — its `sections` are empty and its `date` and `packageVersion` are empty strings — so run 2, which
shipped the authored file, produced a plate with no changelog sections and no version pill. The emitted index is also
what dedupes the What's-new rail: A and B share semver `0.2.0` on purpose, and `MergeIndex` keeps one entry per
version (B replaces A), where the hand-built index used to list `0.2.0` twice. `-NoNotes` skips the tool and embeds
the authored folders, which is only worth doing when the tool itself is broken.

**Iteration switches**

| Switch | What it does |
|---|---|
| `-Scenario inapp` / `os` | which of the two update paths to measure (see the table above); default `inapp` |
| `-SkipPackA` / `-SkipPackB` | reuse the `.msix` already in `artifacts\local-e2e\A` / `...\B` — the two packs are most of the wall clock |
| `-KeepFeed` | leave `C:\wavee-feed` and the installed package in place after the run |
| `-Driver ui` | stop before applying and ask you to press **Settings › About › Update now** by hand (default `quit` drives install-on-quit unattended); `inapp` only |
| `-Drill snooze` | arm `app.update.snoozedVersion` = B before the relaunch: the toast must not reappear, the update must still apply |
| `-Drill network` | stop the feed mid-flight; the apply must fail loudly (`deployment failed 0x...`) and leave A installed and intact |
| `-Drill downgrade` | after B is live, point the feed back at A: the in-app checker must refuse to go backwards; the OS `OnLaunch` path may still roll back (that is the documented rollback mechanism) |
| `-NoNotes` | skip the `Wavee.ReleaseTool` pass and embed / serve the authored notes folders as-is |
| `-QuadA` / `-QuadB` / `-SemverA` / `-SemverB` / `-NotesA` / `-NotesB` | the two identities and their authored release notes (point `-NotesB` at a synthetic `0.2.1` folder to exercise stacked notes) |
| `-LaunchTimeoutSec` | how long to wait for a Wavee process after activating it (default 120; the `os` path uses at least 180, because the activation downloads and registers B first) |
| `-Port` / `-FeedDir` / `-OutDir` / `-Arch` / `-NoAot` | the loopback port, where the feed is laid out, where artefacts go, and the packaging knobs |
| `-RemoveCert` | also drop the dev certificate from `LocalMachine\TrustedPeople` on the way out |

Drills describe variations on the *app's* update path, so `-Scenario os` ignores them (and says so in the table).

**The feed layout** (identical in shape to the GitHub one, with `<base>` = `http://127.0.0.1:8099/`):

```
C:\wavee-feed\                              served as http://127.0.0.1:8099/
  pkg\Wavee_0.2.0.9001_arm64.msix           MainPackage/@Uri -> <base>pkg/...
  pkg\Wavee_0.2.0.9002_arm64.msix
  wavee-local\Wavee.arm64.appinstaller      root Uri == the URL it is served from (App Installer's redirect rule)
  wavee-local\whatsnew-index.json           <base><feed-release>/whatsnew-index.json
  wavee-v0.2.0\whatsnew.json + media\       <base>wavee-v<semver>/whatsnew.json
  feed-requests.log                         time / method / path / status / range / bytes / user-agent
```

**Why the base URL is baked at pack time.** `pack-wavee-msix.ps1 -UpdateBaseUrl http://127.0.0.1:8099/` stamps
`AssemblyMetadata UpdateBaseUrl` into the assembly, exactly like `-FeedRelease`. There is no environment variable and
no runtime switch: the package genuinely polls loopback, so the code that runs is the code that ships. The pack script
refuses a non-absolute URL, refuses a non-http(s) scheme, and refuses plain `http` unless the host is loopback — a
package that would fetch its updates unauthenticated over the wire cannot be built by accident.

Loopback is safe here for the same reason the plan says: only AppContainer processes are loopback-blocked, and Wavee
is `runFullTrust`. The one known way this fails is a machine-wide proxy with no loopback bypass, which would stop
AppXSvc reaching `127.0.0.1`.

**Artefacts** (`artifacts\local-e2e\`)

| File | What to look at |
|---|---|
| `01-baseline.png` | A running, before anything is offered |
| `02-after-update.png` | B running with the after-update plate up — the one thing only eyes can judge |
| `wavee-A-final-wavee.log` | A's log, copied out *before* `Remove-AppxPackage` deletes its LocalCache |
| `wavee-B-final-wavee.log` | B's log |
| `feed-requests.log` | every request the feed served, tab separated — the two user agents are the whole point |
| `A\` and `B\` | the two signed packages plus their `.cer` (what `-SkipPackA` / `-SkipPackB` reuse) |
| `notes-A\` and `notes-B\` | what `Wavee.ReleaseTool` emitted — the `whatsnew.json` that was embedded *and* served, its `whatsnew-index.json`, and the copied media |
| `CHANGELOG.<semver>.md` | the dated copy handed to the release tool; the working tree is never touched |

**Troubleshooting**

| Symptom | Cause / fix |
|---|---|
| `0x800B0109` on install | the dev certificate is not trusted — P3 failed, or A and B were signed by different certs (repack both, or drop `-SkipPack*`) |
| `0x80073D02 ERROR_PACKAGES_IN_USE` | a playback-module child process outlived the app; the harness kills them, but a debugger attached to one will hold the package |
| listener `Access is denied` | not elevated, or the port is reserved by someone else — the thrown message carries the exact `netsh http add urlacl url=http://127.0.0.1:8099/ user=<domain>\<user>` to run once |
| `install-on-quit gave up in state ...` | the ~60 s `RegisterApplicationRestart` floor (P9 waits it out) or a metered connection with the metered toggle off |
| `reg load of the Wavee hive failed` | a Wavee process is still running; the harness refuses to touch the container hive while one is, and retries for 20 s after the last exit |
| the check never fires | `app.update.lastCheckedMs` is inside its launch cooldown; P9 deletes it, so this only bites a hand-driven run |
| `inapp`: the app never says `update available`, and P6 reports `no auto-update association` FAILED | something installed A through the feed anyway — a leftover association from an earlier `os` run that P1's `Remove-AppxPackage` did not clear, or a hand install. Read the three `association probe:` rows to see *which* proof fired. The bare install is what keeps App Installer out of the way; without it the run is an `os` run |
| a wait fails with `no match for /.../; searched <full path> mark=<n> now=<n> ; ...` | that is the whole diagnosis, one entry per file scanned. If `mark` equals `now`, nothing was written after the mark; if the line is visibly in the copied log *before* that offset, the mark was taken too late (P9 takes the one mark, and P10/P11 reuse it — never re-mark inside a phase); `now=GONE`, or a `now` **smaller** than `mark`, means the log directory was reset under the run — which `app data survived the update` asserts against directly |
| `app data survived the update` FAILED | the update threw the app's own data away: a file present at the P9 mark is gone, or shrank. Every mark-keyed wait in P10/P11 is unreliable from that point on, so fix this before believing any other failure in the run |
| `no Application Hang for Wavee during the apply` FAILED | the quit-time apply stopped pumping messages and WER killed the process mid-update (§9). Check the `Application-log fault event` rows for the matching `MoAppHang` / event 1002 |
| `Wavee relaunched` fails on the `os` path | the activation is doing the download; raise `-LaunchTimeoutSec` (the `os` scenario already floors it at 180 s) |
| the plate has no sections, or the rail shows one version twice | the run went through `-NoNotes`, or the release tool failed in P2 (a WARN row says why) and the harness fell back to the authored folder |

---

## 8b. (retired) scratch feed on GitHub

The scratch-feed recipe redirected the old script into a throwaway tag prefix and feed (`-FeedRelease`, `-TagPrefix`,
`-Branch`). The runner has no such redirection: it always works on `release/<semver>`, `wavee-v*` and `wavee-stable`.
The local end-to-end (§8) is the rehearsal of the update path; the runner's dry run (§3) is the rehearsal of a release.

---

## 9. The in-app update experience (what to actually look at)

| Path | Trigger | Expected |
|---|---|---|
| **Available** | the scheduler's check finds a higher feed root `Version` | toast + notification-centre card + a Settings › About banner naming the version and codename |
| **Update now** | the toast / About button | `Downloading` with real percentages → `Installing` → Windows terminates the app and **relaunches it automatically** |
| **After update** | the first launch on the new quad | the after-update dialog ("Welcome to Wavee X.Y.Z") plus the OS "Wavee updated" toast; About shows the new quad; the notification card is marked read. The relaunched process logs `[app] startup - relaunched by Windows after an update` (see `--relaunched-after-update` below) |
| **Later** | the toast's secondary button | the state goes `Snoozed` for that quad — no re-toast; Settings still says an update is waiting; the OS applies it silently on some later launch anyway |
| **Metered** | a metered network with the metered toggle off | `Failed › Metered` with an explicit "you are on a metered connection" message, not a generic error |
| **Network failure** | pull the network mid-download | `Failed › Network` + Retry; the snooze is untouched |
| **OS silent path** | installed from a bare `.msix` (no association) | diagnostics shows `association = false` + **Repair auto-update**; after repairing, App Installer applies the next update on a launch |
| **Unpackaged build** | `dotnet run` | the updater is inert; "Update now" opens the release page |
| **Every state, no network** | Settings › Privacy & diagnostics › Developer › **Simulate an update** | walks the full state machine, including each failure kind |

**`--relaunched-after-update`.** `PackageUpdater` calls `RegisterApplicationRestart` before the deployment so Windows
brings the app back afterwards. It used to pass `null`, which reuses the *original* command line verbatim — so the
process that came back was byte-for-byte indistinguishable from a normal launch and no log could tell you an update had
just happened. It now registers an explicit command line: this process's own arguments plus
`--relaunched-after-update` (`AppRelaunch.RelaunchedAfterUpdateFlag`; the whole line is capped at the OS's
`RESTART_MAX_CMD_LINE` = 1024 chars, and the original arguments are dropped rather than the flag if it does not fit).
The flag is **inert** — `Program.Main` logs one line for it and nothing else reads it. It parses as neither an absolute
URI nor a path, so `ActivationArgs.Classify` still reports `ActivationKind.Launch` and the single-instance / deep-link
path never sees it.

**Install-on-quit does not block the GUI thread — and it LEAVES when Windows asks.** The apply runs on the thread pool
while the main thread pumps Win32 messages (`MessagePump.RunUntil`, `FluentGpu.Windows/Hosting/MessagePump.cs`). This
is not a nicety: the deployment ends in `ForceTargetApplicationShutdown`, which knocks on whatever windows the process
still owns — by then our top-level window is gone, but the STA apartment's hidden `OleMainThreadWndClass` COM window
and the WinRT/shell proxies behind SMTC, jump lists and notifications are not — and a thread parked in
`GetAwaiter().GetResult()` answers none of them. Windows concluded the app was hung: WER `MoAppHang` plus Application
event **1002** *"Wavee.exe stopped interacting with Windows and was closed"*, on **every** in-app-quit update. The
update still landed (that is why the bug hid for so long), but each one filed a hang report.

**Pumping alone did not fix it.** A pumped run took the same ~32 s and filed the same report (deployment started
23:23:07.6, event 1002 at 23:23:40.05, package registered 23:23:40.5 — the instant the OS killed us), because the
Restart Manager pass is not asking permission: `WM_QUERYENDSESSION` (`ENDSESSION_CLOSEAPP`) → `WM_ENDSESSION` →
`WM_CLOSE` means *exit*, and it then waits ~30 s for the process to go away before the deployment can finish. Answering
TRUE while continuing to wait on `ApplyAsync` is a deadlock: the deployment waits for us, we wait for the deployment.
So `RunUntil` creates a hidden **top-level** sentinel window of its own (the end-session broadcast skips
`HWND_MESSAGE` windows, and the OS-owned proxies just `DefWindowProc` it away without telling us), records the request,
and returns `PumpOutcome.ShutdownRequested`; `InstallPendingUpdateOnQuit` logs
`install-on-quit: Windows asked us to exit (the deployment is taking over); staged <quad>` and returns so `Main` exits
normally. Nothing is lost: the package is staged, and `RegisterApplicationRestart` was already called before the
deployment started, so Windows brings Wavee back with `--relaunched-after-update`.
The harness asserts this: **`no Application Hang for Wavee during the apply`** (hard, `inapp` only), and the
"the update staged" check accepts the shutdown-requested ending as success.

Every row above except the last three is reachable from the local harness in section 8, with no GitHub: **Available** and **Update now** are P9 (`-Driver quit` drives install-on-quit, `-Driver ui` waits for you to press the button), **After update** is P11 (`02-after-update.png` plus the `app.whatsnew.previousVersion` assertion), **Later** is `-Drill snooze`, **Network failure** is `-Drill network`, and **OS silent path** is `-Drill bare` (P6 asserts the association is absent, P10 that applying the update created it); `-Drill downgrade` covers the rollback direction in P12. **Metered**, **Unpackaged build** and **Simulate update** stay hand-driven - the harness cannot make a machine metered, and the other two are not update-path runs at all.

---

## 10. Signing gotchas

All of them — `Invalid tenant id`, `SignerSign() failed 0x80004005`, the wrong active subscription, publisher mismatch
`0x8007000B`, `signtool verify /pa` failing for self-signed builds, the HTTP-not-HTTPS timestamp URL, and
`vswhere` / MSVC `link.exe` not found during the AOT link — are documented once in the **`releasing` skill**:
`.claude/skills/releasing/SKILL.md`. They apply verbatim to Wavee; only the script, template and identity differ.

## See also

- `ops/release/README.md` — the tooling layout and the one-command summary.
- `ops/release/wavee/README.md` — authoring `whatsnew.json`, the validator's rules, the media budgets.
- `ops/release/feed-release-body.md` — why `wavee-stable` must never be deleted (it is also that release's body).
- `ops/build/README.md` — the packaging scripts and their flags.
- `docs/guide/microsoft-store-onboarding.md` — the Store account, identity, listing, and the msstore-cli setup (§6) behind §5c.
- `.claude/skills/releasing/SKILL.md` — signing config, secrets, troubleshooting.
- `docs/guide/playplay-private-split.md` — the PlayPlay junction and what `-PublicOnly` leaves out.
