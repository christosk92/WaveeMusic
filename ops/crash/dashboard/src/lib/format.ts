/** Shortens a fingerprint/report/install id for display: first 8 chars + ellipsis, full value in `title`. */
export function shortId(id: string, length = 8): string {
  if (id.length <= length) return id;
  return `${id.slice(0, length)}…`;
}

const BYTE_UNITS = ["B", "KB", "MB", "GB"] as const;

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  const exp = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), BYTE_UNITS.length - 1);
  const value = bytes / 1024 ** exp;
  return `${exp === 0 ? value : value.toFixed(1)} ${BYTE_UNITS[exp]}`;
}

const RELATIVE_UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ["year", 60 * 60 * 24 * 365],
  ["month", 60 * 60 * 24 * 30],
  ["week", 60 * 60 * 24 * 7],
  ["day", 60 * 60 * 24],
  ["hour", 60 * 60],
  ["minute", 60],
];

const relativeFormatter = new Intl.RelativeTimeFormat("en", { numeric: "auto" });

/** "3 hours ago", "just now", etc., from an ISO timestamp. */
export function formatRelativeTime(iso: string, now: Date = new Date()): string {
  const then = new Date(iso).getTime();
  const diffSeconds = Math.round((then - now.getTime()) / 1000);
  const absSeconds = Math.abs(diffSeconds);

  if (absSeconds < 45) return "just now";

  for (const [unit, secondsInUnit] of RELATIVE_UNITS) {
    if (absSeconds >= secondsInUnit) {
      const value = Math.round(diffSeconds / secondsInUnit);
      return relativeFormatter.format(value, unit);
    }
  }
  return relativeFormatter.format(Math.round(diffSeconds / 60), "minute");
}

const dateTimeFormatter = new Intl.DateTimeFormat("en", {
  dateStyle: "medium",
  timeStyle: "short",
});

export function formatDateTime(iso: string): string {
  return dateTimeFormatter.format(new Date(iso));
}

export function formatCount(n: number): string {
  return new Intl.NumberFormat("en").format(n);
}

/** Rounded percentage change from `previous` to `current`, or `null` when there's nothing to compare
 *  against (both zero). */
export function pctChange(current: number, previous: number): number | null {
  if (previous === 0 && current === 0) return null;
  if (previous === 0) return 100;
  return Math.round(((current - previous) / previous) * 100);
}

/** Rounded percentage delta, e.g. `+12%` / `-4%` / `0%`. */
export function formatDeltaPct(current: number, previous: number): string {
  const pct = pctChange(current, previous) ?? 0;
  return `${pct > 0 ? "+" : ""}${pct}%`;
}

export function formatDurationMs(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(1)} s`;
  const minutes = Math.floor(seconds / 60);
  const remSeconds = Math.round(seconds % 60);
  return `${minutes}m ${remSeconds}s`;
}
