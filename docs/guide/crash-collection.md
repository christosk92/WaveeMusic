# Crash collection identities and diagnostics

Crash reporting still uses the existing Off / Ask / Auto modes and separate dump opt-in. These additions describe what was captured and attached; they do not change consent or introduce fail-fast interception.

## Parent and reporter

The parent sends `C <compact JSON>` on the private handler stdin pipe before ordinary control messages. `ParentContext` contains its PID, increasing sequence, monotonic start basis, summary metadata, executable basename, framework, OS and build date. PE debug identity and loaded module range are sampled once in the healthy parent. Context is replayed to a replacement handler.

Route names, first-frame state and GPU profile are refreshed on navigation, completed frames and the existing five-second heartbeat. No route arguments are sent. The unchanged frame callback allocates nothing. A GPU transition that fails before a completed frame can still be identified in the separately captured adapter/recovery log; the context records the last published parent profile.

The receiver stores only matching parent PID/session/start basis and increasing sequence. Lines are limited to 16,384 characters while reading, including oversized lines that are drained and discarded. The outbound queue keeps at most one pending context snapshot in its own slot, independent of the ordinary 256-line budget, while preserving beats and control-message order. An accepted post-frame context also latches the child's first-frame state, even when an ordinary `F` line was dropped. At parent exit the reader gets a bounded 250 ms to drain final pipe state.

Child-created Hang / ExitCode / UncleanExit summaries and report headers use that parent context. Missing or invalid context leaves parent metadata unknown; it never substitutes the child's GPU, session, module range or process uptime. Additive `parentPid` identifies the subject process and `reporterPid` the report creator. Native and managed reports created in the parent carry the same PID in both fields. Historical summaries without these fields remain unknown.

Log tails are filtered to the parent PID and, when known, exact parent session before keeping the final 300 lines. This excludes previous launches that reused the PID in the same daily log. Matching structured entries own their following continuation lines and drop placeholders. An unrelated process's message containing `pid=<parent>` cannot change ownership. Pending parent file-queue entries use the same filter and merge using the existing deduplication rules.

## Dump facts

| Summary field | Meaning |
|---|---|
| `hasDump`, `dumpBytes` | Local file retention and captured byte count, independent of upload consent and server receipt. New captures preserve their original byte count even after local cap deletion. |
| `dumpLocalCreated` | Whether MiniDumpWriteDump succeeded locally, including a file subsequently removed by the local cap. Null on historical reports or when no capture attempt was made. |
| `dumpIncluded` | Whether the packed multipart body contains a nonempty dump part. Delivery is tracked separately by send state and the server's dump availability. |
| `dumpOmissionReason` | `consent`, `missing`, `read-failed` or `size-cap`; absent when a dump is included. |

Consent is checked before any dump read. Requested files are classified independently as absent/empty, unreadable, or too large; known local cap deletion retains `size-cap` using the recorded creation size, even after an earlier nonconsenting upload. The packer then checks the complete multipart body against the 20 MiB service cap. Outbox metadata and `send.json` record the actual inclusion result. The additive summary facts are saved locally and transmitted with the same result. New fields default to null and are omitted from old-summary round trips.

## Forensic checkpoints and redaction

The scrubber recognizes exact `[d3d12]`, `[d3d12.recovery]` and `[d3d12.teardown]` markers. `recorded`, `currentReason`, `hr`, `ntstatus`, `exceptionCode` and GPU `reason` survive only as `0x` plus eight hex digits. `vendorId` and `deviceId` accept four to eight hex digits. Recovery/teardown phase strings come from a fixed code-owned set. Unrelated fields, malformed values, lookalike markers and arbitrary strings stay redacted; existing path/account/credential redaction still runs last.

Engine checkpoints cover recovery start/end/failure, first submission after recovery, target drain/release and device drain/release. App checkpoints use `shutdown.phase`, the numeric `ShutdownPhase` IDs and their code-owned stage names. They flush before entering each shutdown subsystem so an abrupt native exit leaves the last entered stage. The IDs in `Crash.Context.cs` cover shell cleanup (10–16) and the app exit tail (20–34). A cleanup failure during unwinding is logged as `shutdown.cleanup.secondary` while the original exception continues to propagate; a cleanup error on healthy shutdown still propagates normally.

`CrashCollectionTests` exercises context framing, parent metadata, incomplete/stale snapshots, bounded reads, interleaved tails, typed diagnostic validation, consent/file-read decisions, actual multipart inclusion and cleanup error precedence. `CrashContractTests` pins the additive multipart shape shared with the crash worker.
