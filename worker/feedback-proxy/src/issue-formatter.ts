import type { FeedbackSubmitRequest } from "./types";

const TYPE_LABELS: Record<number, string> = {
  0: "Bug",
  1: "Support",
  2: "Feature Request",
  3: "Feedback",
};

const TYPE_GITHUB_LABELS: Record<number, string> = {
  0: "bug",
  1: "support",
  2: "enhancement",
  3: "feedback",
};

const SEVERITY_NAMES: Record<number, string> = {
  0: "Low",
  1: "Medium",
  2: "High",
  3: "Critical",
};

const SEVERITY_LABELS: Record<number, string | null> = {
  0: "priority: low",
  1: null, // Medium gets no extra label
  2: "priority: high",
  3: "priority: critical",
};

const REPRODUCIBILITY_NAMES: Record<number, string> = {
  0: "Always",
  1: "Sometimes",
  2: "Rarely",
  3: "Unable to reproduce",
  4: "Not applicable",
};

export function formatTitle(req: FeedbackSubmitRequest): string {
  return `[${TYPE_LABELS[req.type] ?? "Feedback"}] ${req.title}`;
}

export function formatBody(req: FeedbackSubmitRequest, imageUrls?: string[]): string {
  const lines: string[] = [];

  lines.push("## Description", "", req.body, "");

  // Metadata table
  lines.push("## Metadata", "");
  lines.push("| Field | Value |");
  lines.push("|-------|-------|");
  lines.push(`| **Type** | ${TYPE_LABELS[req.type] ?? "Unknown"} |`);
  lines.push(`| **Severity** | ${SEVERITY_NAMES[req.severity] ?? "Unknown"} |`);
  lines.push(`| **Reproducibility** | ${REPRODUCIBILITY_NAMES[req.reproducibility] ?? "Unknown"} |`);
  if (req.appVersion) lines.push(`| **App Version** | ${req.appVersion} |`);
  if (req.includeDeviceMetadata && req.osVersion)
    lines.push(`| **OS** | ${req.osVersion} |`);
  if (req.includeDeviceMetadata && req.deviceInfo)
    lines.push(`| **Device** | ${req.deviceInfo} |`);

  // Screenshots
  if (imageUrls && imageUrls.length > 0) {
    lines.push("");
    lines.push("## Screenshots");
    lines.push("");
    for (const url of imageUrls) {
      lines.push(`![screenshot](${url})`);
      lines.push("");
    }
  }

  // Diagnostics log
  if (req.includeDiagnostics && req.diagnosticsLog) {
    lines.push("");
    lines.push("<details>");
    lines.push("<summary>Diagnostics Log</summary>");
    lines.push("");
    lines.push("```");
    lines.push(req.diagnosticsLog);
    lines.push("```");
    lines.push("");
    lines.push("</details>");
  }

  lines.push("");
  lines.push("---");
  lines.push("*Submitted via Wavee feedback form*");

  return lines.join("\n");
}

export function getLabels(req: FeedbackSubmitRequest): string[] {
  const labels = ["user-feedback"];
  const typeLabel = TYPE_GITHUB_LABELS[req.type];
  if (typeLabel) labels.push(typeLabel);
  const sevLabel = SEVERITY_LABELS[req.severity];
  if (sevLabel) labels.push(sevLabel);
  return labels;
}
