# Opt-in crash & diagnostics pipeline for Wavee 0.3

Target: the current 0.3 working tree on `main` (uncommitted changes included). First implementation step copies this
plan, expanded with the full code shapes, to `docs/plans/wavee/crash-diagnostics-implementation.md` (house rule).

## Context

Partner Center's Health page shows a 4.76 % crash rate on 1.2.1011.0 and nothing else: no stack, no log, no way to
tell a GPU driver fault from a managed exception, and it only ever sees Store installs that opted into Windows
diagnostics. Wavee ships as NativeAOT MSIX with `StackTraceSupport=false`, so even the local crash report carries
RVA-only frames that nobody symbolicates. Requested: (A) an honest audit of what exists, (B) an OPT-IN pipeline that
uploads anonymized crash bundles to a near-free backend we own, with an "always works" recovery experience where the
user can SEE the report, upload it manually and factory-reset in-app, independent of the engine/GPU/login/profile.

Decisions taken with the user (2026-09-24):
- Backend: **our own Cloudflare Worker + R2 + D1**, dashboard in **Fluent UI v9** on Cloudflare Pages. No Sentry, no SDK.
- **Random install id** (GUID, stored in the profile, wiped by factory reset) is sent with each report.
- Consent default **Off**; asked on the setup wizard Terms page **and** by the first crash prompt. Manual Send always works.
- v1 keeps **hang detection with minidumps** and the **Reports list with per-report send state**.
- v1 cuts **safe start (`--safe`)** and **Shift-held-at-launch**. Recovery is entered by `--recovery`, a boot-loop, or `Platform.Boot` throwing.

## A. What exists today (verified)

| Piece | Where | State |
|---|---|---|
| Process crash net | `src/apps/Wavee/Shell/Shell.Host.cs:633` `InstallCrashNet` | logs Critical + flush; unobserved task exceptions logged and observed |
| Managed crash writer | `src/apps/Wavee/Platform/Platform.Host.cs:147` `HostInstallCrashWriters` → `Diagnostics.CrashReport.Write` (`Screens/Diagnostics.Host.cs:73-203`) | one text file `logs\crash-report-<stamp>.txt`: version/commit/quad/arch/os/module base, `ex.ToString()`, RVA list, 600-line log tail; keep 10 |
| File naming / RVA parse (pure) | `Screens/Diagnostics.cs:355` `CrashFiles`; tests `Wavee.Tests/DiagnosticsCoreTests.cs:230-265` | reused shape, to be moved |
| Run marker (pure) | `Platform/Platform.Settings.cs:603` `RunMarker` running/clean/crashed; tests `PlatformWave6Tests.cs:379` | detects unclean exits incl. OS kills |
| Crash prompt policy (pure) | `Platform.Settings.cs:640` `CrashPromptPolicy.Decide`; tests `PlatformWave6Tests.cs:419` | ManagedReport > WerDump > UncleanExit; `versionChanged` suppresses evidence-free prompts |
| WER dump probe | `Diagnostics.Host.cs:231` `NewCrashDump` | DEAD on user machines: LocalDumps needs an HKLM key written as admin; nothing registers it |
| Next-launch crash dialog | `Screens/Feedback.UI.cs:120-157` (`ChromeCore`), `:171` `OpenDialog`, `:245` `DialogCard` | ContentDialog → "Report on GitHub" (prefilled issue), Copy, Save As, preview, "Don't ask again"; deferred while the wizard is pending |
| Crash probe | `Feedback.UI.cs:146` `--crash-probe` (`failfast` or throw) | managed + FailFast only |
| Redactor (pure, AOT regexes) | `Screens/Feedback.cs:584-706` `RedactionRules` + `ReportRedactor.Redact` | idempotent; paths, user/machine/spotify-id/display/device literals, emails, bearer/kv secrets, IPs, MACs, device ids |
| Off-thread compose | `Feedback.UI.cs:486` `ReportComposer` | reads the crash file, redacts every line |
| Crash reports card | `Feedback.UI.cs:595` `CrashReportsCardCore`, mounted `Settings.UI.About.cs:37` | 10 rows, Report/Open |
| Factory reset | `Platform.Host.cs:181` `RequestFactoryResetAndRelaunch` + `FactoryResetPlan` (pure, `Platform.Settings.cs:669`); apply at boot `Platform.cs:1087` | marker in %TEMP%; only reachable from Settings ▸ Storage inside the engine |
| Restart broker | `Platform.Host.cs:228` `RestartAfterExit` → `Wavee.exe --relaunch-after <pid>`; arm `Diagnostics.Probe.Arms.cs:59` | proves the exe already multiplexes roles |
| HTTP pattern | `Platform/Update.Host.cs:504-511` | `new HttpClient(Wire.Handler("update", …))`, 15 s timeout, `UserAgent` (`Platform.Settings.cs:563`) |
| Build stamp | `Wavee.csproj:44-53` AssemblyMetadata → `WaveeVersionInfo.Parse` (`Platform.Settings.cs:572`) | where the ingest URL rides in (like `UpdateBaseUrl`) |
| Symbols per release | `ops/build/pack-wavee-msix.ps1:182-188`; `ops/release/wavee-release.ps1:258-262, 912-962, 1120` | `Wavee-<quad>-win-<arch>-symbols.zip` (Wavee.pdb + map.xml + SYMBOLS.txt) as a release asset; manual `cdb` per `docs/guide/releasing-wavee.md §5b` |
| Store health | `docs/guide/microsoft-store-onboarding.md:87` | needs `.appxsym` we do not produce; sideloaded installs invisible |
| Engine GPU fallback | `C:\wavee\fluent-gpu\src\FluentGpu.Windows\D3D12\D3D12Device.cs:1088` | WARP only as terminal fallback; no force-WARP input; device-lost has no app-visible seam (`Rhi.cs:158`) |
| Settings keys | `Platform/Platform.cs:277-282` | `app.runMarker`, `crash.pendingReport`, `crash.promptOptOut`, `crash.uncleanExitOffered`, `diagnostics.crash.lastDump*` |
| Privacy page | `PRIVACY.md:6-7, 48-49` | says "no crash-reporting service" — must change |
| Dead loc namespace | `assets/loc/en-US.json:2677-2683` `crash` | unreferenced; replaced |

Gaps: (1) only managed exceptions yield a report — a native AV, stack overflow, FailFast or hang yields nothing;
(2) no upload path at all; (3) RVA-only frames, manual `cdb`; (4) recovery needs the engine + a working profile; a
boot crash loop just loops; (5) no consent model; (6) no hang detection; (7) the log's `ex.Message` sites
(`Spotify.Api.cs:339`, `Modules.Host.cs:1660, 2006`), `Lyrics.Host.cs:1540` (title/artist at Debug) and non-profile
paths (`D:\Music\…` from the file-drop target, `App.cs:161`) pass the current regex redactor untouched.

## B. Architecture

```
┌──────────────────────────── Wavee.exe (app) ──────────────────────────────────┐
│ Shell.Run (after AcquireInstance, after InstallCrashNet):                       │
│   Crash.Host.Install(logFolder)  → spawns Wavee.exe --crash-handler <pid> …    │
│   (never for --headless / relaunch broker / second-instance hand-off)          │
│   SetUnhandledExceptionFilter (+ VEH candidate, decided by the WP-0 spike)     │
│ UI thread: posted heartbeat every 2 s; first-frame witness; modal/exiting bits │
│ managed crash ─► AppDomain.UnhandledException ─┐                                │
│ native AV     ─► UnhandledExceptionFilter ─────┼─► Crash.Host.RequestDump(kind) │
│ FailFast / stack overflow: no hook — the child reads the exit code             │
└───────────────────────────────┬────────────────────────────────────────────────┘
                                │ stdin pipe (beats, requests) + parent handle
┌────────────────────── Wavee.exe --crash-handler (child) ──────────────────────┐
│ loop: read pipe / 1 s tick / parent handle                                     │
│  request → MiniDumpWriteDump(parent, tid, ExceptionPointers) → summary → dump  │
│  tick    → hang watchdog (HangRules) → all-thread dump once, parent lives on   │
│  parent gone → exit code ≠ 0 and no bundle yet ⇒ exitcode/uncleanexit summary │
│  parent gone (any code) ⇒ child exits within 1 s                              │
└───────────────────────────────┬────────────────────────────────────────────────┘
                                ▼
   logs\crash\<stamp>-<kind>\{summary.json, report.txt, log-tail.txt, minidump.dmp?}   (pruned)
                                │
   next launch: RecoveryPolicy → normal | in-app prompt (engine) | RECOVERY MODE (Win32 TaskDialog)
                                │
   ConsentPolicy(off|ask|auto) → Crash.Scrubber → Crash.Bundle.Pack (multipart) → outbox → Crash.Uploader
                                │
   POST https://crash.wavee.app/v1/report  (Cloudflare Worker) → R2 objects + D1 rows, symbolicated at ingest
                                │
   Dashboard (Fluent UI v9, Cloudflare Pages, behind Cloudflare Access)
```

