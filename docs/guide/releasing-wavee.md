# Releasing Wavee (signed MSIX)

The runbook for shipping the **Wavee** music client. Wavee and the FluentGpu gallery are two products in one tree and
release **independently** — and by two different mechanisms:

| | Gallery | Wavee |
|---|---|---|
| Trigger | push tag `v*` → `.github/workflows/msix.yml` | **run `ops/release/wavee-release.ps1` on this machine** |
| Tag prefix | `v*` | **`wavee-v*`** |
| Script | `ops/build/pack-msix.ps1` | `ops/release/wavee-release.ps1` (which calls `ops/build/pack-wavee-msix.ps1` per arch) |
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
| **Version tag** | `wavee-vX.Y.Z` (annotated, on `main`) | immutable, never deleted |
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
`M.m.p.WaveeBuild`. `WaveeBuild` is a committed monotonic counter that only the release script increments; the semver
and the codename are display and routing values.

```
semver    0.2.0                        <- hand-edited in Wavee.Version.props
codename  Breaker                      <- hand-edited, one per minor
quad      0.2.0.7                      <- M.m.p + <WaveeBuild>, script-owned
info      0.2.0+build.7.sha.d4227b3    <- stamped by the pack script
```

---

## 1. The three hand edits (before you run anything)

1. **`src/apps/Wavee/Wavee.Version.props`** — set `<WaveeVersion>` (the semver `M.m.p`) and, on a new minor,
   `<WaveeCodename>`. **Never touch `<WaveeBuild>`** — the script bumps and commits it; hand-editing it breaks the
   monotonic feed gate.
2. **`CHANGELOG.md`** — add `## [X.Y.Z] - unreleased` with `### Added` / `### Changed` / `### Fixed` / `### Removed` /
   `### Known limitations` sections. The script replaces `unreleased` with today's UTC date; the release tool parses
   that entry into the What's new page's itemised list, so a bullet that is not in the changelog does not ship.
   Bullets may carry `(#123)` / `(!123)` refs — the tool resolves each one against GitHub.
3. **`ops/release/wavee/<semver>/whatsnew.json`** (+ an optional `media/` beside it) — the editorial layer: tagline, up
   to three hero highlights, notices. Authoring rules and the exact field-ownership table are in
   **`ops/release/wavee/README.md`**. Budgets the validator enforces: **≤150 KB** per still, **≤600 KB** per motion
   file, **≤1.5 MB** total, no GIFs, no duplicate basenames, and deep links must be `wavee://open?route=<known route>`.

Leave `sections`, `links`, `packageVersion`, `date`, `generatedAt` and the `media` hash list empty — the tool writes
them. Everything in that folder ships **inside the MSIX** as well as as release assets, so every byte there is a byte
in every install.

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

The junction is per-checkout and is **not** inherited by a Claude scratchpad — `CLAUDE.md` carries the exact
`New-Item -ItemType Junction` recipe if you are releasing from one.

---

## 3. Rehearse: `-DryRun`

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-release.ps1 -DryRun -SkipTests
```

A dry run does everything up to and including **signing**, then stops before any commit, tag or upload. It never
touches the working tree: instead of dating `CHANGELOG.md` in place it dates a **copy** inside the staging folder and
points the release tool at that, so `git status` stays clean and `<WaveeBuild>` is not bumped. It ends by printing the
exact `git push` / `gh release` commands a real run would issue next.

Drop `-SkipTests` when you want the full gate (Debug + Release build of `Wavee.slnx`, `Wavee.Tests`, Pester) inside the
rehearsal; that is what a real run does by default.

Review `artifacts\release\<semver>-dryrun\`:

- [ ] `Wavee_<quad>_arm64.msix` and `Wavee_<quad>_x64.msix` — both present, both **Trusted-Signed** (the script
      verifies each with `signtool verify /pa`), both carrying the quad you expect
- [ ] `Wavee.arm64.appinstaller` / `Wavee.x64.appinstaller` — root `Version` = the new quad, `MainPackage/@Uri` points
      at the **version tag's** msix, the root `Uri` points at the **feed**
- [ ] `whatsnew.json` — the merged document: your tagline and highlights **plus** the changelog sections, dated, with
      issue/PR titles and states resolved
- [ ] `whatsnew-index.json` — newest-first, this release prepended to whatever the feed already had
- [ ] `RELEASE_BODY.md` — reads like a release, not like a diff
- [ ] the media files, flat, by basename; `THIRD-PARTY-NOTICES.txt`; `MANIFEST.txt` (sha256 over every asset)
- [ ] `git status` clean

Then **install the arm64 package** (`Add-AppxPackage artifacts\release\<semver>-dryrun\Wavee_<quad>_arm64.msix`) and
smoke it:

- [ ] Settings › About — `Wavee X.Y.Z "Codename"`, the quad, the sha, the channel pill
- [ ] the What's new page renders from the **embedded** copy (`Assets/whatsnew/`) with no network
- [ ] Settings › Diagnostics › **Simulate update** walks every update state (toast → progress → installing →
      after-update dialog) with no network
- [ ] first-run wizard, sign-in, play a track, `start wavee://open?route=settings` focuses the *running* instance, and
      a test toast round-trips (packaged AUMID path)

