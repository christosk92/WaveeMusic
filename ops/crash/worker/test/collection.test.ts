import { describe, expect, it } from "vitest";
import { collectionFacts, validateSummary } from "../src/validate.js";
import { makeSummary } from "./helpers.js";

describe("optional crash collection facts", () => {
  it("keeps missing historical fields unknown while retaining recorded local dump facts", () => {
    expect(collectionFacts({ hasDump: true, dumpBytes: 42 })).toEqual({
      parentPid: null, reporterPid: null, localDumpCreated: true, localDumpBytes: 42,
      dumpIncluded: null, dumpOmissionReason: null,
    });
  });

  it.each([null, [], "invalid", {}])("does not invent facts for %j", (summary) => {
    expect(Object.values(collectionFacts(summary))).toEqual([null, null, null, null, null, null]);
  });

  it("discards malformed diagnostic facts", () => {
    expect(collectionFacts({ parentPid: -1, reporterPid: 1.5, hasDump: "true", dumpBytes: -1,
      dumpIncluded: 1, dumpOmissionReason: "arbitrary client text" })).toEqual({
      parentPid: null, reporterPid: null, localDumpCreated: null, localDumpBytes: null,
      dumpIncluded: null, dumpOmissionReason: null,
    });
    expect(collectionFacts({ parentPid: 0x100000000, reporterPid: 0 }).parentPid).toBeNull();
  });

  it("uses explicit creation outcome and leaves legacy absence unknown", () => {
    expect(collectionFacts({ hasDump: false }).localDumpCreated).toBeNull();
    expect(collectionFacts({ hasDump: false, dumpLocalCreated: true }).localDumpCreated).toBe(true);
    expect(collectionFacts({ hasDump: false, dumpLocalCreated: false }).localDumpCreated).toBe(false);
  });

  it.each(["consent", "missing", "read-failed", "size-cap"])("accepts %s without changing ingest compatibility", (reason) => {
    const result = validateSummary({ ...makeSummary(), parentPid: 123, reporterPid: 456,
      dumpIncluded: false, dumpOmissionReason: reason });
    expect(result.ok).toBe(true);
    if (result.ok) expect(result.value).toMatchObject({ parentPid: 123, reporterPid: 456,
      dumpIncluded: false, dumpOmissionReason: reason });
  });
});
