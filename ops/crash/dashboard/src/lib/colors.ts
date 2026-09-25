import { tokens, type BadgeProps } from "@fluentui/react-components";
import type { Kind } from "../api/types";

/** `Badge`'s stock `color` tokens (plan §0.1: no hand-rolled colour — everything through the
 *  component's own intent tokens). */
export function kindBadgeColor(kind: string): NonNullable<BadgeProps["color"]> {
  switch (kind as Kind) {
    case "Managed":
      return "danger";
    case "Native":
      return "severe";
    case "Hang":
      return "warning";
    case "ExitCode":
      return "important";
    case "UncleanExit":
      return "subtle";
    default:
      return "informative";
  }
}

export function kindLabel(kind: string): string {
  switch (kind as Kind) {
    case "Managed":
      return "Managed exception";
    case "Native":
      return "Native crash";
    case "Hang":
      return "Hang";
    case "ExitCode":
      return "Exit code";
    case "UncleanExit":
      return "Unclean exit";
    default:
      return kind;
  }
}

/** Chart series colours, drawn from theme tokens so charts stay correct across light/dark
 *  (plan §3 Overview: "colours from `tokens.colorBrandBackground`, …"). */
export const chartColors = {
  crash: tokens.colorPaletteRedForeground1,
  hang: tokens.colorPaletteMarigoldBackground3,
  closed: tokens.colorNeutralStroke1,
  brand: tokens.colorBrandBackground,
} as const;

export function statusBadgeColor(status: string): NonNullable<BadgeProps["color"]> {
  switch (status) {
    case "open":
      return "danger";
    case "resolved":
      return "success";
    case "ignored":
      return "subtle";
    default:
      return "informative";
  }
}
