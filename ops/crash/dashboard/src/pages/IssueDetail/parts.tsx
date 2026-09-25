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
import { Open16Regular } from "@fluentui/react-icons";
import { forwardRef, type Ref } from "react";
import type { AppBreakdownEntry, AppFrame, AppIssue, AppIssueDetail, AppOccurrence, AppSparklineDay } from "../../api/types";
import { chartColors, kindBadgeColor } from "../../lib/colors";
import { formatBytes, formatRelativeTime } from "../../lib/format";
import { GITHUB_REPO_URL, githubIssueUrl } from "../Issues/parts";

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

export function DetailMeta({ detail, topFrame }: { detail: AppIssueDetail; topFrame: string | null }) {
  const styles = useStyles();
  const { issue } = detail;
  return (
    <span className={styles.metaRow}>
      {topFrame && <Text font="monospace">{topFrame}</Text>}
      <Badge appearance="tint" color={kindBadgeColor(issue.kind)} size="small">
        {issue.kind}
      </Badge>
      <Caption1>
        {issue.count} reports · {issue.installs} installs · first {formatRelativeTime(issue.firstSeen)} · last{" "}
        {formatRelativeTime(issue.lastSeen)}
      </Caption1>
    </span>
  );
}

// ── Stack tab ────────────────────────────────────────────────────────────────────────────────────────

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

// ── Log tail tab ─────────────────────────────────────────────────────────────────────────────────────

/** `AppIssueDetail`/`AppReport` (`api/types.ts`) carry no log-tail field, so there is nothing this tab
 *  can render yet — always the empty state until the Worker/wire shape grows one. */
export const HAS_LOG_TAIL = false;

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

