# Crash upload contract fixtures

The bytes the Wavee client sends to `POST /v1/report`, captured once and checked from both ends (#165), so the
desktop app and the crash Worker can never drift apart silently:

| File | What it is |
|---|---|
| `managed.multipart` | A managed-exception bundle: `summary` + `report` + `tail`, no dump. |
| `native.multipart` | A native-fault bundle: `summary` (with `exceptionCode` 0xC0000005, `faultModule` `ntdll.dll`, `faultOffset`) + `report` + `tail` + a 64-byte `MDMP` `dump`. |

Each file is the **byte-exact** `multipart/form-data` body that the C# client's `Crash.Bundle.Pack`
(`src/apps/Wavee/Platform/Crash.Upload.cs`) produces for fixed inputs. The first line of the body carries the
boundary, `--wavee-<reportId>`; the HTTP `Content-Type` that goes with it is
`multipart/form-data; boundary=wavee-<reportId>`.

## Who checks them

- **C#** — `src/apps/Wavee.Tests/CrashContractTests.cs` packs the same fixed bundles and asserts the bytes equal
  these files (linked into the test project as `Fixtures/crash-contract/`). A change to the summary shape, the
  JSON options or the multipart framing fails here first.
- **Worker** — `ops/crash/worker/test/contract.test.ts` POSTs each file, boundary taken from its first line, with
  the test ingest key, and expects `201` plus the stored row (`fp_version` 2; native: `fault_module` `ntdll.dll`,
  `exception_code` 3221225477, `has_dump` 1; managed: `has_dump` 0).

## Regenerating

Only when the client's wire format changes on purpose:

1. Run the C# tests (`dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj`). On a mismatch `CrashContractTests`
   writes what the client now produces to `crash-contract-actual/<name>.multipart` (the failure message prints
   the full path) and fails.
2. Copy those files over `ops/crash/contract/<name>.multipart`.
3. Run both suites again: the C# tests and `npm test` in `ops/crash/worker`. The Worker must still answer `201`
   — if it doesn't, the Worker (validate.ts / index.ts) needs the matching change, deployed **before** a client
   that sends the new format ships.

The files are binary (`.gitattributes`: `ops/crash/contract/*.multipart binary`): CRLF line breaks are part of
the multipart framing and must never be normalized. They hold synthetic data only — fixed ids, no real crash.
