# Wavee auto-update + "What's new" — research dossier (synthesis, 2026-08-29)

Synthesized from four investigator reports (MSIX update mechanics, changelog/release-notes pipelines, version naming +
channels, codebase hooks) plus re-verification of every contested fact against the primary page or a live probe on
2026-08-29. Markers: **VERIFIED** = the primary doc/header/file/live response was read this session; **BELIEVED** =
secondary source or inference, flagged inline. Repo paths are relative to `C:\wavee\fluent-gpu\`. A visual prototype of
the target UX already exists at `docs/plans/wavee/auto-update-whatsnew-mica.html` (Mica mock, not code).

---

## 1. Executive summary

**State of the world today (all VERIFIED):**

- The in-app "Download" button is broken for every consumer PC. `AppInstallerUpdateService.DownloadAsync`
  (`src/apps/Wavee/App/AppInstallerUpdateService.cs` L120-139) shells `ms-appinstaller:?source=<feed>`; Microsoft
  disabled that protocol by default in App Installer 1.21.3421.0 (2023-12-12) and its status page (reviewed Aug 2026)
  lists "Consumer devices (default): Disabled", re-enable only via the `EnableMSAppInstallerProtocol` Group Policy
  (https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/distribution-feature-status). The service then
  publishes `AppUpdateState.Downloaded` (L130) although nothing was downloaded — a false state shown to the user.
- The update feed URL 404s right now. `releases/latest/download/Wavee.x64.appinstaller` → 302 →
  `releases/download/v0.1.2/Wavee.x64.appinstaller` → 404 (live curl this session). GitHub's `latest` is repo-global —
  "the most recent non-prerelease, non-draft release, sorted by the created_at attribute"
  (https://docs.github.com/en/rest/releases/releases#get-the-latest-release) — and the repo's only releases are the
  gallery's `v0.1.0`–`v0.1.2` (live API this session; no `wavee-v*` release exists). Every future gallery release would
  hijack Wavee's feed again. Both the OS auto-update and `CheckAsync` (which reports `Failed`) are affected.
- The rollback runbook is wrong. `docs/guide/releasing-wavee.md` §5 (L120-122) says "App Installer only moves forward";
  Microsoft: `ForceUpdateFromAnyVersion` "Allows the app to update from version x to version x++ or to downgrade from
  version x to version x--" (https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings) and Wavee's
  template already ships it `true` (`ops/build/Wavee.AppInstaller.template.xml` L33).
- A prerelease tag cannot build. `wavee-v0.3.0-beta.1` → `version=0.3.0-beta.1.<run>` (`.github/workflows/wavee-msix.yml`
  L47) → `pack-wavee-msix.ps1` L41 throws (`^\d+\.\d+\.\d+\.\d+$`).
- The `.appinstaller` prompt attributes the template relies on (`ShowPrompt="true"`, comment L9-10) are documented as
  not shown for desktop-bridge apps: "currently shows a prompt for UWP applications but not for desktop applications
  that have been packaged in a Windows app package … this functionality provides a silent update"
  (https://learn.microsoft.com/en-us/uwp/schemas/appinstallerschema/element-onlaunch). Wavee is `runFullTrust` → the OS
  path is silent-on-next-launch, full stop.
- Nothing ships release notes: `IAppUpdateService` (`src/apps/Wavee.Core/Notifications/AppUpdate.cs` L14-27) carries a
  URL string only; `CHANGELOG.md` is never read by CI (`generate_release_notes: true`, workflow L140).

**Recommended architecture in 10 bullets:**

1. **Feed = product-scoped rolling releases, not `releases/latest`.** CI publishes each real tag's `.msix` under
   `wavee-vX.Y.Z` and re-uploads the two `.appinstaller` files (+ `whatsnew-index.json`) into a fixed rolling release
   `wavee-stable` (later `wavee-beta`). Feed URL: `releases/download/wavee-stable/Wavee.<arch>.appinstaller`. Also set
   `make_latest: false` on the gallery workflow as a second fence. Zero migration cost: no Wavee release exists yet.
2. **Delete the `ms-appinstaller:` path outright** (no fallback, no env switch). Replace with hand-rolled WinRT
   `PackageManager` interop in `FluentGpu.WindowsApi/Packaging/` (the `ToastNotifier`/`HStringHandle` pattern).
3. **"Update now" = the Files-app pattern:** `RegisterApplicationRestart` → `IPackageManager6.AddPackageByAppInstallerFileAsync(feedUri,
   ForceTargetAppShutdown, GetDefaultPackageVolume())` with `DeploymentProgress` wired to the in-app panel + an OS
   progress toast; Windows kills and relaunches Wavee. Microsoft's own rule: apps deployed via `.appinstaller` "must make
   use of the App Installer file APIs" for code-driven updates (https://learn.microsoft.com/en-us/windows/msix/non-store-developer-updates).
4. **"Later" = snooze; the OS applies silently on next launch** (OnLaunch + AutomaticBackgroundTask stay in the feed).
   Deferred in-process staging (`AddPackageByUriAsync` + `DeferRegistrationWhenPackagesAreInUse`) is a phase-2 option
   for Win11 22H2+ only, never with `DependencyPackageUris`.
5. **Declare `rescap:packageManagement`** in the sideload manifest (sideload needs no approval); strip for any Store build.
6. **Raise `MinVersion` to `10.0.19041.0`** (Windows 10 2004): sideloading is on by default there, `AddPackageByUriAsync`
   exists, every `.appinstaller` setting is ≥1903, and Windows 10 mainstream support ended 2025-10-14
   (https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/distribution-feature-status). Keep the `2018`
   feed namespace (prompt attributes are moot for desktop apps anyway); verify on a Win10 22H2 VM.
7. **Versioning = "Trains":** SemVer tag `wavee-vX.Y.Z[-beta.N]`, one hand-picked codename per MINOR (sea/wave series:
   Breaker, Crest, Drift…), MSIX quad `X.Y.Z.(run_number + committed offset)`, a committed `Wavee.Version.props` as the
   single source (version + codename + offset), a CI gate that refuses any publish whose quad is not strictly greater
   than the target feed's current root `Version`. Beta = a separate package identity (`cproducts.Wavee.Beta`) with its
   own feed and data root (phase 2; design the manifest/pack templating for it now).
8. **Changelog = hand-written `CHANGELOG.md` (Keep a Changelog 1.1 + `Known limitations`) + hand-written
   `ops/release/wavee/<semver>/whatsnew.json`** (codename, tagline, hero highlights with webp/mp4 media + `wavee://` deep
   links, notices). A tiny AOT console tool (`Wavee.ReleaseTool`, sharing the app's STJ source-gen model) validates both
   on the tag, snapshots issue titles/states, emits the merged `whatsnew.json`, a ≤12-entry `whatsnew-index.json`, and the
   GitHub release body (`generate_release_notes: false`; generated PR list appended as a folded appendix).
9. **Notes are embedded in the MSIX AND published as release assets.** First launch after an update reads the embedded
   JSON (zero network); the "update available" toast previews the next version via one quota-free asset GET; the GitHub
   REST API is spent only on live issue-state refresh (≤20 calls per page open, once per 24 h per issue, 304s count).
10. **In-app UX:** `wavee://open?route=whatsnew&arg=<version>` → a `WhatsNewPage` (hero cards, grouped changelog, issue
    chips with open/closed badges, stacking since `LastRunVersion`), auto-opened once after an update (setting, default
    on), reachable from Settings › About + the notification centre; a markdown-lite inline tokenizer (pure class, unit
    tested) renders item text; `IAppUpdateService` gains `Downloading/Installing` states + progress + a notes snapshot.

---

## 2. Update mechanics

### 2.1 Decision table

| Path | OS requirement | What the user sees | Verdict |
|---|---|---|---|
| **A. OS-driven `.appinstaller`** (OnLaunch/AutomaticBackgroundTask) | 1803+ for the background task; 1903+ for `ShowPrompt`/`UpdateBlocksActivation`/`ForceUpdateFromAnyVersion` per the update-settings table (VERIFIED https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings); the `element-onlaunch` page says `ForceUpdateFromAnyVersion` is 1809 — a doc conflict, moot at MinVersion 19041 | Nothing while running. Desktop-bridge apps get NO prompt (VERIFIED element-onlaunch remarks); update is staged and registers on the next launch; a surviving package process at that moment → `0x80073D02 ERROR_PACKAGES_IN_USE` and the old version re-registers (VERIFIED winerror; BELIEVED sequencing from Claude Desktop case https://github.com/anthropics/claude-code/issues/63397) | **Keep as the safety net.** Only installs that went through the `.appinstaller` get the association; a plain `.msix` install never joins (VERIFIED: the dev box's Wavee has no entry in `Get-AppxPackageAutoUpdateSettings`). |
| **B. In-app `PackageManager` (App Installer file APIs)** | `AddPackageByAppInstallerFileAsync` 16299+, capability row lists `packageManagement` (VERIFIED https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.addpackagebyappinstallerfileasync); `CheckUpdateAvailabilityAsync` 17763+ (VERIFIED) | Wavee's own toast/panel with real progress; on "Update now" Windows closes and relaunches Wavee (Files app ships exactly this: https://github.com/files-community/Files/blob/main/src/Files.App/Services/App/AppUpdateSideloadService.cs) | **Chosen core path.** |
| **C. `ms-appinstaller:` protocol** (today's `DownloadAsync`) | Disabled by default since App Installer 1.21.3421.0 (VERIFIED distribution-feature-status; https://www.microsoft.com/en-us/msrc/blog/2023/12/microsoft-addresses-app-installer-abuse) | App Installer opens and refuses (exact UI not observed; docs: "no longer work for most users") | **Broken on stock Windows 11. Remove.** |
| **D. Download `.msix`/`.appinstaller` in-app, `ShellExecute` it** | any | Modal App Installer dialog (Install/Update/**Reinstall** on downgrade), full download, fails if Wavee still running | Fallback only for the **unpackaged** build; never the packaged path. |
| **E. `AddPackageByUriAsync(msix, {DeferRegistrationWhenPackagesAreInUse, ForceUpdateFromAnyVersion, ExpectedDigests})`** | 19041+; `.appinstaller` URIs only from build 22556 (Win11 22H2) (VERIFIED https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.addpackagebyuriasync) | "Installed, restart to apply" (`DeploymentResult.IsRegistered=false`); old keeps running, new registers on next launch — only when `DependencyPackageUris` is empty (VERIFIED https://github.com/microsoft/WindowsAppSDK/issues/4827) | Phase 2, Win11 22H2+ only, using the `.appinstaller` URI so the OS association survives. |

### 2.2 Chosen path and fallback ladder

1. **Check** (existing 30 s / 24 h scheduler): GET the rolling feed `.appinstaller`, read root `Version` (existing
   `ReadFeedVersionAsync`). Additionally, when packaged, call `IPackageManager.FindPackageForUser("", PackageFullName)`
   → `IPackage6.CheckUpdateAvailabilityAsync()` — VERIFIED: "currently only works for applications installed via
   .appinstaller files" and "If you try to use this method on the Package object returned by the Current property, this
   method will fail with an 'Access denied' error" (https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.package.checkupdateavailabilityasync).
   `Unknown=0` means "no App Installer association" → surface that on the diagnostics page (the user installed from a
   bare `.msix`; offer "Repair auto-update" = `ShellExecute` the `.appinstaller`, which recreates the association).
2. **Update now:** `RegisterApplicationRestart(null, 0)` (kernel32; 60 s minimum uptime rule — VERIFIED
   https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-registerapplicationrestart) → kill every
   package-identity child process (out-of-proc modules) → `IPackageManager6.AddPackageByAppInstallerFileAsync(feedUri,
   ForceTargetAppShutdown (0x40), IPackageManager3.GetDefaultPackageVolume())` → subscribe progress
   (`DeploymentProgress {state Queued=0|Processing=1, percentage u32}`, coarse — VERIFIED
   https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.deploymentprogress) → Windows terminates and
   relaunches. Microsoft: "For non-UWP apps you need to call RegisterApplicationRestart before applying the update"
   (VERIFIED non-store-developer-updates).
3. **Later:** snooze (persist `app.update.snoozedVersion`); the OS `.appinstaller` machinery applies it silently on the
   next launch. No in-process staging in v1.
4. **Failure:** `DeploymentResult.ExtendedErrorCode/ErrorText` → `Failed` with the HRESULT mapped (`0x80073D02` in use,
   `0x80073D06` downgrade without Force, `0x80073CFB` same version different bits, `0x80073CFF` sideload policy,
   `0x80072F76` missing Content-Length — VERIFIED https://learn.microsoft.com/en-us/windows/win32/appxpkg/troubleshooting)
   → offer "Open release page" (browser) as the last rung.
5. **Unpackaged build:** open the release page only (today's behaviour, honest).

### 2.3 Facts the implementation depends on

- **Capability:** "In order to apply updates from your code, your app package must declare the packageManagement
  capability. Note that this is required for cross-publisher scenario, but managing your own app should work without
  having to declare the capability" (VERIFIED non-store-developer-updates) — yet the API page lists it. Resolution:
  declare `<rescap:Capability Name="packageManagement"/>`; "you can sideload apps that declare restricted capabilities
  without needing to receive any approval" (VERIFIED https://learn.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations).
- **GitHub asset HTTP semantics (live probe today):** `github.com/.../releases/download/<tag>/<asset>` → 302 with
  `Cache-Control: no-cache` → `release-assets.githubusercontent.com/...?se=<~1 h expiry>` → 200 with `Content-Length`
  and `Accept-Ranges: bytes`; range requests return 206. That satisfies App Installer's documented hosting needs
  (byte ranges + Content-Length on GET and HEAD — VERIFIED https://learn.microsoft.com/en-us/windows/msix/app-installer/troubleshoot-appinstaller-issues).
  The `.appinstaller` is served as `application/octet-stream`; BELIEVED the deployment engine does not enforce MIME
  (the gallery's OS auto-update through the same host works on this box: `LastCheckedForUpdates 6/14/2026`).
  Never persist the signed URL — re-resolve the redirect on every fetch.
- **Differential download:** 64 KB SHA-256 blocks in `AppxBlockMap.xml`, only for lower→higher updates (VERIFIED
  https://learn.microsoft.com/en-us/windows/msix/app-package-updates). A NativeAOT single EXE changes almost entirely per
  release → expect near-full downloads (BELIEVED). Metered-network behaviour of the OS updater is undocumented; gate the
  in-app path on `NetworkPolicy.IsMetered`.
- **Restart:** `CoreApplication.RequestRestartAsync` is UWP-only; WinAppSDK `AppInstance.Restart` unavailable (no WinAppSDK).
  Manual relaunch fallback: `IApplicationActivationManager::ActivateApplication` (CLSID `45BA127D-10A8-46EA-8AB7-56EA9078943C`)
  with AUMID `cproducts.Wavee_43x7j183z9t4g!Wavee` — only after the last package process has exited.
- **Hand-rolled IIDs (VERIFIED from Windows SDK 10.0.26100 `windows.management.deployment.h`):** `IPackageManager`
  `9a7d4b65-5e8f-4fc7-a2e5-7f6925cb8b53` (slot `FindPackageByUserSecurityIdPackageFullName`); `IPackageManager3`
  `daad9948-36f1-41a7-9188-bc263e0dcb72` (`GetDefaultPackageVolume`); `IPackageManager6`
  `0847e909-53cd-4e4f-832e-57d180f6e447` (`AddPackageByAppInstallerFileAsync`); `IPackageManager9`
  `1aa79035-cc71-4b2e-80a6-c7041d8579a7` (`AddPackageByUriAsync`); `IAddPackageOptions`
  `05cee018-f68f-422b-95a4-66679ec77fc0`; `IAsyncOperationWithProgress<DeploymentResult,DeploymentProgress>`
  `5a97aab7-b6ea-55ac-a5dc-d5b164d94e94` (progress handler `f1b926d1-1796-597a-9bea-6c6449d03eef`, completed handler
  `6e1c7129-61e0-5d88-9fd4-f3ce65a05719`); `IDeploymentResult` `2563b9ae-b77d-4c1f-8a7b-20e6ad515ef3`;
  `IDeploymentResult2` (`IsRegistered`) `fc0e715c-5a01-4bd7-bcf1-381c8c82e04a`; `IAsyncOperation<PackageUpdateAvailabilityResult>`
  `010bd015-43ef-576c-be1e-bc38c5b6b66b`; `IPackageUpdateAvailabilityResult` `114e5009-199a-48a1-a079-313c45634a71`;
  `IAppInstallerInfo` `29ab2ac0-d4f6-42a3-adcd-d6583c659508`; `IAsyncInfo` `00000036-0000-0000-C000-000000000046`.
  Runtime classes: `Windows.Management.Deployment.PackageManager` (activatable via `RoActivateInstance`),
  `Windows.ApplicationModel.Package` (statics `4e534bdf-2960-4878-97a4-9624deb72f2d`). BELIEVED: TerraFX.Interop.Windows
  does not project `Windows.Management.Deployment` → hand vtables (like `IStringMap` in `ToastInterop.cs` L74-95).
- **App Installer regressions outside our control:** 1.27.350.0 broke `.appinstaller` updates with `0x80070057`
  (https://github.com/microsoft/winget-cli/issues/5908; https://github.com/microsoft/WindowsAppSDK/issues/6056). The
  diagnostics page must show App Installer version + `AppInstallerInfo.LastChecked/PausedUntil`.

---

## 3. Versioning + channels

### 3.1 Chosen scheme ("Trains"), fully worked

| Stage | Stable example | Beta example (phase 2) |
|---|---|---|
| Git tag (the only release trigger) | `wavee-v0.3.1` | `wavee-v0.4.0-beta.2` |
| `src/apps/Wavee/Wavee.Version.props` (committed; replaces `<Version>` in the csproj) | `WaveeVersion=0.3.1`, `WaveeCodename=Breaker`, `WaveeBuildOffset=0` | `0.4.0`, `Crest`, `0` |
| CI `version` job | parses tag → `M.m.p` + optional `-pre.N`; **fails if `M.m.p` ≠ props**; `quad = M.m.p.(run_number+offset)`; `channel = pre ? beta : stable` | |
| MSIX `Identity/@Version` (4×UINT16, first ≠ 0, Store-only "4th must be 0" rule — VERIFIED https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements) | `0.3.1.1050` | `0.4.0.1058` |
| Identity / DisplayName | `cproducts.Wavee` / "Wavee" | `cproducts.Wavee.Beta` / "Wavee Beta" (own data root `%LOCALAPPDATA%\Wavee.Beta`, own `wavee-beta://` scheme, own toast CLSID, own StartupTask id) |
| `InformationalVersion` + `AssemblyMetadata` | `0.3.1+build.1050.sha.d4e5f6a`; metadata `Codename`, `Channel`, `Commit`, `BuildDate`, `PackageVersion` | `0.4.0-beta.2+build.1058.sha.a1b2c3d` |
| Display (About hero) | `Wavee 0.3.1 "Breaker"` / `build 0.3.1.1050 · d4e5f6a · 2026-08-30 · x64 · MSIX` | `Wavee 0.4.0 "Crest" · Beta 2 · …` |
| One-line form (crash header, diagnostics copy, User-Agent) | `Wavee/0.3.1 (build 0.3.1.1050; stable; Windows 10.0.26200; x64)` — RFC 9110 product token (VERIFIED https://httpwg.org/specs/rfc9110.html#field.user-agent); ThirdParty HttpClient only, never the Spotify-facing UA | |
| GitHub release | `wavee-v0.3.1`, name `Wavee 0.3.1 — Breaker`, `make_latest: true` | `prerelease: true` |
| Rolling feed release updated | `wavee-stable` (both arches' `.appinstaller` + `whatsnew-index.json`) and, once beta exists, also `wavee-beta` (finals go to both channels) | `wavee-beta` only |
| Feed URL baked into the `.appinstaller` root `Uri` | `https://github.com/christosk92/WaveeMusic/releases/download/wavee-stable/Wavee.<arch>.appinstaller` | `.../wavee-beta/Wavee.Beta.<arch>.appinstaller` |
| Codename rule | one per MINOR; patches and betas inherit; a MAJOR starts the next lap of the series | |
| CI monotonic gate | GET each target feed's current root `Version`; fail unless new quad > current AND new `M.m.p` ≥ feed `M.m.p`; fail if `run+offset > 65535` | |

Facts underpinning it: `github.run_number` is per-workflow, starts at 1, unchanged on re-run (VERIFIED
https://docs.github.com/en/actions/writing-workflows/choosing-what-your-workflow-does/accessing-contextual-information-about-workflow-runs);
BELIEVED it resets if the workflow file is renamed (https://github.com/orgs/community/discussions/26709) — hence the
committed offset + gate. Same quad, different bits → `0x80073CFB` (BELIEVED
https://learn.microsoft.com/en-us/answers/questions/2197947/add-appxpackage-deployment-failed-with-hresult-0x8). The
in-app comparer `AppUpdateVersion.IsNewer` (strips `-pre`/`+meta`, 4-part numeric) stays the only comparison key; the
codename never enters the version string.

### 3.2 Alternative considered

Single identity with stage encoded in PATCH (`M.m.(patch*100+stage).run`, `99` = final) gives structural beta<final
ordering on one feed but shows an unreadable quad in Settings, needs a decoder in every reader, and has no peer
precedent; CalVer (`YYYY.MMDD.n.0`) is counter-free and Store-clean but carries no magnitude and renumbers on slips.
Windows Terminal (`Microsoft.WindowsTerminalPreview`, even/odd minors — VERIFIED https://learn.microsoft.com/en-us/windows/terminal/install)
and Files (`FilesCommunity.FilesPreview`) both use separate identities, which is what makes beta-vs-stable ordering
irrelevant to the OS.

### 3.3 Pitfalls

| Pitfall | Trigger | Mitigation |
|---|---|---|
| Prerelease tag breaks the build | `pack-wavee-msix.ps1` L41 regex | Pass only the computed quad to `-Version`; semver goes to `InformationalVersion` |
| Run-number reset / re-run of an old run | workflow rename; `gh run rerun` | Offset in props + feed-head gate; never re-run release runs, re-tag |
| Hotfix after a beta on one feed | `0.3.2` published while feed head is `0.4.0-beta.1` | Per-channel feeds; gate compares semver too; merge fix forward |
| `ForceUpdateFromAnyVersion=true` = silent downgrade if a lower version reaches the feed | misdirected publish | Keep the flag (it is the only "roll back the feed" lever); the gate is the guard; fix runbook §5 |
| Store future | `0.x` major and non-zero 4th part are both Store-rejects | Reach 1.0 first; Store builds `.0` with a higher `M.m.p` than any sideloaded quad; strip `packageManagement` |
| Rolling release asset replacement | two-file feed replaced per release | BELIEVED softprops overwrites same-name assets atomically enough; verify no stale CDN on first real release |
| `.appinstaller` schema vs OS | `2018` namespace on Win10 | Keep 2018 (superset of 2017/2; prompts moot for desktop apps); test on a Win10 22H2 VM; `2021` only if `UpdateUris` fallback is ever wanted (Win11-only) |

### 3.4 Codename series (proposals)

1. **Sea states / wave anatomy (alphabetical, strongest brand fit):** Abyss, Breaker, Crest, Drift, Ebb, Fetch,
   Groundswell, Harbor, Inlet, Jetty, Kelp, Lagoon, Maelstrom, Neap, Offshore, Pipeline, Quay, Riptide, Swell, Trough,
   Undertow, Vortex, Whitecap, Xebec, Yaw, Zephyr.
2. Italian tempo marks (Adagio, Brillante, Cantabile, Dolce, Energico, Forte…) — gaps at H/J/K/Q/U/W/X/Y/Z.
3. Studio/synthesis vocabulary (Attack, Bitcrush, Chorus, Decay, Echo, Flanger, Gain… Wavetable).
Precedents: Ubuntu adjective+animal alphabetical per release (VERIFIED https://en.wikipedia.org/wiki/Ubuntu_version_history);
macOS California places then year-based (VERIFIED https://en.wikipedia.org/wiki/MacOS_version_history); Android dropped
public dessert names at 10 (VERIFIED https://en.wikipedia.org/wiki/Android_version_history).

---

## 4. Changelog / "What's new"

### 4.1 Authoring pipeline

- `CHANGELOG.md` stays the single human source of the structured changelog — Keep a Changelog 1.1 (`## [X.Y.Z] - YYYY-MM-DD`,
  `### Added/Changed/Deprecated/Removed/Fixed/Security`, `[Unreleased]` on top — VERIFIED https://keepachangelog.com/en/1.1.0/)
  plus the existing custom `### Known limitations`. Bullets may reference `#123`, `owner/repo#123`, `@handle`; CI lifts them.
- `ops/release/wavee/<semver>/whatsnew.json` (hand-written) + `media/*.webp|*.mp4` holds what the changelog cannot:
  codename, tagline, hero highlights with media and `wavee://` deep links, notices, `minOs`.
- Not adopted: release-please (commit-derived prose — VERIFIED https://github.com/googleapis/release-please), changesets
  (Node/monorepo), towncrier fragments (only worth it with ≥2 concurrent authors — VERIFIED
  https://towncrier.readthedocs.io/en/stable/tutorial.html), git-cliff (useful only for the contributor appendix; needs a token —
  VERIFIED https://git-cliff.org/docs/integration/github).

### 4.2 CI validation (new job before `build`; fails the tag)

- `CHANGELOG.md` has `## [X.Y.Z] - <real date>` for the tag (not "unreleased"); `X.Y.Z` equals `Wavee.Version.props`.
- `whatsnew.json` validates against the STJ source-gen model; every media ref exists, ≤150 KB per webp still, ≤600 KB
  per animated webp/mp4, ≤1.5 MB total; no GIF.
- Every referenced issue/PR exists (`gh api repos/…/issues/n`, CI token) — snapshot `title/state/state_reason/pull_request`
  into the emitted JSON with `generatedAt`.
- Emits: merged `whatsnew.json`, `whatsnew-index.json` (≤12 newest `{version,name,date,channel}`), `RELEASE_BODY.md`
  (hero + changelog section + `<details>`-folded PR/contributor appendix from `POST /repos/{o}/{r}/releases/generate-notes` —
  VERIFIED https://docs.github.com/en/repositories/releasing-projects-on-github/automatically-generated-release-notes;
  softprops prepends generated notes to a custom body when both are on, so set `generate_release_notes: false` and
  `body_path` — VERIFIED https://github.com/softprops/action-gh-release).
- `pack-wavee-msix.ps1` copies `whatsnew.json` + current media + prior index into the package (`Assets/whatsnew/`).

### 4.3 `whatsnew.json` schema (per release; flat, no polymorphism, unknown members ignored)

```jsonc
{
  "schema": 1,
  "product": "wavee",
  "version": "0.3.0",                      // semver as tagged (wavee-v0.3.0)
  "packageVersion": "0.3.0.1047",          // MSIX quad; exact match against PackageIdentity.Version
  "name": "Breaker",                       // codename (per MINOR)
  "tagline": "Lyrics that follow you, and a lighter first run.",
  "date": "2026-08-27",
  "channel": "stable",                     // stable | beta
  "lang": "en",
  "minOs": "10.0.19041.0",
  "arch": ["x64", "arm64"],
  "links": {
    "release":   "https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.3.0",
    "changelog": "https://github.com/christosk92/WaveeMusic/blob/wavee-v0.3.0/CHANGELOG.md",
    "compare":   "https://github.com/christosk92/WaveeMusic/compare/wavee-v0.2.0...wavee-v0.3.0"
  },
  "highlights": [
    {
      "id": "synced-lyrics-overlay",
      "title": "Synced lyrics, anywhere",
      "body": "Lyrics ride along in the **mini player** and full-screen view. Press `L` to toggle.",
      "media": { "kind": "video", "src": "media/lyrics.mp4", "poster": "media/lyrics.webp",
                 "alt": "Lyrics in the mini player", "width": 1200, "height": 675, "bytes": 512000 },
      "deepLink": "wavee://now-playing?lyrics=1",
      "issues": [412]
    }
  ],
  "sections": [
    { "kind": "added",                     // added|changed|fixed|removed|deprecated|security|known
      "items": [
        { "id": "s1",
          "text": "**Developer mode** — a Settings toggle that gates the diagnostic surfaces.",
          "issues": [ { "repo": "christosk92/WaveeMusic", "number": 388, "title": "Hide dev tools by default",
                        "state": "closed", "stateReason": "completed", "isPullRequest": false } ],
          "prs":    [ { "repo": "christosk92/WaveeMusic", "number": 401, "title": "feat: developer mode" } ],
          "contributors": [ { "login": "ChristosKarapasias", "firstTime": false } ] } ] }
  ],
  "notices": [ { "kind": "breaking", "text": "Environment-variable switches are gone; use Settings." } ],
  "contributors": [ { "login": "someone", "firstTime": true } ],
  "generatedAt": "2026-08-27T14:03:11Z",   // issue-state snapshot time ("as of")
  "media": [ { "src": "media/lyrics.webp", "bytes": 91234, "sha256": "…" } ]
}
```
`whatsnew-index.json`: `{ "schema": 1, "product": "wavee", "releases": [ { "version", "name", "date", "channel" }, … ] }`,
newest first. One `[JsonSerializable(typeof(WhatsNewDocument))]` context in `Wavee.Core` shared by app and tool;
collections as `List<T>`/`T[]`, enums via `JsonStringEnumConverter<T>`.

### 4.4 GitHub API facts

- Unauthenticated REST: 60 requests/hour per IP (VERIFIED https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api).
  A 304 from a conditional request is free **only** "while correctly authorized with an Authorization header" — an
  unauthenticated 304 still costs one (VERIFIED https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api).
  Wavee ships no token → budget the REST API; treat 200 and 304 alike.
- Release-asset downloads are not REST calls (documented link patterns —
  VERIFIED https://docs.github.com/en/repositories/releasing-projects-on-github/linking-to-releases); assets ≤2 GiB each,
  ≤1000 per release, no bandwidth cap (VERIFIED https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases).
- `GET /repos/{o}/{r}/issues/{n}` → `state`, `state_reason` (`completed|reopened|not_planned|duplicate|null`), `title`,
  `html_url`, `labels`, `closed_at`, and `pull_request{}` when the number is a PR (VERIFIED
  https://docs.github.com/en/rest/issues/issues#get-an-issue). No unauthenticated batch endpoint.
- BELIEVED: `api.github.com` returns 403 without a `User-Agent`; the `ThirdParty` HttpClient (`HttpPools.cs`) sends none
  today — set the RFC 9110 product token on a dedicated client.
- Client policy: issue-state refresh only when the What's new page is open, ≤20 requests per opening, ≤1 per issue per
  24 h, stop on `403`/`x-ratelimit-remaining: 0`, fall back to the snapshot with "as of <generatedAt>".

### 4.5 Media hosting

Embedded in the MSIX (current release; zero network at the exact moment "What's new" fires; covered by the signature)
+ release assets for older/next releases (302 → signed URL, cache by `(tag, name)` under `%LOCALAPPDATA%\Wavee\cache\whatsnew\`).
`raw.githubusercontent.com` (BELIEVED `max-age=300`) is acceptable but bloats the repo. Engine has an image pipeline
(`ImageEl` → `DefaultImageFetcher`, PNG/JPEG by default, WebP opt-in via Accept) and `Media/MediaPlayerElement` for mp4;
no GIF/Lottie — do not plan for them.

### 4.6 In-app UX anatomy

Patterns: VS Code `update.showReleaseNotes` default true (VERIFIED
https://raw.githubusercontent.com/microsoft/vscode/main/src/vs/platform/update/common/update.config.contribution.ts);
PowerToys SCOOBE page: hero image extraction, `#NNNN` autolinks, grouping by `major.minor` (VERIFIED
https://raw.githubusercontent.com/microsoft/PowerToys/main/src/settings-ui/Settings.UI/SettingsXAML/OOBE/Views/ScoobeReleaseNotesPage.xaml.cs);
Files opens a notes tab after updating (BELIEVED https://files.community/blog/posts/v3-9-7).

- Header: `Wavee 0.3.0 "Breaker"`, date, channel pill, Installed/Available state, GitHub link.
- Hero: 1–3 highlight cards (poster first; autoplay only when reduced-motion is off — reduced motion is a value in the
  engine), title, 1–2 sentence body, optional "Try it" deep link.
- Body: Added → Changed → Fixed → Removed/Deprecated → Security → Known limitations; collapse past ~8 items; items in
  markdown-lite (`**bold**`, `*italic*` (rendered as weight — the shaper has no italic axis), `` `code` ``, `[text](url)`,
  bare URLs, `#123`, `owner/repo#123`, `@handle`, `\` escapes; no headings/tables/images/HTML inside items).
- Issue chips: link immediately with a neutral badge; upgrade to open / closed-completed / not planned / duplicate /
  PR-merged when the API answers; hover shows title.
- Stacking: when `lastRun < current−1`, "Since you last opened Wavee (0.2.0 → 0.3.1)", one section per release, newest
  first; highlights merged at top.
- Surfacing: auto-open once after update (setting `app.whatsnew.autoShow`, default on); unread dot on Settings › About;
  notification-centre card on `Completed`; toast copy for `Available` = codename + first highlight title.
- Degraded: embedded content always renders; remote media placeholders; never a blocking spinner; diagnostics page lists
  cache path, last fetch, rate-limit headers, embedded-vs-remote source.
- Localization: English content, localized chrome; `lang` + optional `whatsnew.<bcp47>.json` overlay keyed by `id`.
- Store (future): the "What's new in this version" listing field is plain text, 1500 chars (VERIFIED
  https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info) — the
  tool can emit it from `tagline` + highlight titles.

---

## 5. Codebase hooks (all VERIFIED by reading the file this session)

| File | Lines | Seam / what to extend |
|---|---|---|
| `src/apps/Wavee.Core/Notifications/AppUpdate.cs` | L9 `enum AppUpdateState { None, Available, Downloaded, Completed, Failed }`; L14-27 `IAppUpdateService { Current, Version, ReleaseNotesUrl, Error, Changed, CheckAsync, DownloadAsync, RestartToApply, Acknowledge }`; L30-42 `NullAppUpdateService` | Widen: add `Downloading`, `Installing`, `Snoozed` states, an `AppUpdateProgress` snapshot (percent, state), `WhatsNewDocument? Notes`, `bool AutoUpdateAssociated`; rename `DownloadAsync` → `ApplyAsync`. Doc comment L7-8 ("no real updater yet") is stale. |
| `src/apps/Wavee.Core/Notifications/AppUpdateVersion.cs` | L22 `IsNewer`, L39 `IsFirstRunAfterUpdate`, L48 `ReleaseTagVersion` | Keep as the comparison key; add `WaveeVersionInfo` (semver, quad, codename, channel, commit, date) parse/format + "releases in `(lastRun, current]`" range logic — pure, unit-tested. |
| `src/apps/Wavee/App/AppInstallerUpdateService.cs` | L33 `RepoUrl`; L48 `Instance`; L59-79 ctor (feed L65 = `releases/latest/download/Wavee.<arch>.appinstaller`; `LastRunVersion` compare → `Completed` L70-76, write L77); L81-118 `CheckAsync` (preserves `Completed` on "up to date" L103-106); **L120-139 `DownloadAsync` = `Process.Start("ms-appinstaller:?source=…")` then `Publish(Downloaded)` L130**; L143 `RestartToApply` = same; L147-170 `ReadFeedVersionAsync` (XmlReader, DTD prohibited); L172 `ReleaseNotesFor` (`releases/tag/wavee-v<X.Y.Z>`); L175-182 `Publish` (no lock, fires on the calling thread) | Replace `DownloadAsync` outright with the `PackageManager` apply; feed URL from the rolling release; keep ctor signature `(IAppSettings, HttpClient, string currentVersion, string arch, IWaveeLog)` or update `Services.cs` L348; keep the `Completed`-survives rule and "never prompt from a dev/unparsable version". |
| `src/apps/Wavee/App/AppUpdateScheduler.cs` | L20 30 s first delay; L24 1 h launch cooldown; L49 `PeriodicTimer` 24 h; L27 `Start(svc, settings)` | Unchanged cadence; add metered gating for the notes prefetch. |
| `src/apps/Wavee/App/PlaybackBridge.cs` | L822-823 `AppUpdateScheduler.Start(updater, updateSettings)` inside `Activate` (real window only) | Start site stays. |
| `src/apps/Wavee/App/Services.cs` | L348 `AppUpdate = new AppInstallerUpdateService(settings, HttpPools.Get(HttpPool.ThirdParty), AppVersion.Current, <arch>, Log)`; L349 `NotificationCenterBridge(…, AppUpdate, …)` | Inject the new `IPackageUpdater` (WindowsApi) + notes store; `HostVersion` (L420-424) is a second version reader to unify. |
| `src/apps/Wavee/App/AppVersion.cs` | L16-35 `Current` (InformationalVersion, `+meta` stripped), `IsDev` | Surface commit/date/codename from `AssemblyMetadata` instead of stripping. |
| `src/apps/Wavee/Platform/AppSettings.cs` | L297 `LastRunVersion ("app.lastRunVersion")`, L298 `UpdateLastCheckedMs`, L226 `NotifyAppUpdates`, L300 `PendingCrashReport` (the "next launch" pattern); L367-420 `AppDataSettings` scalar-only `Get/Set` (registry HKCU) | Add `app.update.snoozedVersion`, `app.whatsnew.autoShow`, `app.whatsnew.lastSeenVersion`; structured data → JSON file under `SettingsShared.AppDataRoot` (`%LOCALAPPDATA%\Wavee`). |
| `src/apps/Wavee/App/NotificationCenterBridge.cs` | L62-72 `Activate` subscribes `_update.Changed → post(Rebuild)`; L164-166 builds `AppUpdateNotification` when `Current != None` | Add progress + notes to the notification record. |
| `src/apps/Wavee/App/ToastEscalator.cs` | L31 group `"wavee.live"`, L178 tag `"live:update"`; L168-170 hard-coded English titles; L173 launch `wavee://open?route=settings` | Launch → `wavee://open?route=whatsnew&arg=<version>`; use `ToastBuilder.Progress` + `ToastNotifier.Update` for download progress; move strings to loc keys. |
| `src/apps/Wavee/Features/Shell/NotificationPanel.cs` | L204-206 `SeeWhatsNew` → `LoginView.OpenUrl(ReleaseNotesUrl)` | Navigate to the in-app page instead; add a progress row. |
| `src/apps/Wavee/Features/Shell/SettingsPage.About.cs` | L80 `AboutHero(version)`; L126 "Check for updates" hyperlink; L148-186 `CheckForUpdates` (Task.Run + `HostDispatch.Current` hop) | Hero shows number + codename + channel + quad + sha + date; add "What's new" entry with unread dot. |
| `src/apps/Wavee/Features/Shell/ShellRoutes.cs` | L26-43 `s_exact` (`home … settings, api-console, playback-diagnostics, sidebar-customize, home-customize`) | Add `"whatsnew"`; `WaveeShell.GoDeepLinkOpen` rejects unknown routes. |
| `src/apps/Wavee/Features/Shell/ContentHost.cs` | L194-196 settings arm of `PageFor` | Add the `whatsnew` arm (`Embed.Comp(() => new WhatsNewPage())`, `Key="page:whatsnew"`). |
| `src/apps/Wavee/Features/Setup/SetupDialog.cs` | L27 `Open(IOverlayService, Action<Action> post, IAppSettings, SetupSession, bool bare)` — raw overlay + `PopupChrome.Modal`, 896×576 plate | Precedent for a large hero dialog (ContentDialog clamps to 548×756). |
| `src/FluentGpu.Controls/RichTextBlock.cs` | L43 `Run`, L47 `Bold` (weight 600), L53 `Hyperlink(text, onClick)`; no italic | Target of the markdown-lite tokenizer (`TextSpan[]`). `src/apps/Wavee/Components/RichText.cs` L21 `Of(html,…)` is the HTML-subset precedent. |
| `src/FluentGpu.WindowsApi/Notifications/ToastNotifier.cs` | L201 `Show(ToastBuilder)`; L338 `Update(IReadOnlyDictionary<string,string> values, string tag, string? group)` | Add a `ToastProgress → values` helper (`ToastProgress.cs` record exists, no mapper). `ToastBuilder.cs` L279 `Progress(...)`. |
| `src/FluentGpu.WindowsApi/Notifications/ToastInterop.cs` | L27 `HStringHandle`; L74-95 hand-slotted `IStringMap` | Pattern for the `PackageManager` vtables. |
| `src/FluentGpu.WindowsApi/WindowsGeolocationProvider.cs` | L386 `QueryAsyncInfo`, L397 `ReadAsyncStatus`, L323/333 `GetResults` — IAsyncInfo polling, no `Task` bridge, no completed-handler CCW | Extract a reusable `WinRtAsync` bridge (poll or `[GeneratedComInterface]` handler CCW) for `IAsyncOperationWithProgress`. |
| `src/FluentGpu.WindowsApi/Packaging/PackageIdentity.cs` | L73 `IsPackaged`, L80 `PackageFullName`, L87 `PackageFamilyName`, L95 `ApplicationUserModelId`, L108 `Version` (kernel32 only) | Add `GetAppInstallerInfo`/`SignatureKind` probes (Store vs sideload vs developer). |
| `src/apps/Wavee/Backend/Spotify/HttpPools.cs` | L24 `ThirdParty` (15 s timeout, HTTP/1.1, no UA, no ETag) | Dedicated GitHub client with UA + ETag. |
| `src/apps/Wavee/App/NetworkPolicy.cs` | L33 `IsMetered`, L36 `Metered` signal | Gate in-app download/prefetch. |
| `src/apps/Wavee/assets/loc/en-US.json` | L1003-1006 `settings.about.*`; L1117-1129 `notifications.update.*` (`seeWhatsNew` L1127) | Add `whatsnew.*` keys; generator emits `Wavee.Strings`. |
| `ops/build/Wavee.AppInstaller.template.xml` | L19 `2018` ns; L21 `Uri=__APPINSTALLER_URI__`; L31 `OnLaunch HoursBetweenUpdateChecks="0" ShowPrompt="true" UpdateBlocksActivation="false"`; L32 `AutomaticBackgroundTask`; L33 `ForceUpdateFromAnyVersion` | `Name` → `__IDENTITY__`; comment L9-10 about prompts is wrong for desktop apps. |
| `ops/build/Wavee.AppxManifest.xml` | L12 `Identity Name="cproducts.Wavee"`; L24 `MinVersion 10.0.17763.0`; L50 protocol `wavee`; L67 toast CLSID; L75 `StartupTask`; L81-83 capabilities (`runFullTrust` only) | Add `packageManagement`; template identity/display/protocol/CLSID/task per channel; MinVersion 19041. |
| `ops/build/pack-wavee-msix.ps1` | L35-41 version default + 4-part regex; L83-84 `/p:InformationalVersion=$Version`; L137 manifest substitution | `-Quad`/`-Semver`/`-Channel`/`-IdentityName`; stamp `AssemblyMetadata`; copy `Assets/whatsnew/`. |
| `.github/workflows/wavee-msix.yml` | L44-47 version job; L118-132 `.appinstaller` sed (feed = `releases/latest/download/…` L129, msix pinned to tag L130); L133-142 `softprops/action-gh-release@v2` (`generate_release_notes: true` L140, `prerelease` L142) | Version job rewrite + gate; validation job; publish to `wavee-vX.Y.Z` and re-upload feed files to `wavee-stable`; `body_path`. Gallery twin `.github/workflows/msix.yml` L122-128: add `make_latest: false`. |
| `CHANGELOG.md` | L11 `## [0.2.0] - unreleased`; sections L13-54 | Parsed by the release tool. |
| `docs/guide/releasing-wavee.md` | L120-122 "App Installer only moves forward" | Correct; document prerelease tags, codename line, rolling feeds. |
| `src/apps/Wavee.Tests/AppInstallerUpdateServiceTests.cs`, `TestAppSettingsShim.cs` (`MemoryAppSettings`), `Modules/ScriptedHttpHandler.cs` | pure-logic tests only | Add service tests (scripted feed 200/404/malformed), tokenizer, changelog parser, range stacker, issue budget. |

**GAP list (must be built):**
1. `Windows.Management.Deployment` interop (`IPackageManager`/6/3, `IPackage6`, options, async-with-progress bridge, `DeploymentResult`), `RegisterApplicationRestart` P/Invoke, child-process teardown before apply.
2. `Downloading/Installing/Snoozed` states + progress + notes in the `IAppUpdateService` seam and every consumer (bridge, panel, About, simulator, `FakeAppUpdateService`).
3. Rolling-feed CI + monotonic gate + prerelease-safe version job + `Wavee.Version.props` + `AssemblyMetadata` stamping.
4. `Wavee.ReleaseTool` (changelog parser, schema validation, issue snapshot, body generation) sharing the `Wavee.Core` STJ context.
5. `WhatsNewDocument` model + embedded/asset loader + disk cache + issue-state refresher with budget.
6. Markdown-lite inline tokenizer → `TextSpan[]`; issue chips; `WhatsNewPage` + route + deep link; post-update auto-open; Settings › About entry; toast/panel copy.
7. Diagnostics page section: feed URL, App Installer association (`GetAppInstallerInfo`), App Installer version, last check, rate-limit headers, cache path.
8. Manifest/pack/feed templating per channel (beta identity, data root, scheme, CLSID) — designed now, shipped in phase 2.
9. Loc keys for every new string (and the `ToastEscalator` debt).

---

## 6. Risks, unknowns, verify on a real machine

1. **Does `AddPackageByAppInstallerFileAsync` follow GitHub's 302 to the ~1 h signed `release-assets.githubusercontent.com`
   URL** for both files on Windows 10 2004–22H2? (Proven only via the gallery feed on Win11 26340.)
2. **Exact behaviour with `ForceTargetAppShutdown` while the app has out-of-proc module children** — the Claude Desktop
   case shows a surviving child yields `0x80073D02` and a silent revert to the old version.
3. **RegisterApplicationRestart 60 s rule**: an "Update now" within 60 s of launch will not relaunch — fall back to
   `ActivateApplication` from a detached helper or tell the user.
4. **Rolling release asset replacement** (`wavee-stable`): confirm no stale asset is served immediately after re-upload;
   confirm the OS updater tolerates the same feed URL whose content changes (it must; `.appinstaller` root `Version` bumps).
5. **`2018` namespace with `ForceUpdateFromAnyVersion` on Windows 10 22H2** parses and honours the flag (doc conflict
   1809 vs 1903 minimum).
6. **`packageManagement` declared + Trusted Signing** installs per-user without elevation on a stock Win11 (expected yes).
7. **`Package.GetAppInstallerInfo()` for a bare-`.msix` install** returns null (confirms the "Repair auto-update" affordance).
8. **Whether `AppInstallerManager.SetAutoUpdateSettings` (Win11 22000+)** can attach the association from inside the app
   without elevation — would remove the need for the `.appinstaller` ShellExecute repair.
9. **GitHub REST without `User-Agent`** → 403 (BELIEVED); confirm and set the product token regardless.
10. **App Installer 1.27.350.0 regression** (`0x80070057`) — test on a machine with the current App Installer before release.
11. **Toast-driven cold launch on 17763** irrelevant after MinVersion 19041; re-verify protocol activation delivers the
    URI on packaged launch (`ActivationArgs.cs` L64-71 caveat).
12. **Metered networks:** OS updater behaviour undocumented; the in-app path gates on `NetworkPolicy.IsMetered`.
13. **Hand-rolled `IAsyncOperationWithProgress` completed/progress handler CCWs under NativeAOT** — the polling model in
    `WindowsGeolocationProvider` is the proven fallback.

---

## 7. Contradictions between investigators and resolution

| Topic | Reports | Resolution (re-verified) |
|---|---|---|
| Is `ms-appinstaller:` usable by default? | All four: no | VERIFIED today against distribution-feature-status (reviewed Aug 2026): "Consumer devices (default): Disabled". Remove the path. |
| Are downgrades possible? | update-mechanics + version-schemes: yes with `ForceUpdateFromAnyVersion`; runbook §5: no | VERIFIED update-settings: "or to downgrade from version x to version x--". Runbook is wrong; keep roll-forward as the operational default. |
| `CheckUpdateAvailabilityAsync` for sideloaded packages | update-mechanics: only `.appinstaller` installs, `Package.Current` access denied | VERIFIED on the API page: both statements verbatim. Use `FindPackageForUser`; treat `Unknown` as "no association". |
| `packageManagement` required for self-update? | version-schemes: "requires"; update-mechanics/codebase: own package works without | VERIFIED non-store-developer-updates says both ("must declare" and "managing your own app should work without"); the API page lists it. Declare it — free for sideload. |
| `ForceUpdateFromAnyVersion` minimum OS | update-mechanics: 1809 (element doc); version-schemes: 1903 (update-settings table) | Both Microsoft pages, they disagree; irrelevant once MinVersion is 19041. |
| GitHub asset redirect host / expiry | update-mechanics (live): `release-assets.githubusercontent.com`, ~1 h; changelog-pipelines (BELIEVED): `objects.githubusercontent.com`, 300 s | Live curl today: `release-assets.githubusercontent.com` with `se=` ≈ 1 h. Either way: never cache the signed URL. |
| Feed URL strategy | update-mechanics: rolling `wavee-latest` tag; changelog-pipelines: `make_latest:false` on gallery or move; version-schemes: keep `releases/latest` + rolling `wavee-beta` | Rolling per-channel releases (`wavee-stable`/`wavee-beta`) — symmetric across channels, immune to the shared-repo hazard, zero migration cost today, WaveeMusic precedent (`experimental-latest`) works on this box; plus `make_latest:false` on the gallery as a second fence. |
| Apply API | codebase-grounding: `AddPackageAsync` without forced shutdown + own relaunch; update-mechanics: `AddPackageByAppInstallerFileAsync` + `ForceTargetAppShutdown` + `RegisterApplicationRestart` | Microsoft: `.appinstaller`-deployed apps must use the App Installer file APIs; non-UWP apps must call `RegisterApplicationRestart`. Files ships it. Chosen. |
| ShowPrompt honoured? | template comment: yes; update-mechanics: not for desktop apps | VERIFIED element-onlaunch remarks: silent for desktop-bridge apps. Template comment to be corrected. |
| 304 and rate limit | changelog-pipelines: unauthenticated 304 counts | VERIFIED best-practices page. Budget accordingly. |

---

## 8. Consolidated sources

Microsoft (MSIX / App Installer / WinRT):
- https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/distribution-feature-status
- https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings
- https://learn.microsoft.com/en-us/uwp/schemas/appinstallerschema/element-onlaunch
- https://learn.microsoft.com/en-us/uwp/schemas/appinstallerschema/element-appinstaller
- https://learn.microsoft.com/en-us/windows/msix/app-installer/auto-update-and-repair--overview
- https://learn.microsoft.com/en-us/windows/msix/app-installer/installing-windows10-apps-web
- https://learn.microsoft.com/en-us/windows/msix/app-installer/troubleshoot-appinstaller-issues
- https://learn.microsoft.com/en-us/windows/msix/app-installer/how-to-embed-an-appinstaller-file
- https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-desktopappinstaller
- https://www.microsoft.com/en-us/msrc/blog/2023/12/microsoft-addresses-app-installer-abuse
- https://learn.microsoft.com/en-us/windows/msix/non-store-developer-updates
- https://learn.microsoft.com/en-us/windows/msix/app-package-updates
- https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.addpackagebyappinstallerfileasync
- https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager.addpackagebyuriasync
- https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.addpackageoptions
- https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.deploymentprogress
- https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.deploymentresult.isregistered
- https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.package.checkupdateavailabilityasync
- https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.packageupdateavailability
- https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.package.getappinstallerinfo
- https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.appinstallermanager
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-registerapplicationrestart
- https://learn.microsoft.com/en-us/windows/win32/appxpkg/troubleshooting
- https://learn.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations
- https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements
- https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info
- https://learn.microsoft.com/en-us/windows/terminal/install
- Windows SDK 10.0.26100 headers: `winrt/windows.management.deployment.h`, `winrt/windows.applicationmodel.h`

Issues / case studies:
- https://github.com/microsoft/WindowsAppSDK/issues/4827 · https://github.com/microsoft/WindowsAppSDK/issues/6056
- https://github.com/microsoft/winget-cli/issues/5908 · https://github.com/MicrosoftDocs/msix-docs/issues/301
- https://github.com/anthropics/claude-code/issues/63397
- https://learn.microsoft.com/en-us/answers/questions/2197947/add-appxpackage-deployment-failed-with-hresult-0x8

Peer apps:
- https://github.com/files-community/Files/blob/main/src/Files.App/Services/App/AppUpdateSideloadService.cs
- https://github.com/microsoft/PowerToys/blob/main/src/runner/UpdateUtils.cpp
- https://raw.githubusercontent.com/microsoft/PowerToys/main/src/settings-ui/Settings.UI/SettingsXAML/OOBE/Views/ScoobeReleaseNotesPage.xaml.cs
- https://raw.githubusercontent.com/microsoft/vscode/main/src/vs/platform/update/common/update.config.contribution.ts
- https://github.com/microsoft/terminal/blob/main/build/config/template.appinstaller

GitHub platform:
- https://docs.github.com/en/rest/releases/releases#get-the-latest-release
- https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api
- https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api
- https://docs.github.com/en/rest/issues/issues#get-an-issue
- https://docs.github.com/en/repositories/releasing-projects-on-github/linking-to-releases
- https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases
- https://docs.github.com/en/repositories/releasing-projects-on-github/automatically-generated-release-notes
- https://docs.github.com/en/actions/writing-workflows/choosing-what-your-workflow-does/accessing-contextual-information-about-workflow-runs
- https://github.com/softprops/action-gh-release · https://github.com/orgs/community/discussions/26709
- Live: https://api.github.com/repos/christosk92/WaveeMusic/releases (2026-08-29)

Formats / versioning:
- https://keepachangelog.com/en/1.1.0/ · https://semver.org/ · https://calver.org/
- https://github.com/googleapis/release-please · https://towncrier.readthedocs.io/en/stable/tutorial.html · https://git-cliff.org/docs/integration/github
- https://sparkle-project.org/documentation/publishing/ · https://raw.githubusercontent.com/electron-userland/electron-builder/master/packages/builder-util-runtime/src/updateInfo.ts
- https://httpwg.org/specs/rfc9110.html#field.user-agent
- https://en.wikipedia.org/wiki/Ubuntu_version_history · https://en.wikipedia.org/wiki/MacOS_version_history · https://en.wikipedia.org/wiki/Android_version_history

Repo files: see §5 (all under `C:\wavee\fluent-gpu\`).
