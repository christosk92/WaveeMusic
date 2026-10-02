import {
  Badge,
  Button,
  Caption1,
  Link,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Tooltip,
  createTableColumn,
  makeStyles,
  tokens,
  type TableColumnDefinition,
  type TableColumnSizingOptions,
} from "@fluentui/react-components";
import { HorizontalBarChart, VerticalBarChart } from "@fluentui/react-charts";
import { History24Regular, Open16Regular } from "@fluentui/react-icons";
import { forwardRef, type Ref } from "react";
import type { AppBreakdownEntry, AppFrame, AppIssue, AppIssueDetail, AppOccurrence, AppSparklineDay } from "../../api/types";
import { chartColors, kindBadgeColor } from "../../lib/colors";
import { formatBytes, formatRelativeTime, nativeFaultText } from "../../lib/format";
import { EmptyState } from "../../scene/EmptyState";
import { GITHUB_REPO_URL, githubIssueUrl, isRegressed, regressionSummary } from "../Issues/parts";

const useStyles = makeStyles({
  chartHost: {
    minHeight: "140px",
  },
  metaRow: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    flexWrap: "wrap",
  },
  unresolved: {
    fontStyle: "italic",
    color: tokens.colorNeutralForeground3,
  },
});

// ── GitHub prefill ───────────────────────────────────────────────────────────────────────────────────

/** Builds a prefilled "new GitHub issue" URL from an `AppIssue` (plan: `[Open GitHub issue]` command).
 *  Every fact here comes straight off `AppIssue` — no invented fields. */
export function githubNewIssueUrl(issue: AppIssue): string {
  const title = `${issue.kind}: ${issue.title}`;
  const body = [
    `**Fingerprint:** \`${issue.fingerprint}\``,
    `**Kind:** ${issue.kind}`,
    `**Count:** ${issue.count} across ${issue.installs} install(s)`,
    `**First seen:** ${issue.firstSeen}`,
    `**Last seen:** ${issue.lastSeen}`,
  ].join("\n");
  const params = new URLSearchParams({ title, body });
  return `${GITHUB_REPO_URL}/issues/new?${params.toString()}`;
}

/** `PageHeader` wraps every `commands`/`filters` node in an `OverflowItem`, which forwards a ref to its
 *  child — a plain function component here would trigger React's "function components cannot be given
 *  refs" warning, so this renders either a `Link` or a `Button` and forwards the ref to whichever one. */
export const GitHubAction = forwardRef<HTMLAnchorElement | HTMLButtonElement, { issue: AppIssue }>(
  function GitHubAction({ issue }, ref) {
    if (issue.githubIssue) {
      return (
        <Link
          ref={ref as Ref<HTMLAnchorElement>}
          href={githubIssueUrl(issue.githubIssue)}
          target="_blank"
          rel="noreferrer"
        >
          #{issue.githubIssue}
        </Link>
      );
    }
    return (
      <Button
        ref={ref as Ref<HTMLButtonElement>}
        appearance="secondary"
        icon={<Open16Regular />}
        onClick={() => window.open(githubNewIssueUrl(issue), "_blank", "noopener,noreferrer")}
      >
        Open GitHub issue
      </Button>
    );
  },
);

// ── Header subtitle meta ────────────────────────────────────────────────────────────────────────────

/** "Resolved in ≤ 0.3.2 · 3 days ago" for a resolved issue (the Worker records the newest semver seen when it
 *  was resolved; only a report from a newer one reopens it); null otherwise. */
export function resolvedNote(issue: AppIssue): string | null {
  if (issue.status !== "resolved") return null;
  const head = issue.resolvedVersion ? `Resolved in ≤ ${issue.resolvedVersion}` : "Resolved";
  return issue.resolvedAt ? `${head} · ${formatRelativeTime(issue.resolvedAt)}` : head;
}

export function DetailMeta({ detail, topFrame }: { detail: AppIssueDetail; topFrame: string | null }) {
  const styles = useStyles();
  const { issue } = detail;
  const newest = detail.reports[0];
  const fault = newest ? nativeFaultText(newest) : null;
  const resolved = resolvedNote(issue);
  return (
    <span className={styles.metaRow}>
      {isRegressed(issue) && (
        <>
          {/* Plain badge: the summary is right beside it (the Issues list puts it in a tooltip instead). */}
          <Badge appearance="filled" color="danger" size="small">
            Regressed
          </Badge>
          <Caption1>{regressionSummary(issue)}</Caption1>
        </>
      )}
      {fault && <Text font="monospace">{fault}</Text>}
      {topFrame && <Text font="monospace">{topFrame}</Text>}
      <Badge appearance="tint" color={kindBadgeColor(issue.kind)} size="small">
        {issue.kind}
      </Badge>
      <Caption1>
        {issue.count} reports · {issue.installs} installs · first {formatRelativeTime(issue.firstSeen)} · last{" "}
        {formatRelativeTime(issue.lastSeen)}
      </Caption1>
      {resolved && <Caption1>{resolved}</Caption1>}
    </span>
  );
}

// ── Stack tab ────────────────────────────────────────────────────────────────────────────────────────

/** The frames the Worker sent, else the issue's own `lastFrames` — what remains once every report has
 *  been purged (a current Worker already falls back to it in `frames`; this covers an older one). */
export function stackFramesFor(detail: AppIssueDetail): AppFrame[] {
  return detail.frames ?? detail.issue.lastFrames ?? [];
}

function hex(n: number): string {
  return `0x${n.toString(16)}`;
}

const STACK_COL_WIDTHS = {
  index: "48px",
  rva: "120px",
  offset: "90px",
} as const;

