# Wavee crash & diagnostics pipeline

> **Scope: the Wavee app** (`src/apps/**`) and the standalone service in `ops/crash/`. Opt-in only — off by
> default; see `PRIVACY.md` "Crash reports (opt-in)" for the promise made to users. Full design and the
> work-package breakdown: `docs/plans/wavee/crash-diagnostics-implementation.md`. This guide is the
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

---

## 1. Pipeline

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
   POST https://crash.cproducts.dev/v1/report  (Cloudflare Worker) → R2 objects + D1 rows, symbolicated at ingest
                                │
   Dashboard (Fluent UI v9, the same Worker's static assets on crash.cproducts.dev, behind Cloudflare Access)
```

Five crash classes feed the handler (`Crash.Kind`): `Managed` (any thread's unhandled exception),
`Native` (an AV in foreign code, if the WP-0 spike found a working hook), `Hang` (the watchdog gives up
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
├── outbox\                        <reportId>.bundle  (packed multipart, scrubbed, waiting) · <reportId>.retryN
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
| `Auto` | not UncleanExit, offline | `Toast` (queued to the outbox, sent on reconnect) |
| `Off` or `Ask` | not UncleanExit | `Prompt` — **`Off` still shows the local prompt** with a manual Send; the report is never uploaded without that explicit click |

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

`--crash-probe <mode>` (`throw`, `failfast`, `native`, `hang`, `boot`) forces one crash class end to end,
for verifying the pipeline without waiting for a real bug. Full expected log lines and bundle shapes:
`docs/plans/wavee/crash-diagnostics-implementation.md` §G "Verification".

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

Every scrubbed, packed bundle destined for upload lands in `outbox\<reportId>.bundle` exactly once
(`Crash.Uploader.Enqueue`: scrub → pack → write → mark `send.json` `Queued` → `Drain()`). `Drain()` runs
off-thread after `Update.Host.Start` and again on every `Platform.Network` connectivity edge — nothing
leaves the machine speculatively; there's no DNS lookup or preflight before a real send is due.

Response handling on drain:

| Response | Action |
|---|---|
| 2xx | delete the `.bundle`, mark `send.json` `Sent` |
| 4xx (401, 400, 409, 413) | delete the `.bundle` **and log it** — these won't succeed on retry (bad key, bad shape, already-uploaded, too large); mark `send.json` `Failed` |
| 429 / 5xx | keep the `.bundle`, append a backoff counter to the file name (`.bundle.retry3`), retry on the next drain; give up (delete) after **3 launches** worth of retries |

The recovery dialog's "Send this report" and the in-app prompt's "Send" button both go through
`Crash.Uploader.SendNow` — the synchronous twin of `Enqueue`, used when a human is watching and wants an
immediate result rather than a queued background attempt.

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

Responses: `201 {"id":"<reportId>"}` on success · `401` bad/missing ingest key · `400` malformed body or a
`summary` that fails validation · `409` duplicate `reportId` · `410` the report's `installId` was erased
(§9 — the tombstone left by `DELETE /v1/installs/:installId`) · `413` bundle over the 20 MB cap · `429`
rate-limited. The client's outbox (§6) treats `2xx` as done, any `4xx` (including `410`) as done-and-log,
`429`/`5xx` as retry-with-backoff.

Total bundle size cap is 20 MB; if a dump would push a bundle over the cap, the client drops the dump
first (`summary.dumpDropped` records that it happened) rather than failing the whole upload — a scrubbed
`report.txt` + `log-tail.txt` without a dump is still useful for grouping and, for a `Managed` crash, for
symbolicated frames.

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
`reports` (not a hand-decremented counter — same "recompute from source of truth" rule the ingest path
uses) — **deleting an issue outright once its count reaches 0** rather than leaving a zero-count husk —
then inserts a permanent row into `deleted_installs(install_id, deleted_at)`. Responds `200 {"deleted": n}`
(`n` = reports actually removed); `404` when nothing was ever stored for that id (including calling it a
second time on an id already fully erased). Accepted with **either** the `X-Wavee-Ingest` key or a
Cloudflare Access session — the one route that isn't gated by exactly one of those, since both the app and
the dashboard need to be able to trigger it. Rate-limited the same way as ingest.

Once tombstoned, `POST /v1/report` for that install id gets `410 Gone` (§7) instead of silently being
re-admitted — this is what makes rotating the install id after a successful delete matter: without a fresh
id, an `Auto`-mode report from a later crash on the same PC would just bounce off the tombstone forever.

Deletion here is immediate and explicit; it exists alongside, not instead of, the **90-day automatic
retention** every report gets regardless (§2, the R2 lifecycle rule in `ops/crash/README.md`) — erasure is
for "delete this now," the lifecycle rule is the backstop for everyone who never asks.
