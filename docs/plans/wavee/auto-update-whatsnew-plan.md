# Wavee auto-update + "What's new" — technical plan

**Status:** designed 2026-08-29; implementation plan = `auto-update-whatsnew-implementation.md`.

> **Superseded (2026-08-29):** releases are cut **locally by hand** (`ops/release/wavee-release.ps1`), not by GitHub
> Actions. §2.5 (CI version job), §4.4 (CI flow), the workflow rows of §7, §9 "P0" and §10 are replaced by
> `auto-update-whatsnew-implementation.md` (the approved implementation plan with code, component tree, scripts and the
> E2E test). Also: `<WaveeBuildOffset>` → committed counter `<WaveeBuild>` (quad = `M.m.p.WaveeBuild`), and every
> `WhatsNew*` C# type below is named `ReleaseNotes*` in code (`WhatsNew*` already means Spotify's new-releases feed).
 **Inputs:** the research dossier
`docs/plans/wavee/auto-update-whatsnew-research-dossier.md` (every OS/GitHub fact below is VERIFIED there unless marked
BELIEVED) and the Mica prototype `docs/plans/wavee/auto-update-whatsnew-mica.html` (scenes: What's new page, Settings ›
About, after-update dialog, the toast/notification-centre flow; `U` walks the update state machine).

This plan replaces the current updater **outright** (no legacy path, no env switch): the `ms-appinstaller:` hand-off is
deleted, the `releases/latest` feed is abandoned, `CHANGELOG.md` becomes a build input, and the version gets a name.

---

## 0. Decisions (final)

| # | Decision | Why (short) |
|---|---|---|
| D1 | **Feed = rolling per-channel GitHub release** `wavee-stable` (later `wavee-beta`), URL `releases/download/wavee-stable/Wavee.<arch>.appinstaller`. Each real tag keeps its own `wavee-vX.Y.Z` release for the `.msix`. **Published by the local script, never by CI.** | `releases/latest` is repo-global; the gallery's `v0.1.2` owns it today and the Wavee feed 404s. Gallery workflow also gets `make_latest: false`. |
| D2 | **"Update now" = in-app `PackageManager.AddPackageByAppInstallerFileAsync(feed, ForceTargetAppShutdown)`** after `RegisterApplicationRestart`; Windows closes + relaunches Wavee. | `ms-appinstaller:` is disabled by default on consumer Windows since Dec 2023 — today's button is dead and lies (`Downloaded`). Microsoft's rule for `.appinstaller`-deployed apps; the Files app ships exactly this. |
| D3 | **"Later" = snooze; the OS applies silently on next launch** (OnLaunch + AutomaticBackgroundTask stay). No in-process staging in v1. | Desktop-bridge apps get no App Installer prompt; the OS path is silent by design. Deferred staging (`AddPackageByUriAsync`) is Win11-22H2-only → phase 2. |
| D4 | **Versioning = "Trains":** tag `wavee-vX.Y.Z[-beta.N]`; committed `Wavee.Version.props` (semver + codename + build offset) is the single source; MSIX quad `X.Y.Z.(run+offset)`; **one hand-picked codename per MINOR** from the sea/wave series; CI refuses non-monotonic publishes. | Numeric quad stays the only comparison key (OS + `AppUpdateVersion.IsNewer`); the name is display-only. |
| D5 | **Beta = separate package identity** `cproducts.Wavee.Beta` with its own feed/data root/scheme/CLSID. Templated now, shipped in phase 2. | Same-identity channel switching cannot downgrade cleanly; Terminal/Files precedent. |
| D6 | **Changelog = hand-written `CHANGELOG.md` (Keep a Changelog 1.1 + `Known limitations`) + hand-written `ops/release/wavee/<semver>/whatsnew.json`** (codename, tagline, hero highlights, media, deep links, notices). A tiny AOT tool (`Wavee.ReleaseTool`) validates both on the tag, snapshots issue state, and emits the merged document + index + release body. | Human-quality prose, machine-checked. `generate_release_notes` output becomes a folded appendix, not the notes. |
| D7 | **Notes ship inside the MSIX and as release assets.** First launch after an update renders from the embedded copy (zero network); the GitHub REST API is spent only on live issue-state refresh, budgeted (60 req/h unauthenticated, 304s count). | Offline-correct; no token in the client. |
| D8 | **In-app surfaces:** `whatsnew` route + `wavee://open?route=whatsnew&arg=<version>`, auto-open after update (setting, default on), Settings › About hero with name/quad/sha/channel, one toast per state, notification-centre card, OS toast for *staged* and *updated*. | Prototype scenes 1–4. |
| D9 | **Manifest:** declare `rescap:packageManagement`; `MinVersion` → `10.0.19041.0`; keep the `2018` `.appinstaller` namespace. | Sideload needs no approval for the capability; Win10 < 2004 is out of support. |
| D10 | **Delete:** `ms-appinstaller:` path, `AppUpdateState.Downloaded`, the `ReleaseNotesUrl`-only contract, `generate_release_notes: true`, csproj `<Version>`, the "App Installer only moves forward" runbook claim. | No legacy paths. |

---

## 1. System overview

```
 ┌──────────────────────────── DEVELOPER (repo) ─────────────────────────────┐
 │  CHANGELOG.md            ops/release/wavee/0.3.0/whatsnew.json + media/    │
 │  src/apps/Wavee/Wavee.Version.props  (WaveeVersion=0.3.0 Codename=Crest)  │
 │  git tag wavee-v0.3.0  ──────────────────────────────────────────────┐    │
 └──────────────────────────────────────────────────────────────────────┼────┘
                                                                        ▼
 ┌──────────────────────── CI  .github/workflows/wavee-msix.yml ─────────────┐
 │ version ──► validate (Wavee.ReleaseTool) ──► build x64 / arm64 ──► sign   │
 │   │  quad=0.3.0.1047      │ parses CHANGELOG + whatsnew.json                │
 │   │  gate: quad > feed    │ snapshots issue state (CI token)                │
 │   │  semver == props      │ emits whatsnew.json, whatsnew-index.json,       │
 │   │                       │       RELEASE_BODY.md  → artifact "notes"        │
 │   └────────────────────────────────────────────────────────────► release   │
 │        (a) wavee-v0.3.0 : Wavee_0.3.0.1047_{x64,arm64}.msix, whatsnew.json, │
 │            media/*, THIRD-PARTY-NOTICES.txt, body = RELEASE_BODY.md         │
 │        (b) wavee-stable : Wavee.{x64,arm64}.appinstaller (replaced),        │
 │            whatsnew-index.json (replaced)                                   │
 └──────────────────────────────────────┬─────────────────────────────────────┘
                                        │ HTTPS (302 → release-assets.githubusercontent.com, ~1 h signed URL)
          ┌─────────────────────────────┼──────────────────────────────────────┐
          ▼                             ▼                                      ▼
 ┌─────────────────────┐   ┌────────────────────────────┐   ┌──────────────────────────────┐
 │ Windows App         │   │ Wavee.exe (packaged)       │   │ api.github.com (REST, no     │
 │ Installer service   │   │  AppUpdateScheduler        │   │ token, 60/h) — issue state   │
 │  OnLaunch check,    │   │  AppInstallerUpdateService │◄──│ refresh only, budgeted        │
 │  background task,   │◄──│  ├ ReadFeedVersion (GET)   │   └──────────────────────────────┘
 │  silent apply on    │   │  ├ CheckUpdateAvailability │
 │  next launch        │   │  ├ ApplyAsync ─────────────┼──► FluentGpu.WindowsApi/Packaging/PackageUpdater
 └─────────────────────┘   │  └ WhatsNewStore           │        RegisterApplicationRestart
                           │      embedded → cache →    │        IPackageManager6.AddPackageByAppInstallerFileAsync
                           │      remote asset          │        DeploymentProgress → IAppUpdateService.Progress
                           └────────────┬───────────────┘
                                        ▼
            UI: WhatsNewPage · AfterUpdateDialog · Settings›About · Toast · NotificationPanel · OS toast (ToastEscalator)
```

