# What's new images (framed), local update E2E, toast fix — implementation plan

## Context

The auto-update + "What's new" work is green in `fluent-gpu` (builds, 6646 tests, VerticalSlice, Pester, signed
`-DryRun`, installed smoke). Three follow-ups came out of the smoke:

1. **Images.** The highlight cards render an empty tinted band because `ops/release/wavee/0.2.0/whatsnew.json` has no
   `media` block. The user wants real, *beautiful framed* marketing images. Research (Linear, Raycast, Vercel, Notion,
   Microsoft Store/App Store rules, shots.so/Xnapper/Pika, Satori, ImageMagick, cwebp, Pillow) converged on one recipe:
   a single accent gradient field per release, the screenshot at ~88 % width with 14 px radius, a 1 px translucent white
   stroke, a layered (contact + mid + ambient) shadow, captions *outside* the image, no 3-D tilt, 16:9 at 1200×675,
   rendered at 2× and downscaled once. Everything is reproducible offline with tools already on this machine
   (`msedge --headless=new`, `ffmpeg`, PowerShell 5.1, Python). Two latent app bugs surfaced: the poster `ImageEl` sets
   `Height` only, so the decoder target collapses to **1×118 px** (the first real poster would render as a smear), and
   WebP decoding depends on the optional Store codec — so posters ship as **JPEG**.
2. **A fully local end-to-end update test** (no GitHub): package A installed from a loopback-HTTP `.appinstaller`,
   package B dropped into the same feed, A finds it, Windows downloads + installs through `PackageUpdater`, relaunches
   as B, the after-update dialog appears; plus a way to re-open that dialog from Settings › About.
3. **Toast close button outside the card**: `InfoBar`'s horizontal panel has `Grow = 1` but no `Shrink`, so a 39-char
   message + "Retry" overruns the 380 px card and pushes the ✕ past the painted plate (a control bug, not the call site).

Rules unchanged: no legacy paths, no env switches, no source-text tests, subagents never build/run, orchestrator
verifies once on the merged tree.

---

## Track 1 — Framed release images

### 1.1 Pipeline (new `ops/release/tools/`)

```
 Wavee (installed, running)         Capture-WaveeWindow.ps1          frame.html + New-ReleaseImage.ps1              ffmpeg
 ─────────────────────────          ──────────────────────────       ──────────────────────────────────────         ──────
 wavee://open?route=<page> ───────► DWM extended-frame rect ────►    <img file:///…/shot.png> in .shot ────────►     scale 1200:675 lanczos
 window sized 1600×1000 (MoveWindow) CopyFromScreen @ native DPI      msedge --headless=new --force-device-scale-factor=2   -q:v ↑ until ≤150 KB
                                     artifacts\media-src\<name>.png   --window-size=1200,675 --screenshot=card@2x.png       ops\release\wavee\<ver>\media\<name>.jpg
```

**`ops/release/tools/frame.html`** — one template, three variants selected by `data-variant` on `<body>`
(`card` | `detail` | `twoup`), all CSS variables injectable by the script (`--tint-a`, `--tint-b`, `--base`,
`--radius`, `--scale`, `--srcw`, `--zoom`, `--cx`, `--cy`) and `<img>` sources substituted (`__SHOT__`, `__SHOT2__`).
Recipe (from the research, kept verbatim in the file):

