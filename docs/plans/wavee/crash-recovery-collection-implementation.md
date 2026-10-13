# Confirmed crash fixes and collection update (0.3.5)

Tracking: Wavee [#209](https://github.com/christosk92/WaveeMusic/issues/209), engine
[#55](https://github.com/christosk92/fluent-gpu/issues/55). Scope excludes simulated reports and speculative driver
or overlay workarounds. The production access violations and original fail-fast exit codes remain unresolved.

## Engine contract

Before an idle wait, the render loop checks persistent control state, not just its coalescing wake event:

```csharp
bool controlPending = !_running || Volatile.Read(ref _resizeQuiesce) != 0
    || (_deviceLost is { } pendingLoss && pendingLoss.RecoverRequest != 0 && pendingLoss.RecoverDone == 0);
if (!controlPending && !(!motionDue && _publisher.HasPendingFrame))
{
    // existing display-clock / wake wait
}
```

Recovery retains priority; a resize park is acknowledged on the following turn. Disposal of a removed device or
target bypasses normal fence retirement. Healthy work still retires before ownership is released; unexpected native
errors remain failures. Cold `d3d12.recovery` and `d3d12.teardown` checkpoints bracket recovery, its first submission,
target drain and device release. The engine guide documents the complete fence and checkpoint contracts.

## Collection contract

The healthy parent sends an initial typed context through the existing child's stdin. Subsequent context updates
contain only changed route name, GPU classification and first-frame state. The child validates the parent PID,
monotonic sequence, start basis and session identity before replacing its latest context. Parent exit/hang reports
use this context; a missing handshake yields explicit unknown metadata.

```csharp
public sealed record ParentContext(int Pid, long Sequence, long StartedTickMs, Summary Metadata,
    string ModuleName, string Framework, string Os, string BuildDate);
```

Shared log entries are filtered by the parent PID before tail truncation. Continuations follow their owning entry.
GPU HRESULTs, vendor/device IDs and code-owned recovery phases survive strict diagnostic-field validation; paths,
account identifiers and arbitrary string values retain the existing redaction policy. Shutdown phases flush before
entering potentially failing cleanup operations.

The optional summary fields are `parentPid`, `reporterPid`, `dumpLocalCreated`, `dumpIncluded` and
`dumpOmissionReason` (`consent`, `missing`, `read-failed`, `size-cap`). The multipart packer determines actual dump
inclusion after the body-size decision. Local creation and upload inclusion are separate facts. Existing local
consent is authoritative and never widened.

## Service and dashboard

No database migration is needed. Report detail reads optional collection facts from the stored R2 summary; old or
malformed summary data yields unknown fields. R2 operational failures remain retryable service errors. Download
availability comes from the server's actual multipart attachment facts, never a client's inclusion claim.

```text
Report detail
  Facts
    Parent process       1234 / Not recorded
    Reporter process     5678 / Not recorded
    Local dump           Created / Not recorded
    Uploaded dump        Available / None
    Dump omission        Consent / Missing / Read failed / Size cap / Not recorded
```

## Verification and rollout

Only the orchestrator builds, tests or launches. Implementation ownership is disjoint: engine; app collection and
shutdown; service/dashboard. Run Debug and Release solution builds and tests for both repositories, engine
VerticalSlice, Windows fence tests, release Pester, Worker tests/typechecks and dashboard tests/build/typecheck.
Re-run the bounded WARP/hardware recovery/resize probes and removed-device shutdown reproduction. Regenerate the
synthetic multipart fixtures from the tested C# packer if the wire shape changes, then verify both client and Worker.

Commit source and issue references, publish the backward-compatible Worker/dashboard first, then release 0.3.5 for
arm64 and x64 with exact symbols through the stable feed. Submit the same source tag to the Microsoft Store with
its required two-architecture bundle. Record feed verification and Store status separately: certification is not
publication. Keep remaining production issues open until new-version evidence establishes their outcomes.
