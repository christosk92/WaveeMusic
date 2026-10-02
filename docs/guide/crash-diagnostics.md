# Wavee crash & diagnostics pipeline

> **Scope: the Wavee app** (`src/apps/**`) and the standalone service in `ops/crash/`. Opt-in only — off by
> default; see `PRIVACY.md` "Crash reports (opt-in)" for the promise made to users. Full design and the
> work-package breakdown: `docs/plans/wavee/crash-diagnostics-implementation.md`; the production-readiness
> pass (#165 — grouping v2, native fault fields, retention, alerts):
> `docs/plans/wavee/crash-production-readiness-implementation.md`. This guide is the
> day-to-day reference once the pipeline exists — what a bundle contains, how to read one by hand, how to
> run the probes, and where the service and dashboard live.

## Contents

1. [Pipeline](#1-pipeline)
2. [Bundle layout](#2-bundle-layout)
3. [Consent matrix](#3-consent-matrix)
4. [Reading a bundle by hand](#4-reading-a-bundle-by-hand)
5. [Running the probes](#5-running-the-probes)
6. [Outbox rules](#6-outbox-rules)
7. [Ingest contract](#7-ingest-contract)
8. [The service and the dashboard](#8-the-service-and-the-dashboard)
9. [Erasure — the right to be forgotten](#9-erasure--the-right-to-be-forgotten)
10. [Native fault fields](#10-native-fault-fields)
11. [Grouping (fingerprint v2)](#11-grouping-fingerprint-v2)
12. [Alerts and regressions](#12-alerts-and-regressions)
13. [Retention](#13-retention)
14. [Deleting one report](#14-deleting-one-report)

---

## 1. Pipeline

```
┌──────────────────────────── Wavee.exe (app) ──────────────────────────────────┐
│ Shell.Run (after AcquireInstance, after InstallCrashNet):                       │
│   Crash.Host.Install(logFolder)  → spawns Wavee.exe --crash-handler <pid> …    │
│   (never for --headless / relaunch broker / second-instance hand-off)          │
│   AddVectoredExceptionHandler (fault stack first) + unhandled filter fallback  │
│ UI thread: posted heartbeat every 2 s; first-frame witness; modal/exiting bits │
│ managed crash ─► AppDomain.UnhandledException ─┐                                │
│ native AV     ─► VEH, filter as fallback ──────┼─► Crash.Host.RequestDump(kind) │
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
   POST https://crash.cproducts.dev/v1/report  (Cloudflare Worker) → R2 objects + D1 rows, symbolicated at ingest
                                │
   Dashboard (Fluent UI v9, the same Worker's static assets on crash.cproducts.dev, behind Cloudflare Access)
```

Five crash classes feed the handler (`Crash.Kind`): `Managed` (any thread's unhandled exception),
`Native` (an access violation, illegal instruction, in-page error, array-bounds or divide-by-zero fault in
foreign code — the VEH catches it, the unhandled filter is the fallback; §10 has what such a report
carries), `Hang` (the watchdog gives up
on the heartbeat), `ExitCode` (fail-fast, a runtime fatal error, a stack overflow — nothing to hook, so
the handler infers it from the parent's exit code), and `UncleanExit` (Task Manager or the OS killed the
process — never prompts, just gets listed as "Closed"). `ExitCode 0` writes nothing.

The **out-of-process handler** is the same signed `Wavee.exe`, re-invoked as `--crash-handler <pid>
<logFolder> <datedLogPath>`. It talks to the parent over a `stdin` pipe (one byte per heartbeat, one line
per dump request) plus a process handle — no named kernel objects, no admin rights, no WER `LocalDumps`
registry key (which needs both and is why the old WER probe was dead on user machines). See
`docs/plans/wavee/crash-diagnostics-implementation.md` §B.0–B.1 for why every rejected alternative
(in-process `MiniDumpWriteDump`, WER LocalDumps) doesn't work here.

## 2. Bundle layout

```
%LOCALAPPDATA%\Wavee\logs\crash\              (packaged: the package LocalCache equivalent; logResolved= says which)
├── 20260924-143012-118-managed\   summary.json · report.txt · log-tail.txt · minidump.dmp
├── 20260923-091500-004-hang\      summary.json · report.txt · log-tail.txt · minidump.dmp
├── 20260921-220301-777-exitcode\  summary.json · report.txt · log-tail.txt
├── outbox\                        <reportId>.bundle  (packed multipart, scrubbed, waiting) · <reportId>.meta (§6)
└── handler.log
```

Folder name: `yyyyMMdd-HHmmss-fff-<kind>` (`Crash.Files.BundleName`). Write order is deliberate —
`summary.json` first (so even a disk-full write leaves *something* readable), then `report.txt`, then
`log-tail.txt` (last 300 lines of the dated log, scrubbed at write time), then the dump last. Pruned to
the newest 10 bundles and ≤ 200 MB total on every write.

`summary.json` is the machine-readable half (`Crash.Summary` — every field the Worker's ingest contract
in §7 below validates against, camelCase on the wire). `report.txt` is the human-readable half: today's
`Describe` text (exception, RVA frames, module base/size, versions, GPU) — this is what the settings UI
shows in the crash prompt's preview and what you'd paste into a GitHub issue by hand. `minidump.dmp`
defaults to `MiniDumpNormal | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules` (stacks + modules, no
heap — the heap holds the DPAPI-unprotected credential and live tokens); a user can opt a single manual
Send into `MiniDumpWithIndirectlyReferencedMemory` via an explicit checkbox.

## 3. Consent matrix

Setting `Keys.CrashReporting` (`Crash.Reporting`: `Off = 0`, `Ask = 1`, `Auto = 2`), default **Off**.
`Crash.ConsentPolicy.Decide(mode, kind, online)`:

| Mode | Kind | Result |
|---|---|---|
| any | `UncleanExit` | `Nothing` — never prompts, never uploads (nothing evidences a Wavee-caused failure) |
| `Auto` | not UncleanExit, online | `UploadSilently` |
| `Auto` | not UncleanExit, offline | `Toast` — the report is **enqueued** to the outbox (§6) and the queued toast says it'll send once you're back online; the drain sends it on reconnect or at the next launch |
| `Off` or `Ask` | not UncleanExit | `Prompt` — **`Off` still shows the local prompt** with a manual Send; the report is never uploaded without that explicit click |

`mode` here is `Crash.ConsentPolicy.Effective(mode, Uploader.Configured)`: on a build that can't send — no
ingest URL, key or quad stamped in (`WaveeVersionInfo.CrashReportingAvailable`; a local `dotnet run`, an
unstamped `dev` pack) — `Auto` behaves as `Ask`. `ConsentPolicy.Offered(configured)` is what the setup
wizard's consent card and the Settings mode combo offer: Off / Ask each time / Automatic, or only Off / Ask
each time on such a build (the Settings combo shows Automatic disabled, described by
`crash.modeAutoNeedsService`).

Dump inclusion is a separate, stricter gate (`Crash.ConsentPolicy.DumpAllowed`): a dump only rides along
when the user manually sends (`manualSend == true`), or when reporting isn't `Off`, the include-dump
setting is on, **and** the kind isn't `Hang` — hang dumps are always ask-first, never auto-attached, even
in `Auto` mode, because a hang dump can be large and the hang itself might be a one-off (a debugger
attach, a suspend/resume edge) rather than a real bug.

## 4. Reading a bundle by hand

`report.txt` and `summary.json` are plain text/JSON — open them in Notepad, no tooling needed. The frames
are RVA-only (`StackTraceSupport=false` in the shipped NativeAOT build), so resolving them to method
names needs the matching build's PDB. That's the same symbol story the local crash report has always had
— follow **`docs/guide/releasing-wavee.md` §5b "Symbolicating a crash report"** end to end: download the
matching `Wavee-<quad>-win-<arch>-symbols.zip` release asset, verify its sha256 against the package, and
run `cdb -lines -z … -y … -c "ln Wavee+0x<rva>; q"` per RVA. `summary.json`'s `debugId`, `moduleBase` and
`rvas` fields carry everything §5b's recipe needs; `quad`/`arch`/`commit` tell you which zip to fetch.

A `.dmp` (when present) opens directly in WinDbg/cdb with the same symbols path — the "WinDbg one-liner"
referenced from the dashboard's report detail view is this same §5b recipe, just pointed at the dump
instead of a bare RVA list.

## 5. Running the probes

`--crash-probe <mode>` (`throw`, `throw-ui`, `failfast`, `native`, `hang`, `boot`) forces one crash class
end to end, for verifying the pipeline without waiting for a real bug. Full expected log lines and bundle
shapes: `docs/plans/wavee/crash-diagnostics-implementation.md` §G "Verification" and
`docs/plans/wavee/crash-production-readiness-implementation.md` "Verification". `throw` faults on a pool
thread (a `Managed` bundle). `throw-ui` throws inside a posted UI action instead — the engine's UI post
drain catches it and the frame goes on, so it writes **no** bundle; it exists to prove the `[post]` Warn
line reaches `wavee-*.log` (`Platform.Host.RouteFor`), the only trace a swallowed UI-thread fault leaves.
`native` faults inside ntdll (a `Native` bundle with `faultModule` `ntdll.dll`, §10).

**Send a test crash report** (Settings › General › Developer, shown only in developer mode) runs the whole pipeline
without crashing: `Crash.Host.WriteTestReport` throws a `Crash.SimulatedCrashException` through three no-inline Wavee
frames, catches it, and writes a normal `Managed` bundle — `summary.json`, `report.txt` (its "Frames (RVA)" parsed
from the caught trace in a NativeAOT build), `log-tail.txt` — plus a real minidump of the still-running process. The
child writes that dump for the `test` dump kind (`D test …`, `Crash.Handler.DumpKinds`), which never latches the
child's exit-code inference, and the button touches neither the once-per-process crash latches nor the `pending`
marker: a real crash or non-zero exit later in the same run is still captured, and the next launch never offers the
test as "Wavee crashed last time". The bundle then goes through `Crash.Uploader.Enqueue` with its dump (scrub → pack
→ outbox → POST) whatever the reporting mode — the click is the manual send — and the real outcome toast follows
(the sent card names its short id; Logs › Reports lists it either way). On the dashboard its issue is titled `Wavee.Crash+SimulatedCrashException · <innermost Wavee frame>`, and
repeated tests from one build group into that one issue (§11). A build that can't send (`Uploader.Configured` false)
disables the button.

**Unpackaged** (fast iteration):

```powershell
dotnet run --project src/apps/Wavee -- --fake --crash-probe throw
dotnet run --project src/apps/Wavee -- --fake --crash-probe hang
dotnet run --project src/apps/Wavee -- --fake --crash-probe boot    # run twice, then a plain launch
dotnet run --project src/apps/Wavee -- --recovery                   # recovery mode by hand, no crash needed
```

**Packaged** (the only way to see the real MSIX process lifetime — child spawn under package identity,
`Remove-AppxPackage` waiting for the child to exit): build an E2E package via `ops/release/tests/local-update-e2e.ps1`,
launch it with `explorer.exe shell:AppsFolder\<AUMID>` (a probe flag can't ride a Start Menu launch, so
packaged probe runs go through the same harness with its arg-injection support — see the harness script
for the current mechanism), and **wipe `LocalCache` after** every run (`CLAUDE.md` "Packaged runs" —
never leave a real `%LOCALAPPDATA%\Wavee` around from an unpackaged `dotnet run`, and never leave a stale
packaged one either). `logResolved=` in the startup log line confirms which store a given run actually
used.

Each probe's bundle should land in `logs\crash\<stamp>-<kind>\` per the layout in §2; a hang probe should
additionally show the app recover and keep running (the hang watchdog dumps once and does not kill the
process); a boot-loop probe should show Recovery Mode on the third launch, no window.

## 6. Outbox rules

Every scrubbed, packed bundle destined for upload lands in `outbox\<reportId>.bundle` exactly once, beside
its `<reportId>.meta` sidecar (`Crash.Uploader.Enqueue`: scrub → pack → write the `.bundle` and `.meta` →
mark `send.json` `Queued` → `Drain()`). The `.meta` is a flat `key=value` file — `bundleDir`, `attempts`
(server retries so far), `includeDump`, `lastTryUtc`, `launches` (launches that saw a server retry) — and is
the only place backoff state lives; the `.bundle` name never changes.

`Drain()` runs off-thread **at launch** (the last step of `Crash.Uploader.Install`, when `Configured`), on
every `Platform.Network` edge to online, and **on every enqueue**. It is single-flight: a drain requested
while one runs makes the running one loop again over a fresh listing. Nothing leaves the machine
speculatively; there's no DNS lookup or preflight before a real send is due. An outbox entry whose bundle
folder is gone (the user deleted the report) is an orphan and is dropped unsent; `Crash.Host.Delete` calls
`Uploader.ForgetBundle(dir)` before deleting the folder, and `send.json` writes never recreate a missing
folder, so a deleted report is never uploaded and never comes back.

Response handling (`Crash.UploadPolicy.Classify` → `Next`, pure and table-tested):

| Response | Outcome | Action |
|---|---|---|
| 2xx | `Sent` | delete the `.bundle` + `.meta`, mark `send.json` `Sent` |
| 409 | `AlreadySent` | the service already holds this `reportId` (an earlier attempt landed but its answer was lost) — exactly like 2xx: `Sent` |
| 410 | `Erased` | the install id was erased (§9): delete, mark `Failed` ("install erased") |
| any other 4xx (400, 401, 413) | `Rejected` | won't succeed on retry (bad shape, bad key, too large): delete **and log it**, mark `Failed` with the status |
| 429 / 5xx | `RetryServer` | keep; bump `attempts` and `lastTryUtc` in the `.meta`; the next try is due after 2^attempts minutes, capped at 60 (`UploadPolicy.Due`); give up (delete, `Failed` "gave up") once **3 launches** have seen a server retry |
| no response (offline, DNS, connect, timeout) | `RetryNetwork` | keep, mark `Queued` ("offline"); retried on the next drain — launch, reconnect or enqueue — and **never given up**: an offline PC keeps its reports until it's back |

Each request gets its own timeout, `UploadPolicy.Timeout(bodyBytes)` = 30 s + 1 s per 50 KB, clamped to
30 s…10 min (a full 20 MB bundle with a dump gets about 7½ minutes); `HttpClient.Timeout` itself is
infinite. A fixed 30 s used to time out large dumps on slow connections.

**Who sends.** The in-app crash prompt's "Send" — and a Reports row's Send, which opens the same prompt
with its preview and dump checkbox — **enqueues** like any other upload, passing a `settled` callback. The
toast reflects the real outcome (`UploadToasts.For`): "sent" only once the drain got a 2xx/409, the failure
toast (`crash.sendFailed`) on a final failure, the queued toast once while it waits; all share the dedupe
key `crash:<id>`, so the latest state replaces the earlier one. Automatic mode goes through the same
`Enqueue`. Only **Recovery mode** uses `Crash.Uploader.SendNow` — the synchronous twin of `Enqueue`, for the
engine-free TaskDialog that wants an immediate result; on a final outcome it also removes that report's
outbox entry, so the drain can't send it a second time.

## 7. Ingest contract

Client → server, exactly (this is the contract WP-C's uploader and the Worker's `POST /v1/report`
handler both code against — see `docs/plans/wavee/crash-diagnostics-implementation.md` §I, and
`ops/crash/worker/src/validate.ts` for the server-side check):

```
POST {ingest}/v1/report
Headers: X-Wavee-Ingest: <public ingest key>
         User-Agent: Wavee/<semver>
Body:    multipart/form-data
  summary   application/json   — the Summary shape below, camelCase
  report    text/plain
  tail      text/plain
  dump      application/octet-stream   (optional)
```

`Summary`'s required fields (validated server-side; everything else is optional and defaulted if a build
omits it): `reportId`, `installId`, `kind` (one of `Managed` | `Native` | `Hang` | `ExitCode` |
`UncleanExit`), `quad`, `arch`, `rvas` (number array), `moduleBase`, `debugId`. `debugId`'s string form
everywhere is an upper-case GUID **with** dashes, no braces, plus `-<age>` (e.g.
`7E2C1234-AB12-CD34-EF56-1234567890AB-1`) — the same format `PeDebugId.TryRead` produces on the client and
the `.symmap` header encodes on the server.

Optional fields with server-side checks: `exceptionCode` (uint32, the NTSTATUS of a `Native` fault),
`faultModule` (sanitized to a lower-case base name matching `^[a-z0-9][a-z0-9._-]{0,63}$`, else empty),
`faultOffset` (≥ 0) — §10; `version` must match `^[0-9A-Za-z.+-]{1,64}$` (it becomes a JSON path key in
`issues.versions_json`), and `rvas` is capped at 64 entries. A `summary.json` written before these fields
existed still reads: they are trailing, defaulted parameters of `Crash.Summary`.

Responses: `201 {"id":"<reportId>"}` on success · `401` bad/missing ingest key · `400` malformed body or a
`summary` that fails validation · `409` duplicate `reportId` · `410` the report's `installId` was erased
(§9 — the tombstone left by `DELETE /v1/installs/:installId`) · `413` bundle over the 20 MB cap · `429`
rate-limited. The client's outbox (§6) treats `2xx` **and `409`** as sent (a duplicate means an earlier
attempt already landed), `410` as erased, any other `4xx` as rejected-and-logged, `429`/`5xx` as
retry-with-backoff, and no response at all as a network retry that never gives up.

Total bundle size cap is 20 MB; if a dump would push a bundle over the cap, the client drops the dump
first (`Crash.Bundle.Pack` reports it; the `.meta`'s `includeDump` and `send.json`'s `dumpIncluded` then
read `false`) rather than failing the whole upload — a scrubbed `report.txt` + `log-tail.txt` without a
dump is still useful for grouping and symbolicated frames. There is no `summary.dumpDropped` field, and
the summary's own `hasDump`/`dumpBytes` describe the local bundle only: the Worker derives `has_dump` and
`dump_bytes` from the `dump` part it actually received, never from the summary.

## 8. The service and the dashboard

The Worker (`ops/crash/worker`), its D1 schema and R2 layout, and the deploy runbook (`wrangler d1`,
`wrangler r2`, secrets, Cloudflare Access) live in **`ops/crash/README.md`** — read that before touching
the service. Route table, request/response shapes, and the `.symmap` binary format the Worker's
`src/symbolicate.ts` parses are also documented there and in plan §I; do not duplicate them here.

The dashboard (`ops/crash/dashboard`, Vite + React + `@fluentui/react-components` v9) is a separate app
but not a separate deploy: the Worker's own deploy (`npm --prefix ops/crash/worker run deploy`) builds it
and uploads its `dist/` as the Worker's static assets, served on the same `crash.cproducts.dev` hostname
behind the same Cloudflare Access application as the `GET`/`PATCH` routes
(`docs/plans/wavee/crash-hosting-implementation.md`) — see `ops/crash/dashboard/README.md`. It reads
the same `/v1/*` API this guide documents, same-origin: Overview (`GET /v1/stats` — totals, per-day crash/hang/closed,
per-kind counts), Issues (`GET /v1/issues`, grouped by fingerprint) → Issue detail (`GET /v1/issues/:fp`,
which also carries the latest report's resolved `frames`, the last 50 `occurrences`, a 14-day
`sparkline14d`, and `breakdowns` by version/arch/GPU tier), Reports (`GET /v1/reports`, a keyset-paginated
list filterable by `since`/`kind`/`quad`/`q` — `q` matches a report or install id prefix) → Report detail
(`GET /v1/reports/:id`, which also carries `this_install` — that install's report count in the last 30
days and the quad it was first seen on — and the report's `debug_id`), Versions, and Symbols (`GET
/v1/symbols`, the `symbols` D1 table — which `.symmap`s the Worker has actually resolved against). Full
shapes: `docs/plans/wavee/crash-dashboard-implementation.md` §4 and `ops/crash/worker/src/types.ts`.

Since #165: Issue detail falls back to the issue's own `last_frames_json` once no report of it is left
(§13), and its Log tail tab shows the newest report's tail (or says reports older than 90 days are deleted
while the issue's counts are kept); issues carry a "Regressed" badge and a "Resolved in ≤ <version>" note
(§12); Native reports show `<CODE> in <module>+0x<offset>` (§10); Report detail's Delete is real (§14);
Overview has "Run retention now" (§13). The app's Settings › Logs › Reports shows each report's short id
(e.g. `3f9c-2b1a`, also prefilled into the GitHub crash form's `report-id` field); the Reports search
strips `-`, so pasting that id finds the report.

## 9. Erasure — the right to be forgotten

The random install id is the erasure handle: no account, e-mail, or IP is ever stored (§7, §8's own
"no data goes to the author" line in `PRIVACY.md`), so "delete my data" means exactly "delete every report
carrying this install id." Two ways to trigger it, one route underneath:

- **In-app.** Settings › Privacy & diagnostics → "Your data on the crash service" shows the install id
  (copyable) and a **"Delete my data…"** button (enabled only when `Crash.Uploader.Configured`). Confirming
  calls `Crash.Uploader.DeleteRemote`, which clears the local outbox first (nothing queued gets sent after
  the delete), calls `DELETE {ingest}/v1/installs/<installId>` with the `X-Wavee-Ingest` key, and — on a
  `200` or a `404` (both count as success: `404` just means nothing was left to delete) — **rotates
  `Keys.CrashInstallId` to a fresh GUID**, so any future report, if reporting stays on, cannot be linked to
  the deleted ones. A network failure surfaces as "Couldn't reach the crash service — try again later" and
  does not rotate the id (so a retry still targets the right install).
- **By hand, after uninstalling.** The install id shown in that Settings row is still what you need once
  Wavee itself is gone — open a GitHub issue or e-mail with it (`PRIVACY.md` "Your rights"); the developer
  runs the same delete from the dashboard's Report detail page → "Delete this install's data…" (Cloudflare
  Access-authenticated, same underlying route).

Server side (`DELETE /v1/installs/:installId`, §7/§8, `ops/crash/worker/src/index.ts`
`handleDeleteInstall`): finds every `reports` row for that install id, deletes each one's four R2 objects
and the row itself, recomputes every `issues` row those reports contributed to from what's left in
`reports` (not a hand-decremented counter; ingest counts incrementally since #165, but erasure still
recomputes from the source of truth — §13 has the one consequence) — **deleting an issue outright once
its count reaches 0** rather than leaving a zero-count husk —
then inserts a permanent row into `deleted_installs(install_id, deleted_at)`. Responds `200 {"deleted": n}`
(`n` = reports actually removed); `404` when nothing was ever stored for that id (including calling it a
second time on an id already fully erased). Accepted with **either** the `X-Wavee-Ingest` key or a
Cloudflare Access session — the one route that isn't gated by exactly one of those, since both the app and
the dashboard need to be able to trigger it. Rate-limited the same way as ingest.

Once tombstoned, `POST /v1/report` for that install id gets `410 Gone` (§7) instead of silently being
re-admitted — this is what makes rotating the install id after a successful delete matter: without a fresh
id, an `Auto`-mode report from a later crash on the same PC would just bounce off the tombstone forever.

Deletion here is immediate and explicit; it exists alongside, not instead of, the **90-day automatic
retention** every report gets regardless (§13: the daily purge, with the R2 lifecycle rule in
`ops/crash/README.md` as its backstop) — erasure is for "delete this now," retention is for everyone who
never asks.

## 10. Native fault fields

A `Native` bundle comes from a fault in foreign code (a driver, a system DLL), so no managed exception
carries a type or a stack. Since #165 it carries three summary fields of its own plus a stack captured in
the hook (`Crash.NativeHook`, `Crash.Native.cs`, `Crash.Handler.TryReadFault`; plan appendix A3):

| Field | Source | Notes |
|---|---|---|
| `exceptionCode` | `EXCEPTION_RECORD.ExceptionCode`, e.g. `0xC0000005` | uint32, serialized as a number |
| `faultModule` | the loaded module whose range holds `ExceptionAddress`, resolved **in the child** | base name only, lower-case, `[a-z0-9._-]` ≤ 64 chars, else `""` (`Crash.FaultModule.Normalize`, mirroring the Worker's sanitizer); also passes through the scrubber's redactor |
| `faultOffset` | `ExceptionAddress − module base` | ≥ 0 |
| `rvas` | the hook's stack capture, projected onto Wavee.exe | at most 32 |

**VEH stack capture.** The vectored exception handler runs `CaptureFaultStack` **first**, before anything
that allocates (the heap may be what just faulted): it records the exception address and up to 62 return
addresses from `RtlCaptureStackBackTrace` into a `NativeMemory` buffer allocated once at install.
`FaultRvas()` projects that buffer through the pure `NativeFrames.SelectAppRvas`: start at the frame equal
to the exception address — or, when the walk doesn't contain it (x64 under emulation can miss it), skip
the handler's own leading Wavee frames — then keep only frames inside Wavee.exe, at most 32. NativeAOT code
has unwind data, so the OS unwinder walks through `KiUserExceptionDispatcher` on x64 and ARM64. Those RVAs
symbolicate server side exactly like a managed crash's.

**Fault module, out of process.** The crashing process never resolves the module (no loader lock inside a
hook). The child, while the parent is still parked on the dump reply, reads `EXCEPTION_POINTERS` →
`EXCEPTION_RECORD` with `ReadProcessMemory`, lists the parent's modules (`K32EnumProcessModulesEx` /
`K32GetModuleInformation` / `K32GetModuleBaseNameW` — it already holds QUERY|VM_READ), finds the one
holding `ExceptionAddress` and normalizes its base name. It then writes the dump, updates `summary.json`
(`hasDump`, `dumpBytes`, `faultModule`, `faultOffset`) and appends `fault=<module>+0x<offset>` to
`report.txt`. The child finalizes the summary; the parent no longer rewrites it after the dump. The
`report.txt` headline reads `native fault 0x<code> at 0x<exception address>` (it used to print a pointer's
address), and the report gains a "Frames (RVA)" section.

**The minidump keeps the exception record.** `minidumpapiset.h` wraps `MINIDUMP_EXCEPTION_INFORMATION` in
`pshpack4.h`. Declared plain `Sequential`, the 64-bit C# struct was 24 bytes with `ExceptionPointers` at
offset 8, so dbghelp read a garbage pointer, every dump with exception info failed with `ERROR_NOACCESS`,
and the retry (`retry=noexception` in `handler.log`) wrote a dump without the exception stream. With
`[StructLayout(LayoutKind.Sequential, Pack = 4)]` the layout is 16 bytes, `ExceptionPointers` @ 4,
`ClientPointers` @ 12 (`Crash.Handler.ExceptionInfoLayout()` pins it in a test), and `cdb -z <dmp> -c
".ecxr;k;q"` lands on the faulting context. The retry stays as a net; seeing `retry=noexception` again is
a bug.

## 11. Grouping (fingerprint v2)

The Worker groups reports into issues by fingerprint = SHA-1 of a key built in
`ops/crash/worker/src/grouping.ts` (`FINGERPRINT_VERSION = 2`; `reports.fp_version` and
`issues.fp_version` record it). v1 keyed on the top frame — usually a .NET ThrowHelper — and collapsed
every `Native`, every `Hang` and every `ExitCode` report into one issue each. Issues created under v1 keep
`fp_version = 1` and simply stop receiving reports; nothing is re-fingerprinted.

**Normalization** (`normalizeSymbol`): compiler-generated ordinals shift whenever code moves inside a
method, so they must not split an issue. It strips `unbox_` and collapses `DisplayClass<n>_<n>` →
`DisplayClass`, `_b__<n>[_<n>]` → `_b__`, `_d__<n>` → `_d__` and local-function `_g__<Name>_<n>_<n>` →
`_g__<Name>`. Accepted cost: two lambdas in the same method can merge.

**Framework frames are skipped** (`FRAMEWORK_FRAME_PATTERNS`, matched after normalization): `S_P_*`
(System.Private.*), `System_*`, `Microsoft_*`, `Internal_*`, the linked NuGet libraries (`SQLitePCL_`,
`Google_Protobuf_`, `NLayer_`, `ZstdSharp_`), `__*` helpers, the runtime's `Rh*`/`Rhp*`/`Pal*`, C++ `::`
and GC `WKS_`/`SVR_` names, and `memset`/`memcpy`/`RaiseException`/`KiUserExceptionDispatcher` and
friends. `Wavee_*` and `FluentGpu_*` are app frames (the engine is ours). `topFrameKey` takes the top 3
app frames, normalized; an all-framework stack falls back to the top 3 raw names, and a stack with nothing
resolved (no symmap for that build) to the top 3 RVAs.

| Kind | Key | Title (`deriveTitle`, written once when the issue is created) |
|---|---|---|
| `Managed` | `v2\|Managed\|<exceptionType>\|<topFrames>` | `<ExceptionType> · <first Wavee frame>` |
| `Native` | `v2\|Native\|<code hex8>\|<faultModule>\|<topFrames>`; with no frames, `…\|<faultModule>+<offset hex>` | `<STATUS_NAME> in <module> · <first Wavee frame>`, or `… in <module>+0x<offset>` |
| `ExitCode` | `v2\|ExitCode\|<exit code hex8>` — one issue per code | `Exit code <STATUS_NAME>` |
| `Hang` | `v2\|Hang` — one bucket, frames ignored | `Hang (UI stopped responding)` |
| `UncleanExit` | `v2\|UncleanExit` | `Unclean exit` |

`STATUS_NAME` comes from an NTSTATUS table (`ACCESS_VIOLATION`, `STACK_OVERFLOW`, `STACK_BUFFER_OVERRUN
(fail-fast)`, `HEAP_CORRUPTION`, …; anything else prints as `0x<hex8>`). The client unwraps the exception
type before it's sent (`Crash.Report.Unwrap`): a single-inner `AggregateException`, a
`TargetInvocationException` or a `TypeInitializationException` reports its inner exception's type.

## 12. Alerts and regressions

`ops/crash/worker/src/alerts.ts`. Ingest records each report against its issue
(`recordIssueOccurrence` → `new` | `regressed` | `existing`); on `new` and `regressed` the Worker posts one
Discord embed to the `DISCORD_WEBHOOK_URL` secret (a secret only, never a `[vars]` entry; unset → no-op;
only a `https://discord.com/api/webhooks/<id>/<token>`-shaped URL is used; 5 s timeout, posted through
`ctx.waitUntil` so ingest never waits on it). The embed carries the issue title ("New crash issue: …" or
"Regression — a resolved issue is back: …"), a link to `/issues/<fp>` on the dashboard, the kind, the
version (`semver (quad) · arch`), the channel, `<count> from <installs> installs`, the GitHub issue number
when one is linked, and a fingerprint prefix. Never the exception message, an install or report id, or log
text; `allowed_mentions.parse` is empty and markdown/control characters are stripped. Alerts fire only
for builds whose symmap the service holds (`debugId !== null` after symbolication) — every shipping
channel uploads one under the hard release gate, and the public ingest key can't spam the channel with
made-up quads.

**Regression rule.** Resolving an issue (`PATCH /v1/issues/:fp` with `status: "resolved"`; the status
must be `open`, `resolved` or `ignored`, anything else is a `400`) stamps `resolved_at` and
`resolved_version` = the newest semver in the issue's `versions_json` (`maxSemver`; a prerelease sorts
below its release). A later report reopens the issue only when its semver is **newer** than
`resolved_version` (`isNewerSemver`): `status` back to `open`, `regressed_at` set, `regressions + 1`, and
the regression alert. The reopen is one `UPDATE … WHERE status='resolved'`, so concurrent reports alert
once. A report from the same or an older version — users who haven't updated yet — counts toward the issue
and leaves it resolved. `ignored` never reopens, and an issue resolved before any version was recorded
never reopens on its own.

## 13. Retention

`ops/crash/worker/src/retention.ts`. A cron trigger (`[triggers] crons = ["17 3 * * *"]`, daily at
**03:17 UTC**) runs `purgeExpiredReports`: `reports` rows whose `received_at` is more than **90 days** old
are deleted together with their four R2 parts (`reports/<quad>/<id>/…`), in batches of 200 rows (800 keys
per R2 `delete`, under its 1000-key limit) and at most 10 batches per run — ≤ 30 subrequests, under the
free plan's 50. A run that hits the batch limit reports `more: true` and the next run continues. The purge
is driven from D1 and logs counts only (`retention.purge`).

**Issues are kept.** An issue's `count`, `installs`, `versions_json`, first/last seen, status, title and
`last_frames_json` — the latest resolved stack, kept so Issue detail still shows frames once every report
of it has aged out — outlive the purge; ingest counts incrementally instead of recomputing from `reports`.
None of it holds report contents (no message, install id, log or dump). Erasure (§9) still recomputes the
issues it touches from what's left in `reports`, so erasing an install after a purge can shrink an issue's
lifetime counts to the 90-day window — accepted.

The R2 lifecycle rule (delete `reports/` objects 90 days after upload, `ops/crash/README.md`) stays as a
backstop for anything the purge misses. `POST /v1/retention/run` (Cloudflare Access only; the dashboard
Overview's "Run retention now") runs the same purge on demand and answers `{deleted, batches, more}`.

## 14. Deleting one report

`DELETE /v1/reports/:id` (Cloudflare Access only; the dashboard's Report detail → Delete) deletes that
report's R2 parts and its row, then recomputes its issue or deletes it at count 0 (`recomputeOrDeleteIssue`,
the same rule as erasure) → `200 {"deleted":1,"fingerprint":"<fp>"}`; an unknown id is a `404`, no Access
session a `401`. It is the maintainer's tool for one bad report (a synthetic test, a duplicate) and leaves
no tombstone — a user's erasure is §9.

In the app, a Reports row's **Delete** (Settings › Logs › Reports) is local: it confirms, then
`Crash.Host.Delete` calls `Uploader.ForgetBundle(dir)` (dropping the outbox entry) and removes the bundle
folder. A copy already on the service stays until retention (§13) or "Delete my data…" (§9). The row's
**Copy** puts the scrubbed preview — exactly what Send would upload — on the clipboard.
