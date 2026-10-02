import { describe, expect, it, beforeEach } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { route } from "../src/index.js";
import { D1Store } from "../src/store.js";
import { clearSymmapCacheForTests } from "../src/symbolicate.js";
import { FINGERPRINT_VERSION } from "../src/grouping.js";
import { BASE, makeDeps, makeEnv } from "./helpers.js";

// The client ↔ Worker contract (#165): ops/crash/contract/<name>.multipart are the byte-exact bodies the C# client's
// `Crash.Bundle.Pack` produces for fixed inputs, asserted on the C# side by src/apps/Wavee.Tests/CrashContractTests.cs
// (ops/crash/contract/README.md). Here the Worker must ingest those exact bytes.

function readFixture(name: string): Uint8Array {
  // `.href` (a string): the workers-types global URL and node's URL are distinct types to the test tsconfig.
  return new Uint8Array(readFileSync(fileURLToPath(new URL(`../../contract/${name}.multipart`, import.meta.url).href)));
}

/** The body's first line is `--<boundary>` (`--wavee-<reportId>`). */
function boundaryOf(body: Uint8Array): string {
  const firstLine = new TextDecoder().decode(body.subarray(0, Math.max(0, body.indexOf(0x0d)))); // ASCII
  expect(firstLine.startsWith("--wavee-")).toBe(true);
  return firstLine.slice(2);
}

beforeEach(() => {
  clearSymmapCacheForTests();
});

describe("contract: the real C# client's multipart bodies", () => {
  it.each(["managed", "native"])("%s.multipart ingests with 201", async (name) => {
    const body = readFixture(name);
    const boundary = boundaryOf(body);
    const env = makeEnv();
    const res = await route(
      new Request(`${BASE}/v1/report`, {
        method: "POST",
        headers: {
          "Content-Type": `multipart/form-data; boundary=${boundary}`,
          "X-Wavee-Ingest": env.INGEST_KEY,
        },
        body,
      }),
      env,
      new D1Store(env.DB),
      makeDeps(),
    );
    expect(res.status, await res.clone().text()).toBe(201);

    const { id } = (await res.json()) as { id: string };
    expect(boundary).toBe(`wavee-${id}`);
    const row = await new D1Store(env.DB).getReport(id);
    expect(row).not.toBeNull();
    expect(row!.fp_version).toBe(FINGERPRINT_VERSION);
    if (name === "native") {
      expect(row!.fault_module).toBe("ntdll.dll");
      expect(row!.exception_code).toBe(0xc0000005);
      expect(row!.has_dump).toBe(1);
      expect(row!.dump_bytes).toBeGreaterThan(0);
      expect(await env.BUCKET.get(`reports/${row!.quad}/${id}/minidump.dmp`)).not.toBeNull();
    } else {
      expect(row!.has_dump).toBe(0);
      expect(row!.dump_bytes).toBe(0);
    }
  });
});