```css
:root{ --w:1200px; --h:675px; --base:#202020; --tint-a:#3d5a8f; --tint-b:#6b3a63; --radius:14px; --scale:.88; }
.canvas{ position:relative; width:var(--w); height:var(--h); overflow:hidden; background:var(--base);
         display:flex; align-items:center; justify-content:center; }
.canvas::before{ content:""; position:absolute; inset:-10%;
  background: radial-gradient(52% 60% at 18% 12%, color-mix(in oklab,var(--tint-a) 90%,transparent) 0%, transparent 62%),
              radial-gradient(48% 56% at 84% 22%, color-mix(in oklab,var(--tint-b) 85%,transparent) 0%, transparent 64%),
              radial-gradient(70% 70% at 60% 108%, color-mix(in oklab,var(--tint-a) 55%,transparent) 0%, transparent 70%),
              var(--base); }
.canvas::after{ content:""; position:absolute; inset:0; background:radial-gradient(120% 100% at 50% 45%, transparent 42%, rgba(0,0,0,.42) 100%); }
.grain{ position:absolute; inset:0; opacity:.045; mix-blend-mode:overlay; background-image:url("data:image/svg+xml;…feTurbulence baseFrequency=.85…"); } /* kills gradient banding */
.shot{ position:relative; z-index:2; width:calc(var(--w)*var(--scale)); border-radius:var(--radius); overflow:hidden; line-height:0; background:var(--base);
  box-shadow: 0 0 0 1px rgba(255,255,255,.13), inset 0 1px 0 rgba(255,255,255,.06),
              0 2px 4px rgba(0,0,0,.28), 0 12px 24px rgba(0,0,0,.34), 0 30px 60px rgba(0,0,0,.45); }
.shot img{ display:block; width:100%; height:auto; }
/* detail: fixed 760×400 viewport, the 2× PNG translated by --cx/--cy and scaled by --zoom, soft accent ring (0 0 0 8px rgba(61,90,143,.22), 0 0 0 9px rgba(120,160,235,.28)) */
/* twoup: .stack 1056×520 — .back 840px (weaker stroke, saturate(.94) brightness(.92)) + .front 470px bottom-right (stronger stroke, bigger ambient shadow) */
```
No caption inside the image (captions are the card title/body; store listings are the only place text is baked in).

**`ops/release/tools/New-ReleaseImage.ps1`** (PS 5.1, ASCII, no BOM):
```powershell
param([Parameter(Mandatory)][string]$Shot, [Parameter(Mandatory)][string]$Out,        # .jpg (default) or .webp
      [ValidateSet('card','detail','twoup')][string]$Variant='card', [string]$Shot2,
      [string]$TintA='#3d5a8f', [string]$TintB='#6b3a63', [double]$Scale=0.88, [int]$Radius=14,
      [double]$Zoom=1.35, [int]$Cx=0, [int]$Cy=0,                                       # detail crop, in source px
      [int]$MaxBytes=150000, [int]$Width=1200, [int]$Height=675)
# 1. copy frame.html → $env:TEMP\wavee-frame\<guid>\frame.html with __SHOT__/__SHOT2__ = absolute file:/// URLs and the CSS vars injected
# 2. & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=2 --window-size=$Width,$Height
#      --default-background-color=00000000 --virtual-time-budget=2000 --screenshot="<tmp>\card@2x.png" "file:///<tmp>/frame.html"
#    ($edge = the same probe as the prototype renders: Program Files (x86)\Microsoft\Edge\Application\msedge.exe)
# 3. encode: for ($q = 3; $q -le 12; $q++) { ffmpeg -y -loglevel error -i card@2x.png -vf "scale=${Width}:${Height}:flags=lanczos" -q:v $q $Out; if ((Get-Item $Out).Length -le $MaxBytes) { break } }
#    (.webp: -c:v libwebp -quality 88→60 stepping; only if the author insists — JPEG is the default because WebP needs the Store codec to DECODE in the app)
# 4. print "<Out>  <bytes> B  q=<q>  <Width>x<Height>"; fail if still > $MaxBytes
```
`ops/release/tools/Capture-WaveeWindow.ps1`:
```powershell
param([Parameter(Mandatory)][string]$Out, [string]$Route, [string]$Arg, [int]$W=1600, [int]$H=1000, [int]$SettleMs=2500)
# find the Wavee main window (Get-Process Wavee | MainWindowHandle), MoveWindow to (W,H) at (40,40),
# Start-Process "wavee://open?route=$Route&arg=$Arg" when given, sleep $SettleMs,
# DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS=9) → rect WITHOUT the drop shadow, CopyFromScreen(rect) → PNG (native DPI, no scaling)
```
Both are dev-box tools (they need a running, signed-in Wavee) — documented in `ops/release/wavee/README.md`
"Making the images" with the exact three commands. Source PNGs stay out of git (`artifacts/media-src/`); only the
≤150 KB JPEGs live under `ops/release/wavee/<semver>/media/`.

