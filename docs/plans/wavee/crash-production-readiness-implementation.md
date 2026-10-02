# Crash reporting — production readiness (#165)

## Context

Wavee's opt-in crash pipeline went live on 2026-10-02: the Cloudflare Worker and dashboard at `https://crash.cproducts.dev`,
Access team `cproducts`, D1 + R2, and verified symbolication for 1.2.1011.0 arm64. The service works, but the path a
**real end user's crash** takes has never run end to end, and research found blocking gaps on every leg:

- **Shipping.** Store builds (the main channel; Partner Center's 4.76 % crash rate is on store quad 1.2.1011.0) are
  packed without the ingest URL/key and never upload symbols (`wavee-store-submit.ps1:699-717`). The feed release
  hard-fails preflight on a global `wrangler` that doesn't exist (`wavee-release.ps1:603-610`). The ingest gate only
  *warns*, and only for `stable` (`Wavee.Release.psm1:442-466`).
- **Client.**
  - `Uploader.Configured` checks the URL only; an empty quad → the Worker returns 400 → the report is dropped forever.
  - A 409 (duplicate) is marked Failed.
  - The outbox never drains at launch.
  - Automatic + offline never queues.
  - The "sent" toast fires before any HTTP request.
  - Deleted reports still upload, and their folder gets recreated.
  - The 30 s timeout is too short for 20 MB dumps.
  - Native dumps lose the exception record: `MINIDUMP_EXCEPTION_INFORMATION` lacks `Pack = 4`.
  - Native crashes and hangs carry no frames.
  - The native headline prints the wrong address.
- **Service.**
  - PRIVACY.md promises 90-day deletion, but only the R2 files expire; D1 rows (install id, last route,
    exception message, frames) live forever.
  - Grouping/titles use the top frame, usually a .NET ThrowHelper. All Native, all Hang and all ExitCode reports each
    collapse into one issue.
  - No alerts.
  - "Delete report" is a stub.
  - The Issue detail Log tail tab is always empty.
- **Docs/legal.**
  - PRIVACY.md mode names, retention, hostname and per-report Copy/Delete don't match the app.
  - The crash issue template has stale paths and no report id.
  - The crash strings exist only in en-US (none in nl/ko-KR).
  - The pipeline has no CHANGELOG entry, so the release `issue refs` gate would refuse it.

**Outcome:** a crash on any shipped channel arrives scrubbed, symbolicated and correctly grouped. You get a Discord
ping for new issues and real regressions. The user sees truthful send states and can copy, delete or erase. Data
expires as PRIVACY.md promises. Every gate is green.

## Owner decisions (2026-10-02)

| Topic | Decision |
|---|---|
| Retention | Daily purge of `reports` rows + their R2 parts older than 90 days. **Issues keep lifetime stats/status** (counts become incremental). Erasure keeps recompute-or-delete. |
| Alerts | Discord webhook (secret `DISCORD_WEBHOOK_URL`; unset → no-op) on **new issue** and **regression**. |
| Regression | Resolving records the newest semver seen. Only a report from a **newer semver** reopens it and alerts. |
| Grouping | Native: faulting module + offset + Wavee stack (captured in the VEH). ExitCode: by code. Hang: one bucket. Server fingerprint/title skip framework frames. |
| Hard gate | `stable`, `store`, `beta` fail without URL + key; only `dev` may ship unstamped. |
| Key source | `op read "op://Personal/Wavee crash ingest key/credential" --account my.1password.eu`, unless `-CrashIngestKey` is passed. |
| Translation | ~110 crash/consent strings → nl + ko-KR via `ops/loc` (LocPack). |
| GitHub | **One umbrella issue** "Crash reporting production readiness" (#165). Every CHANGELOG bullet ends ` (#165)`; every commit body has `Fixes #165`. |

## Execution model (house rules)

- **Step 1 of execution:** copy this plan, expanded with the full code below, to
  `docs/plans/wavee/crash-production-readiness-implementation.md`.
- Implementation is done by parallel **Opus** subagents on disjoint files. Only the orchestrator builds, tests,
  deploys and launches.
- Gates run once after the implementation waves (no checkpoint builds).
- Every account-changing step needs explicit approval at the time: `gh`, wrangler deploy/secret/migration, R2 writes,
  1Password, commits. Approving this plan does not approve those.
- No source-text tests. No env-var switches. No legacy paths.

```
Wave 0  orchestrator: commit the uncommitted hosting work (approval) · `git rm -r --cached ops/crash/worker/node_modules`
        (approval) · gh issue #165 (approval) · plan doc
Wave 1  parallel: W1 Worker · W2 Dashboard · W3a App native/bundle core · W3b App upload/UI/strings · W4 Release · W5 Docs
Wave 2  orchestrator: all gates → CrashContractTests writes *.actual → copy to ops/crash/contract/ → re-run C# + vitest
Wave 3  W6 translations (needs final en-US) → LocPack draft → owner review → qa/import/lint
Wave 4  deploy runbook → E2E verification → release rehearsals → commits (Fixes #165)
```

**Couplings, fixed by contract:**
- The summary gains `exceptionCode`, `faultModule`, `faultOffset` (C# record trailing optional params; Worker optional fields).
- W3b provides `Uploader.ForgetBundle(dir)`, which W3a's `Host.Delete` calls.
- W3a provides `ConsentPolicy.Effective` / `Offered`, which W3b uses.
- W2 codes against W1's wire shape.

---

## W1 · Worker — `ops/crash/worker/**`, `ops/crash/README.md`, `ops/crash/contract/README.md`, `.gitattributes`

**`migrations/0001_retention_alerts_grouping_v2.sql`** (new; `schema.sql` stays the frozen baseline;
`wrangler.toml` `[[d1_databases]]` gets `migrations_dir = "migrations"`):

```sql
ALTER TABLE reports ADD COLUMN exception_code INTEGER NOT NULL DEFAULT 0;
ALTER TABLE reports ADD COLUMN fault_module  TEXT    NOT NULL DEFAULT '';
ALTER TABLE reports ADD COLUMN fault_offset  INTEGER NOT NULL DEFAULT 0;
ALTER TABLE reports ADD COLUMN fp_version    INTEGER NOT NULL DEFAULT 1;
ALTER TABLE issues  ADD COLUMN regressed_at TEXT;
ALTER TABLE issues  ADD COLUMN regressions  INTEGER NOT NULL DEFAULT 0;
ALTER TABLE issues  ADD COLUMN resolved_at  TEXT;
ALTER TABLE issues  ADD COLUMN resolved_version TEXT;          -- newest semver seen when resolved (regression rule)
ALTER TABLE issues  ADD COLUMN last_frames_json TEXT;          -- stack outlives the 90-day purge
ALTER TABLE issues  ADD COLUMN fp_version   INTEGER NOT NULL DEFAULT 1;
CREATE INDEX reports_received ON reports(received_at);
CREATE INDEX reports_install  ON reports(install_id);
```

**`wrangler.toml`**: `[triggers] crons = ["17 3 * * *"]`. `DISCORD_WEBHOOK_URL` is a secret only, never a `[vars]` entry.

**`src/grouping.ts`** (new; **replaces** `fingerprint.ts` and `deriveTitle`):
- `FINGERPRINT_VERSION = 2`.
- `normalizeSymbol` strips `unbox_` and collapses `DisplayClass\d+_\d+`, `_b__\d+(_\d+)?`, `_d__\d+` and local-function
  ordinals. The vectors are real names from 1.2.1011.0 `Wavee.map.xml`.
- `FRAMEWORK_FRAME_PATTERNS`: `^S_P_[A-Za-z]+_`, `^System_`, `^Microsoft_`, `^Internal_`,
  `^(SQLitePCL|Google_Protobuf|NLayer|ZstdSharp)_`, `^__`, `^Rhp?[A-Z]`, `^Pal[A-Z]`, `::`, `^(WKS|SVR)_`, and the
  memset/RaiseException/KiUserExceptionDispatcher family. `Wavee_*` and `FluentGpu_*` are app frames.
- `topFrameKey`: the top 3 app frames, normalized; all-framework → top 3 raw; nothing resolved → RVAs.
- `fingerprintKey` by kind:

  | Kind | Key |
  |---|---|
  | Hang | `v2\|Hang` |
  | UncleanExit | `v2\|UncleanExit` |
  | ExitCode | `v2\|ExitCode\|<hex8>` |
  | Native | `v2\|Native\|<code>\|<module>\|<topFrames>`; falls back to `<module>+<offset>` when there are no frames |
  | Managed | `v2\|Managed\|<exceptionType>\|<topFrames>` |

- `deriveTitle`:
  - `Hang (UI stopped responding)`
  - `Exit code <STATUS_NAME>`, from an NTSTATUS name table (0xC0000005 ACCESS_VIOLATION, 0xC0000409 fail-fast, …)
  - `<CODE> in <module> · <first Wavee frame>`
  - `<ExceptionType> · <first Wavee frame>`

**`src/validate.ts` / `src/types.ts`**:
- `exceptionCode` (uint32).
- `faultModule`, sanitized to a lower-case base name `^[a-z0-9][a-z0-9._-]{0,63}$`.
- `faultOffset` ≥ 0.
- `version` must match `^[0-9A-Za-z.+-]{1,64}$`, because it becomes a JSON path key.
- `rvas` capped at 64.
- `Env.DISCORD_WEBHOOK_URL?`.

**`src/store.ts`**:
- `upsertIssueFromReports` is deleted.
- `insertReport` takes server-derived `hasDump`/`dumpBytes` (from the actual dump part) plus the 4 new columns.
- New methods: `installSeenForIssue`, `getReportRef`, `deleteReport`, `listExpiredReportRefs(cutoff, limit)` and
  `deleteExpiredReports(cutoff, limit)`. The latter uses `DELETE … WHERE id IN (SELECT … ORDER BY received_at, id LIMIT ?2)`
  because D1 allows at most 100 bound params.
- `recordIssueOccurrence(o) → "new" | "regressed" | "existing"`:

```ts
const ins = await db.prepare(`INSERT INTO issues(fingerprint,title,kind,first_seen,last_seen,count,installs,versions_json,
  status,github_issue,last_frames_json,fp_version) VALUES (?1,?2,?3,?4,?5,1,1,?6,'open',NULL,?7,?8)
  ON CONFLICT(fingerprint) DO NOTHING`).bind(fp, title, kind, at, at, JSON.stringify({[semver]:1}), framesJson, 2).run();
if (ins.meta.changes === 1) return "new";
await db.prepare(`UPDATE issues SET count=count+1, installs=installs+?1, last_seen=?2, last_frames_json=?3,
  versions_json=json_set(COALESCE(NULLIF(versions_json,''),'{}'),'$."'||?4||'"',
    COALESCE(json_extract(NULLIF(versions_json,''),'$."'||?5||'"'),0)+1) WHERE fingerprint=?6`)
  .bind(newInstall?1:0, at, framesJson, semver, semver, fp).run();
const cur = await getIssue(fp);                      // regression rule: only a NEWER semver reopens
if (cur?.status !== "resolved" || !isNewerSemver(semver, cur.resolved_version ?? "")) return "existing";
const re = await db.prepare(`UPDATE issues SET status='open', regressed_at=?1, regressions=regressions+1
  WHERE fingerprint=?2 AND status='resolved'`).bind(at, fp).run();   // atomic: one request alerts
return re.meta.changes === 1 ? "regressed" : "existing";
```

- `patchIssue` handles the transition to `resolved`: it sets `resolved_at`, and sets `resolved_version` to the max
  semver in `versions_json` (pure `maxSemver`/`isNewerSemver` in `src/semver.ts`; prerelease < release).
- Status must be `open`, `resolved` or `ignored`; anything else → 400.

**`src/retention.ts`** (new): `purgeExpiredReports(store, bucket, nowMs, {batch:200, maxBatches:10})`.
- It is driven from D1. Per batch it lists refs, runs one `bucket.delete([...4 parts each])` (800 keys ≤ 1000), then
  deletes the rows.
- That is ≤ 30 subrequests per run (free-plan limit is 50).
- Issues are untouched. It logs counts only.

**`src/alerts.ts`** (new):
- `buildDiscordPayload(e)` builds an embed with the title, a dashboard link `/issues/<fp>`, kind, version (quad, arch),
  channel, counts and an optional GitHub #.
- It never includes the message, ids or log; it sets `allowed_mentions.parse: []` and strips markdown/control characters.
- `postDiscord` only posts when the URL matches `^https://(ptb\.|canary\.)?discord(app)?\.com/api/webhooks/\d+/[\w-]+$`,
  with a 5 s timeout.
- `discordSink(env, ctx)` posts via `ctx.waitUntil`.
- Alerts fire only when the build's symmap exists (`debugId !== null`). The public ingest key therefore can't spam
  Discord with made-up quads.

**`src/index.ts`**:
- `route(request, env, store, deps: {alerts, now})`.
- `handleReport` computes `hasDump` from the part, symbolicates, groups (v2), inserts, records the occurrence and alerts
  on new/regressed.
- New Access-gated routes:
  - `DELETE /v1/reports/:id` → R2 parts + row + `recomputeOrDeleteIssue` → `200 {deleted:1, fingerprint}`.
  - `POST /v1/retention/run` → `{deleted, batches, more}`.
- `handleIssueDetail` falls back to `last_frames_json`.
- `export default { fetch(req, env, ctx?), scheduled(c, env, ctx) { ctx.waitUntil(runRetention(env, c.scheduledTime)) } }`.

**`src/symbolicate.ts`**: cache a missing map for at most 5 min (today a report that arrives before its map stays
unresolved for the isolate's lifetime).

**Tests**:
- New `test/helpers.ts` (shared `makeEnv`/`makeSummary`/`ingest`/recording `AlertSink`).
- `fixtures.ts`: the D1 double applies `schema.sql` + `migrations/*.sql`; `batch()` uses `run()` for non-SELECT statements.

| File | What it covers |
|---|---|
| `test/grouping.test.ts` (replaces `fingerprint.test.ts`) | Normalization vectors. Classifier table. ThrowHelper-only difference → same fingerprint. Lambda ordinals → same. ExitCode by code. Hang ignores frames. Native module+frames, then the offset fallback. RVA fallback. `v2\|` prefix. Titles for all 5 kinds. |
| `test/retention.test.ts` | Backdated rows + parts purged; 89-day rows kept; issue stats untouched; batching/`more`; `scheduled()` via captured `waitUntil`; the route needs Access. |
| `test/alerts.test.ts` | Payload has no PII. Sink gets `new` once. Resolve then report from the **same** version → no reopen; **newer** version → `regressed`, status `open`, `regressions` 1. `ignored` never reopens. No symmap → no alert. Bad URL → skipped. |
| `test/index.test.ts` | `has_dump` derived from the part. Fault fields stored and sanitized. DELETE report 401/404/200 + issue recompute. PATCH bad status → 400. |
| `test/contract.test.ts` | POSTs `ops/crash/contract/{managed,native}.multipart` (real C# client bytes) → 201, plus row assertions. |
| `test/symbolicate.test.ts` | The negative-cache TTL. |

**Docs**:
- `.gitattributes`: `ops/crash/contract/*.multipart binary`.
- `ops/crash/README.md`: migrations, cron, the secret, the new routes, the R2 rule as backstop.

## W2 · Dashboard — `ops/crash/dashboard/src/**`, `ops/crash/dashboard/README.md`

- **Data layer**: `api/types.ts` and `mapping.ts` get the new fields (`exceptionCode`, `faultModule`, `faultOffset`,
  `regressedAt`, `regressions`, `resolvedAt`, `resolvedVersion`, `lastFrames`).
- **Hooks**:
  - `useDeleteReport` becomes a real `DELETE /v1/reports/:id` and invalidates reports, issues, issue(fp), stats and report(id).
  - New `useRunRetention`.
  - `useReportPartText` moves to `api/hooks.ts` (its mock text to `api/mock.ts`).
- **Issue detail**:
  - Delete `HAS_LOG_TAIL`. The Log tail shows the newest report's tail; when no reports remain it shows an empty
    state: "Reports older than 90 days are deleted; this issue's counts are kept".
  - Stack falls back to `lastFrames`.
  - "Regressed" badge.
  - "Resolved in ≤ <version>" note.
  - Native facts: `<CODE> in <module>+0x<off>`.
- **Other pages**:
  - Issues list: regressed badge.
  - Report detail: native facts; working Delete.
  - Overview: "Run retention now" command (confirm + toast).
  - Reports search strips `-`, so the app's `3f9c-2b1a` id matches.
- **Tests**: mapping, IssueDetail (tail / regressed / purged), ReportDetail (DELETE + navigate), Overview (retention).

## W3a · App native capture + bundle core

Files: `Crash.cs`, `Crash.NativeHook.cs`, `Crash.Handler.cs`, `Crash.Host.cs`, `Crash.Report.cs`, `Crash.Bundles.cs`,
new `Crash.Native.cs`; tests in `CrashCoreTests.cs` and new `CrashNativeTests.cs`.

**Capture approach.** An allocation-free `RtlCaptureStackBackTrace` into a `NativeMemory` buffer inside the VEH, run
**first**, before anything that allocates. The faulting module is resolved **in the child** via `ReadProcessMemory`
(EXCEPTION_POINTERS → EXCEPTION_RECORD.ExceptionAddress) + `K32EnumProcessModulesEx` / `K32GetModuleInformation` /
`K32GetModuleBaseNameW`. The child already holds QUERY|VM_READ, and nothing takes the loader lock in the crashing
process. NativeAOT code has unwind data, so the OS unwinder walks through `KiUserExceptionDispatcher` on x64 and ARM64.

- **`Crash.Native.cs`** (pure):
  - `NativeFrames.SelectAppRvas(frames, exceptionAddress, imageBase, imageSize)` starts at the exception-address frame.
    If that frame is missing, it skips the handler's own leading Wavee frames. It keeps in-image frames, at most 32.
  - `FaultModule.Find(ranges, addr)`.
  - `FaultModule.Normalize`: base name, lower-case, `[a-z0-9._-]` ≤ 64, else `""` (mirrors the Worker).
- **`Crash.NativeHook.cs`**: `CaptureFaultStack(info)` records `s_faultAddress` + `s_frameCount`; exposes
  `FaultAddress` and `FaultRvas()`.
- **`Crash.Host.cs`**:
  - `RequestDumpCore` puts `rvas: FaultRvas()` and `exceptionCode: code` in the summary.
  - Headline fix: `native fault 0x{code:x8} at 0x{FaultAddress:x}` (it currently prints the pointer address).
  - `report.txt` gains a "Frames (RVA)" section via `Report.AppendRvaSection`.
  - The child finalizes the summary; the parent's post-OK rewrites are dropped.
  - `Delete(dir)` calls `Uploader.ForgetBundle(dir)` first.
- **`Crash.Handler.cs`**:
  - `[StructLayout(LayoutKind.Sequential, Pack = 4)] MINIDUMP_EXCEPTION_INFORMATION`, plus a public
    `ExceptionInfoLayout()` probe for the test (expects 16 / 4 / 12).
  - `HandleDumpRequest` order: `TryReadFault(ptrs)` while the parent is still parked → write the dump →
    `Bundles.UpdateSummary(dir, s => s with { HasDump, DumpBytes, FaultModule, FaultOffset })` → append `fault=<m>+0x<off>`.
- **`Crash.Bundles.cs`**: `WriteSend` never recreates a missing folder; new `ReadSummary` / `UpdateSummary` /
  `AppendReportLine`.
- **`Crash.Report.cs`**: `BuildSummary(..., long[]? rvas, uint exceptionCode)`. `Unwrap` peels `AggregateException`
  (single inner), `TargetInvocationException` and `TypeInitializationException` for the type.
- **`Crash.cs`**:
  - The 3 new `Summary` params (trailing, defaulted, so old `summary.json` still reads).
  - `ConsentPolicy.Offered(configured)`: Off/Ask/Auto, or Off/Ask on a build that can't send.
  - `ConsentPolicy.Effective(mode, configured)`: Auto → Ask when the build can't send.
- **Tests**:
  - Layout 16/4/12 (64-bit).
  - `SelectAppRvas`: start, fallback, filtering, cap, all-foreign.
  - `FaultModule` Find/Normalize.
  - An old summary deserializes; `ExceptionCode` serializes as a number.
  - `Unwrap`; `Effective` / `Offered`.
  - `WriteSend` never recreates a deleted bundle; `UpdateSummary` round trip (temp dirs).

## W3b · App upload, UI, strings

Files: `Crash.Upload.cs`, `Crash.Scrub.cs`, `Platform.Settings.cs` (`WaveeVersionInfo` only), `Crash.UI.cs`,
`Setup.UI.cs` (`CrashConsentCard` only), `Feedback.cs`, `Feedback.UI.cs`, `assets/loc/en-US.json`; tests
`CrashBundleTests.cs`, `FeedbackTests.cs`, new `CrashUploadPolicyTests.cs`, new `CrashContractTests.cs`, and
`Wavee.Tests.csproj`.

- **`WaveeVersionInfo.CrashReportingAvailable`** = URL && key && quad non-empty. `Uploader.Configured` uses it.
- **Pure `UploadPolicy`** (testable; replaces the private inline logic):

```csharp
public enum UploadOutcome : byte { Sent, AlreadySent, Rejected, Erased, RetryServer, RetryNetwork }
public static UploadOutcome Classify(int? http) => http switch { null => RetryNetwork, >= 200 and < 300 => Sent,
    409 => AlreadySent, 410 => Erased, 429 or >= 500 => RetryServer, _ => Rejected };
public static Step Next(UploadOutcome o, int serverRetryLaunches, string status) => o switch {
    Sent or AlreadySent => new(true, SendState.Sent, null), Erased => new(true, SendState.Failed, "install erased"),
    Rejected => new(true, SendState.Failed, status),
    RetryServer when serverRetryLaunches >= 3 => new(true, SendState.Failed, "gave up"),
    RetryServer => new(false, SendState.Queued, status), _ => new(false, SendState.Queued, "offline") };  // network never gives up
public static bool Due(int serverAttempts, DateTime? lastTry, DateTime now) => /* 2^n min, cap 60 */;
public static TimeSpan Timeout(long bodyBytes) => TimeSpan.FromSeconds(Math.Clamp(30 + bodyBytes / 50_000, 30, 600));
```

- **Uploader changes**:
  - `OutboxMeta` and `Bundle.BoundaryFor` become public.
  - `HttpClient.Timeout` is infinite; each request is timed by `UploadPolicy.Timeout`.
  - `Enqueue(..., Action<SendRecord>? settled)`.
  - Drain re-loops on `s_drainRequested`, drops orphans, and goes Classify → Next → Settle.
  - `SendNow` removes that report's outbox entry on a final outcome.
  - `Install` ends with `if (Configured) Drain();`, the launch drain.
  - New `ForgetBundle(dir)`.
- **`Crash.Scrub.cs`**: `FaultModule` goes through the redactor.
- **`Crash.UI.cs`**:
  - Effect A uses `Effective(mode, Configured)`.
  - Automatic + offline **enqueues** and shows the queued toast.
  - Sent/failed toasts fire on the real outcome; `crash.sendFailed` gets used; all share the dedupe key `crash:<id>`.
  - Mode combo: Automatic disabled with the description `modeAutoNeedsService` on a build that can't send.
  - Reports rows: `[View] [Copy] [Delete] [Send]` plus the short id. Send opens the prompt (manual send with preview +
    dump checkbox). Copy puts the scrubbed preview on the clipboard. Delete confirms, then `Host.Delete`.
- **`Setup.UI.cs`**: the consent card offers `ConsentPolicy.Offered(Configured)`.
- **`Feedback`**: `ReportPrefill.ReportId`; the crash form field `report-id` is prefilled with `ShortId`.
- **`en-US.json`**: `crash.rowCopy`, `rowCopied`, `rowDelete`, `rowDeleteConfirmTitle`, `rowDeleteConfirmBody`,
  `rowId`, `modeAutoNeedsService`, `promptListTitle`; reworded `deleteAllConfirmBody` (90 days).
- **Contract test**:
  - `Wavee.Tests.csproj` links `ops/crash/contract/*.multipart` as `Fixtures/crash-contract/`.
  - `CrashContractTests` packs fixed managed/native bundles (a 64-byte `MDMP` dump for native) and asserts the bytes
    match the fixture.
  - On a mismatch it writes `crash-contract-actual/<name>.multipart` and fails with copy instructions.
- **Tests**:
  - The `Classify` / `Next` / `Due` / `Timeout` / `IsOrphan` tables.
  - Toast choice.
  - `OutboxMeta` round trip.
  - `CrashReportingAvailable` needs all three.
  - `Crash_url_carries_report_id`.

## W4 · Release tooling

Files: `ops/release/Wavee.Release.psm1`, `wavee-release.ps1`, `wavee-store-submit.ps1`,
`tests/Wavee.Release.Tests.ps1`, `ops/build/publish-wavee-aot.ps1`.

**`Wavee.Release.psm1`**:
- `$script:CrashDefaults = @{ Url='https://crash.cproducts.dev'; KeyRef='op://Personal/Wavee crash ingest key/credential'; OpAccount='my.1password.eu' }`.
- `Get-CrashIngestGate -Channel -CrashIngestUrl -CrashIngestKey` returns `{Fail; Message}`. It fails for
  stable/beta/store when the URL is empty or non-https, or the key is empty. The message never contains the key.
- `Resolve-CrashIngestKey [-Explicit]`:
  - `-Explicit` wins; otherwise `Invoke-Native 'op' @('read', $ref, '--account', $acct)`.
  - It takes the last line matching `^[A-Za-z0-9_\-\.=+/]{16,256}$`.
  - It throws without echoing the output.
  - It returns `{Key; Source}`.
- `Get-WranglerPath -RepoRoot` → `ops\crash\worker\node_modules\.bin\wrangler.cmd`; throws with an `npm --prefix ops/crash/worker ci` hint.
- `Test-WranglerLogin -ExitCode -Output` (pure) parses `wrangler whoami`.
- `Find-ByteText` (Latin-1 ordinal IndexOf, UTF-8 + UTF-16LE).
- `Assert-CrashIngestStamp -Msix|-ExePath -Url -Key`: the exe must contain both the URL and the key. It never prints the key.
- `Invoke-SymbolsUpload` gains a mandatory `-Wrangler` path (still `--remote`).
- `Publish-WaveeSymbolMap -Msix|-ExePath -SymbolsDir [-SymbolsZip] -Quad -Arch -ReleaseToolProject -Wrangler [-SkipUpload]`:
  extract the exe, `dotnet run Wavee.ReleaseTool symbol-map`, upload, clean up the temp exe. Shared by both scripts.

**`wavee-release.ps1`**:
- `$CrashIngestUrl` defaults to `'https://crash.cproducts.dev'`; `$CrashIngestKey = ''` means 1Password.
- `Get-CrashKey` caches the key per process, so it also works on `-Resume`; the key is never written to `release-state.json`.
- Preflight:
  - `wrangler (crash symbols)` is hard: `Get-WranglerPath` + `whoami`, skipped under `-DryRun`/`-NoUpload`.
  - `crash ingest` becomes **hard**.
- `Invoke-Pack` passes the resolved key, then runs `Assert-CrashIngestStamp` per arch (also for an adopted `-X64Msix`).
- `Invoke-Symbols` becomes a `Publish-WaveeSymbolMap` loop.

**`wavee-store-submit.ps1`**:
- The same params, preflight checks and `Get-CrashKey`.
- `Invoke-StorePack` stamps and asserts (including `-PackageDir` / `-X64Msix` adoption).
- **New `symbols` phase** after pack, before `msixupload`: `Publish-WaveeSymbolMap -Quad $storeQuad`, skipping the
  upload under `-DryRun`.
- The header's phase table is updated.

**`publish-wavee-aot.ps1`**: `-Quad -CrashIngestUrl -CrashIngestKey -OutDir` for verify builds (ASCII-only file).

**Pester**:
- Gate matrix (incl. beta, non-https, whitespace, key never in the message).
- `Resolve-CrashIngestKey`: explicit wins (op is never called), op argument shape, last-line pick, failure without echo.
- `Get-WranglerPath`; `Test-WranglerLogin`.
- `Find-ByteText` / `Assert-CrashIngestStamp` against a temp msix zip.
- `Invoke-SymbolsUpload` uses `-Wrangler`.
- `Publish-WaveeSymbolMap` with mocked dotnet: argument list, temp exe removed, `-SkipUpload`, zip expand.
- **Delete** the source-text test "wires -CrashIngestUrl/-CrashIngestKey". The stamp evidence proves the wiring instead.

## W5 · Docs, privacy, GitHub template, CHANGELOG

- **`PRIVACY.md`**:
  - Mode names match the UI: Off / Ask each time / Automatic. The first-run wording maps to these, and Automatic is
    only offered by builds that can send.
  - Name `https://crash.cproducts.dev` (Cloudflare Worker + D1 + R2).
  - Native fault module name + offset (may reveal installed software such as a driver).
  - Retention: rows and files are deleted after 90 days by a daily job, with the R2 rule as a backstop. Issue
    statistics are kept: counts, versions, first/last seen and the latest Wavee code location — never contents.
  - Discord alert contents (no message, ids or log).
  - Per-report Copy/Delete.
  - The erasure tombstone.
- **`docs/guide/crash-diagnostics.md`**:
  - Fix the stale lines 102, 156-157, 166, 168-170 and 202-203: offline Automatic queues; launch drain; the
    `.meta`-sidecar backoff table with network-never-gives-up; the prompt enqueues while Recovery uses `SendNow`;
    409 = sent; no `dumpDropped`; the server derives `has_dump`.
  - Add sections: grouping v2, native fault fields, retention, alerts/regressions (newer-version rule), delete report.
- **`.github/ISSUE_TEMPLATE/crash_report.yml`**: a `report-id` input; correct paths (Settings › Logs › Reports;
  `logs\crash\<stamp>-<kind>\report.txt`).
- **`CHANGELOG.md` [Unreleased]**:
  - Added: opt-in crash reporting with recovery mode, consent, per-report Copy/Delete, native module + stack,
    store/stable/beta builds report and resolve, nl/ko strings.
  - Fixed: the uploader bugs.
  - Every bullet ends ` (#165)`.
- **`docs/guide/releasing-wavee.md` §5b, `docs/guide/microsoft-store-onboarding.md`, `.claude/skills/releasing/SKILL.md`**:
  - wrangler comes from the Worker and needs `whoami`.
  - The key comes from 1Password.
  - The gate is hard.
  - Stamp evidence runs after packing.
  - The store `symbols` phase.

## W6 · Translations (after W3b's en-US is final)

1. **Pack (orchestrator):** `dotnet run --project ops/loc/Wavee.LocPack -- pack --culture nl|ko-KR --packet crash-consent`
   with prefixes `crash`, the `setup.terms.crash*` keys, `setup.terms.privacyLink`, `report.crash*` and
   `settings.about.privacyPolicy`.
2. **Draft (agent):**
   - Writes `ops/loc/work/{nl,ko-KR}/crash-consent.drafts.json`. Dutch uses informal "je"; Korean uses polite 해요/합니다
     as in `ko-KR.json`; `{placeholders}` stay verbatim.
   - Adds a `crash` surface to `ops/loc/surfaces.json`.
   - Adds "crash report", "install id" and "memory snapshot" to `glossary.json`.
3. **Orchestrator:** `draft --from` → owner review in `ops/loc/review/index.html` → `qa` → `import` → `lint` → both test suites.

---

## Deployment runbook (Wave 4 — each command marked ⚑ needs approval)

```powershell
cd C:\wavee\waveemusic\ops\crash\worker
npx wrangler d1 migrations list  wavee-crash --remote        # 0001 pending
npx wrangler d1 migrations apply wavee-crash --remote        # ⚑ backward compatible with the live code
npx wrangler secret put DISCORD_WEBHOOK_URL                  # ⚑ you create the webhook (Discord channel → Integrations); also save to 1Password ⚑
npm run deploy                                               # ⚑ output shows schedule "17 3 * * *"
# smoke: POST wrong key → JSON 401; DELETE /v1/reports/x → 302 to Access
$key = op read "op://Personal/Wavee crash ingest key/credential" --account my.1password.eu
foreach ($id in '3821fb615dad14bf9627697c797e91d8','5dfbb2678df2fb42d7fa77ad37a8abb2') {           # ⚑ remove the 2 synthetic reports
  curl.exe -s -X DELETE "https://crash.cproducts.dev/v1/installs/$id" -H "X-Wavee-Ingest: $key" }
# contract smoke on prod ⚑: POST ops/crash/contract/managed.multipart → 201; DELETE its install → 200; re-POST → 410
```

## Verification

**Gates.** The user's Debug Wavee locks `bin\Debug`, so Debug builds/tests use `--artifacts-path artifacts\gates\debug`.
- `dotnet build Wavee.slnx` Debug + Release (TreatWarningsAsErrors).
- `dotnet test src/apps/Wavee.Tests` Debug + Release.
- Worker `npm test`, `typecheck`, `typecheck:test`.
- Dashboard `npm test`, `npm run build`.
- `Invoke-Pester ops/release/tests`.
- `LocPack lint`.

**Real client → service run.**
- Setup:
  - A NativeAOT verify build: `publish-wavee-aot.ps1 -Symbols -Quad 0.0.1.0 -CrashIngestUrl … -CrashIngestKey $key -OutDir artifacts\crash-verify\win-arm64`.
  - `Assert-CrashIngestStamp -ExePath …`.
  - `Publish-WaveeSymbolMap -ExePath … -Quad 0.0.1.0` ⚑ (R2).
  - Run with `--profile $env:TEMP\wavee-crash-verify`, which runs beside the user's Wavee; don't use `--fake`,
    because that forces in-memory settings.

| # | Steps | Pass |
|---|---|---|
| 1 | Ask mode, `--crash-probe throw`, relaunch, Send | Toast only after `upload.sent … status=HTTP 201`. Title `InvalidOperationException · Wavee_…` (not ThrowHelper). Frames resolved. **Discord "New crash issue"**. A repeat gives count 2 and no new alert. |
| 2 | `--crash-probe native`, Send with dump | `ACCESS_VIOLATION in ntdll.dll · Wavee_…`. Fault module/offset/code shown, `has_dump` 1. `cdb -z dmp -c ".ecxr;k;q"` shows the exception context. No `retry=noexception` in `handler.log`. |
| 3 | `--crash-probe hang` ×2 | One "Hang" issue, count 2. |
| 4 | `--crash-probe failfast` | Grouped by exit code with its NTSTATUS name. |
| 5 | Automatic + airplane mode, probe throw, relaunch; then online. Variant: quit offline, relaunch online | Queued toast; row reads "Waiting"; then `upload.sent`, the Sent toast and row "Sent". The variant drains at launch. |
| 6 | Reset a sent bundle's `send.json`, Send again | Server 409 → row "Sent"; outbox empty. |
| 7 | Row Copy / Delete / Delete all | Clipboard holds the scrubbed report; folder + outbox entry gone, never recreated. |
| 8 | Settings › Delete my data | Count toast; dashboard drops the reports; install id rotates. |
| 9 | Unstamped `dotnet run -- --profile p2` | The wizard offers 2 modes; Automatic is disabled with a description. |
| 10 | Packaged MSIX pass: pack `-Quad 0.0.2.0 -Channel dev -IdentityName cproducts.WaveeCrashVerify … -Install` → symmap ⚑ → `Invoke-CommandInDesktopPackage … --crash-probe native` → relaunch → Send | Same as #2, from LocalCache; no `%LOCALAPPDATA%\Wavee` is created. |
| 11 | Resolve the #1 issue; send another report from the **same** quad, then from a **newer** quad (0.0.1.1) | Same version → stays resolved, no alert. Newer → reopens with the "Regressed" badge and a **Discord regression** alert. |
| 12 | Retention: backdate a row via `npx wrangler d1 execute … "UPDATE reports SET received_at='2026-06-01T00:00:00.000Z' WHERE id=…"` → dashboard "Run retention now"; backdate another row and check after 03:17 UTC | `{deleted:1}`; the row and R2 parts are gone, issue count unchanged. The cron deleted the second row. |
| 13 | `wavee-release.ps1 -DryRun -SkipTests` | `crash ingest ok: stamping https://crash.cproducts.dev; key from 1Password`; a `stamp evidence` line per arch; both symbol maps built, upload skipped. |
| 14 | `wavee-store-submit.ps1 -DryRun -SkipTests` | Same, on the store quad; `symbols` phase builds `symbols\1.2.x\win-*\Wavee.symmap`. |

**Cleanup ⚑**:
- Delete my data in each verify profile.
- `wrangler r2 object delete` the 0.0.1.x / 0.0.2.0 symmaps and `DELETE FROM symbols WHERE quad LIKE '0.0.%'`.
- Uninstall `cproducts.WaveeCrashVerify`.

## Risks / notes

- **x64 under emulation on ARM64**: the walk may not hit the exception address exactly → the handler-frame skip
  fallback applies. Verify an x64 package on real x64 hardware if available.
- **First-chance AVs a driver handles itself**: the VEH may report one and latch `s_reported`. This predates this
  work; consider a follow-up that only reports from the unhandled filter.
- **Name normalization may merge two lambdas** in the same method. Accepted for stable grouping across builds.
- **Alerts** depend on a symmap; every shipping channel uploads one under the hard gate.
- **Erasure after a purge** can shrink lifetime counts to the window. Accepted (recompute semantics kept).
- **Releases now hard-require** 1Password unlocked (`op` → Windows Hello) and wrangler logged in in `ops/crash/worker`.
- **Free plan**: ≤ 5 crons per account, ≤ 50 subrequests per invocation (the purge uses ≤ 30).
- **Once on `migrations apply`**, never `execute --file` a migration again.

---

# Appendix — code shapes (the contract every work package codes against)

## A1 · Worker `src/grouping.ts` (replaces `src/fingerprint.ts` + `deriveTitle`)

```ts
import type { Frame, Kind } from "./types.js";
export const FINGERPRINT_VERSION = 2;

/** BCL, NativeAOT runtime, ILC thunks, C runtime and the NuGet libraries Wavee.csproj links. Matched against
 *  normalizeSymbol(name). App frames = everything else (Wavee_*, FluentGpu_* — the engine is ours). */
export const FRAMEWORK_FRAME_PATTERNS: readonly RegExp[] = [
  /^S_P_[A-Za-z]+_/,                                   // System.Private.* (CoreLib, Reflection.Execution, Interop, TypeLoader…)
  /^System_/,                                          // every System.* assembly
  /^Microsoft_/,                                       // Microsoft.Data.Sqlite, Microsoft.Win32.*
  /^Internal_/,                                        // Internal.Runtime.* inside CoreLib
  /^(SQLitePCL|Google_Protobuf|NLayer|ZstdSharp)_/,    // third-party packages
  /^__/,                                               // __GenericDict_*, __GetNonGCStaticBase_*, __security_check_cookie…
  /^Rhp?[A-Z]/, /^Pal[A-Z]/,                           // RhpThrowEx, RhThrowHwEx, RhpNewFast, PalRaiseFailFast…
  /::/, /^(WKS|SVR)_/,                                 // C++ runtime / GC
  /^(mem(set|cpy|move|cmp)|strlen|wcslen|RtlRaiseException|RaiseException|RaiseFailFastException|KiUserExceptionDispatcher|DebugBreak)$/,
];

/** Compiler-generated ordinals shift whenever code moves inside a method; they must not split an issue. */
export function normalizeSymbol(raw: string): string {
  return raw
    .replace(/^unbox_/, "")
    .replace(/DisplayClass\d+_\d+/g, "DisplayClass")
    .replace(/_b__\d+(?:_\d+)?/g, "_b__")
    .replace(/_d__\d+/g, "_d__")
    .replace(/(_g__[A-Za-z0-9]+)_\d+_\d+/g, "$1");
}
export function isFrameworkFrame(name: string): boolean {
  const n = normalizeSymbol(name);
  return FRAMEWORK_FRAME_PATTERNS.some((rx) => rx.test(n));
}
export interface GroupingInput {
  kind: Kind; exceptionType: string; exitCode: number;
  exceptionCode: number; faultModule: string; faultOffset: number; frames: readonly Frame[];
}
const hex8 = (n: number) => (n >>> 0).toString(16).toUpperCase().padStart(8, "0");

/** Top-3 app frames (framework skipped; all-framework → top-3 raw); RVAs when nothing resolved (no symmap). */
export function topFrameKey(frames: readonly Frame[]): string {
  if (frames.length === 0) return "";
  if (!frames.some((f) => f.name !== null)) return frames.slice(0, 3).map((f) => f.rva.toString(16)).join("|");
  const app = frames.filter((f) => f.name === null || !isFrameworkFrame(f.name));
  return (app.length > 0 ? app : frames).slice(0, 3).map((f) => (f.name === null ? "?" : normalizeSymbol(f.name))).join("|");
}
export function fingerprintKey(i: GroupingInput): string {
  switch (i.kind) {
    case "Hang": return "v2|Hang";
    case "UncleanExit": return "v2|UncleanExit";
    case "ExitCode": return `v2|ExitCode|${hex8(i.exitCode)}`;
    case "Native": {
      const top = topFrameKey(i.frames), mod = i.faultModule || "?";
      return top !== "" ? `v2|Native|${hex8(i.exceptionCode)}|${mod}|${top}`
                        : `v2|Native|${hex8(i.exceptionCode)}|${mod}+${i.faultOffset.toString(16)}`;
    }
    default: return `v2|Managed|${i.exceptionType}|${topFrameKey(i.frames)}`;
  }
}
export async function computeFingerprint(i: GroupingInput): Promise<string> { return sha1Hex(fingerprintKey(i)); }

const STATUS_NAMES: Record<number, string> = {
  0xc0000005: "ACCESS_VIOLATION", 0xc0000006: "IN_PAGE_ERROR", 0xc000001d: "ILLEGAL_INSTRUCTION",
  0xc000008c: "ARRAY_BOUNDS_EXCEEDED", 0xc0000094: "INTEGER_DIVIDE_BY_ZERO", 0xc00000fd: "STACK_OVERFLOW",
  0xc0000409: "STACK_BUFFER_OVERRUN (fail-fast)", 0xc0000374: "HEAP_CORRUPTION", 0xc0000602: "FAIL_FAST_EXCEPTION",
  0xc000041d: "FATAL_USER_CALLBACK_EXCEPTION", 0x80000003: "BREAKPOINT", 0xc0000135: "DLL_NOT_FOUND",
  0xc0000142: "DLL_INIT_FAILED", 0x40000015: "FATAL_APP_EXIT",
};
export const codeName = (c: number) => STATUS_NAMES[c >>> 0] ?? `0x${hex8(c)}`;
const firstApp = (f: readonly Frame[]) => {
  const named = f.filter((x) => x.name !== null && !isFrameworkFrame(x.name!)).map((x) => normalizeSymbol(x.name!));
  return named.find((n) => n.startsWith("Wavee_")) ?? named[0] ?? null;
};
export function deriveTitle(i: GroupingInput): string {   // insert-only on the issues row
  switch (i.kind) {
    case "Hang": return "Hang (UI stopped responding)";
    case "UncleanExit": return "Unclean exit";
    case "ExitCode": return `Exit code ${codeName(i.exitCode)}`;
    case "Native": { const top = firstApp(i.frames); const where = `${codeName(i.exceptionCode)} in ${i.faultModule || "unknown module"}`;
                     return top ? `${where} · ${top}` : `${where}+0x${i.faultOffset.toString(16)}`; }
    default: { const top = firstApp(i.frames); const head = i.exceptionType || "Managed"; return top ? `${head} · ${top}` : head; }
  }
}
```
Normalization vectors (real 1.2.1011.0 names): `Wavee_Wavee_ArtistPopular___c__DisplayClass28_0___Render_b__0_d____GetFieldHelper`,
`FluentGpu_Engine_FluentGpu_Media_PcmAudioPlayer__OpenAsync_d__24__MoveNext`,
`System_Linq_System_Linq_Enumerable___ToArray_g__EnumerableToArray_324_0`, `unbox_…`, `__GenericDict_…`.

## A2 · Worker semver, store occurrence, retention, alerts, index

```ts
// src/semver.ts — pure. Prerelease < release; numeric identifiers compared numerically; unparsable → oldest.
export function compareSemver(a: string, b: string): number;   // <0 | 0 | >0
export const isNewerSemver = (candidate: string, baseline: string) => baseline === "" ? false : compareSemver(candidate, baseline) > 0;
export function maxSemver(versions: readonly string[]): string; // "" for none
```
`isNewerSemver(x, "")` is **false**: an issue resolved before any version was recorded never auto-reopens.

- `patchIssue` → resolved: `resolved_at = nowIso`, `resolved_version = maxSemver(Object.keys(versions_json))`.
- `recordIssueOccurrence`: INSERT … ON CONFLICT DO NOTHING → "new"; else incremental UPDATE; then reopen only when
  `status='resolved' AND isNewerSemver(semver, resolved_version)`, atomically
  (`UPDATE … WHERE fingerprint=? AND status='resolved'`, changes === 1 → "regressed").

```ts
// src/retention.ts
export const RETENTION_DAYS = 90, PURGE_BATCH = 200 /* ×4 parts = 800 ≤ R2's 1000 keys/delete */, PURGE_MAX_BATCHES = 10;
export const retentionCutoffIso = (nowMs: number, days = RETENTION_DAYS) => new Date(nowMs - days * 86_400_000).toISOString();
export async function purgeExpiredReports(store: Store, bucket: R2Bucket, nowMs: number,
    o = { batch: PURGE_BATCH, maxBatches: PURGE_MAX_BATCHES }): Promise<{ deleted: number; batches: number; more: boolean }> {
  const cutoff = retentionCutoffIso(nowMs);
  let deleted = 0, batches = 0;
  while (batches < o.maxBatches) {
    const refs = await store.listExpiredReportRefs(cutoff, o.batch);
    if (refs.length === 0) return { deleted, batches, more: false };
    await bucket.delete(refs.flatMap((r) => R2_PART_FILES.map((f) => `reports/${r.quad}/${r.id}/${f}`)));
    deleted += await store.deleteExpiredReports(cutoff, refs.length);
    batches++;
    if (refs.length < o.batch) return { deleted, batches, more: false };
  }
  return { deleted, batches, more: true };
}
```

```ts
// src/alerts.ts
export interface IssueEvent { transition: "new" | "regressed"; fingerprint: string; title: string; kind: Kind;
  semver: string; quad: string; arch: string; channel: string; count: number; installs: number;
  githubIssue: number | null; dashboardOrigin: string; at: string; }
export interface AlertSink { issueEvent(e: IssueEvent): void }
const WEBHOOK_RX = /^https:\/\/(?:ptb\.|canary\.)?discord(?:app)?\.com\/api\/webhooks\/\d+\/[\w-]+$/;
const safe = (s: string, max = 200) => s.replace(/[\u0000-\u001f`*_~|@\[\]()]/g, "").slice(0, max);
export function buildDiscordPayload(e: IssueEvent) {   // never: exception message, install id, report id, log text
  const head = e.transition === "new" ? "New crash issue" : "Regression — a resolved issue is back";
  return { username: "Wavee crashes", allowed_mentions: { parse: [] as string[] },
    embeds: [{ title: safe(`${head}: ${e.title}`, 256), url: `${e.dashboardOrigin}/issues/${encodeURIComponent(e.fingerprint)}`,
      color: e.transition === "new" ? 0xd13438 : 0xf7630c,
      fields: [
        { name: "Kind", value: e.kind, inline: true },
        { name: "Version", value: safe(`${e.semver} (${e.quad}) · ${e.arch}`), inline: true },
        { name: "Channel", value: safe(e.channel || "?"), inline: true },
        { name: "Reports", value: `${e.count} from ${e.installs} install${e.installs === 1 ? "" : "s"}`, inline: true },
        ...(e.githubIssue ? [{ name: "GitHub", value: `#${e.githubIssue}`, inline: true }] : []),
      ],
      footer: { text: `fingerprint ${e.fingerprint.slice(0, 12)}` }, timestamp: e.at }] };
}
export async function postDiscord(url: string | undefined, e: IssueEvent, f: typeof fetch = fetch): Promise<"sent" | "skipped" | "failed"> {
  if (!url || !WEBHOOK_RX.test(url)) return "skipped";
  try {
    const r = await f(url, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify(buildDiscordPayload(e)), signal: AbortSignal.timeout(5000) });
    if (!r.ok) { console.warn("alerts.discord.failed", r.status); return "failed"; }
    return "sent";
  } catch (err) { console.warn("alerts.discord.failed", err instanceof Error ? err.name : "error"); return "failed"; }
}
export const discordSink = (env: Env, ctx?: ExecutionContext): AlertSink => ({
  issueEvent(e) { const p = postDiscord(env.DISCORD_WEBHOOK_URL, e); if (ctx) ctx.waitUntil(p); else void p; },
});
```

```ts
// index.ts — handleReport core (after validation / tombstone / duplicate checks)
const dumpBytes = dumpBuffer?.byteLength ?? 0, hasDump = dumpBytes > 0;          // derived, never summary.hasDump
const sym = await symbolicate(env.BUCKET, summary.quad, summary.arch, summary.rvas, deps.now());
const g: GroupingInput = { kind: summary.kind, exceptionType: summary.exceptionType, exitCode: summary.exitCode,
  exceptionCode: summary.exceptionCode, faultModule: summary.faultModule, faultOffset: summary.faultOffset, frames: sym.frames };
const fingerprint = await computeFingerprint(g), title = deriveTitle(g), framesJson = JSON.stringify(sym.frames);
const receivedAt = new Date(deps.now()).toISOString();
const newInstall = !(await store.installSeenForIssue(fingerprint, summary.installId));    // BEFORE the insert
await store.insertReport({ summary, framesJson, fingerprint, fpVersion: FINGERPRINT_VERSION, receivedAt, hasDump, dumpBytes });
const t = await store.recordIssueOccurrence({ fingerprint, title, kind: summary.kind, semver: summary.version,
  receivedAt, newInstall, framesJson, fpVersion: FINGERPRINT_VERSION });
if (t !== "existing" && sym.debugId !== null) {   // alert only for builds whose symmap we hold
  const issue = await store.getIssue(fingerprint);
  if (issue) deps.alerts.issueEvent({ transition: t, fingerprint, title: issue.title, kind: summary.kind,
    semver: summary.version, quad: summary.quad, arch: summary.arch, channel: summary.channel, count: issue.count,
    installs: issue.installs, githubIssue: issue.github_issue, dashboardOrigin: new URL(request.url).origin, at: receivedAt });
}
// export default { fetch(request, env, ctx?) → route(request, env, new D1Store(env.DB), { alerts: discordSink(env, ctx), now: Date.now }),
//                  scheduled(c, env, ctx) { ctx.waitUntil(runRetention(env, c.scheduledTime)) } }
// export async function runRetention(env, nowMs) { const r = await purgeExpiredReports(new D1Store(env.DB), env.BUCKET, nowMs);
//   console.log(JSON.stringify({ event: "retention.purge", ...r })); return r; }
```

## A3 · App native capture (W3a)

```csharp
// Platform/Crash.Native.cs — pure
public static partial class Crash
{
    public static class NativeFrames
    {
        public const int MaxCapture = 62, MaxRvas = 32;
        public static long[] SelectAppRvas(ReadOnlySpan<nint> frames, nint exceptionAddress, nint imageBase, nuint imageSize, int max = MaxRvas)
        {
            bool In(nint a) => a >= imageBase && (nuint)(a - imageBase) < imageSize;
            int start = frames.IndexOf(exceptionAddress);
            if (start < 0) { start = 0; while (start < frames.Length && In(frames[start])) start++; }
            var list = new List<long>(max);
            for (int i = start; i < frames.Length && list.Count < max; i++)
                if (In(frames[i])) list.Add((long)(frames[i] - imageBase));
            return list.ToArray();
        }
    }
    public static class FaultModule
    {
        public static int Find(ReadOnlySpan<(nint Base, uint Size)> modules, nint address)
        { for (int i = 0; i < modules.Length; i++) if (address >= modules[i].Base && (nuint)(address - modules[i].Base) < modules[i].Size) return i; return -1; }
        /// Base name only, lower-case, [a-z0-9._-], ≤ 64 chars; anything else → "" (mirrors the Worker's sanitizer).
        public static string Normalize(string? raw);
    }
}
```
```csharp
// Crash.NativeHook.cs — VEH: capture FIRST, allocation-free
static unsafe nint* s_frames;            // NativeMemory.AllocZeroed(MaxCapture * sizeof(nint)) in Install
static int s_frameCount; static nint s_faultAddress;
static unsafe void CaptureFaultStack(EXCEPTION_POINTERS* info)
{
    s_faultAddress = ((EXCEPTION_RECORD*)info->ExceptionRecord)->ExceptionAddress;
    if (s_frames != null) s_frameCount = RtlCaptureStackBackTrace(0, (uint)NativeFrames.MaxCapture, s_frames, null);
}
public static nint FaultAddress => s_faultAddress;
public static unsafe long[] FaultRvas() => NativeFrames.SelectAppRvas(new ReadOnlySpan<nint>(s_frames, s_frameCount), s_faultAddress, s_imageBase, s_imageSize);
[LibraryImport("ntdll.dll")] private static unsafe partial ushort RtlCaptureStackBackTrace(uint skip, uint count, nint* backTrace, uint* hash);
```
```csharp
// Crash.Handler.cs
[StructLayout(LayoutKind.Sequential, Pack = 4)]   // minidumpapiset.h wraps it in pshpack4.h
struct MINIDUMP_EXCEPTION_INFORMATION { public uint ThreadId; public nint ExceptionPointers; public int ClientPointers; }
public readonly record struct NativeLayout(int Size, int ExceptionPointersOffset, int ClientPointersOffset);
public static unsafe NativeLayout ExceptionInfoLayout()
{ MINIDUMP_EXCEPTION_INFORMATION p = default; byte* b = (byte*)&p;
  return new(sizeof(MINIDUMP_EXCEPTION_INFORMATION), (int)((byte*)&p.ExceptionPointers - b), (int)((byte*)&p.ClientPointers - b)); }
// HandleDumpRequest: fault = TryReadFault(ptrs) (parent still parked) → WriteMiniDump → Bundles.UpdateSummary(dir, s => s with
//   { HasDump, DumpBytes, FaultModule, FaultOffset }) → Bundles.AppendReportLine(dir, "fault=<m>+0x<off>") → reply.
// TryReadFault: ReadProcessMemory EXCEPTION_POINTERS → EXCEPTION_RECORD head → K32EnumProcessModulesEx(LIST_MODULES_ALL)
//   → K32GetModuleInformation ranges → FaultModule.Find → K32GetModuleBaseNameW → FaultModule.Normalize.
```

## A4 · App upload policy (W3b)

```csharp
public enum UploadOutcome : byte { Sent, AlreadySent, Rejected, Erased, RetryServer, RetryNetwork }
public static class UploadPolicy
{
    public const int GiveUpAfterServerRetryLaunches = 3;
    public static UploadOutcome Classify(int? http) => http switch
    { null => UploadOutcome.RetryNetwork, >= 200 and < 300 => UploadOutcome.Sent, 409 => UploadOutcome.AlreadySent,
      410 => UploadOutcome.Erased, 429 or >= 500 => UploadOutcome.RetryServer, _ => UploadOutcome.Rejected };
    public readonly record struct Step(bool DeleteEntry, SendState State, string? Error);
    public static Step Next(UploadOutcome o, int serverRetryLaunches, string status) => o switch
    {   UploadOutcome.Sent or UploadOutcome.AlreadySent => new(true, SendState.Sent, null),
        UploadOutcome.Erased => new(true, SendState.Failed, "install erased"),
        UploadOutcome.Rejected => new(true, SendState.Failed, status),
        UploadOutcome.RetryServer when serverRetryLaunches >= GiveUpAfterServerRetryLaunches => new(true, SendState.Failed, "gave up"),
        UploadOutcome.RetryServer => new(false, SendState.Queued, status),
        _ => new(false, SendState.Queued, "offline") };
    public static bool Due(int serverAttempts, DateTime? lastTryUtc, DateTime nowUtc) =>
        serverAttempts == 0 || lastTryUtc is not { } t || (nowUtc - t).TotalMinutes >= Math.Min(60, Math.Pow(2, serverAttempts));
    public static TimeSpan Timeout(long bodyBytes) => TimeSpan.FromSeconds(Math.Clamp(30 + bodyBytes / 50_000, 30, 600));
    public static bool IsOrphan(string bundleDir, bool exists) => bundleDir.Length > 0 && !exists;
}
public static class UploadToasts
{ public enum Toast : byte { None, Sent, Queued, Failed }
  public static Toast For(SendState s, bool queuedAlreadyShown) => s switch
  { SendState.Sent => Toast.Sent, SendState.Failed => Toast.Failed, SendState.Queued => queuedAlreadyShown ? Toast.None : Toast.Queued, _ => Toast.None }; }
```

## A5 · Release module (W4)

```powershell
$script:CrashDefaults = @{ Url = 'https://crash.cproducts.dev'; KeyRef = 'op://Personal/Wavee crash ingest key/credential'; OpAccount = 'my.1password.eu' }
function Get-CrashIngestGate {   # HARD for shipping channels; message never contains the key
    param([Parameter(Mandatory)][string]$Channel, [Parameter(Mandatory)][AllowEmptyString()][string]$CrashIngestUrl,
          [Parameter(Mandatory)][AllowEmptyString()][string]$CrashIngestKey)
    $shipping = @('stable', 'beta', 'store') -contains $Channel
    $missing = @()
    $u = "$CrashIngestUrl".Trim()
    if ($u.Length -eq 0) { $missing += 'the ingest URL (-CrashIngestUrl)' } elseif ($u -notmatch '^https://') { $missing += 'an https ingest URL' }
    if ("$CrashIngestKey".Trim().Length -eq 0) { $missing += 'the ingest key' }
    if (-not $shipping -or $missing.Count -eq 0) { return [pscustomobject]@{ Fail = $false; Message = '' } }
    [pscustomobject]@{ Fail = $true; Message = "$Channel build without $($missing -join ' and '): its crashes could never reach the crash service" }
}
function Resolve-CrashIngestKey {   # -Explicit wins; else `op read`. Returns @{Key; Source}; never prints Key.
    param([AllowEmptyString()][string]$Explicit = '', [string]$Reference = $script:CrashDefaults.KeyRef, [string]$Account = $script:CrashDefaults.OpAccount)
    $e = "$Explicit".Trim()
    if ($e) { return [pscustomobject]@{ Key = $e; Source = '-CrashIngestKey' } }
    $r = Invoke-Native 'op' @('read', $Reference, '--account', $Account) -AllowFailure
    if ($r.ExitCode -ne 0) { throw "could not read the crash ingest key from 1Password ($Reference, account $Account; op exited $($r.ExitCode)). Unlock 1Password or pass -CrashIngestKey" }
    $key = @($r.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ -match '^[A-Za-z0-9_\-\.=+/]{16,256}$' }) | Select-Object -Last 1
    if (-not $key) { throw "1Password returned no usable crash ingest key at $Reference" }
    [pscustomobject]@{ Key = $key; Source = "1Password ($Reference)" }
}
function Get-WranglerPath { param([Parameter(Mandatory)][string]$RepoRoot)
    $p = Join-Path $RepoRoot 'ops\crash\worker\node_modules\.bin\wrangler.cmd'
    if (-not (Test-Path -LiteralPath $p)) { throw "the crash Worker's wrangler is not installed ($p): run 'npm --prefix ops/crash/worker ci'" }
    $p }
function Test-WranglerLogin { param([int]$ExitCode, [AllowEmptyCollection()][string[]]$Output)   # pure
    $t = @($Output) -join "`n"
    if ($ExitCode -ne 0) { return [pscustomobject]@{ Ok = $false; Detail = "wrangler whoami exited $ExitCode" } }
    if ($t -match 'not authenticated|wrangler login') { return [pscustomobject]@{ Ok = $false; Detail = "wrangler is not logged in: run 'npx wrangler login' in ops/crash/worker" } }
    $m = [regex]::Match($t, 'associated with the email\s+(\S+?)\.?(\s|$)')
    [pscustomobject]@{ Ok = $true; Detail = $(if ($m.Success) { $m.Groups[1].Value } else { 'logged in' }) } }
function Find-ByteText { param([Parameter(Mandatory)][byte[]]$Bytes, [Parameter(Mandatory)][string]$Text)
    $l1 = [Text.Encoding]::GetEncoding(28591); $hay = $l1.GetString($Bytes)
    [pscustomobject]@{ Utf8  = $hay.IndexOf($l1.GetString([Text.Encoding]::UTF8.GetBytes($Text)), [StringComparison]::Ordinal)
                       Utf16 = $hay.IndexOf($l1.GetString([Text.Encoding]::Unicode.GetBytes($Text)), [StringComparison]::Ordinal) } }
# Assert-CrashIngestStamp -Msix|-ExePath -Url -Key: Wavee.exe bytes (zip entry or file) must contain the URL and the key
#   (UTF-8 or UTF-16LE); returns "Wavee.exe carries <url> (utf8@0x…) and the ingest key"; never prints the key.
# Publish-WaveeSymbolMap -Msix|-ExePath -SymbolsDir [-SymbolsZip] -Quad -Arch -ReleaseToolProject -Wrangler [-SkipUpload]:
#   1. Wavee.pdb present, else expand -SymbolsZip; 2. extract Wavee.exe (removed in finally); 3. dotnet run symbol-map;
#   4. Invoke-SymbolsUpload -Wrangler (skip on -SkipUpload) → [pscustomobject]@{ Symmap; Key; Uploaded; Reason; Bytes }
```