Communication contracts, one owner each:

| Contract | Owner (file) | Consumers |
|---|---|---|
| Feed document (`.appinstaller` root `Version`, `Uri`, `MainPackage/@Uri`) | `ops/build/Wavee.AppInstaller.template.xml` | Windows App Installer, `AppInstallerUpdateService.ReadFeedVersionAsync` |
| `whatsnew.json` / `whatsnew-index.json` (schema §4.2) | `src/apps/Wavee.Core/WhatsNew/WhatsNewDocument.cs` (STJ source-gen context) | `Wavee.ReleaseTool` (writer), `WhatsNewStore` (reader), pack script (copies) |
| Version triple (semver, quad, codename, channel, commit, date) | `src/apps/Wavee/Wavee.Version.props` → `AssemblyMetadata` → `Wavee.Core.WaveeVersionInfo` | CI, pack script, About, crash header, User-Agent, `WhatsNewStore` |
| `IAppUpdateService` v2 (§3.2) | `src/apps/Wavee.Core/Notifications/AppUpdate.cs` | bridge, panel, About, dialog, simulator, tests |
| `IPackageUpdater` (§3.4) | `src/FluentGpu.WindowsApi/Packaging/IPackageUpdater.cs` | `AppInstallerUpdateService` only |

---

## 2. Versioning, naming, channels ("Trains")

### 2.1 Grammar

```
tag        := "wavee-v" semver
semver     := M "." m "." p [ "-beta." N ]          # only "beta" prerelease id is accepted; N ≥ 1
quad       := M "." m "." p "." (run_number + offset)   # every part 0..65535; MSIX Identity/@Version
codename   := hand-picked word, one per (M,m); patches + betas inherit
channel    := "stable" | "beta"                       # beta ⇔ semver has "-beta."
display    := "Wavee " M.m.p " \"" codename "\""  [ " · Beta " N ]
one-line   := "Wavee/" M.m.p " (build " quad "; " channel "; Windows " os "; " arch ")"   # RFC 9110 product token
```

Worked: tag `wavee-v0.3.0`, run 1047, offset 0 → quad `0.3.0.1047`, display `Wavee 0.3.0 "Crest"`,
`InformationalVersion = 0.3.0+build.1047.sha.d4227b3`. Hotfix `wavee-v0.3.1` (run 1050) → `0.3.1.1050`, still "Crest".
Beta `wavee-v0.4.0-beta.2` (run 1058) → quad `0.4.0.1058`, display `Wavee 0.4.0 "Drift" · Beta 2`, published to
`wavee-beta` only, `prerelease: true`.

### 2.2 Single source: `src/apps/Wavee/Wavee.Version.props` (new, committed)

```xml
<Project>
  <PropertyGroup>
    <WaveeVersion>0.2.0</WaveeVersion>          <!-- semver; CI fails the tag if it differs from the tag -->
    <WaveeCodename>Breaker</WaveeCodename>       <!-- one per MINOR; see the series in §2.4 -->
    <WaveeBuildOffset>0</WaveeBuildOffset>       <!-- added to github.run_number; bump if the workflow is ever renamed -->
  </PropertyGroup>
</Project>
```

`Wavee.csproj` imports it and **drops** `<Version>`; it stamps:

```xml
<Version>$(WaveeVersion)</Version>
<InformationalVersion Condition="'$(InformationalVersion)'==''">$(WaveeVersion)-dev</InformationalVersion>
<ItemGroup>
  <AssemblyMetadata Include="Codename" Value="$(WaveeCodename)" />
  <AssemblyMetadata Include="Channel"  Value="$(WaveeChannel)" />      <!-- CI passes; "dev" locally -->
  <AssemblyMetadata Include="PackageVersion" Value="$(WaveePackageVersion)" />  <!-- the quad; "" locally -->
  <AssemblyMetadata Include="Commit"   Value="$(WaveeCommit)" />
  <AssemblyMetadata Include="BuildDate" Value="$(WaveeBuildDate)" />
</ItemGroup>
```

`Wavee.Core.WaveeVersionInfo` (pure, unit-tested) parses these into `{ SemVer, Quad, Codename, Channel, Commit,
BuildDate, IsDev }` and formats `Display`, `OneLine`, `UserAgent`. `AppVersion.Current` becomes a thin façade over it
(keeps returning the `+meta`-stripped string for existing callers); `Services.HostVersion` is deleted in favour of it.

### 2.3 Channel identity table (templated now; beta shipped in phase 2)

| Field | stable | beta |
|---|---|---|
| Identity Name | `cproducts.Wavee` | `cproducts.Wavee.Beta` |
| DisplayName | Wavee | Wavee Beta |
| Protocol | `wavee` | `wavee-beta` |
| Toast activator CLSID | existing | new GUID (constant in the manifest template) |
| StartupTask id | `WaveeStartup` | `WaveeBetaStartup` |
| Data root | `%LOCALAPPDATA%\Wavee` | `%LOCALAPPDATA%\Wavee.Beta` (`SettingsShared.AppDataRoot` reads the channel) |
| Feed release | `wavee-stable` | `wavee-beta` (finals publish to **both**) |
| Feed asset | `Wavee.<arch>.appinstaller` | `Wavee.Beta.<arch>.appinstaller` |

"Release channel" in Settings › About (prototype scene 2) is, in v1, a **link** to the beta `.appinstaller` with the
explanation that Beta installs side by side; in phase 2 it becomes the picker. Never a same-identity feed swap.

### 2.4 Codename series (sea states / wave anatomy, alphabetical; hand-picked, skip freely)