### 1.2 App fixes that the first real poster needs

| File | Change |
|---|---|
| `src/apps/Wavee/Features/ReleaseNotes/HighlightCard.cs` `Media()` (~L85-105) | the poster `ImageEl` gets `AspectRatio = 16f/9f`, `AlignSelf = Stretch`, `DecodePx = 1200f`, `Corners` top-only = `Radii.Card` (band is the card's top), `Fit = Cover`; the band's fixed `Height = 118/92` becomes the 16:9 aspect of the card width (compact dialog keeps a `MaxHeight`); automation name = `h.Media.Alt` |
| `src/apps/Wavee.Core/ReleaseNotes/ReleaseNotesValidation.cs` `ValidateMedia` | new rule: `src`/`poster` must be `media/<basename>` (what `CopyMedia` flattens to and what `MediaPath` resolves) — error text says so; unit test |
| `ops/release/wavee/README.md` | "Making the images" section (pipeline + rules: 1200×675 JPEG ≤150 KB, one tint pair per release, no baked text, alt text describes the feature); note WebP is accepted by the validator but only decodes with the Store codec → don't |
| `ops/release/wavee/0.2.0/whatsnew.json` | the single highlight gets `"media": { "kind": "image", "src": "media/redesigned.jpg", "alt": "The new Wavee home page", "width": 1200, "height": 675 }` |
| `ops/release/wavee/0.2.0/media/redesigned.jpg` | produced by the pipeline from a capture of Home (route `home`) on the installed 0.2.0.1 — orchestrator step, not an agent's |

Test: `ReleaseNotesValidationTests` gains the `media/<basename>` rule cases; `HighlightCard` has no headless test
(engine-bound) — the installed smoke is the evidence (poster visible, not a smear).

---

## Track 2 — Toast close button (`FluentGpu.Controls`)

Root cause (`src/FluentGpu.Controls/InfoBar.cs:353-362` horizontal panel, `:403-412` vertical panel): the panel `BoxEl`
has `Grow = 1f` and no `Shrink`; `Element.Shrink` defaults to 0 and `FlexLayout.cs:527` discards negative free space
when no child can shrink, so a 39-char message + action (39 < the 60-char vertical heuristic) measures 324 px in a
286 px slot and the 38 px close column is arranged at x≈391 — outside the 380 px plate. Neither the toast frame
(`Toast.cs:341-354`) nor the InfoBar root clips.

Fix (two lines, control-wide): both panels get `Shrink = 1f, MinWidth = 0f` (the message `TextEl` already has
`Wrap = WrapWholeWords, Shrink = 1f`, so it wraps). Do **not** add `Basis = 0f` (collapses a standalone InfoBar) and do
not paper over with `ClipToBounds`. Optional parity note in the file: WinUI's `InfoBarPanel` switches to vertical by
*measured width*, not a character count — leave the heuristic, but mention it.

Gate: a new VerticalSlice check in the controls suite — build `InfoBar.Create(Error, "", "Couldn't reach GitHub. Try
again later.", actionLabel: "Retry", isClosable: true)` inside a 380 px-wide box, run layout headlessly, assert the
close button's laid rect is inside the root rect and the message wrapped to ≥2 lines. Registered in that suite's
`Run` (no new suite).

---

## Track 3 — Fully local update E2E (no GitHub)

### 3.0 Shape

```
 harness (elevated PS 5.1)                 C:\wavee-feed  ← LocalFeedServer.psm1 (HttpListener, HEAD + Range, request log)
 ────────────────────────                  http://127.0.0.1:8099/
 pack A (quad 0.2.0.9001)  ───────────►     pkg\Wavee_0.2.0.9001_<arch>.msix
   -UpdateBaseUrl http://127.0.0.1:8099/    wavee-local\Wavee.<arch>.appinstaller   (root Uri == served URL; MainPackage/@Uri → pkg\…)
   -FeedRelease wavee-local                 wavee-local\whatsnew-index.json          wavee-v0.2.0\whatsnew.json (+ media)
 Add-AppxPackage -AppInstallerFile http://127.0.0.1:8099/wavee-local/Wavee.<arch>.appinstaller   ← creates the association
 launch A → [update] up to date …          (in-app checker GET, UA "Wavee/…", proven from the request log)
 pack B (0.2.0.9002) → flip the feed       (same .appinstaller name, Version 0.2.0.9002, MainPackage → B)
 drive: installOnQuit=1 + CloseMainWindow   →  [update] update available … → install-on-quit: staging … → AppXSvc GETs pkg\B (206s)
 Get-AppxPackage == 0.2.0.9002 → relaunch  →  [update] updated: 0.2.0.9001 -> 0.2.0.9002 → after-update plate (screenshot) → previousVersion
```
Only outbound dependency: the dev-cert timestamp server (`http://timestamp.digicert.com`). Nothing on GitHub is
touched: no tag, branch, release, or `gh`.

Why loopback HTTP rather than `file:`: the in-app checker and the notes store use the shared `SocketsHttpHandler`
client (http/https only — a `file:` request throws `NotSupportedException` → `Failed › Network`), so serving over
127.0.0.1 exercises the REAL code path with zero production branches. The deployment engine accepts http/UNC/local
`.appinstaller` URIs (`Add-AppxPackage -AppInstallerFile C:\…` is a documented example); the `.appinstaller` root
`Uri` must equal the served location (App Installer's redirect rule); `ForceUpdateFromAnyVersion=true` allows A→B and
B→A so the harness can loop. Wavee is `runFullTrust` (no AppContainer), so the loopback block does not apply.

### 3.1 `UpdateBaseUrl` — build-time metadata (default GitHub), never a runtime switch

`src/apps/Wavee/Wavee.csproj` (after `WaveeFeedRelease`, ~L31; item after L41):
```xml
<WaveeUpdateBaseUrl Condition="'$(WaveeUpdateBaseUrl)' == ''">https://github.com/christosk92/WaveeMusic/releases/download/</WaveeUpdateBaseUrl>
…
<AssemblyMetadata Include="UpdateBaseUrl" Value="$(WaveeUpdateBaseUrl)" />
```
`src/apps/Wavee.Core/Versioning/WaveeVersionInfo.cs` — append a 10th positional param (the test helper builds the record positionally with 9):
```csharp
public sealed record WaveeVersionInfo(string SemVer, string Core, int? Beta, string Quad, string Codename, string Channel,
    string Commit, string BuildDate, string FeedRelease = "wavee-stable", string UpdateBaseUrl = WaveeVersionInfo.DefaultUpdateBaseUrl)
{
    public const string DefaultUpdateBaseUrl = "https://github.com/christosk92/WaveeMusic/releases/download/";
    /// <summary>Trim; empty → default; guarantee the trailing slash. Shared by Parse and ReleaseNotesStore.</summary>
    public static string NormalizeUpdateBaseUrl(string? raw)
    { string s = raw?.Trim() ?? ""; if (s.Length == 0) return DefaultUpdateBaseUrl; return s.EndsWith('/') ? s : s + "/"; }
    // Parse(): … string baseUrl = NormalizeUpdateBaseUrl(Get("UpdateBaseUrl")); return new(…, feed, baseUrl);
}
```
`App/AppInstallerUpdateService.cs:96` → `FeedUrl = me.UpdateBaseUrl + me.FeedRelease + "/" + assetPrefix + _arch + ".appinstaller";`
(`ReleaseNotesLinks.RepoUrl` stays GitHub for human links).
`App/ReleaseNotesStore.cs`: delete `const ReleasesRoot`; ctor gains a 6th optional `string? releasesRoot = null`
(`embeddedRoot` is the 5th and is passed positionally by tests) → `_releasesRoot = WaveeVersionInfo.NormalizeUpdateBaseUrl(releasesRoot)`;
the doc URL (`:146`), index URL (`:195`) and any media fetch use `_releasesRoot`. `App/Services.cs:354` passes
`releasesRoot: AppVersion.Info.UpdateBaseUrl`.
`ops/build/pack-wavee-msix.ps1`: `[string]$UpdateBaseUrl = 'https://github.com/christosk92/WaveeMusic/releases/download/'`;
validate absolute http(s), **plain http only for a loopback host**, normalize the trailing slash; `/p:WaveeUpdateBaseUrl=$UpdateBaseUrl`
in `$pubArgs`; banner + summary print the base when non-default. `ops/build/README.md` flag row.
Tests: `WaveeVersionInfoTests` (default; loopback stamped; trailing slash normalized; empty → default);
`AppInstallerUpdateServiceTests.FeedUrl_IsBuiltUnderTheStampedBaseUrl` (`http://127.0.0.1:8099/wavee-local/Wavee.x64.appinstaller`);
`ReleaseNotesStoreTests` — a store built with `releasesRoot: "http://127.0.0.1:8099/"` requests `…/wavee-v0.4.0/whatsnew.json` and `…/wavee-stable/whatsnew-index.json`.

### 3.2 Re-open the after-update dialog from Settings › About

- `Platform/AppSettings.cs`: `public static readonly SettingKey<string> ReleaseNotesPreviousVersion = new("app.whatsnew.previousVersion", "");`
  — written next to `pendingFrom` in the updater ctor (`AppInstallerUpdateService.cs:113`) and **never cleared** (the one-shot
  `pendingFrom` is consumed by the plate; About and the harness need the from-quad afterwards). Mirror in `TestAppSettingsShim.cs`; test the write.
- `assets/loc/en-US.json`: `update.about.showSummaryAgain = "Show the update summary again"`.
- `Features/Shell/SettingsPage.About.cs` `AboutUpdatePanel.Render`: add `var overlay = UseContext(Overlay.Service);` (unconditional, top of
  Render — the panel is inside the OverlayHost subtree, the same rule `AfterUpdateChrome` documents), build
  ```csharp
  Action? showSummary = (overlay is not null && !me.IsDev && svc is not null) ? () =>
  {
      string from = svc.Settings.Get(WaveeSettings.ReleaseNotesPreviousVersion); if (from.Length == 0) from = me.Quad;
      AfterUpdateDialog.Open(overlay, svc.Settings, from, me, svc.ReleaseNotes, key => nav?.Invoke(key, null));
  } : null;
  ```
  and pass it to `Hero(...)`, which appends `HyperlinkButton.Create(Loc.Get(Strings.Update.About.ShowSummaryAgain), showSummary)`
  under "What's new in <codename> →". (`Open` clearing `pendingFrom` is correct: an explicit re-open must not re-arm the auto plate.)

### 3.3 `ops/release/tests/LocalFeedServer.psm1` (new) — the loopback feed

`HttpListener` on `http://127.0.0.1:<port>/` in a background runspace serving a root folder: `Content-Length` on every
response, `HEAD`, single-range `GET` (206 with `Content-Range`; 416 when unsatisfiable; multi-range → 200 whole body),
`Accept-Ranges: bytes`, `Cache-Control: no-cache`, a path-traversal-safe `Resolve-FeedFile`, content types
(`application/appinstaller`, `application/msix`, `application/json`, …), and one tab-separated log line per request
(`time  method  path  status  range  bytes  user-agent`) — the request log is the harness's evidence that the **deployment
engine** (not the app) downloaded the package, in Range slices. Pure helpers exported for Pester: `Get-RangeSlice`
(`bytes=a-b | bytes=a- | bytes=-n` → status/start/end/count), `Get-FeedContentType`, `Resolve-FeedFile`;
`Start-LocalFeedServer -Root -Port -LogPath` (throws with the exact `netsh http add urlacl …` line if the bind fails) /
`Stop-LocalFeedServer`. ~120 lines, PS 5.1, ASCII, no BOM.

`ops/release/tests/LocalFeedServer.Tests.ps1` (new, Pester 3.4): range arithmetic (0-9, 90-, -10, 0-999, 100- → 416,
5-3 → 416, -0 → 416, multi-range → 200, empty → 200, length 0), traversal rejection, url-decoding, content types, and an
admin-only live test (random port, 1000-byte file: GET 200 + `Content-Length`, HEAD without body, `AddRange(10,19)` →
206 + `Content-Range: bytes 10-19/1000`, 404, four log lines). `Wavee.Release.Tests.ps1` gains two `New-WaveeAppInstaller`
cases (loopback http URIs verbatim; UNC backslashes untouched).

### 3.4 `ops/release/tests/local-update-e2e.ps1` (new) — the harness

`#requires -Version 5.1 -RunAsAdministrator` (cert import into `LocalMachine\TrustedPeople`, `reg load` of the package's
Helium hive, listener bind). Parameters: `-Arch` (host default) `-Port 8099` `-FeedRelease wavee-local` `-QuadA 0.2.0.9001`
`-QuadB 0.2.0.9002` `-SemverA/-SemverB 0.2.0` `-NotesA/-NotesB` (default `ops\release\wavee\<semver>`; a synthetic 0.2.1
folder exercises stacked notes) `-Driver quit|ui` `-Drill none|snooze|network|downgrade|bare` `-FeedDir C:\wavee-feed`
`-OutDir artifacts\local-e2e` `-KeepFeed` `-SkipPackA/-SkipPackB` (reuse A|B msix) `-RemoveCert` `-NoAot` `-Publisher`
`-IdentityName` `-CheckTimeoutSec 120` `-ApplyTimeoutSec 600`. Every phase is `try/catch`-recorded into a PASS/WARN/FAIL
table printed at the end; exit code = fail count; `finally` always cleans up.

Helpers (in the script): `Record` / `Assert-True (-Soft)`; `Get-WaveePackage / Get-WaveePfn / Get-WaveeProcess`;
`Start-Wavee` (`shell:AppsFolder\<pfn>!Wavee` — a packaged app without `allowElevation` activates at medium IL even from
the elevated shell; `-LaunchVia explorer` fallback); `Wait-WaveeExit`; **log**: `Get-WaveeLogDirs` (packaged =
`%LOCALAPPDATA%\Packages\<pfn>\LocalCache\Local\Wavee\logs`, else `%LOCALAPPDATA%\Wavee\logs`), `Get-LogMark`,
`Wait-LogLine -Pattern -TimeoutSec -Mark -FailPattern` (opens with `FileShare.ReadWrite`), `Copy-WaveeLog`; **settings**:
`Mount-WaveeSettings` (packaged: `reg.exe load HKU\WaveeE2E …\SystemAppData\Helium\User.dat` — only while no package
process runs, retried for 20 s, then locate `*\Software\Wavee\Wavee\Settings` inside the hive; unpackaged: the real
`HKCU\Software\Wavee\Wavee\Settings`), `Get-/Set-/Remove-WaveeSetting` via `reg.exe` (REG_SZ / REG_DWORD / REG_QWORD as
`AppDataStore` writes them), `Dismount-WaveeSettings`; **screens**: `Save-WindowShot` (window rect → CopyFromScreen PNG),
`Get-ShotMeanLuma` (the modal scrim darkens the shell — a cheap "plate is up" signal, soft-asserted; the PNG is kept for eyes);
**feed**: `Publish-LocalFeed -Quad -Semver -Msix -NotesDir -IndexSemvers -IndexQuads` (copies the msix to `pkg\`, renders the
`.appinstaller` with `New-WaveeAppInstaller` (`-FeedUri http://127.0.0.1:<port>/<feed>/Wavee.<arch>.appinstaller`,
`-MsixUri http://127.0.0.1:<port>/pkg/<msix>`), copies the notes to `wavee-v<semver>\`, writes the cumulative
`whatsnew-index.json` in the ReleaseTool's casing, then GETs the feed over the wire and asserts the root `Version`).

Phases and assertions:
```
P0 preflight  elevated · PS 5.1 · Windows SDK · dotnet · port free · sideloading reg value (warn) · notes dirs · template · Add-Type ok
P1 clean      stop Wavee · Remove-AppxPackage (every version) · delete the unvirtualized settings key · wipe $FeedDir · new $OutDir
P2 pack A, B  pack-wavee-msix.ps1 -Arch -Quad -Semver -Channel stable -Codename Breaker -NotesDir -FeedRelease wavee-local
              -UpdateBaseUrl http://127.0.0.1:8099/ -Publisher -IdentityName -OutputDir <A|B> [-NoAot]   (no -Install)
              assert Get-MsixIdentity == quad/arch/name/publisher; B's .cer thumbprint == A's (same dev cert)
P3 trust      Import-Certificate A\*.cer → Cert:\LocalMachine\TrustedPeople (skip if present)
P4 server     Start-LocalFeedServer; assert HEAD Content-Length, Range 0-9 → 206, unknown → 404
P5 feed A     Publish-LocalFeed A (index [A])
P6 install A  Add-AppxPackage -AppInstallerFile <feedUri>; assert package == QuadA; Get-AppxPackageAutoUpdateSettings.AppInstallerUri == feedUri (soft);
              request log has GET /pkg/Wavee_<A>.msix (the engine downloaded over loopback)
P7 launch A   Start-Wavee; Wait-LogLine 'up to date: feed <A>, running <A>' (90 s = 30 s first check); request log has
              GET /wavee-local/… with UA 'Wavee/…' (the in-app checker); no github.com in [update]/[whatsnew] lines; shot 01-baseline.png
P8 feed B     Publish-LocalFeed B (index [B, A])
P9 drive      quit: CloseMainWindow → Wait-WaveeExit → Mount: installOnQuit=1, delete lastCheckedMs (1 h cooldown), [snooze: snoozedVersion=B]
              → Dismount → Start-Wavee → Wait-LogLine 'update available: <B> \(running <A>\)' → ensure uptime ≥ 65 s (RegisterApplicationRestart floor)
              → [network: Stop-LocalFeedServer] → CloseMainWindow → Wait-LogLine 'install-on-quit finished: Installing|staged <B>; restarting'
              FailPattern 'deployment failed 0x[0-9A-F]{8}.*|install-on-quit gave up.*'      ui: prompt "About › Update now", wait 'staged <B>; restarting'
P10 verify B  Wait-WaveeExit; poll Get-AppxPackage == QuadB (60 s); request log: /pkg/Wavee_<B>.msix fetched with 200|206 by a non-Wavee UA,
              ≥1 partial (206) response (soft); Copy-WaveeLog A-final
P11 relaunch  WER relaunch or Start-Wavee; Wait-LogLine 'updated: <A> -> <B>'; shot 02-after-update.png; luma < baseline×0.85 (soft = plate up);
              Wait-LogLine 'up to date: feed <B>, running <B>'; close; Mount: previousVersion == A, pendingFrom == '', lastRunVersion == B (INFO lastSeenVersion)
P12 drills    downgrade: feed → A, launch B, assert 'up to date: feed <A>, running <B>' (in-app refusal), relaunch twice, soft-assert package == A (OS OnLaunch path)
              bare: P6 via Add-AppxPackage -Path (no association) → assert AutoUpdateSettings absent → after P10 assert AppInstallerUri == feedUri (apply created it)
P13 cleanup   stop app · Copy-WaveeLog B-final · Remove-AppxPackage · Stop-LocalFeedServer · copy feed-requests.log · rm $FeedDir (unless -KeepFeed) · [-RemoveCert] · results table
```

### 3.5 Runbook

`docs/guide/releasing-wavee.md`: new **§8 "Local end-to-end (no GitHub)"** — what it proves, the one elevated command
(`powershell -NoProfile -ExecutionPolicy Bypass -File ops\release\tests\local-update-e2e.ps1`), iteration switches
(`-SkipPackA -SkipPackB`, `-KeepFeed`, `-Driver ui`, `-Drill …`), the feed layout, why the base URL is baked at pack
time, artefacts (`artifacts\local-e2e\*.png`, `wavee-A-final.log`, `wavee-B-final.log`, `feed-requests.log`),
troubleshooting (`0x800B0109` cert not trusted → P3; `0x80073D02` packages in use → module child; listener `Access is
denied` → not elevated / urlacl; `install-on-quit gave up` → 60 s floor / metered). The old GitHub scratch-feed recipe
becomes **§8b (optional)**; §9's observation table stays and maps onto the phases/drills. `ops/release/README.md`: one sentence.

### 3.6 Risks (handled)
Loopback + packaged app — only AppContainer processes are loopback-blocked; Wavee is full-trust (P7 proves the in-app GET
from the request log). AppXSvc fetching 127.0.0.1 — loopback is machine-wide (P6 proves the engine download before any
update); a system proxy without a loopback bypass is the one known way this fails → plan B (deferred, not built): a
UNC/local feed + a `file:` branch in the two readers. `HttpListener` URL ACL → elevated harness / exact `netsh` hint.
Dev-signed msix → cert in `LocalMachine\TrustedPeople`; sideloading is on by default on Win11. 60 s restart floor → P9
waits for ≥65 s uptime. Helium hive lock → mount only between runs, `reg.exe` only. `Remove-AppxPackage` deletes
LocalCache → logs copied first. `ForceTargetAppShutdown` may kill the process before `install-on-quit finished` is
written → P9 accepts either line; P10's package version is the decisive assertion.

---

## Execution (Opus subagents, disjoint files; agents never build/run/launch/git; orchestrator verifies once)

| Agent | Files | Deliverable |
|---|---|---|
| G1 images tooling | new `ops/release/tools/{frame.html,New-ReleaseImage.ps1,Capture-WaveeWindow.ps1}`; `ops/release/wavee/README.md` ("Making the images") | Track 1.1 |
| G2 app (C#) | `Features/ReleaseNotes/HighlightCard.cs`; `Wavee.Core/ReleaseNotes/ReleaseNotesValidation.cs` (+ `Wavee.Tests/ReleaseNotes/ReleaseNotesValidationTests.cs`); `Wavee.csproj`; `Wavee.Core/Versioning/WaveeVersionInfo.cs`; `App/{AppInstallerUpdateService,ReleaseNotesStore,Services,AppVersion}.cs`; `Platform/AppSettings.cs`; `Wavee.Tests/TestAppSettingsShim.cs`; `Features/Shell/SettingsPage.About.cs`; `assets/loc/en-US.json`; tests `WaveeVersionInfoTests`, `AppInstallerUpdateServiceTests`, `ReleaseNotesStoreTests` | Tracks 1.2, 3.1, 3.2 |
| G3 controls | `src/FluentGpu.Controls/InfoBar.cs` (both panels: `Shrink = 1f, MinWidth = 0f`); a VerticalSlice check in the controls suite asserting the close button's rect is inside the root and the message wrapped | Track 2 |
| G4 ops | `ops/build/pack-wavee-msix.ps1` (`-UpdateBaseUrl`), `ops/build/README.md`; new `ops/release/tests/{LocalFeedServer.psm1,LocalFeedServer.Tests.ps1,local-update-e2e.ps1}`; `ops/release/tests/Wavee.Release.Tests.ps1` (two `It`s); `docs/guide/releasing-wavee.md` §8/§8b; `ops/release/README.md` | Tracks 3.3–3.5 |

Orchestrator verification:
1. `dotnet build src/FluentGpu.slnx` Debug + Release; `dotnet test src/apps/Wavee.Tests`; `dotnet run --project src/FluentGpu.VerticalSlice` (new InfoBar check green); `Invoke-Pester ops\release\tests` (elevated for the listener test).
2. Pack-flag guards: `-UpdateBaseUrl http://example.com/` throws (non-loopback http); `notaurl` throws.
3. Images: with 0.2.0.1 running → `Capture-WaveeWindow.ps1 -Route home -Out artifacts\media-src\home.png` → `New-ReleaseImage.ps1 -Shot … -Out ops\release\wavee\0.2.0\media\redesigned.jpg` (≤150 KB, 1200×675) → add the `media` block → `Wavee.ReleaseTool validate` passes → `pack-wavee-msix.ps1 -Arch arm64 -NoSign` → install → the card shows the framed poster (not a smear); the error toast's ✕ is inside the card.
4. `local-update-e2e.ps1` (elevated) → OVERALL: PASS; inspect `02-after-update.png`; `-SkipPackA -SkipPackB -Drill network` and `-Drill bare`; About › "Show the update summary again" re-opens the plate on B.