### B.0 WP-0 spike (precondition, before the handler is built)
`--crash-probe native` = a deliberate foreign-code fault (`[LibraryImport("kernel32")] RtlFillMemory(0, 16, 0)`;
a managed `*(int*)0` becomes an NRE under NativeAOT and proves nothing). Register both candidates and log which fires:
`SetUnhandledExceptionFilter` and `AddVectoredExceptionHandler(First=0)` filtered to fault codes (AV, illegal
instruction, in-page error) whose IP lies outside `Wavee.exe`'s image range. NativeAOT does not participate in SEH
(dotnet/runtime #69336), so this may answer "neither". Outcome decides the B.1 table's native row: with a hook we get
a dump with the exception stream; without one the native row collapses to the child's exit-code path
(`0xC0000005`, no dump). Everything else in the plan stands either way.

### B.1 Capture — out-of-process handler = the same signed `Wavee.exe --crash-handler`
Rejected: WER LocalDumps (HKLM + admin, `learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps`);
in-process `MiniDumpWriteDump` (DbgHelp not thread-safe, runs on a corrupted process, cannot dump a hang).
Child = Crashpad's design without the named kernel objects: **stdin pipe** (`RedirectStandardInput`) carries one byte
per beat and one line per dump request (`kind tid exceptionPointersHex`); parent handle by pid. Not assigned to the
modules' kill-on-close job (`fluent-gpu/.../ChildProcessJob.cs`, used at `Modules.Host.cs:72`).

| Crash class | In-process hook | Handler action |
|---|---|---|
| Unhandled managed exception (any thread) | `AppDomain.UnhandledException` (`Platform.Host.cs:149`) | `RequestDump(Managed)` → dump of the live process; parent writes `summary.json` + `report.txt` (today's `Describe` text) |
| Native AV in foreign code | filter/VEH per WP-0 | `RequestDump(Native, EXCEPTION_POINTERS*)` |
| FailFast, runtime fatal, stack overflow | none | parent exit code recorded: kind `ExitCode`, no dump |
| Hang | none | watchdog all-thread dump; parent keeps running |
| Task Manager / OS forced close | none | exit `1` / `0x40010004`: kind `UncleanExit`, never prompts, listed as "Closed" |
| Exit code 0 | — | child writes nothing (fixes the critic's false-bundle finding) |

Dispatch: `if (Crash.Handler.TryRun(args, out int code)) return code;` is the first line of `App.Main` after
`Glyphs.Register` (`App.cs:81`), before `Platform.Boot` — the child never opens settings, the log or the store; it gets
the log folder, parent pid and the dated log path as arguments and logs to `logs\crash\handler.log`.

### B.2 Core code shapes (engine-free, `Platform/Crash.cs`)
```csharp
namespace Wavee;
public static partial class Crash
{
    public enum Kind : byte { Managed, Native, Hang, ExitCode, UncleanExit }

    /// summary.json — machine-readable half. Source-generated JSON (AOT).
    public sealed record Summary(
        string ReportId, string InstallId, Kind Kind, string StampUtc,
        string Version, string Quad, string Commit, string Channel, string Arch, string OsBuild,
        string Gpu, string GpuTier, bool SoftwareAdapter, bool Packaged, string Locale,
        string SessionId, long UptimeMs, bool BeforeFirstFrame, string LastRoute,
        string ExceptionType, string ExceptionMessage, long[] Rvas, long ModuleBase, long ModuleSize,
        string DebugId,               // RSDS GUID-age from the PE debug directory (PeDebugId.TryRead)
        int ExitCode, bool HasDump, long DumpBytes);
    [JsonSerializable(typeof(Summary))] internal sealed partial class SummaryJson : JsonSerializerContext { }

    public static class Files            // pure path arithmetic; replaces CrashFiles
    {
        public const string Folder = "crash", Outbox = "outbox", SummaryName = "summary.json",
            ReportName = "report.txt", TailName = "log-tail.txt", DumpName = "minidump.dmp";
        public const int KeepBundles = 10; public const long MaxFolderBytes = 200L << 20;
        public static string BundleName(DateTimeOffset local, Kind kind);      // yyyyMMdd-HHmmss-fff-<kind>
        public static bool TryParse(string folderName, out DateTime stamp, out Kind kind);
        public static IReadOnlyList<string> Prune(IReadOnlyList<(string Path, long Bytes)> newestFirst, int keep, long maxBytes);
    }

    public static class PeDebugId { public static bool TryRead(Stream pe, out string debugId, out string pdbName); }

    /// Hang verdict; the child feeds it its own unbiased clock + the parent's last beat.
    public static class HangRules
    {
        public const int NoBeatMs = 20_000, HungWindowMs = 10_000, RecoveredAfterMs = 60_000;
        public static bool IsHung(long nowUnbiasedTicks, long lastBeatUnbiasedTicks, bool hasWindow, bool hungWindowMs10s,
            bool debuggerAttached, bool modalPump, bool exiting, int suspendEpochAtBeat, int suspendEpochNow)
            => hasWindow && !debuggerAttached && !modalPump && !exiting && suspendEpochAtBeat == suspendEpochNow
               && nowUnbiasedTicks - lastBeatUnbiasedTicks >= NoBeatMs * 10_000L && hungWindowMs10s;
    }

    public enum Reporting : byte { Off = 0, Ask = 1, Auto = 2 }

    public static class ConsentPolicy    // one bundle at launch
    {
        public enum Action : byte { Nothing, Prompt, UploadSilently, Toast }
        public static Action Decide(Reporting mode, Kind kind, bool online)
            => kind == Kind.UncleanExit ? Action.Nothing
             : mode == Reporting.Auto ? (online ? Action.UploadSilently : Action.Toast)
             : Action.Prompt;                          // Off still shows the local prompt with a manual Send
        public static bool DumpAllowed(Reporting mode, Kind kind, bool includeDump, bool manualSend)
            => manualSend || (mode != Reporting.Off && includeDump && kind != Kind.Hang);   // hang dumps: ask always
    }

    public static class RecoveryPolicy
    {
        public const int BootFailuresForRecovery = 2;
        public static bool Enter(bool recoverySwitch, int consecutiveBootFailures, bool bootThrew)
            => recoverySwitch || bootThrew || consecutiveBootFailures >= BootFailuresForRecovery;
        /// versionChanged: an update deployment killed the previous process — never counts toward the loop.
        public static int NextBootFailures(RunOutcome previous, bool previousReachedFirstFrame, bool versionChanged, int current)
            => previous == RunOutcome.Unclean && !previousReachedFirstFrame && !versionChanged ? current + 1 : 0;
    }
}
```
`RunMarker` gains a fourth value `"frame"` written by `Shell.OnFirstFrameRendered` (`Shell.Host.cs:120`): the
persisted first-frame witness `NextBootFailures` needs (`running` → died before first frame; `frame` → after).

Keys (`Platform.cs:277-282`): add `crash.reporting` (int, 0), `crash.includeDump` (bool, false),
`crash.consentAsked` (bool, false), `crash.installId` (string, "" → GUID on first use), `crash.bootFailures` (int, 0),
`crash.pendingBundle` (string). Delete `crash.pendingReport`, `crash.promptOptOut`, `crash.uncleanExitOffered`,
`diagnostics.crash.lastDumpPath`, `diagnostics.crash.lastDumpTicksUtc`.

### B.3 Local store
```
%LOCALAPPDATA%\Wavee\logs\crash\              (packaged: the package LocalCache equivalent; logResolved= says which)
├── 20260924-143012-118-managed\   summary.json · report.txt · log-tail.txt · minidump.dmp
├── 20260923-091500-004-hang\      summary.json · report.txt · log-tail.txt · minidump.dmp
├── 20260921-220301-777-exitcode\  summary.json · report.txt · log-tail.txt
├── outbox\                        <reportId>.bundle  (packed multipart, scrubbed, waiting) · <reportId>.retryN
└── handler.log
```
Write order: `summary.json` first (disk-full safe), then `report.txt`, `log-tail.txt` (last 300 lines of the dated
log, read with `FileShare.ReadWrite|Delete` like `WaveeLogSessions.ReadSharedLines`, scrubbed at write time), then
the dump. Prune on every write (keep 10, ≤ 200 MB). Dump flags default `MiniDumpNormal | MiniDumpWithThreadInfo |
MiniDumpWithUnloadedModules` (stacks + modules, no heap: the heap holds the DPAPI-unprotected credential and live
tokens). `MiniDumpWithIndirectlyReferencedMemory` only when the user ticks the explicit "include referenced memory"
box on a manual Send. Sizes measured in the packaged probe run before `MaxDumpBytes` is fixed (start at 25 MB).

### B.4 Scrubber — `Crash.Scrubber` (engine-free, `Platform/Crash.Scrub.cs`), structural not regex-only
Builds on `ReportRedactor.Redact` (same `RedactionRules`, same idempotence) and adds:
1. **Log tail is parsed, not grepped.** Each `key=value` line keeps only allowlisted fields (`ts`, `level`, `cat`,
   `event`, `route`, `arg`, `navId`, `state`, `code`, `status`, `reason`, every numeric field, `frameMs…`); every
   other value becomes `<dropped>`; lines below Info are dropped regardless of `Keys.LogFileMinLevel`; any line whose
   category is `auth`/`wire` or whose text contains `Authorization`, `Cookie`, `client-token`, `set-cookie` is
   replaced by `[line dropped: credential]`; the `startup` line's `account=`, `credential=`, `sid=` fields are dropped.
2. **Any absolute path** not under `\WindowsApps\`, `\Program Files`, `\Windows\` → `<path>` (report.txt and tail).
3. `Environment.GetCommandLineArgs()` → whitelist of flags only (`--fake`, `--profile` without its value, `--relaunched-after-update`).
4. `module=` line → `<install>\Wavee.exe`.
5. Never collected: environment variables, computer name, SID, IP.
6. Kept and named in the consent copy: track/album/playlist ids in `nav.route arg=`.
7. Minidump: never scrubbed; gated by its own sub-consent.
Tests: per-rule before/after, idempotence over a real 0.3 `report.txt` fixture, the allowlist, "no `C:\Users\<name>`,
no `account=`, no `Authorization`" over a captured tail fixture.

### B.5 Upload — `Crash.Bundle.Pack` (pure) + `Crash.Uploader` (host)
- Endpoint from build-time metadata exactly like `UpdateBaseUrl`: `<AssemblyMetadata Include="CrashIngestUrl" Value="$(WaveeCrashIngestUrl)"/>` + `CrashIngestKey`; empty on `dotnet run` and the E2E package ⇒ never uploads. No env var.
- Request: `POST {ingest}/v1/report`, `multipart/form-data` parts `summary` (json), `report` (text), `tail` (text), `dump` (optional, `application/octet-stream`), headers `X-Wavee-Ingest: <public key>`, `User-Agent: Wavee/<semver>`. Cap 20 MB; over cap ⇒ drop the dump first and say so in `summary.DumpDropped`.
- Identifiers: `InstallId` (random GUID, profile-stored) + per-report `ReportId` + per-process `SessionId`. Nothing else.
- Outbox: packed once to `outbox\<reportId>.bundle`; `Uploader.Drain()` off-thread after `Update.Host.Start` and on `Platform.Network` connectivity edges (`Platform.Host.cs:280-289`); 2xx ⇒ delete; 4xx ⇒ delete + log size/status; 429/5xx ⇒ keep, backoff in the file name (`.retry3`), 3 launches old ⇒ delete. Nothing leaves before a send (no DNS, no preflight).
- HTTP shape: `Update.Host.cs:504-511` (`Wire.Handler("crash", …)`, 30 s timeout).

### B.6 Symbols — no vendor; the release script produces a symbol map the Worker resolves against
- `Wavee.ReleaseTool symbol-map -Pdb Wavee.pdb -Exe Wavee.exe -Out Wavee-<quad>-win-<arch>.symmap` (new verb in
  `src/apps/Wavee.ReleaseTool`, DbgHelp `SymInitialize/SymLoadModuleEx/SymEnumSymbols` via `LibraryImport`): a
  **binary** map (magic, count, `N × {rva u32, size u32, nameOffset u32}`, UTF-8 string table, sorted by rva) plus
  the debug id, so the Worker does `new Uint32Array(buffer)` + binary search with no JSON parse (Workers free plan
  has a 10 ms CPU budget per request). Method names are ILC-mangled (`Wavee_Wavee_…`), fine for grouping. Line
  numbers stay a local step (`ops/tools/crash-symbolicate.ps1 -ReportId …` pulls `summary.json` and runs the
  existing `cdb` runbook against the archived symbols zip).
- `wavee-release.ps1` gains phase **`symbols`** (after pack, before sign): build the map for each arch, `wrangler r2
  object put wavee-crash/symbols/<quad>/win-<arch>.symmap`, and a gate: a `stable` release with an empty
  `WaveeCrashIngestUrl` warns. `-DryRun`/`-NoUpload` skip with a warning; `-Resume` re-runs idempotently. The symbols
  zip on the GitHub release stays (manual path + Store evidence).
- `summary.json` carries `DebugId`, `ModuleBase`, `Rvas`, so any bundle is resolvable by hand with `releasing-wavee.md §5b`.

### B.7 Hang watchdog
Parent: `System.Threading.Timer` every 2 s posts a beat through the UI marshaller (`Shell.Host.cs:361`) — a *posted*
beat, not `FrameCompleted` (idle renders no frame; a post is delivered whenever the pump runs); `Shell.OnFirstFrameRendered`
sets the first-frame witness; `FilePicker` callers wrap in `Crash.Host.ModalScope()`; when the loop returns
(`App.cs:172`, `UiThreadId = null`) the parent sends `exiting` so the up-to-10-min `ApplyOnExit` tail never reads as a hang.
Child: hang = no beat 20 s **and** `IsHungAppWindow(hwnd)` for 10 s **and** the window still exists, no debugger
(`CheckRemoteDebuggerPresent`), no modal, no `exiting`, suspend epoch unchanged (PowerSession edges), unbiased clock.
One dump per process; `hang.recovered afterMs=` if the beat returns within 60 s; in-app toast on the next frame
("Wavee stopped responding for 31 s — a report was saved"). Hang dumps are **ask-always** (never auto-sent).

### B.8 Recovery mode — engine-free
Entered when `RecoveryPolicy.Enter` says so: `--recovery`, `Platform.Boot()` throwing (Main wraps it), or
`crash.bootFailures ≥ 2` (bumped in `BeginGuiRun` from the run marker's `running`-vs-`frame` value, `versionChanged`
excluded). Rendered with **`TaskDialogIndirect`** (comctl32 v6): new `src/apps/Wavee/app.manifest` +
`<ApplicationManifest>` in `Wavee.csproj` keeping `requestedExecutionLevel asInvoker`, adding the
`Microsoft.Windows.Common-Controls` 6.0 dependency, and **no** `dpiAware` (the engine calls
`SetProcessDpiAwarenessContext` at `Win32Platform.cs:35`). Fallback `MessageBoxW` with numbered options. Strings via
`FluentGpu.Localization.LoadFolder` + `Loc` (no D3D dependency, `Localization.cs:1-3`) with English fallback literals
if the loc folder itself is unreadable. Actions: View report (`notepad.exe`), Open folder (`explorer.exe`), Copy
(user32 clipboard), Send (scrubbed bundle, works with consent Off; shows what it sends first), Reset (second
TaskDialog confirm with the existing `factoryResetConfirmBody`, then `FactoryResetMarkerPath` written directly and
guarded for disk-full when `Boot` threw), Start normally, Quit.

```
┌─ Wavee ──────────────────────────────────────────────────────────────────────┐
│  ⚠  Wavee could not start properly                                           │
│  It closed unexpectedly twice in a row before showing a window.              │
│  Last report: 24 Sep 2026 14:30 · NullReferenceException · 0.3.0 (0.3.0.41)  │
│  Report id 3f9c…a1e2  ·  saved in Wavee\logs\crash\20260924-143012-118-managed│
│  ▸ Start normally                                                            │
│  ▸ Send this report to the developer                                         │
│      Anonymized · what is sent opens in Notepad first · no account data      │
│  ▸ Reset Wavee to a fresh install…                                           │
│      Signs you out and deletes all local Wavee data on this PC              │
│  [ View report ]  [ Open folder ]  [ Copy ]                        [ Quit ]  │
│  ☐ Always send reports automatically              Privacy policy (GitHub)    │
└──────────────────────────────────────────────────────────────────────────────┘
```

## C. The service — Cloudflare Worker + R2 + D1 + Fluent UI v9 dashboard (`ops/crash/`)
Lives in this repo (`ops/crash/worker`, `ops/crash/dashboard`), deployed by hand with `wrangler` (no CI, like releases).
Free tiers: Workers 100k req/day, R2 10 GB + no egress fees, D1 5 GB / 5M reads/day, Pages free, Cloudflare Access
free ≤ 50 users. Verify at setup: R2 binding on the Workers free plan (a 2026 source claims Paid is required —
unverified; Workers Paid is $5/mo if so). Worker must not log or store `cf-connecting-ip`; rate-limit per IP with the
Rate Limiting binding; PRIVACY.md says "the service sees the connection like any web server and does not store the address".

Routes (`ops/crash/worker/src/index.ts`, Hono or plain fetch):
| Route | Auth | Does |
|---|---|---|
| `POST /v1/report` | `X-Wavee-Ingest` public key + rate limit | parse multipart; validate `summary.json` (schema + size caps); write R2 `reports/<quad>/<reportId>/…`; load `symbols/<quad>/win-<arch>.symmap` (isolate-cached), resolve `Rvas` → names; fingerprint = sha1(kind, exceptionType, top 3 names) → upsert `issues`; insert `reports`; 201 `{id}`; 413 over cap; 409 duplicate id |
| `GET /v1/issues?since=&version=` | Cloudflare Access (dashboard) | issues with counts, first/last seen, versions, distinct installs |
| `GET /v1/issues/:fp` · `GET /v1/reports/:id` · `GET /v1/reports/:id/:part` | Access | detail, scrubbed text parts, dump download |
| `GET /v1/versions` | Access | reports per quad/arch/kind (counts only — there is no MAU denominator; Partner Center keeps the rate for Store installs) |
| `PUT /v1/symbols/:quad/:arch` | wrangler (admin) | not exposed; the release script uses `wrangler r2 object put` |

D1 schema (`ops/crash/worker/schema.sql`):
```sql
CREATE TABLE reports(id TEXT PRIMARY KEY, install_id TEXT, session_id TEXT, kind TEXT, quad TEXT, semver TEXT, commit_sha TEXT,
  channel TEXT, arch TEXT, os_build TEXT, gpu TEXT, gpu_tier TEXT, software_adapter INTEGER, packaged INTEGER, locale TEXT,
  uptime_ms INTEGER, before_first_frame INTEGER, last_route TEXT, exception_type TEXT, exception_message TEXT,
  exit_code INTEGER, has_dump INTEGER, dump_bytes INTEGER, frames_json TEXT, fingerprint TEXT, received_at TEXT);
CREATE TABLE issues(fingerprint TEXT PRIMARY KEY, title TEXT, kind TEXT, first_seen TEXT, last_seen TEXT, count INTEGER,
  installs INTEGER, versions_json TEXT, status TEXT DEFAULT 'open', github_issue INTEGER);
CREATE INDEX reports_fp ON reports(fingerprint, received_at); CREATE INDEX reports_quad ON reports(quad, received_at);
```

Dashboard (`ops/crash/dashboard`, Vite + React + `@fluentui/react-components` v9, `wrangler pages deploy`, behind Access):
```
┌ Wavee crashes ─────────────────────────────────────────── last 30 days ▾  0.3.x ▾ ┐
│ ▤ Overview  ▤ Issues  ▤ Reports  ▤ Versions  ▤ Symbols                             │
│ ┌ 128 reports ┐ ┌ 23 issues ┐ ┌ 41 installs ┐ ┌ 9 hangs ┐ ┌ 3 native ┐             │
│ Issues (DataGrid: title · kind · count · installs · first/last seen · versions)     │
│  ▸ NullReferenceException · Wavee_Entities_Detail_UI_Hero__Render   67  19  0.3.0.41│
│  ▸ Hang 31 s · route artist                                          9   6  0.3.0.40│
│  ▸ ExitCode 0xC0000409 (fail-fast)                                   4   4  …       │
└─────────────────────────────────────────────────────────────────────────────────────┘
Issue detail: symbolicated stack (names, RVAs, module), occurrence sparkline by day, version/arch/GPU breakdown,
"Open GitHub issue" (prefilled title/body, sets issues.github_issue), report list → Report detail: summary card,
report.txt / log-tail viewers (monospace, search), minidump download + the WinDbg one-liner from releasing-wavee.md §5b.
```

## D. In-app UX (engine side)
Prototypes (private artifacts, approved copy source): crash dialogs https://claude.ai/artifact/WLUQi61Ema21byr3m2c5Gg
(recovery dialog, in-app prompt, toast, wizard card, settings group, reports list) and the Fluent v9 dashboard
https://claude.ai/artifact/KVTd2JjQn9B8EdLpbUBsMV (Overview, Issue detail, Report detail). **The prototype's simpler
wording wins over the wireframes below wherever they differ**: "Wavee couldn't start", "Start Wavee / Send the crash
report / Reset Wavee…", "What's in the report" as four plain bullets, "Never your Spotify account, password, name or
files", the consent radio "No / Ask me first / Always send", the settings rows "Crash reports · Include a memory
snapshot · Waiting to send · Saved reports · Privacy policy", and the report states "Sent / Not sent / Waiting to send".
The en-US keys are written from the prototype during WP-E.
Setup wizard Terms page (`Setup.UI.cs:910-929`), one card, default Off, no new step:
```
┌ 🛡 Help fix crashes ─────────────────────────────────────────────────────────────┐
│ Send an anonymized report when Wavee crashes         [ Off ▾ ] Ask each time · Auto│
│ Only the error, version, Windows build, GPU and the last log lines — personal data │
│ removed, never your Spotify account. A random install id tells one PC from another.│
│ You can change this any time in Settings.                                          │
└────────────────────────────────────────────────────────────────────────────────────┘
```
In-app prompt (replaces `ChromeCore`'s crash arm and the crash form of `DialogCard`; ContentDialog 548 DIP):
```
┌─ Wavee closed unexpectedly ─────────────────────────────────────────────────┐
│ ⓘ Last run ended 14:30 · InvalidOperationException: --crash-probe           │
│ Send an anonymized report? It helps fix crashes like this one.              │
│ What is sent ───────────────────────────────────────────  [Copy] [Save as…] │
│ ┌ Wavee 0.3.0 (0.3.0.41) · 7e209e37 · arm64 · Windows 11 (26100)          ┐ │
│ │ report 3f9c…a1e2 · install 8a1d… · NVIDIA … (Strong) · packaged · en-US │ │
│ │ System.InvalidOperationException: --crash-probe                         │ │
│ │    at Wavee!<BaseAddress>+0x7b1fc6 …                                    │ │
│ │ --- log-tail.txt (300 lines, personal data removed) ---                 │ │
│ └─────────────────────────────────────────────────────────────────────────┘ │
│ Personal paths, account details and secrets are removed. Track and playlist │
│ ids from the last minutes are kept.                                         │
│ ☐ Include a memory snapshot (4.2 MB) — stacks and modules, no heap          │
│ ☐ Always send automatically         Report on GitHub instead…  Privacy      │
│                                                        [ Send ]  [ Not now ] │
└─────────────────────────────────────────────────────────────────────────────┘
```
Auto mode: no dialog, a 6 s toast "Crash report sent · id 3f9c…a1e2". Wizard-deferral behaviour kept (`Feedback.UI.cs:128`).

Settings › General › **Privacy & diagnostics** (new Catalog section, `Settings.cs:66-90`, unique glyphs per row):
```
│ ⚑ Crash reports                                     [ Off ▾ ] Ask each time · Automatic  │
│   What is sent: version, Windows build, GPU, the error and its frames, the last 300 log  │
│   lines with personal data removed. Never your Spotify account, password, name or files. │
│ ▣ Include a memory snapshot                                                   ( off )    │
│ ⇪ Reports waiting to send                            2 queued · [ Send now ] [ Discard ] │
│ ⎘ Saved reports                                  8 on this PC · 31 MB · [ Open ] [ Clear ]│
│ ↗ Privacy policy                                                                          │
```
Settings › Logs › **Reports** (replaces the About "Crash reports" expander):
```
│ ● 24 Sep 14:30  Crash   NullReferenceException · Detail.UI.Hero        sent 14:35  [ View ]          │
│ ● 23 Sep 09:15  Hang    31 s on route artist · dump 6.1 MB             not sent    [ View ] [ Send ] │
│ ● 21 Sep 22:03  Exit    code 0xC0000409 (fail-fast) · no dump          queued      [ View ] [ Send ] │
│ ○ 20 Sep 08:00  Closed  Windows or Task Manager ended Wavee            —           [ View ]          │
```
Rows keyed `"crash:" + reportId`, mount-time snapshot + `Crash.Host.ReportsVersion` signal bump on every write.
All strings under a new `crash` namespace in `en-US.json` (replacing the dead one); the design's key list
(`crash.mode*`, `crash.prompt*`, `crash.recovery*`, `crash.reports*`, `crash.wizard*`) is carried into the repo plan.
`PRIVACY.md` gains "Crash reports (opt-in)": fields sent, the install id, "off by default", the Cloudflare-hosted
service, retention (90 days, R2 lifecycle rule), how to see/delete/send by hand.

Component tree (WP-E):
```
Shell overlay layer: Setup.WizardChrome → Crash.Chrome → ReleaseNotes.AfterUpdateChrome
└── Embed.Comp(Crash.Chrome) (0×0)
      effect A (Crash.Host.ThisLaunch + ConsentPolicy.Decide) → Prompt | UploadSilently+toast | Toast
      effect B (Crash.Host.HangReported signal) → Notify.Say(hangToast, "View")
      effect C (Crash.Host.ProbeMode) → Crash.Host.ArmProbe(mode, UsePost())
CrashPromptBody : Component (Width 500; signals seeded once from the bundle)
├── InfoBar · TextEl lead · row(What is sent · Copy · Save as…) · BoxEl(220h) → ScrollEl → MicroMeta(preview, composed off-thread per ReportComposer)
├── TextEl note · CheckBox includeDump (only when the bundle has a dump) · CheckBox always
└── row: Hyperlink "Report on GitHub instead…" (→ Feedback.OpenDialog(ReportKind.Crash, prefill report.txt)) · Hyperlink Privacy
Crash.PrivacyRows : Component (mode ComboBox → Keys.CrashReporting + Crash.Host.OnSettingsChanged; includeDump Toggle; queued row; saved row; privacy link)
Crash.ReportsList : Component (rows from Crash.Host.Bundles(); View → notepad; Send → Uploader.SendNow)
```

## E. Deletions (no legacy paths)
`Diagnostics.CrashReport.Write/List/Prune` + loose `crash-report-*.txt` (the `Describe` text moves into the bundle
writer), `CrashFiles` naming (tests in `DiagnosticsCoreTests.cs:230-265` rewritten against `Crash.Files`),
`NewCrashDump` + the two `LastSeenCrashDump*` keys, `CrashPromptPolicy` + `CrashPromptOptOut` + `UncleanExitOffered`
(tests `PlatformWave6Tests.cs:419-464` rewritten as `ConsentPolicyTests`), `ChromeCore`'s crash arm + probe timer,
`CrashReportsCardCore` + its About mount, the dead `crash` loc namespace, `PRIVACY.md` lines 6-7 / 48-49.

## F. Work packages (disjoint files; sonnet subagents; orchestrator builds/tests/launches)
Issues to open first (titles → numbers into CHANGELOG bullets and commit bodies):
1 out-of-process `--crash-handler` with minidumps, fail-fast exit codes and hangs · 2 opt-in anonymized upload to the
Wavee crash service, offline outbox · 3 structural scrubber for uploaded bundles · 4 engine-free recovery mode
(TaskDialog) after a boot loop, `--recovery`, or a failed boot · 5 Settings › Privacy & diagnostics + Logs › Reports;
retire the About card · 6 setup wizard consent card · 7 release `symbols` phase + `WaveeCrashIngestUrl` stamp +
ReleaseTool `symbol-map` · 8 hang watchdog · 9 `--crash-probe native|hang|boot` · 10 crash service Worker + D1 + R2 ·
11 Fluent v9 dashboard · 12 PRIVACY.md + `docs/guide/crash-diagnostics.md` · 13 delete the WER probe, `CrashPromptPolicy`, loose reports and their keys.

| WP | Issues | Files | Pure classes + tests | Acceptance |
|---|---|---|---|---|
| **0 · Spike** | #9 | `Screens/Diagnostics.Probe.Arms.cs:753-777` (`ProbeOptions.Parse` adds `throw|failfast|native|hang|boot`), a throwaway filter/VEH registration behind `--crash-probe native` | `ProbeOptions.Parse` matrix (`ProbeArmsTests.cs:45`) | log line `crash.native.hook fired=filter|veh|none code=0xC0000005`; result recorded in the repo plan before WP-B starts |
| **A · Core** | #1 #8 #13 | new `Platform/Crash.cs`; `Platform/Platform.cs:277-282` keys; `Platform.Settings.cs:603` `RunMarker` (`frame`) | `Summary`+JSON round-trip, `Files` (names/parse/prune keeps 10 & ≤200 MB), `PeDebugId` (fixture PE), `HangRules` matrix (sleep/debugger/modal/exiting/no window), `ConsentPolicy`, `RecoveryPolicy` (versionChanged) → `Wavee.Tests/CrashCoreTests.cs` | tests green; Release build (AOT analyzers) clean |
| **B · Handler + host** | #1 #8 | new `Platform/Crash.Handler.cs` (child arm: pipe reader, `MiniDumpWriteDump`, `IsHungAppWindow`, `CheckRemoteDebuggerPresent`, `QueryUnbiasedInterruptTime`, exit-code path, exits ≤ 1 s after parent death), new `Platform/Crash.Host.cs` (spawn from `Shell.Run` after `InstallCrashNet`, gated `!Headless && !RelaunchBroker`; filter/VEH per WP-0; heartbeat; `RequestDump`; bundle writer; `ModalScope`; `ReportsVersion`), `Platform.Host.cs:147-164` (`HostInstallCrashWriters` → `Crash.Host.OnManagedCrash`), `Shell.Host.cs:120` (first-frame → `RunMarker` `frame` + witness), `App.cs:81` arm line | none beyond A | each probe mode yields the expected bundle kind (§G); packaged run proves the child survives the parent long enough to write and then exits |
| **C · Scrub + upload** | #2 #3 | new `Platform/Crash.Scrub.cs`, `Platform/Crash.Upload.cs` (`Bundle.Pack` pure multipart; `Uploader` host: outbox, drain, network edges); `Platform.Settings.cs:572-590` (`CrashIngestUrl/Key` in `WaveeVersionInfo`); `Wavee.csproj:44-53` metadata | `Scrubber` (structural allowlist, drop rules, path rule, argv whitelist, idempotence over a real fixture), `Bundle.Pack` (byte-exact multipart, cap drops dump first) → `CrashScrubTests.cs`, `CrashBundleTests.cs` | a packed bundle POSTs 201 to the local Worker (`wrangler dev`); outbox retries after a 503 from `ops/release/tests/LocalFeedServer.psm1` (gains `/v1/report`) |
| **D · Recovery** | #4 | new `Screens/Recovery.cs` (reason, actions), `Screens/Recovery.Win32.cs` (TaskDialogIndirect/MessageBoxW, clipboard, notepad), new `src/apps/Wavee/app.manifest` + csproj `<ApplicationManifest>`; `App.cs:77-95` (`--recovery`, Boot try/catch); `Platform.cs:837-865` (`Args.Recovery`) | `Recovery.Actions.For(reason, hasBundle, hasIngest)` → `RecoveryTests.cs` | `--recovery` shows the dialog with no window; two kills before first frame (`--crash-probe boot`) show it on the third launch; View/Copy/Send/Reset exercised; DPI unchanged (per-monitor v2 still reported) |
| **E · In-app UI** | #5 #13 | new `Screens/Crash.UI.cs`; `Feedback.UI.cs:120-154` + `:595-635` (removed), `Settings.UI.About.cs:37`, `Settings.UI.cs` (General mounts the group), `Settings.cs:66-90` (section + rows), `Diagnostics.UI.cs:57-81` (seams → `Crash.Host`), `Diagnostics.Host.cs:205-261` (`BeginGuiRun` reads bundles; probe deleted), `Platform.Settings.cs:629-662` (`CrashPromptPolicy` deleted), `assets/loc/en-US.json` | none new (decisions in A); `SettingsCatalogTests` pass | prompt shows the scrubbed preview before Send; Auto sends silently; Reports list updates on a live hang report |
| **F · Wizard consent** | #6 | `Screens/Setup.UI.cs:910-929`, `Screens/Setup.cs` (`WizardRules.ConsentShown(entry)`: FirstRun + TermsRearm only) | `WizardRules.ConsentShown` → `SetupTests.cs` | fresh `--profile` shows the card; `crash.consentAsked` set on Continue; Reauth entry does not show it |
| **G · Release + ReleaseTool** | #7 | `src/apps/Wavee.ReleaseTool` (`symbol-map` verb, DbgHelp `LibraryImport`), `ops/release/wavee-release.ps1` (phase `symbols`, `-CrashIngestUrl`, `-NoUpload`), `ops/build/pack-wavee-msix.ps1:173-188` (`/p:WaveeCrashIngestUrl=`), `ops/release/Wavee.Release.psm1` (`Invoke-SymbolsUpload`, ingest gate), `ops/release/tests/Wavee.Release.Tests.ps1` (mocked `wrangler`, phase order, gate), `docs/guide/releasing-wavee.md §5b` | symmap writer/reader round-trip test in `Wavee.Tests` (fixture PDB) ; Pester | `-DryRun -SkipTests` runs the phase as a no-op with a warning; a real release lands `symbols/<quad>/win-<arch>.symmap` in R2 |
| **H · Service** | #10 #11 #12 | new `ops/crash/worker/` (`wrangler.toml`, `src/index.ts`, `schema.sql`, `src/symbolicate.ts`, tests with `vitest` + `@cloudflare/vitest-pool-workers`), new `ops/crash/dashboard/` (Vite + React + Fluent v9: Overview, Issues, Issue detail, Report detail, Versions, Symbols), `ops/crash/README.md` (deploy runbook: `wrangler d1 migrations apply`, R2 bucket + 90-day lifecycle, Access policy, Pages deploy), new `docs/guide/crash-diagnostics.md`, `PRIVACY.md`, `CHANGELOG.md` | Worker unit tests: multipart parse, caps (413), symbolicate binary search against a fixture symmap, fingerprint stability, no IP stored | `wrangler dev` accepts a bundle from WP-C; dashboard lists it symbolicated; Access blocks anonymous GET |

Orchestrator-only edits after the WPs land: `App.cs` arm + Boot try/catch wiring, `Diagnostics.Install` seam
rewiring, CHANGELOG bullets ending in `(#n)`, Debug + Release builds, `Wavee.Tests`, Pester, packaged probe run.

## G. Verification
| Class | Command (unpackaged `dotnet run --project src/apps/Wavee -- --fake …`; packaged: E2E package from `ops/release/tests/local-update-e2e.ps1`, launch via `explorer.exe shell:AppsFolder\<AUMID>`, wipe LocalCache after) | Expected `crash` log lines | Bundle |
|---|---|---|---|
| Managed | `--crash-probe throw` | `crash.handler.spawned pid=` → `crash.dump.requested kind=managed` → `crash.dump.written bytes=` (handler.log) → next launch `run.begin previous=Unclean bundle=…-managed prompt=Prompt` | `…-managed\` summary (`Rvas`, `DebugId`), report, tail, dump only if allowed |
| Native AV | `--crash-probe native` | `crash.native.hook fired=…`; with a hook `kind=native code=0xC0000005`, else `kind=exitcode code=0xC0000005` | `…-native\` or `…-exitcode\` |
| Fail-fast | `--crash-probe failfast` | handler: `parent exited code=0xC0000409` | `…-exitcode\`, no dump |
| Hang | `--crash-probe hang` (`Thread.Sleep(45_000)` posted 2 s after first frame) | handler: `hang.suspected noBeatMs= hungMs=` → `hang.dump.written` → `hang.recovered afterMs=`; in-app toast | `…-hang\` all-thread dump; app keeps running |
| Boot loop | `--crash-probe boot` (throw in `Shell.Run` before the harness) ×2, then plain launch | `bootFailures=1`, `=2`, then `recovery.enter reason=bootLoop count=2` + TaskDialog, no window | dialog buttons exercised |
| Recovery by hand | `--recovery` | `recovery.enter reason=switch` | — |
| Task Manager kill | kill from Task Manager | handler: `parent exited code=1` → uncleanexit; next launch `prompt=Nothing` | listed as "Closed" |
| Upload | Ask → Send; Auto → silent; offline (`--fake` + adapter disabled) → outbox → reconnect | `upload.sent id= status=201` / `upload.queued` / `upload.retry attempt=2 status=503` | outbox empties; dashboard shows the report symbolicated (release build) |
| Scrub | `dotnet test --filter CrashScrub` + eyeball a bundle's `log-tail.txt` | — | no `C:\Users\<name>`, `account=`, `Authorization` |
| Service | `cd ops/crash/worker && npx vitest`; `wrangler dev` + a WP-C upload; dashboard `npm run dev` against it | — | issues grouped by fingerprint; Access enforced on the deployed Pages site |

Gates before "done": Debug **and** Release builds clean, `Wavee.Tests` green (+ `CrashCoreTests`, `CrashScrubTests`,
`CrashBundleTests`, `RecoveryTests`, `ProbeArmsTests`), `Invoke-Pester ops/release/tests`, Worker tests, and the packaged
run of the five probes with `logResolved=` confirming bundles landed in LocalCache.

## H. Verify-at-implementation items
1. WP-0 answer (filter vs VEH vs none) — decides the native row.
2. Real dump sizes with the default flag set on a 39 MB NativeAOT exe — fixes `MaxDumpBytes` and the prompt's size text.
3. R2 binding on the Workers free plan; Workers 10 ms CPU with the binary symmap (else Workers Paid $5/mo).
4. `TaskDialogIndirect` + the new manifest does not disturb the engine's DPI call (check the window still reports per-monitor v2).
5. MSIX process lifetime of the child under deployment (`Remove-AppxPackage` waits for package-identity children: the child's ≤ 1 s exit on parent death is what keeps `local-update-e2e.ps1:597-606` and `perf-tour.ps1:237` happy).


## I. Interfaces between work packages (the contract every agent codes against)

All engine-free unless noted. `Signal<T>` is `FluentGpu.Signals` (no D3D dependency; usable from Platform code).

```csharp
// ── WP-A · Platform/Crash.cs ─────────────────────────────────────────────────────────────────────────────────────
namespace Wavee;
public static partial class Crash
{
    public enum Kind : byte { Managed, Native, Hang, ExitCode, UncleanExit }
    public enum Reporting : byte { Off = 0, Ask = 1, Auto = 2 }
    public enum SendState : byte { NotSent, Queued, Sent, Failed }

    public sealed record Summary(/* see B.2 */);
    /// send.json beside summary.json — the per-bundle send state. Written by Uploader; read by Host.Bundles().
    public sealed record SendRecord(SendState State, string? SentAtUtc, string? Error, int Attempts, bool DumpIncluded);
    [JsonSerializable(typeof(Summary))] [JsonSerializable(typeof(SendRecord))]
    internal sealed partial class CrashJson : JsonSerializerContext { }

    /// One on-disk bundle as the UI sees it.
    public sealed record BundleInfo(string Dir, Summary Summary, DateTime StampLocal, SendRecord Send, bool HasDump, long DumpBytes)
    { public string ReportTxt => Path.Combine(Dir, Files.ReportName); public string TailTxt => Path.Combine(Dir, Files.TailName); public string? DumpPath => HasDump ? Path.Combine(Dir, Files.DumpName) : null; }

    public static class Files { /* B.2 + */ public const string SendName = "send.json", HandlerLog = "handler.log";
        public static string Root(string logFolder) => Path.Combine(logFolder, Folder);
        public static string OutboxDir(string logFolder) => Path.Combine(Root(logFolder), Outbox); }
    public static class PeDebugId { public static bool TryRead(Stream pe, out string debugId, out string pdbName); }   // "GUID-age" upper-case, no braces
    public static class HangRules { /* B.2 */ }
    public static class ConsentPolicy { /* B.2 */ }
    public static class RecoveryPolicy { /* B.2 */ }
    public static class InstallId { public static string Ensure(IAppSettings s); }   // Keys.CrashInstallId, GUID "N" on first use
}
// Platform.Keys additions: CrashReporting(int "crash.reporting" 0), CrashIncludeDump(bool "crash.includeDump" false),
// CrashConsentAsked(bool "crash.consentAsked" false), CrashInstallId(string "crash.installId" ""),
// CrashBootFailures(int "crash.bootFailures" 0), CrashPendingBundle(string "crash.pendingBundle" "").
// RunMarker: + const Frame = "frame"; MarkFrame(IAppSettings); Begin(...) treats Frame as Clean and reports
//   `bool previousReachedFirstFrame` via a new overload `Begin(IAppSettings s, out bool previousReachedFirstFrame)`.

// ── WP-B · Platform/Crash.Host.cs (parent side; SHELL role, may touch the engine) ──────────────────────────────
public static partial class Crash
{
    public static class Host
    {
        public static string LogFolder { get; }                       // Platform.LogFolder
        public static void Install(string logFolder, string? datedLogPath);   // spawns the child, installs hooks, starts the heartbeat
        public static void OnManagedCrash(Exception ex);              // called FIRST by HostInstallCrashWriters; writes the managed bundle, requests a dump
        public static void NoteFirstFrame();                          // Shell.OnFirstFrameRendered → RunMarker.MarkFrame + witness to the child
        public static void NoteExiting();                             // when the UI loop returns
        public static IDisposable ModalScope();                       // around FilePicker / nested pumps
        public static BundleInfo? ThisLaunch;                         // latched by BeginGuiRun from Keys.CrashPendingBundle (+ newest unseen bundle); WP-E consumes once
        public static readonly Signal<int> ReportsVersion;            // bumps on every bundle write / send.json change
        public static readonly Signal<int> HangReported;              // bumps when the child reports a hang for THIS process
        public static IReadOnlyList<BundleInfo> Bundles();            // newest first, all kinds
        public static BundleInfo? Read(string dir);
        public static void Delete(string dir); public static void DeleteAll();
        public static string? ProbeMode();                            // Platform.Args crash-probe value or null
        public static void ArmProbe(string mode, Action<Action> post);   // throw|failfast|native|hang (boot is handled in Shell.Run)
        public static void WriteSend(string dir, SendRecord r);       // used by Uploader; bumps ReportsVersion
    }
}
// Child arm: Crash.Handler.TryRun(string[] args, out int exitCode) — "--crash-handler <parentPid> <logFolder> <datedLogPath|->"

// ── WP-C · Platform/Crash.Scrub.cs + Platform/Crash.Upload.cs ───────────────────────────────────────────────────
public static partial class Crash
{
    public sealed record ScrubbedBundle(Summary Summary, string ReportTxt, string TailTxt);
    public static class Scrubber
    {
        public static ScrubbedBundle Scrub(Summary s, string reportTxt, IEnumerable<string> tailLines, RedactionRules rules);
        public static string Preview(ScrubbedBundle b, int maxChars = 60_000);   // what the prompt shows
    }
    public static class Bundle
    {
        public const long CapBytes = 20L << 20;
        /// multipart/form-data body; parts summary (application/json), report (text/plain), tail (text/plain), dump (application/octet-stream, optional)
        public static byte[] Pack(ScrubbedBundle b, ReadOnlyMemory<byte>? dump, long capBytes, string boundary, out bool dumpDropped);
    }
    public static class Uploader
    {
        public static bool Configured { get; }                        // Platform.Version.CrashIngestUrl non-empty
        public static readonly Signal<int> OutboxVersion;
        public static int QueuedCount();
        public static void Enqueue(BundleInfo b, bool includeDump);    // off-thread: scrub → pack → outbox\<id>.bundle → send.json Queued → Drain()
        public static Task<SendRecord> SendNow(BundleInfo b, bool includeDump, CancellationToken ct);   // synchronous path for the recovery dialog
        public static void Drain();                                   // off-thread; called after Update.Host.Start and on connectivity edges
        public static void DiscardQueue();
    }
}
// WaveeVersionInfo gains CrashIngestUrl / CrashIngestKey (WP-G stamps them: AssemblyMetadata CrashIngestUrl/CrashIngestKey from $(WaveeCrashIngestUrl)/$(WaveeCrashIngestKey)).

// ── WP-D · Screens/Recovery.cs + Recovery.Win32.cs (engine-free) ────────────────────────────────────────────────
public static class Recovery
{
    public enum Reason : byte { Switch, BootLoop, BootFailed }
    public enum Outcome : byte { StartNormally, Quit }
    public static class Actions { public static IReadOnlyList<ActionKind> For(Reason reason, bool hasBundle, bool hasIngest); }
    public static Outcome Run(Reason reason, int bootFailures, BundleInfo? latest, string logFolder);   // TaskDialogIndirect; MessageBoxW fallback
}
// Entered from App.Main: `--recovery`, Platform.Boot() throwing, or Keys.CrashBootFailures >= RecoveryPolicy.BootFailuresForRecovery.

// ── WP-E · Screens/Crash.UI.cs ───────────────────────────────────────────────────────────────────────────────────
// Crash.Chrome (0×0 overlay component), CrashPromptBody, Crash.PrivacyRows, Crash.ReportsList — per D's component tree.
// Reads Host.ThisLaunch once, ConsentPolicy.Decide, Uploader.*, Host.Bundles(), Host.ReportsVersion, Host.HangReported.
```

### Symbol map (`.symmap`) binary format — written by WP-G (ReleaseTool), read by WP-H (Worker) and the round-trip test
Little-endian. Header 44 bytes: `magic "WSYM"` (4) · `u32 version = 1` · `u32 count` · `u32 stringTableBytes` ·
`16-byte PDB GUID` · `u32 age` · `u64 imageSize`. Then `count × {u32 rva, u32 size, u32 nameOffset}` sorted by rva
ascending, then the UTF-8 string table (each name NUL-terminated; `nameOffset` indexes into it). Debug id string form
everywhere = upper-case GUID without braces + "-" + age (e.g. `7E2C…-1`), matching `PeDebugId.TryRead`.

### Worker ingest contract (WP-C client ↔ WP-H server)
`POST {ingest}/v1/report` · headers `X-Wavee-Ingest: <key>`, `User-Agent: Wavee/<semver>` · `multipart/form-data`
parts `summary` (application/json = Summary, camelCase), `report` (text/plain), `tail` (text/plain), `dump`
(application/octet-stream, optional). Responses: `201 {"id":"<reportId>"}` · `409` duplicate id · `413` over cap ·
`401` bad key · `429` rate-limited. The client treats 2xx delete, 4xx delete+log, 429/5xx retry with backoff.


## J. Right to erasure (GDPR) — added 2026-09-24

The random install id (`crash.installId`, a 128-bit GUID only that PC knows) is the erasure handle. No account, no
e-mail, no IP is ever stored, so "delete my data" means "delete every report carrying my install id".

**Client (WP-C + WP-E):**
- `Crash.Uploader.DeleteRemote(CancellationToken) → Task<DeleteResult>`: `DELETE {ingest}/v1/installs/<installId>` with
  the `X-Wavee-Ingest` key and `User-Agent`; 200 `{deleted:n}` · 404 (nothing stored) both count as success; network
  failure → "Couldn't reach the crash service — try again later". Also clears the local outbox first (so nothing is
  re-sent), then **rotates the install id** (`Keys.CrashInstallId` = new GUID) so later reports, if reporting stays
  on, are unlinkable to the deleted ones.
- Settings › Privacy & diagnostics gains a row **"Your data on the crash service"** with sub-text "Reports are tied to
  a random install id, never to you. Install id `8a1d…77c0`." and a button **"Delete my data…"** → confirm dialog
  ("Deletes every report this PC ever sent. Reports already deleted by the 90-day rule are gone anyway. Reporting
  stays as it is.") → `DeleteRemote` → toast "Deleted n reports from the crash service" / error toast. Enabled only
  when `Uploader.Configured`.
- The install id is shown in that row (copyable) so a user who uninstalls Wavee can still request erasure by hand
  (PRIVACY.md: open a GitHub issue or e-mail with the install id; the developer runs the same delete from the
  dashboard's Report detail → "Delete this install's data").

**Server (WP-H Worker):**
- `DELETE /v1/installs/:installId` (auth `X-Wavee-Ingest`, rate-limited): select every `reports.id`/`quad` for the
  install, delete the R2 objects `reports/<quad>/<id>/*`, delete the `reports` rows, recompute the affected `issues`
  rows (count/installs/first/last/versions; delete an issue that reaches count 0), insert a tombstone into
  `deleted_installs(install_id TEXT PRIMARY KEY, deleted_at TEXT)`, respond `{deleted: n}`. `POST /v1/report` for a
  tombstoned install id → `410 Gone` (the client treats 410 like other 4xx: drop the outbox item). The dashboard gets
  the same action behind Access (`DELETE /v1/installs/:id` with the Access header also accepted).
- Tests: delete removes rows + objects, issues recomputed, tombstone blocks re-ingest with 410, unknown id → 404.

**Docs:** PRIVACY.md "Your rights" paragraph (in-app delete, the install id, the manual route, 90-day retention);
`docs/guide/crash-diagnostics.md` § erasure.

## K. WP-0 spike result (2026-09-25, CoreCLR `dotnet run`, arm64)
`--crash-probe native` (an AV inside `ntdll!RtlFillMemory`): the **vectored handler fires** (`crash.native.hook fired=veh
code=0xc0000005`); the unhandled-exception **filter never runs** because the runtime turns the fault into a fatal
`AccessViolationException` first. The dump is therefore requested from the VEH (once per process; the filter stays as
the NativeAOT fallback). `MiniDumpWriteDump` with `ClientPointers=TRUE` over the VEH's `EXCEPTION_POINTERS` fails with
`ERROR_NOACCESS` (0x800703e6); the child retries once without the exception stream and gets a 4.1 MB dump whose
faulting thread is still parked inside the hook, so the fault frames are on its stack. Still to measure on a Release
NativeAOT publish (packaged probe run) before the row in B.1 is final; the exception-stream failure is an open item.

## L. Orchestrator findings while wiring (2026-09-25, CoreCLR unpackaged runs)
- **Crash handlers run on the crashing thread and must not touch settings or signals.** The first managed-crash run died
  with a fatal "invalid program" inside a `SettingsChanged` subscriber when `OnManagedCrash` wrote a setting from a
  pool thread. Consequences, all engine-free files: the pending-bundle marker is the file `logs\crash\pending`; the
  boot-failure streak is the file `logs\crash\bootfailures` (also the only thing readable when `Platform.Boot()` itself
  threw, and the only thing that survives a demo profile's in-memory settings); the install id is ensured once at boot
  and only READ on the crash path (`InstallId.Peek`); the child receives it over the pipe (`I <id>`); `RunMarker.MarkCrashed`
  is no longer called from the handler. The keys `crash.pendingBundle` and `crash.bootFailures` were deleted.
- **A posted UI action's exception never reaches `AppDomain.UnhandledException`.** The engine's `DrainUiPosts` caught
  every exception with an empty catch; it now reports through `Diag.Sink` (`[post] posted UI action threw (frame
  continues)`, routed to Warning in the app log) and the frame goes on. `--crash-probe throw` therefore faults on a
  pool thread (the real unhandled path); `throw-ui` is the posted case and proves the log line lands.
- **Version-changed suppression is read off the bundle** (its recorded version vs this build), not off `LastRunVersion`,
  whenever a bundle exists: a demo profile would otherwise report "version changed" on every launch and never count.
- **Verified end to end (Debug, unpackaged):** managed worker throw → bundle + 4.1 MB dump + exit 0xE0434352; UI
  throw → log line, app continues; native AV → VEH fires, dump written on the no-exception-stream retry; fail-fast →
  exit-code bundle; hang → `hang.suspected noBeatMs=30428 hungMs=10078`, all-thread dump, `hang.recovered`; boot loop →
  streak 1, 2, then the plain launch stops at the `TaskDialogIndirect` "Wavee couldn't start". Not yet run: a real
  upload (the Worker cannot run on this ARM64 box: no `workerd`), the packaged NativeAOT pass, Task Manager kill.

### K.2 NativeAOT results (2026-09-25, `bin\publish-aot-symbols\Wavee.exe`, arm64 Release)
- `native`: **both** hooks fire (`fired=veh` first, dump requested there; `fired=filter` afterwards), exit 0xC0000005,
  bundle with a 390 KB dump (the no-exception-stream retry; `ClientPointers` still fails with ERROR_NOACCESS).
- `throw` (pool thread): `AppDomain.UnhandledException` fires, managed bundle + dump, exit **0xC0000409** (NativeAOT
  fail-fasts on an unhandled exception; the child records that code). `failfast`: exit-code bundle, 0xC0000409.
- `hang`: `hang.suspected noBeatMs=30263 hungMs=10094`, dump written, `hang.recovered afterMs=15250`.
So B.1's native row is final: VEH-first dump request, filter as the second witness, no dependency on the runtime.
