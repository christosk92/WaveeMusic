import { KINDS, type Kind, type Summary } from "./types.js";

export type ValidateResult = { ok: true; value: Summary } | { ok: false; error: string };

function isNonEmptyString(v: unknown): v is string {
  return typeof v === "string" && v.length > 0;
}

function isString(v: unknown): v is string {
  return typeof v === "string";
}

function isFiniteNumber(v: unknown): v is number {
  return typeof v === "number" && Number.isFinite(v);
}

function isNumberArray(v: unknown): v is number[] {
  return Array.isArray(v) && v.every((x) => isFiniteNumber(x));
}

/**
 * Validates the `summary` JSON part of a `POST /v1/report` upload against the Summary shape from
 * plan §I. Only the fields the plan calls out as required are enforced strictly (reportId, installId,
 * kind, quad, arch, rvas, moduleBase, debugId); every other field is optional and defaulted so an
 * older or partial client build still ingests.
 */
export function validateSummary(json: unknown): ValidateResult {
  if (typeof json !== "object" || json === null || Array.isArray(json)) {
    return { ok: false, error: "summary must be a JSON object" };
  }
  const o = json as Record<string, unknown>;

  if (!isNonEmptyString(o.reportId)) return { ok: false, error: "summary.reportId is required" };
  if (!isNonEmptyString(o.installId)) return { ok: false, error: "summary.installId is required" };
  if (!isString(o.kind) || !(KINDS as readonly string[]).includes(o.kind)) {
    return { ok: false, error: `summary.kind must be one of ${KINDS.join(", ")}` };
  }
  if (!isNonEmptyString(o.quad)) return { ok: false, error: "summary.quad is required" };
  if (!isNonEmptyString(o.arch)) return { ok: false, error: "summary.arch is required" };
  if (!isNumberArray(o.rvas)) return { ok: false, error: "summary.rvas must be a number array" };
  if (!isFiniteNumber(o.moduleBase)) return { ok: false, error: "summary.moduleBase is required" };
  if (!isString(o.debugId)) return { ok: false, error: "summary.debugId is required" };

  const value: Summary = {
    reportId: o.reportId,
    installId: o.installId,
    kind: o.kind as Kind,
    stampUtc: isString(o.stampUtc) ? o.stampUtc : "",
    version: isString(o.version) ? o.version : "",
    quad: o.quad,
    commit: isString(o.commit) ? o.commit : "",
    channel: isString(o.channel) ? o.channel : "",
    arch: o.arch,
    osBuild: isString(o.osBuild) ? o.osBuild : "",
    gpu: isString(o.gpu) ? o.gpu : "",
    gpuTier: isString(o.gpuTier) ? o.gpuTier : "",
    softwareAdapter: o.softwareAdapter === true,
    packaged: o.packaged === true,
    locale: isString(o.locale) ? o.locale : "",
    sessionId: isString(o.sessionId) ? o.sessionId : "",
    uptimeMs: isFiniteNumber(o.uptimeMs) ? o.uptimeMs : 0,
    beforeFirstFrame: o.beforeFirstFrame === true,
    lastRoute: isString(o.lastRoute) ? o.lastRoute : "",
    exceptionType: isString(o.exceptionType) ? o.exceptionType : "",
    exceptionMessage: isString(o.exceptionMessage) ? o.exceptionMessage : "",
    rvas: o.rvas,
    moduleBase: o.moduleBase,
    moduleSize: isFiniteNumber(o.moduleSize) ? o.moduleSize : 0,
    debugId: o.debugId,
    exitCode: isFiniteNumber(o.exitCode) ? o.exitCode : 0,
    hasDump: o.hasDump === true,
    dumpBytes: isFiniteNumber(o.dumpBytes) ? o.dumpBytes : 0,
  };
  return { ok: true, value };
}