Abyss (0.1, internal) · **Breaker (0.2 — first public)** · Crest (0.3) · Drift (0.4) · Ebb · Fetch · Groundswell ·
Harbor · Inlet · Jetty · Kelp · Lagoon · Maelstrom · Neap · Offshore · Pipeline · Quay · Riptide · Swell · Trough ·
Undertow · Vortex · Whitecap · Xebec · Yaw · Zephyr. A MAJOR restarts the lap. The name appears in: About hero,
What's new hero + release rail, toast titles, GitHub release name (`Wavee 0.3.0 — Crest`), crash header. It never
appears in the version string, the tag, the MSIX quad, or the feed.

### 2.5 Version job — SUPERSEDED: see implementation plan §7.4 (local `wavee-release.ps1` preflight + `Test-FeedMonotonic`)

```
inputs : GITHUB_REF, github.run_number, Wavee.Version.props, feed(s)
1. semver  = strip "refs/tags/wavee-v"; must match ^\d+\.\d+\.\d+(-beta\.\d+)?$ else FAIL
2. props   = xmllint Wavee.Version.props; FAIL if props.WaveeVersion != M.m.p
3. channel = has "-beta." ? beta : stable ; betaN
4. quad    = M.m.p.(run_number + props.WaveeBuildOffset); FAIL if any part > 65535
5. for each target feed (stable → wavee-stable; beta → wavee-beta; stable also updates wavee-beta if it exists):
      cur = GET releases/download/<feed>/Wavee[.Beta].x64.appinstaller  (404 ⇒ first publish, ok)
      FAIL unless quad > cur.Version (4-part numeric)  AND  M.m.p ≥ cur.M.m.p
6. outputs: semver, quad, channel, betaN, codename, identity, feeds[], commit(short sha), buildDate(UTC ISO)
```

Never `gh run rerun` a release run — re-tag (`wavee-vX.Y.Z+1`). The gate turns a mistake into a red job, not a downgrade.

---

## 3. Update engine (client)

### 3.1 State machine

```
                     CheckAsync()                      ApplyAsync()
   ┌──────┐ ───────────────────────► ┌──────────┐ ──────────────────► ┌─────────────┐
   │ None │ ◄──── up to date ─────── │ Checking │                     │ Downloading │──progress──┐
   └──────┘                          └──────────┘                     └──────┬──────┘            │
      ▲  ▲                                 │ feed newer                      │ DeploymentProgress │
      │  │                                 ▼                                 ▼ state=Processing   │
      │  │  Acknowledge()            ┌───────────┐   Snooze(version)   ┌────────────┐              │
      │  └────────────────────────── │ Available │ ──────────────────► │  Snoozed   │              │
      │                              └───────────┘ ◄── newer version ─ └────────────┘              │
      │                                                                                            │
      │           ┌───────────┐   OS relaunch (RegisterApplicationRestart) ┌────────────┐          │
      └───────────│ Completed │ ◄──────── next process start ───────────── │ Installing │ ◄────────┘
   (ctor: last-   └───────────┘                                            └─────┬──────┘
    run ≠ current)                                                               │ DeploymentResult.ExtendedErrorCode
                                                                                 ▼
                                                                           ┌──────────┐
                                                                           │  Failed  │ (HRESULT mapped, "Open release page")
                                                                           └──────────┘
```

Rules kept from today: a `Completed` raised by the constructor survives a later "up to date" check (only `Acknowledge`
clears it); a dev/unparsable current version never prompts; every failure is terminal for the attempt, never for the
process. New: `Snoozed` is per-version (`app.update.snoozedVersion`); a newer feed version un-snoozes. `Installing` is
observable for at most seconds — Windows terminates the process — so the UI treats `Downloading|Installing` as one
"busy" surface with a percentage.

### 3.2 `IAppUpdateService` v2 (owner: `src/apps/Wavee.Core/Notifications/AppUpdate.cs`)

```csharp
public enum AppUpdateState { None, Checking, Available, Snoozed, Downloading, Installing, Completed, Failed }

/// <summary>Snapshot of one updater observation — published whole so readers never see a torn state.</summary>
public sealed record AppUpdateSnapshot(
    AppUpdateState State,
    WaveeVersionInfo? Target,          // feed version (+ codename/date from whatsnew-index when known)
    int ProgressPercent,               // 0..100 while Downloading/Installing, else 0
    AppUpdateFailure? Failure,         // HRESULT + human message + "openReleasePage" rung
    bool AutoUpdateAssociated,         // Package.GetAppInstallerInfo() != null (packaged only)
    DateTimeOffset? LastChecked);

public interface IAppUpdateService
{
    AppUpdateSnapshot Current { get; }
    IObservable<int> Changed { get; }              // unchanged: revision ticks, UI re-reads Current
    string FeedUrl { get; }
    Task CheckAsync(CancellationToken ct);
    Task ApplyAsync(CancellationToken ct);         // Update now (replaces DownloadAsync + RestartToApply)
    void Snooze();                                 // Later
    void Acknowledge();                            // clears Completed/Failed
}
```

`NullAppUpdateService` and the developer-mode `FakeAppUpdateService` (simulator) implement the same surface; the
simulator walks the states on a timer exactly like the prototype's `U` key so every UI state is reachable without a
release.

### 3.3 `AppInstallerUpdateService` (rewrite, same ctor shape)

```
ctor(IAppSettings, HttpClient github, WaveeVersionInfo current, string arch, IPackageUpdater updater, IWaveeLog)
  feed = "https://github.com/christosk92/WaveeMusic/releases/download/" + channelFeed + "/Wavee" + (beta?".Beta":"") + "." + arch + ".appinstaller"
  Completed if AppUpdateVersion.IsFirstRunAfterUpdate(LastRunVersion, current.Quad)      (unchanged rule)
  settings.Set(LastRunVersion, current.Quad)

CheckAsync
  1. Publish(Checking)
  2. remote = ReadFeedVersionAsync()  (unchanged XmlReader; DTD prohibited)          ─┐ network
  3. if packaged: assoc = updater.GetAppInstallerInfo() != null                       │ WinRT (MTA thread)
     avail = updater.CheckUpdateAvailabilityAsync()  (Unknown ⇒ assoc=false; logged)  ─┘
  4. IsNewer(remote, current.Quad) ? (snoozedVersion == remote ? Snoozed : Available) : (keep Completed | None)
  5. settings.Set(UpdateLastCheckedMs)
  6. prefetch: WhatsNewStore.PrefetchIndexAsync(feedRelease) — skipped when NetworkPolicy.IsMetered

ApplyAsync (packaged)
  1. if NetworkPolicy.IsMetered && !settings.UpdateOnMetered → Failed(metered, "Wi‑Fi only") — never silently
  2. Publish(Downloading, 0)
  3. updater.ApplyFromAppInstallerAsync(feedUri, progress => Publish(Downloading, pct), ct)
       └ inside WindowsApi: RegisterApplicationRestart("", 0)
                            terminate package-identity children (PlaybackModuleHost.StopAll())
                            IPackageManager6.AddPackageByAppInstallerFileAsync(uri, ForceTargetAppShutdown, defaultVolume)
                            progress loop → callback; completion → DeploymentResult
  4. result.IsRegistered ⇒ Windows is about to kill us: Publish(Installing, 100); flush settings; return
     else ⇒ Publish(Failed, MapHresult(result.ExtendedErrorCode), ErrorText)
ApplyAsync (unpackaged)  → open releases/tag/wavee-v<semver> in the browser (honest; unchanged)
```

