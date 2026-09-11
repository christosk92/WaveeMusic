# Store bundle repair implementation

## Incident and contract

The flat upload contained two valid standalone MSIX files, but Store ingestion exposed one architecture.
The x64-only repair replaced the previous `.msixupload` because msstore-cli matches an old upload by extension;
the original `.msix` rows remained. Local ZIP tests passed because they asserted that incorrect container shape.

The replacement pipeline is:

```text
clean released application tag + archived MSIX/symbols
    -> verify executable hashes, build stamps, PlayPlay evidence
    -> MakeAppx bundle [arm64, x64], unchanged nested bytes
    -> upload containing exactly that bundle
    -> owned draft: explicit full replacement/deletion set
    -> commit -> recognized Neutral bundle -> verified Published inventory
```

Implemented interfaces (PowerShell):

```powershell
New-WaveeMsixBundle -Msix $both -OutFile $bundle -MakeAppx $sdk.MakeAppx `
    -IdentityName $identity -Publisher $publisher -Quad $quad
New-WaveeMsixUpload -Bundle $bundle -OutFile $upload `
    -IdentityName $identity -Publisher $publisher -Quad $quad
$draft = Set-WaveeStorePackageSet -Submission $draft -SubmissionId $id -UploadName $name -Quad $quad
Assert-WaveeStorePackageSet -Submission $draft -SubmissionId $id -UploadName $name -Quad $quad
Assert-WaveeStorePackageSet -Submission $published -SubmissionId $id -UploadName $name -Quad $quad -Ingested
```

`-SourceRoot` separates app provenance from updated tooling. `-PackageDir` adopts the release packages without
compiling private or outstanding code. Store releases always require both architectures; `-Force`, `-Arch` and
`-SkipArch` cannot bypass that contract. The schema-2 ledger binds resume to hashes and exact submission ownership.
The CLI's automatic extension deletion is reconciled explicitly before commit, never trusted as the desired set.

## Verification

Behavioral ZIP/manifest tests cover missing/duplicate architectures, malformed/nested packages, identity mismatch,
wrong executable hashes and missing PlayPlay evidence. Pure submission fixtures cover retained 102 packages,
extension replacement, wrong IDs, newer/unknown old packages, partial ingested uploads, tampered ledgers and CLI
secret redaction. A real Windows SDK rehearsal uses the original 809 Store binaries and checks unchanged hashes.

Full app checks use an isolated engine checkout from the release period (`b946fc373`) for API compatibility.
This is a validation fixture, not proof of the original engine commit and not a source of replacement binaries.
Original executable hashes, rather than a fresh rebuild, are the repair's provenance evidence.

Initial validation (2026-09-07): Debug and Release succeeded with the isolated engine fixture (existing test-project
warnings remain). Full app-test attempts each passed 7,374, failed one and skipped one; failures varied among
LyricsDiskCacheTests, EntityResidencyTests and CachedStoreTests. The isolated EntityResidency failure exposed a
concurrent write to its `FakeCold` dictionary. These application/test-fixture issues are not changed by this
packaging-only repair; no validation-build output is uploaded. Release-tooling tests and real SDK packaging are
the changed-code gates, with their results recorded in the handoff.

Final tooling validation: **330 passed, 0 failed, 1 skipped**. Both original Store executables also passed native
PE-machine verification (arm64/x64), executable SHA256-to-symbol-stamp matching and PlayPlay map evidence checks.
The real corrective submission is `1152921505701828384`; ingestion accepted one Neutral bundle at `1.2.809.0`
under package ID `2000000000097887908`, with both 102 rows and the previous standalone 809 upload removed from
that submission. **Published was verified on 2026-09-07 at 11:51 UTC**; a subsequent product query confirmed
`LastPublishedApplicationSubmission.Id = 1152921505701828384`, no pending submission, and gradual rollout disabled.
The live status and redacted inventory are recorded in
`artifacts/store-bundle-repair/0.2.8/submission-latest.json`; the ledger's poll phase is complete.

Tooling changes remain isolated and uncommitted pending approval for the repository-required GitHub tracking issue.
The dirty development checkout, release tags and GitHub sideload feed were not modified.

## Publication limits

Automatic publication is retained by user choice. Post-ingestion checks detect server-side discrepancies; they
cannot promise a pre-publication hold. Bundles remain the Store format for subsequent updates. A version conflict
requires reporting the exact rejection, not silently bumping 809. Historical installations are not erased when
old packages are retired from the current submission.

References: [CLI extension replacement issue](https://github.com/microsoft/msstore-cli/issues/124),
[bundle updates](https://learn.microsoft.com/en-us/windows/msix/app-package-updates),
[submission commit and ingestion](https://learn.microsoft.com/en-us/windows/uwp/monetize/commit-an-app-submission).