---

## 4. The real run

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-release.ps1
```

That is the whole command. The run is a ledger of phases; each one records itself in
`artifacts\release\<semver>\release-state.json`.

| # | Phase | What it does |
|---|---|---|
| 0 | `preflight` | the gate table below; on any hard failure nothing has happened yet |
| 1a | `bump` | `<WaveeBuild> + 1`; `CHANGELOG.md` `unreleased` → today (UTC); asserts the diff touched **only** those two files |
| 2 | `notes` | `Wavee.ReleaseTool validate` → `<staging>\notes\` (`whatsnew.json`, `whatsnew-index.json`, `RELEASE_BODY.md`, `store-listing.txt`, `media/`). Reads the feed's published `whatsnew-index.json` first — a 404 means "first release", and **any other failure stops the run**: phase 10 clobbers that file, so a one-entry index would erase the published history. Exit 2 also when a referenced issue/PR could not be read from GitHub (`--allow-unresolved` ships the authored title/state anyway). On failure it restores both files and un-marks the bump |
| 1b | `tag` | commits those two files (`release: Wavee <semver> <codename> (build <quad>)`) and creates the annotated tag — **locally** |
| 3 | `packArm64` | `pack-wavee-msix.ps1 -Arch arm64 … -NoSign`, then asserts the package's identity name / version / arch / publisher |
| 4 | `packX64` | the same for x64 (or `-X64Msix <path>` to adopt a prebuilt package) |
| 5 | `sign` | **one** Azure Trusted Signing `signtool` call over every `.msix`, then verifies each |
| 6 | `appinstaller` | one `.appinstaller` per architecture from the template; re-parses each to prove the substitution |
| 7 | `stage` | flattens the assets into the staging folder and writes `MANIFEST.txt` |
| 8 | `push` | pushes the branch, then the tag — refuses unless `origin/<branch> == HEAD~1` |
| 9 | `release` | `gh release`: draft → upload → publish — always `--latest=false` (`releases/latest` belongs to the gallery's feed) |
| 10 | `feed` | repoints `wavee-stable` (and `wavee-beta` if it exists) — **always last**: this is the moment installed clients see the release |
| 11 | `verify` | assets + byte sizes vs staged, the feed live at the new quad **and pointing at this release's msix** (`MainPackage/@Uri`, not only the root `Version`), msix `Content-Length`, optional install |

**Preflight gates** (hard unless noted): version props parse · semver + quad (a `beta` semver is refused — the beta
channel needs its own package identity and is not built yet) · staging folder free (`-Force` replaces it, `-Resume`
continues it) · working tree clean · on `<Branch>` · `HEAD == origin/<Branch>` · the tag is free locally, on origin and
as a release · `CHANGELOG.md` has a `## [<semver>] - <date|unreleased>` heading · `ops/release/wavee/<semver>/whatsnew.json`
exists · the PlayPlay junction is present · Windows SDK tools · x64 cross toolchain · a Trusted Signing token ·
`gh auth` · *(soft)* whether a `wavee-beta` feed exists and will be repointed too · **feed monotonic** (the new quad
must be strictly greater than each feed's current root `Version`, on every architecture, and the semver must not go
backwards — the gate itself is always hard, but an *unreachable* feed is downgraded to a warning under `-DryRun` /
`-NoUpload`, where nothing can be published) · gates (`dotnet build Wavee.slnx` Debug **and** Release, `Wavee.Tests`, and
the release tooling's Pester suite — the engine's VerticalSlice is the engine repo's gate).

Useful switches: `-SkipTests` (skip that last gate) · `-PublicOnly` (build without PlayPlay) · `-SkipArch x64` /
`-X64Msix <path>` · `-NoUpload` (real bump and tag, stop before pushing) · `-NoSign` (only with `-DryRun` / `-NoUpload`)
· `-NoNotes` (requires `-Force`; a degraded release with a placeholder body) · `-InstallFromFeed` (phase 11 installs
the host-arch package from the published feed) · `-Force` (relaxes the branch / HEAD / staging checks).

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

Expect on the **version** release: `Wavee_<quad>_arm64.msix`, `Wavee_<quad>_x64.msix`, `whatsnew.json`, every media
file, `THIRD-PARTY-NOTICES.txt`, `MANIFEST.txt`. On **`wavee-stable`**: `Wavee.arm64.appinstaller`,
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

## 6. When something fails

| Symptom | What it means | Recovery |
|---|---|---|
| Preflight hard-fails | nothing has happened yet | fix it and re-run |
| `feed monotonic gate failed` | the new quad is not strictly greater than a live feed head | something was published out of band, or `<WaveeBuild>` was hand-edited; bump forward, never sideways |
| Notes validation fails (exit 2) | the release is not shippable; every problem is listed | fix `whatsnew.json` / `CHANGELOG.md`. The script already restored both files and un-marked the bump — just run again |
| Pack or signing fails | the tag exists **locally only** | fix, then `-Resume` (nothing is rebuilt, the counter is not bumped again); or `-Abort` to unwind |
| `origin/<branch> is X but HEAD~1 is Y` | someone pushed while the release was building | rebase the release commit onto origin, then `-Resume` |
| **Tag pushed but the upload died** | the commit and tag are public; the release may still be a draft | **`-Resume`.** Staging is hash-verified against `MANIFEST.txt` first, then the run continues from the exact phase that failed. A draft release is invisible to users, and the feed has not moved yet |
| The feed did not come up at the new quad | GitHub asset-replacement lag | `Test-WaveeFeedLive` already retried 6 × 10 s; run `-Resume` (phase 11) |
| A bad build shipped | — | see §7 |

`-Resume` restarts at the first phase that is not `done`. `-Abort` unwinds an **un-pushed** run completely: it refuses
if `pushed` is true, verifies the tag points at `HEAD` and that `HEAD`'s message is the one this run wrote, then
`git tag -d`, `git reset --hard HEAD~1`, and deletes the staging folder.

**Never delete a pushed release tag.** Installed clients, the feed's `MainPackage/@Uri` and `whatsnew-index.json` all
resolve through it; deleting it breaks installs that already point at it. A bad build is fixed by the **next patch** —
`git revert`, bump to `X.Y.Z+1`, cut it. Optionally mark the bad release a prerelease
(`gh release edit wavee-vX.Y.Z --prerelease`) so the release page flags it while the replacement builds; the feed
itself only moves when the script repoints it.

---

## 7. Rollback

Wavee's `.appinstaller` sets **`ForceUpdateFromAnyVersion="true"`**, so pointing the feed at an *older* release does
reach already-installed clients and moves them **back**. That is a real lever, and it is the only one that reaches
installs that are already out there.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-release.ps1 `
    -RepointFeed 0.2.0 -AllowDowngrade
```

It finds the release `wavee-v0.2.0`, reads the quad off its `.msix` asset names, regenerates both `.appinstaller`
documents pointing at that release, uploads them to `wavee-stable`, and waits for the feed to come up at that quad.
`whatsnew-index.json` is deliberately left alone — the index is cumulative history, not a pointer. The monotonic gate
is inverted only by `-AllowDowngrade`, which prints a warning naming the clients it will move backwards.

**Roll-forward is still the default.** A downgrade re-runs the installer on every client and cannot un-write user data
a bad build already wrote, so use it only when the shipped build is actively harmful; otherwise revert on `main`, cut
`X.Y.Z+1`, and let the normal monotonic path carry everyone forward.

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

Underneath the harness: `Invoke-Pester ops\release\tests` covers the loopback server's range arithmetic, content
types and path resolution (plus one live-listener test that self-skips when not elevated), the pure release helpers,
and the pure **evidence** helpers the rows above rest on — `ConvertTo-FeedPath`, `Get-FeedAssociationRequests` (the
feed-log association proof), `Compare-LogSnapshot` (the survived-the-update verdict) and
`ConvertTo-QuadString` / `Test-QuadMatch` / `Test-AnyQuadMatch` (the apply poll's version comparison). Those are
pure-function tests: rows in, verdict out, no listener and no processes. `Wavee.Tests` covers the update service over
a scripted HTTP handler and a fake `IPackageUpdater`.

---

## 8b. (optional) scratch feed on GitHub

The local harness above replaces this for everyday work. Keep this recipe for the one thing it cannot cover: that the
real GitHub release plumbing (draft, upload, clobber, the rolling feed tag) behaves. Every switch below redirects the
run into a throwaway namespace — a different feed release, a different tag prefix and a different branch — so the real
`wavee-stable`, the real `wavee-v*` tags and `main` are untouched. The feed name is **build-time metadata**
(`pack-wavee-msix.ps1 -FeedRelease`, stamped into the assembly), so a test package genuinely polls the test feed;
there is no env var and no runtime switch.

```powershell
git checkout -b release-test
powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\wavee-release.ps1 `
    -FeedRelease wavee-stable-test -TagPrefix wavee-test-v -Branch release-test -Force -SkipTests -InstallFromFeed
```

The sequence:

- **A.** With props at `0.2.0` / `Breaker`, run the command above. It publishes `wavee-test-v0.2.0` plus the
  `wavee-stable-test` feed and — thanks to `-InstallFromFeed` — installs the host-arch package **through the feed**, so
  the App Installer association exists. Launch it: About shows the quad, What's new renders from the embedded doc,
  `Get-AppxPackageAutoUpdateSettings` lists the test feed, and diagnostics reports `association = true`.
- **B.** Bump props to `0.2.1` (still `Breaker`), add `## [0.2.1] - unreleased`, and author
  `ops/release/wavee/0.2.1/whatsnew.json`. Re-run the same command **without** `-InstallFromFeed`. The feed head moves
  to the new quad.
- **C.** In-app path on the still-installed A: within the check cadence a toast offers the update → **Update now** →
  progress → "Restarting…" → Windows relaunches into B with the after-update dialog. Failure drills: kill the network
  mid-download (Failed › Network + Retry); press **Later** (Snoozed, no re-toast); turn the metered toggle off on a
  metered network (Failed › Metered).
- **D.** OS silent path: `Remove-AppxPackage`, then install A's bare `.msix` directly (no association). Diagnostics now
  shows `association = false` and offers **Repair auto-update**; clicking it restores the association. Close the app
  and launch again — App Installer applies B silently on that launch. This is the only place the OS path is
  observable, and the place `0x80073D02 ERROR_PACKAGES_IN_USE` shows up if a module child process survived.
- **E.** Resume / abort drills: kill the script during phase 9 → `-Resume` finishes it (the draft was never public).
  Run it once more → it fails at "tag exists". `-Abort` on an un-pushed run restores the tree.
- **F.** Cleanup:

  ```powershell
  gh release delete wavee-test-v0.2.0 --repo christosk92/WaveeMusic --cleanup-tag --yes
  gh release delete wavee-test-v0.2.1 --repo christosk92/WaveeMusic --cleanup-tag --yes
  gh release delete wavee-stable-test --repo christosk92/WaveeMusic --cleanup-tag --yes
  git push origin --delete release-test
  git tag -d wavee-test-v0.2.0 wavee-test-v0.2.1
  Get-AppxPackage cproducts.Wavee | Remove-AppxPackage
  Remove-Item artifacts\release -Recurse -Force
  ```

Underneath the manual sequence: `Wavee.Tests` (tokenizer, changelog parser, range stacking, issue budget, the toast
decision table, the update service over a scripted HTTP handler + a fake `IPackageUpdater`),
`Invoke-Pester ops\release\tests` over the pure release helpers, the ReleaseTool fixture folders, VerticalSlice, and
the in-app simulator.

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
| **Every state, no network** | Settings › Diagnostics › **Simulate update** | walks the full state machine, including each failure kind |

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
- `.claude/skills/releasing/SKILL.md` — signing config, secrets, troubleshooting.
- `docs/guide/playplay-private-split.md` — the PlayPlay junction and what `-PublicOnly` leaves out.