Threading: `Publish` fires `Changed` on the calling thread as today; all WinRT calls run on a dedicated MTA thread
inside `PackageUpdater` (the `WindowsGeolocationProvider` pattern), never on the UI thread.

HRESULT map (`AppUpdateFailure.Kind`): `0x80073D02` PackagesInUse → "Close other Wavee windows/modules and retry";
`0x80073D06`/`0x80073CFB` VersionConflict → "Open release page"; `0x80073CFF` SideloadPolicy → "Sideloading is disabled
on this PC"; `0x80072F76`/`0x80072EE7` Network → retry later; `0x80070057` (App Installer 1.27.350.0 regression) →
"Windows App Installer needs an update" with the Store link; anything else → generic with the code shown.

### 3.4 `FluentGpu.WindowsApi/Packaging/` additions (TerraFX-free rule does not apply here; WindowsApi refs TerraFX)

```
Packaging/
  IPackageUpdater.cs          public interface IPackageUpdater
                                { bool IsSupported; AppInstallerInfo? GetAppInstallerInfo();
                                  Task<PackageUpdateAvailability> CheckUpdateAvailabilityAsync(CancellationToken);
                                  Task<PackageDeploymentResult> ApplyFromAppInstallerAsync(Uri feed, Action<int> progress, CancellationToken); }
  PackageUpdater.cs           the implementation; owns the MTA worker thread; RegisterApplicationRestart P/Invoke
  PackageManagerInterop.cs    hand-slotted vtables: IPackageManager (FindPackageByUserSecurityIdPackageFullName),
                              IPackageManager3 (GetDefaultPackageVolume), IPackageManager6 (AddPackageByAppInstallerFileAsync),
                              IPackage6 (CheckUpdateAvailabilityAsync), IPackage8 (GetAppInstallerInfo), IAppInstallerInfo,
                              IDeploymentResult/2, IPackageUpdateAvailabilityResult — IIDs from the dossier §2.3
  WinRtAsync.cs               IAsyncInfo status polling + GetResults + progress read (reusable; Geolocation migrates later)
  PackageDeploymentResult.cs  readonly record struct (IsRegistered, ExtendedErrorCode, ErrorText, ActivityId)
```

Sequence, "Update now":

```
 UI thread            AppInstallerUpdateService        PackageUpdater (MTA)            Windows deployment
 ────────             ─────────────────────────        ────────────────────            ───────────────────
 ApplyAsync ───────►  Publish(Downloading,0)
                      updater.Apply(feed) ──────────►  RegisterApplicationRestart
                                                        StopAll module children
                                                        AddPackageByAppInstallerFileAsync ─► GET feed, GET msix (302→signed)
                                                        poll IAsyncInfo / progress   ◄──── DeploymentProgress{Processing, 37}
                      Publish(Downloading,37) ◄──────── progress(37)
   toast/About bar ◄─ Changed
                                                        Completed ◄─────────────────────── DeploymentResult{IsRegistered}
                      Publish(Installing,100) ◄──────── result
                      settings flush
                                                                                        ForceTargetAppShutdown → process exit
                                                                                        (restart via RegisterApplicationRestart)
 next launch: ctor sees LastRunVersion ≠ quad → Completed → AfterUpdateDialog (if autoShow) + OS toast "Updated"
```

Verify-on-machine items before merging (dossier §6): 302-following on Win10 22H2; `ForceTargetAppShutdown` with a
surviving module child (`0x80073D02`); the 60-second `RegisterApplicationRestart` rule (if the app has been up < 60 s,
"Update now" shows "Restart Wavee first, then update" instead of a silent no-relaunch).

### 3.5 Scheduler, network, diagnostics

`AppUpdateScheduler` cadence unchanged (30 s, 24 h, 1 h cooldown). The notes prefetch and `ApplyAsync` gate on
`NetworkPolicy.IsMetered` + the new `app.update.onMetered` setting. The `playback-diagnostics` page gains an **Updates**
section: feed URL, channel, quad, App Installer association (`GetAppInstallerInfo`: `LastChecked`, `PausedUntil`,
`OnLaunch`), App Installer version (`Get-AppxPackage Microsoft.DesktopAppInstaller` via `PackageManager.FindPackagesForUser`),
last check result, last HRESULT, notes cache path + source (embedded/cache/remote), last GitHub rate-limit headers.
The "Repair auto-update" action (assoc == false) ShellExecutes the `.appinstaller` URL — the OS App Installer UI, not
the protocol — which recreates the association.

---

## 4. Release notes pipeline

### 4.1 Authoring

```
CHANGELOG.md                                  ops/release/wavee/0.3.0/
## [0.3.0] - 2026-08-29                          whatsnew.json      ← hand-written: codename, tagline, highlights, notices
### Added                                        media/docked-video.mp4 + .webp poster, queue.webp, lyrics.webp
- **Docked video** — … (#412, !430)
### Changed / Fixed / Removed / Security / Known limitations
```