export function StackTable({ frames }: { frames: AppFrame[] }) {
  const styles = useStyles();
  return (
    <Table size="small" aria-label="Stack frames" style={{ tableLayout: "fixed", width: "100%" }}>
      <TableHeader>
        <TableRow>
          <TableHeaderCell style={{ width: STACK_COL_WIDTHS.index }}>#</TableHeaderCell>
          <TableHeaderCell>Function</TableHeaderCell>
          <TableHeaderCell style={{ width: STACK_COL_WIDTHS.rva }}>RVA</TableHeaderCell>
          <TableHeaderCell style={{ width: STACK_COL_WIDTHS.offset }}>Offset</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {frames.map((frame, index) => (
          <TableRow key={index}>
            <TableCell style={{ width: STACK_COL_WIDTHS.index }}>{index}</TableCell>
            <TableCell>
              <Tooltip content={frame.name ?? "<unresolved>"} relationship="label">
                <TableCellLayout truncate>
                  {frame.name ? (
                    <Text font="monospace">{frame.name}</Text>
                  ) : (
                    <Text font="monospace" className={styles.unresolved}>
                      &lt;unresolved&gt;
                    </Text>
                  )}
                </TableCellLayout>
              </Tooltip>
            </TableCell>
            <TableCell style={{ width: STACK_COL_WIDTHS.rva }}>
              <Text font="monospace">{hex(frame.rva)}</Text>
            </TableCell>
            <TableCell style={{ width: STACK_COL_WIDTHS.offset }}>{frame.offset}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

// ── Occurrences tab ──────────────────────────────────────────────────────────────────────────────────

export const occurrenceColumns: TableColumnDefinition<AppOccurrence>[] = [
  createTableColumn<AppOccurrence>({
    columnId: "receivedAt",
    compare: (a, b) => (a.receivedAt < b.receivedAt ? 1 : -1),
    renderHeaderCell: () => "Received",
    renderCell: (item) => formatRelativeTime(item.receivedAt),
  }),
  createTableColumn<AppOccurrence>({
    columnId: "arch",
    compare: (a, b) => a.arch.localeCompare(b.arch),
    renderHeaderCell: () => "Arch",
    renderCell: (item) => item.arch,
  }),
  createTableColumn<AppOccurrence>({
    columnId: "gpu",
    compare: (a, b) => a.gpu.localeCompare(b.gpu),
    renderHeaderCell: () => "GPU",
    renderCell: (item) => (
      <Tooltip content={item.gpu} relationship="label">
        <TableCellLayout truncate>{item.gpu}</TableCellLayout>
      </Tooltip>
    ),
  }),
  createTableColumn<AppOccurrence>({
    columnId: "dump",
    compare: (a, b) => a.dumpBytes - b.dumpBytes,
    renderHeaderCell: () => "Dump",
    renderCell: (item) => (item.hasDump ? formatBytes(item.dumpBytes) : "—"),
  }),
];

export const occurrenceColumnSizingOptions: TableColumnSizingOptions = {
  receivedAt: { minWidth: 130, idealWidth: 150 },
  arch: { minWidth: 80, idealWidth: 90 },
  gpu: { minWidth: 220, idealWidth: 260 },
  dump: { minWidth: 90, idealWidth: 100 },
};

// ── Environment tab (breakdown facts) ───────────────────────────────────────────────────────────────

export function EnvironmentTable({ breakdowns }: { breakdowns: AppIssueDetail["breakdowns"] }) {
  const rows: { dimension: string; label: string; count: number }[] = [
    ...breakdowns.version.map((e) => ({ dimension: "Version", label: e.label, count: e.count })),
    ...breakdowns.arch.map((e) => ({ dimension: "Architecture", label: e.label, count: e.count })),
    ...breakdowns.gpuTier.map((e) => ({ dimension: "GPU tier", label: e.label, count: e.count })),
  ];
  return (
    <Table size="small" aria-label="Environment breakdown">
      <TableHeader>
        <TableRow>
          <TableHeaderCell>Dimension</TableHeaderCell>
          <TableHeaderCell>Value</TableHeaderCell>
          <TableHeaderCell>Reports</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((row) => (
          <TableRow key={`${row.dimension}-${row.label}`}>
            <TableCell>{row.dimension}</TableCell>
            <TableCell>{row.label}</TableCell>
            <TableCell>{row.count}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

// ── No reports left ──────────────────────────────────────────────────────────────────────────────────

/** What the report-backed tabs (Occurrences, Environment, Log tail) show once the 90-day purge has removed
 *  every report of an issue: the issue row and its lifetime counts stay, its reports don't. */
export function PurgedReportsState() {
  return (
    <EmptyState
      icon={<History24Regular />}
      title="No reports left for this issue"
      hint="Reports older than 90 days are deleted; this issue's counts are kept."
    />
  );
}

// ── Aside: sparkline + breakdown bars ───────────────────────────────────────────────────────────────

export function Last14DaysChart({ sparkline14d }: { sparkline14d: AppSparklineDay[] }) {
  const styles = useStyles();
  const data = sparkline14d.map((d) => ({ x: d.day.slice(5), y: d.count }));
  return (
    <div className={styles.chartHost}>
      <VerticalBarChart data={data} height={140} barWidth={12} colors={[chartColors.brand]} hideLegend />
    </div>
  );
}

export function BreakdownBars({ entries }: { entries: AppBreakdownEntry[] }) {
  const styles = useStyles();
  const max = Math.max(1, ...entries.map((e) => e.count));
  const chartData = entries.map((e) => ({
    chartTitle: e.label,
    chartData: [
      {
        legend: e.label,
        horizontalBarChartdata: { x: e.count, total: max },
        color: chartColors.brand,
      },
    ],
  }));
  return (
    <div className={styles.chartHost}>
      <HorizontalBarChart data={chartData} chartDataMode="default" />
    </div>
  );
}