Bullet grammar (parsed by the tool, rendered by the app): markdown-lite inline (`**bold**`, `*em*` → weight 600 since
the shaper has no italic axis, `` `code` ``, `[text](url)`, bare URLs, `#123`, `owner/repo#123`, `!123` (PR), `@handle`,
`\` escapes); an optional leading `Scope:` token (`Player:`, `Queue:`) becomes the item's scope chip; trailing
`(#123, !430)` groups are lifted into `issues[]`/`prs[]`. No headings/tables/images/HTML inside items.

### 4.2 Document model (owner: `src/apps/Wavee.Core/WhatsNew/WhatsNewDocument.cs`; STJ source-gen; flat)

```csharp
[JsonSerializable(typeof(WhatsNewDocument))] [JsonSerializable(typeof(WhatsNewIndex))]
internal partial class WhatsNewJsonContext : JsonSerializerContext { }

public sealed class WhatsNewDocument {            // one per release
  public int Schema { get; set; } = 1;  public string Product = "wavee";
  public string Version, PackageVersion, Name, Tagline, Date, Channel, Lang, MinOs;  public string[] Arch;
  public WhatsNewLinks Links;                      // release, changelog, compare
  public WhatsNewHighlight[] Highlights;           // id, title, body(markdown-lite), media?, deepLink?, issues[]
  public WhatsNewSection[] Sections;               // kind: added|changed|fixed|removed|deprecated|security|known ; items[]
  public WhatsNewNotice[] Notices;                 // kind: breaking|info|warning ; text
  public WhatsNewContributor[] Contributors;       // login, firstTime
  public string GeneratedAt;                       // issue-state snapshot time ("as of")
  public WhatsNewMedia[] Media;                    // src, bytes, sha256
}
public sealed class WhatsNewItem { string Id, Scope?, Text; WhatsNewIssue[] Issues; WhatsNewPr[] Prs; WhatsNewContributor[] Contributors; }
public sealed class WhatsNewIssue { string Repo; int Number; string Title; string State; string? StateReason; bool IsPullRequest; }
public sealed class WhatsNewIndex { int Schema; string Product; WhatsNewIndexEntry[] Releases; }   // newest first, ≤12
public sealed class WhatsNewIndexEntry { string Version, Name, Date, Channel; }
```

Enums are strings (`JsonStringEnumConverter<T>` where typed); unknown members ignored; collections are arrays.
The full JSON example is in the dossier §4.3.

### 4.3 `Wavee.ReleaseTool` (new console project `src/apps/Wavee.ReleaseTool`, refs `Wavee.Core`, AOT, no other deps)

```
wavee-release validate  --semver 0.3.0 --quad 0.3.0.1047 --codename Crest --channel stable
                        --changelog CHANGELOG.md --notes ops/release/wavee/0.3.0 --out artifacts/notes
                        [--github-token $GITHUB_TOKEN] [--repo christosk92/WaveeMusic]
  checks : CHANGELOG has "## [0.3.0] - YYYY-MM-DD" (a real date; not "unreleased"); notes/whatsnew.json parses; name==codename;
           version==semver; every media ref exists; ≤150 KB per webp still, ≤600 KB per mp4/animated, ≤1.5 MB total, no GIF;
           every #issue/!pr exists (GET /repos/{o}/{r}/issues/{n}; PR ⇒ pull_request present); deepLinks are wavee:// routes ShellRoutes knows
  emits  : artifacts/notes/whatsnew.json          (authored doc + parsed CHANGELOG sections + issue snapshot + generatedAt + media hashes)
           artifacts/notes/whatsnew-index.json    (previous index from the feed release, prepended, ≤12)
           artifacts/notes/RELEASE_BODY.md        (hero + highlights + changelog; <details> appendix from POST /releases/generate-notes)
           artifacts/notes/store-listing.txt      (tagline + highlight titles, ≤1500 chars, for a future Store channel)
wavee-release render --notes artifacts/notes/whatsnew.json --markdown   (local preview; also what the What's new page renders)
```

Pure pieces (`Wavee.Core.WhatsNew`): `ChangelogParser` (Keep-a-Changelog → sections/items), `MarkdownLite`
(tokenizer → `InlineToken[]`), `IssueRefParser`. All unit-tested with fixture strings, never by reading `.cs` files.

### 4.4 Release flow — SUPERSEDED: see implementation plan §7 (local script; `wavee-msix.yml` is deleted)

```
version ──► validate ──► build{x64,arm64} ──► sign ──► release
             │ dotnet run Wavee.ReleaseTool validate …            artifacts: notes/
             │ uploads notes/ as artifact "wavee-notes"
build:  pack-wavee-msix.ps1 -Quad <quad> -Semver <semver> -Channel <ch> -Codename <name> -IdentityName <id> -Commit <sha> -BuildDate <iso>
        copies artifacts/notes/whatsnew.json + media/ + whatsnew-index.json → payload/Assets/whatsnew/
release:
  1. gh release create wavee-v<semver> --title "Wavee <semver> — <codename>" --notes-file RELEASE_BODY.md [--prerelease]
        assets: Wavee_<quad>_{x64,arm64}.msix, whatsnew.json, media/*, THIRD-PARTY-NOTICES.txt
  2. generate the .appinstaller per arch:  __VERSION__=<quad>  __IDENTITY__=<id>  __APPINSTALLER_URI__=releases/download/<feed>/Wavee[.Beta].<arch>.appinstaller
                                            __MSIX_URI__=releases/download/wavee-v<semver>/Wavee_<quad>_<arch>.msix
  3. for feed in feeds: gh release view <feed> || gh release create <feed> --title "Wavee <channel> update feed" --notes "Do not delete. …" --latest=false
                        gh release upload <feed> Wavee[.Beta].{x64,arm64}.appinstaller whatsnew-index.json --clobber
  gallery msix.yml: add `make_latest: false` (second fence; the feed no longer depends on it)
```

Rolling-asset replacement (`--clobber`) is atomic per asset (BELIEVED — verify no stale CDN copy on the first real
release; dossier §6.4). The `.appinstaller`'s own `Uri` points at the feed, so an installed client keeps polling one
stable URL while `MainPackage/@Uri` moves per tag — same shape as today, different host path.

### 4.5 Client-side notes store (`src/apps/Wavee/App/WhatsNewStore.cs`)

```
 GetAsync(version)                       PrefetchIndexAsync(feedRelease)
 ┌─────────────────────────────┐         ┌────────────────────────────────────────────┐
 │ 1 embedded  Assets/whatsnew/│         │ GET releases/download/<feed>/whatsnew-index.json │
 │   (current version only)    │         │ → cache/whatsnew/index.json                 │
 │ 2 cache %LOCALAPPDATA%\Wavee│         └────────────────────────────────────────────┘
 │   \cache\whatsnew\<ver>\    │
 │ 3 remote releases/download/ │   media: same ladder; remote media placeholders until fetched; never block the page
 │   wavee-v<ver>/whatsnew.json│
 └─────────────────────────────┘
 RefreshIssueStatesAsync(doc): only while the page is open; ≤20 GET /issues/{n} per opening; ≤1 per issue per 24 h
   (cache/whatsnew/issues.json); stop at 403 or x-ratelimit-remaining: 0; otherwise show the snapshot "as of <generatedAt>".
 HttpClient: dedicated GitHub client (UA = WaveeVersionInfo.UserAgent, 15 s, HTTP/1.1, ETag stored but 304 budgeted like 200).
```

Stacking: `WhatsNewRange.Between(lastSeen, current, index)` (pure) returns the releases in `(lastSeen, current]` of the
current channel; the page shows "Since you last looked: N releases — a → b" with highlights merged (newest first, max 3)
and one section group per release. `app.whatsnew.lastSeenVersion` is written when the page is opened or the dialog
dismissed.

---

## 5. UI surfaces (prototype scenes 1–4)

### 5.1 `WhatsNewPage` — route `whatsnew`, deep link `wavee://open?route=whatsnew&arg=<semver>`

```
┌ Wavee · What's new ───────────────────────────────────────────────────────────────────────────┐
│ ✦ Since you last looked: 2 releases — 0.2.1 Breaker → 0.3.0 Crest. Showing everything new.  [Only the latest] │
│ ┌ HERO (card-secondary, hero gradient a/b, reduced-motion aware) ───────────────┐ ┌ RELEASES ───┐ │
│ │ [Latest] [Stable] [0.3.0.1047]                                  [Open on GitHub ↗]│ │ ● 0.3.0     │ │
│ │ Wavee 0.3 «Crest»                                               [Copy link]     │ │   Crest ·   │ │
│ │ tagline …                                                                        │ │ ○ 0.2.1 YOU │ │
│ │ Released 29 Aug 2026 · Size 7.4 MB · Requires Windows 10 2004+ · Tag wavee-v0.3.0│ │ ○ 0.2.0     │ │
│ └──────────────────────────────────────────────────────────────────────────────────┘ │ ○ 0.1.x     │ │
│ HIGHLIGHTS  ┌ media(poster/mp4) ┐ ┌──────────┐ ┌──────────┐                          │ (index ≤12; │ │
│             │ [New]  ▶          │ │ [Rebuilt]│ │[Improved]│                          │  older on   │ │
│             │ Docked video      │ │ Queue …  │ │ Lyrics … │                          │  demand)    │ │
│             │ body · Try it →   │ └──────────┘ └──────────┘                          └─────────────┘ │
│ [+] Added 5      ┌────────────────────────────────────────────────────────────────┐                  │
│                  │ PLAYER  **Docked video** — …          (● #412 closed) (■ !430)  ◉◉ │  ← scope chip,│
│                  │ QUEUE   Drag to reorder …             (● #388 closed)          ◉  │    issue chips,│
│                  └────────────────────────────────────────────────────────────────┘    contributors │
│ [~] Changed 3  … [✓] Fixed 14 [Show all 14] … [−] Removed 1 … [!] Known limitations 3 …           │
│ Chips show the issue's *current* GitHub state (as of <generatedAt> when offline).                   │
└──────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Composition: `WhatsNewPage : Component` (mounted via `Embed.Comp`, `Key="page:whatsnew"`), reads `WhatsNewStore` through
a `Signal<WhatsNewViewState>` (loading → embedded doc → enriched); children: `WhatsNewHero`, `HighlightCard`
(`ImageEl` poster + `MediaPlayerElement` when `!ReducedMotion` and the mp4 is cached), `ChangelogSection` (kind icon +
count + collapse past 8), `ChangelogItem` (`RichTextBlock.Paragraph(MarkdownLite.ToSpans(item.Text, onLink))` + scope
chip + `IssueChip[]` + avatars), `ReleaseRail` (index entries; YOU = `lastSeenVersion`, unread dot = newer than
`lastSeenVersion`). Issue chip states: neutral (no data) → open / closed-completed / not-planned / duplicate / PR-merged;
hover tooltip = title. Everything virtualization-free (≤ ~60 rows); scrolling via the standard page `ScrollView`.

### 5.2 `AfterUpdateDialog` (scene 3; `SetupDialog` overlay precedent, 720×~520 plate)

```
┌────────────────────────────────────────────────────────────────┐
│ [Updated] [0.2.1 → 0.3.0.1047]                                  │
│ Welcome to Wavee 0.3 «Crest»                                    │
│ tagline (1–2 lines)                                             │
│ ┌ highlight ┐ ┌ highlight ┐ ┌ highlight ┐   (≤3, poster only)   │
│ └───────────┘ └───────────┘ └───────────┘                       │
│ ☐ Don't show this after updates        [Full release notes] [Got it] │
└────────────────────────────────────────────────────────────────┘
```

Opens once when `Current.State == Completed && app.whatsnew.autoShow && embedded doc present`, after the shell is
interactive and **never** over the first-run wizard or a crash notice (those win; the dialog defers to the next
launch). "Full release notes" → `whatsnew` route with `arg=<current>`. Checkbox writes `app.whatsnew.autoShow=false`.

### 5.3 Settings › About (scene 2)

```
┌ ♪ │ Wavee 0.3 «Crest»                                   [Check for updates]/[Update now]/[Restart now] │
│    │ 0.3.0.1047 [Stable] [Up to date|Update available|Downloading|Restart to finish|Just updated] d4227b3 · arm64 │
│    │ Installed <date> · last checked <ago>               [What's new in Crest →]                     │
├────┴───────────────────────────────────────────────────────────────────────────────────────────────┤
│ ⟳ Update status        <state sentence>  [████░░░░ 37%] (Downloading only)             [Restart now] │
│ ◍ Release channel      Beta installs side by side …                                    [Stable ▾]   │
│ ⇩ How updates install  ◉ Download in the background, install on next launch (recommended)            │
│                        ○ Install when I quit Wavee     ○ Only notify me                               │
│ 📶 Download on metered connections                                                          [off]    │
│ ✦ Show "What's new" after an update                                                         [on]     │
├────────────────────────────────────────────────────────────────────────────────────────────────────┤
│ 🗒 Release notes → Open What's new  · 🐞 Send feedback · 📋 Copy diagnostics                           │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

"How updates install" maps to `app.update.policy` (`background` | `onQuit` | `notify`): `background` = OS path stays
armed (default); `onQuit` = on `Available`, `ApplyAsync` runs from the shell's close handler (deferred exit until
`Installing`); `notify` = nothing downloads until "Update now". The OS `.appinstaller` settings are static, so
`notify`/`onQuit` are honest only for the in-app path — the copy says so.

### 5.4 Toasts, notification centre, OS toasts (scene 4) — one artefact per state

| State | In-app toast (`Toast.Show`, `DedupeKey="update"`) | Notification-centre card | OS toast (`ToastEscalator`, group `wavee.live`, tag `live:update`) |
|---|---|---|---|
| Checking | "Checking for updates…" (About only, not global) | — | — |
| Available | "Wavee 0.3 Crest is available" · first highlight title · [Update now] [What's new] [Later] | same + size | — (in-app only; the OS path is silent anyway) |
| Snoozed | — | card stays, no toast | — |
| Downloading | progress toast (`ToastOptions.Progress`) "Downloading Crest…" | progress row | `ToastBuilder.Progress` + `ToastNotifier.Update` (data-bound) — only when the window is not foreground |
| Installing | (window is being closed) | — | progress → 100% "Restarting…" |
| Completed | "Updated to Wavee 0.3 Crest" [What's new] | card [What's new] | "Wavee updated to 0.3 Crest" [See what's new] → `wavee://open?route=whatsnew&arg=0.3.0` |
| Failed | error toast with the mapped reason + [Open release page] | card | — |

Sidebar pill "Update ready · Restart to finish" is **not** in v1 (no `staged` state without in-process staging);
the prototype shows it for phase 2.

### 5.5 Localization keys (add to `src/apps/Wavee/assets/loc/en-US.json`; generator emits `Wavee.Strings`)

`whatsnew.title`, `whatsnew.since(count, from, to)`, `whatsnew.onlyLatest`, `whatsnew.highlights`,
`whatsnew.section.{added,changed,fixed,removed,deprecated,security,known}`, `whatsnew.showAll(count)`,
`whatsnew.issue.{open,closed,notPlanned,duplicate,merged}`, `whatsnew.asOf(date)`, `whatsnew.dialog.{welcome(name),dontShow,full,gotIt}`,
`update.state.{upToDate,checking,available,snoozed,downloading,installing,restartToFinish,justUpdated,failed}`,
`update.action.{check,updateNow,later,restartNow,whatsNew,openReleasePage,repair}`, `update.policy.{background,onQuit,notify}(+.hint)`,
`update.metered.{title,hint}`, `update.autoShow.{title,hint}`, `update.channel.{title,hint,stable,beta}`,
`update.failure.{packagesInUse,versionConflict,sideloadPolicy,network,appInstallerOutdated,metered,generic(code)}`,
`update.os.{available,downloading,updated,restarting}(name)`. The three hard-coded English strings in `ToastEscalator`
move here.

---

## 6. Settings + persistence

| Key | Type | Default | Written by |
|---|---|---|---|
| `app.lastRunVersion` (exists) | string (now the **quad**) | "" | updater ctor |
| `app.update.lastCheckedMs` (exists) | long | 0 | `CheckAsync` |
| `app.update.snoozedVersion` | string | "" | `Snooze()` |
| `app.update.policy` | string `background\|onQuit\|notify` | `background` | About |
| `app.update.onMetered` | bool | false | About |
| `app.whatsnew.autoShow` | bool | true | About / dialog checkbox |
| `app.whatsnew.lastSeenVersion` | string (semver) | "" | page open / dialog dismiss |
| `notifications.appUpdates` (exists) | bool | true | Notifications tab |

Files: `%LOCALAPPDATA%\Wavee\cache\whatsnew\index.json`, `…\<semver>\whatsnew.json`, `…\<semver>\media\*`,
`…\issues.json` (per-issue `{state, stateReason, title, fetchedAt}`) — all under the cache root the factory reset clears.

---

## 7. Packaging changes

| File | Change |
|---|---|
| `ops/build/Wavee.AppxManifest.xml` | `Identity Name="__IDENTITY__"`, `DisplayName __DISPLAY__`, protocol `__PROTOCOL__`, toast CLSID `__TOAST_CLSID__`, StartupTask `__STARTUP_ID__`; `MinVersion="10.0.19041.0"`; `<rescap:Capability Name="packageManagement"/>` next to `runFullTrust` |
| `ops/build/Wavee.AppInstaller.template.xml` | `Name="__IDENTITY__"`; fix the comment (desktop apps get no prompt; `ShowPrompt` is inert; `ForceUpdateFromAnyVersion` allows downgrades and is the feed-rollback lever) |
| `ops/build/pack-wavee-msix.ps1` | flags `-Quad -Semver -Channel -Codename -IdentityName -Commit -BuildDate -NotesDir`; regex validates the **quad** only; stamps `/p:InformationalVersion=<semver>+build.<n>.sha.<sha> /p:WaveeChannel /p:WaveePackageVersion /p:WaveeCommit /p:WaveeBuildDate`; copies `<NotesDir>` → `payload/Assets/whatsnew/`; substitutes the identity fields |
| `ops/build/publish-wavee-aot.ps1` | same version stamping for the unpackaged build (channel `dev` unless given) |
| `.github/workflows/wavee-msix.yml` | §2.5 version job + gate, `validate` job, release step per §4.4, `generate_release_notes` removed |
| `.github/workflows/msix.yml` (gallery) | `make_latest: false` |
| `docs/guide/releasing-wavee.md`, `.claude/skills/releasing/SKILL.md`, `ops/build/README.md` | rolling feeds, `Wavee.Version.props` + codename bump, prerelease tags, the corrected rollback section (feed rollback via a lower quad *is* possible with `ForceUpdateFromAnyVersion`; roll-forward remains the default), the `wavee-release validate` preflight |

Phase 0 hotfix (can ship before anything else, 1 PR): rolling feed release in CI + `make_latest:false` on the gallery +
feed URL in the service. Without it no installed client ever updates.

---

## 8. Tests and gates (no source-text tests)

Unit (`src/apps/Wavee.Tests`, xunit, `MemoryAppSettings`, `ScriptedHttpHandler`):
- `WaveeVersionInfoTests` — parse/format for stable, beta, dev, unstamped; `UserAgent` shape; quad ↔ semver.
- `AppUpdateVersionTests` (extend) — 4-part compare with quads; prerelease strip unchanged.
- `ChangelogParserTests` — Keep-a-Changelog fixtures incl. `Known limitations`, scope prefixes, trailing ref groups, malformed headings.
- `MarkdownLiteTests` — every inline token, escapes, nesting limits, `#123`/`owner/repo#123`/`!123`/`@handle`, bare URLs, no HTML.
- `WhatsNewRangeTests` — stacking `(lastSeen, current]`, channel filter, unknown lastSeen, current not in index.
- `IssueStateBudgetTests` — ≤20 per opening, 24 h per-issue TTL, stop at 403/`x-ratelimit-remaining: 0`, snapshot fallback.
- `AppInstallerUpdateServiceTests` (rewrite) — feed 200/404/malformed/no-Version; snooze semantics; `Completed` survives "up to date"; metered refusal; `IPackageUpdater` fake returning `IsRegistered`/HRESULTs → mapped failures; unpackaged opens the release page.
- `ReleaseToolValidateTests` — fixture folders: missing date, codename mismatch, oversize media, GIF, unknown deep link, PR-vs-issue mismatch.
- `WhatsNewJsonContextTests` — round-trip the dossier example; unknown members ignored.

Engine gates: `dotnet build src/FluentGpu.slnx` Debug **and** Release, `dotnet run --project src/FluentGpu.VerticalSlice`
("ALL CHECKS PASSED") — the WindowsApi interop must not disturb the TerraFX-free closure (it lives in WindowsApi, fine).

Manual (packaged install, once per minor; add to the runbook): install previous `.appinstaller` → tag → relaunch →
Available toast → Update now → progress → relaunch → AfterUpdateDialog + OS "Updated" toast → About shows quad/codename →
diagnostics shows association. Plus the dossier §6 verify list (Win10 22H2 VM; module child alive; < 60 s uptime).

---

## 9. Rollout

| Phase | Ships | Deletes |
|---|---|---|
| **P0 — feed fix** (1 PR, releasable today) | rolling `wavee-stable` release in CI, `make_latest:false` on gallery, feed URL in service, prerelease-safe version job (quad only to the pack script) | `releases/latest` feed URL |
| **P1 — engine + versioning + notes** (this plan's Execution) | `IPackageUpdater` + interop, `IAppUpdateService` v2, `Wavee.Version.props` + `WaveeVersionInfo`, `Wavee.ReleaseTool`, `WhatsNewDocument`/`WhatsNewStore`, `WhatsNewPage` + dialog + About + toasts, manifest capability/MinVersion, validate job, docs | `ms-appinstaller:` path, `Downloaded` state, `ReleaseNotesUrl`, csproj `<Version>`, `HostVersion`, `generate_release_notes`, hard-coded toast strings |
| **P2 — beta channel + staged updates** | `cproducts.Wavee.Beta` identity/feed/data root, About channel picker, `AddPackageByUriAsync` deferred staging on Win11 22H2+ with the sidebar "Update ready" pill | — |

First release under P1 is `wavee-v0.2.0 "Breaker"` (the `[0.2.0] - unreleased` changelog entry gets its date; a
`ops/release/wavee/0.2.0/whatsnew.json` is authored by hand).

---

## 10. Execution (parallel Opus subagents; orchestrator builds/tests; disjoint files; pinned signatures)

Ground rules: subagents **never** build, test, run, launch, or `git stash`; they edit only their listed files; the
orchestrator runs Debug+Release builds, `Wavee.Tests`, VerticalSlice, and the canon gate once on the merged tree.
Signatures below are the contract between waves — an agent that needs to change one stops and reports instead.

**Wave 1 (5 agents, no cross-dependencies)**

| Agent | Files (owned exclusively) | Deliverable / pinned signatures |
|---|---|---|
| A1 Core contracts | `src/apps/Wavee.Core/Notifications/AppUpdate.cs`, `AppUpdateVersion.cs`, new `Wavee.Core/Versioning/WaveeVersionInfo.cs`, new `Wavee.Core/WhatsNew/{WhatsNewDocument,WhatsNewJsonContext,ChangelogParser,MarkdownLite,IssueRefParser,WhatsNewRange,IssueStateBudget}.cs` + tests for each in `Wavee.Tests/WhatsNew/*`, `Wavee.Tests/Versioning/*` | §3.2 interface verbatim; `WaveeVersionInfo.Parse(string informational, IReadOnlyDictionary<string,string> metadata)`; `MarkdownLite.Tokenize(string) → InlineToken[]`; `ChangelogParser.Parse(string markdown) → ChangelogDocument`; `WhatsNewRange.Between(string lastSeen, string current, WhatsNewIndex, string channel) → WhatsNewIndexEntry[]` |
| A2 WindowsApi interop | new `src/FluentGpu.WindowsApi/Packaging/{IPackageUpdater,PackageUpdater,PackageManagerInterop,WinRtAsync,PackageDeploymentResult,AppInstallerInfo}.cs`; `PackageIdentity.cs` (add `GetAppInstallerInfo` probe only) | §3.4 interface verbatim; IIDs from dossier §2.3; MTA worker; `RegisterApplicationRestart` P/Invoke; no `ComWrappers` on the call path; AOT-clean (`[GeneratedComInterface]` allowed for handler CCWs, polling preferred) |
| A3 Release tool + CI + packaging | new `src/apps/Wavee.ReleaseTool/*` (+ add to `FluentGpu.slnx`), `.github/workflows/wavee-msix.yml`, `.github/workflows/msix.yml` (`make_latest` only), `ops/build/pack-wavee-msix.ps1`, `ops/build/publish-wavee-aot.ps1`, `ops/build/Wavee.AppxManifest.xml`, `ops/build/Wavee.AppInstaller.template.xml`, new `src/apps/Wavee/Wavee.Version.props`, `src/apps/Wavee/Wavee.csproj` (version block only), new `ops/release/wavee/0.2.0/whatsnew.json`, `CHANGELOG.md` (date the 0.2.0 entry + add the update/what's-new items) | CLI per §4.3 (consumes A1's model — A3 codes against the §4.2 shapes; orchestrator reconciles at merge); workflow per §2.5/§4.4; pack flags per §7 |
| A4 Update service + store + settings | `src/apps/Wavee/App/AppInstallerUpdateService.cs` (rewrite), `AppUpdateScheduler.cs`, new `App/WhatsNewStore.cs`, new `App/FakeAppUpdateService.cs` (simulator), `Platform/AppSettings.cs` (keys §6), `App/AppVersion.cs`, `App/Services.cs` (wiring lines only), `Backend/Spotify/HttpPools.cs` (add `HttpPool.GitHub`), tests `Wavee.Tests/AppInstallerUpdateServiceTests.cs`, `Wavee.Tests/WhatsNewStoreTests.cs` | ctor `(IAppSettings, HttpClient, WaveeVersionInfo, string arch, IPackageUpdater, IWaveeLog)`; `WhatsNewStore.GetAsync(string semver, CancellationToken) → Task<WhatsNewDocument?>`, `PrefetchIndexAsync`, `RefreshIssueStatesAsync(WhatsNewDocument, CancellationToken)`; `Services.AppUpdate` stays `IAppUpdateService`; `Services.WhatsNewStore` new |
| A5 UI | new `Features/WhatsNew/{WhatsNewPage,WhatsNewHero,HighlightCard,ChangelogSection,ChangelogItem,IssueChip,ReleaseRail,AfterUpdateDialog}.cs`, `Features/Shell/SettingsPage.About.cs`, `Features/Shell/{ShellRoutes,ContentHost,NotificationPanel}.cs`, `App/{NotificationCenterBridge,ToastEscalator}.cs`, `Features/Shell/DiagnosticsPanel.cs` (Updates section), `assets/loc/en-US.json` (§5.5 keys) | codes against §3.2 + A4's store signatures; `Embed.Comp(() => new WhatsNewPage())`, `Key="page:whatsnew"`; deep-link arm in `WaveeShell.GoDeepLinkOpen`; `RichTextBlock.Paragraph(MarkdownLite.ToSpans(...))` |

**Wave 2 (after the orchestrator's first green build)** — 2 agents: (B1) docs: `docs/guide/releasing-wavee.md`,
`.claude/skills/releasing/SKILL.md`, `ops/build/README.md`, `docs/guide/README.md` index line; (B2) a review agent that
adversarially checks: no `ms-appinstaller` string remains, no `Downloaded` state, no env-var gate, no source-text test,
`FluentGpu.Controls`/VerticalSlice closure still TerraFX-free, every new user string has a loc key.

Orchestrator verification (once, merged tree): `dotnet build src/FluentGpu.slnx` (Debug + Release) →
`dotnet test src/apps/Wavee.Tests` → `dotnet run --project src/FluentGpu.VerticalSlice` →
`pwsh ops/build/pack-wavee-msix.ps1 -Arch x64 -Quad 0.2.0.0 -Semver 0.2.0 -Channel stable -Codename Breaker -NoSign` →
`dotnet run --project src/apps/Wavee.ReleaseTool -- validate …` against the real `CHANGELOG.md` → packaged install
smoke on this machine (About hero, What's new page from the embedded doc, simulator walk of every state).

---

## 11. Open items (decide during P1, none block starting)

1. Whether `AppInstallerManager.SetAutoUpdateSettings` (Win11 22000+) can attach the association without elevation —
   would replace the ShellExecute "Repair auto-update" rung.
2. Highlight media autoplay policy on battery (proposal: poster only on battery, like reduced motion).
3. Contributor avatars: `avatars.githubusercontent.com/<login>` is a plain image GET (no REST budget) — include in v1 if
   `ImageEl` handles the redirect; otherwise initials.
4. Whether the beta feed should also receive stable finals (plan says yes; keeps beta users from lagging behind stable).
