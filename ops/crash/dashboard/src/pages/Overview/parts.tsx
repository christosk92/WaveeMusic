import {
  Badge,
  Body1,
  Card,
  Caption1,
  LargeTitle,
  Link,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Tooltip,
  createTableColumn,
  makeStyles,
  tokens,
  type BadgeProps,
  type TableColumnDefinition,
  type TableColumnSizingOptions,
} from "@fluentui/react-components";
import { DonutChart, HorizontalBarChart, VerticalStackedBarChart } from "@fluentui/react-charts";
import type { MouseEvent, ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import type { AppDayBucket, AppIssue, AppSymbol, AppVersionCount } from "../../api/types";
import { chartColors, kindBadgeColor, kindLabel } from "../../lib/colors";
import { formatCount, formatRelativeTime } from "../../lib/format";

const useStyles = makeStyles({
  statsRow: {
    display: "grid",
    gridTemplateColumns: "repeat(5, 1fr)",
    gap: tokens.spacingHorizontalL,
    gridColumn: "span 12",
  },
  statCard: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingVerticalL,
  },
  statFooterRow: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
    whiteSpace: "nowrap",
    overflow: "hidden",
    minWidth: 0,
  },
  statFooterText: {
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
    minWidth: 0,
  },
  chartHost: {
    minHeight: "220px",
  },
  whereRow: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
  },
});

// ── Stat tiles ───────────────────────────────────────────────────────────────────────────────────────

export interface StatTileProps {
  label: string;
  value: number;
  caption: ReactNode;
}

export function StatTile({ label, value, caption }: StatTileProps) {
  const styles = useStyles();
  return (
    <Card>
      <div className={styles.statCard}>
        <Caption1>{label}</Caption1>
        <LargeTitle>{formatCount(value)}</LargeTitle>
        <div className={styles.statFooterRow}>{caption}</div>
      </div>
    </Card>
  );
}

export function DeltaCaption({ pct }: { pct: number | null }) {
  const styles = useStyles();
  if (pct === null) return <Caption1 className={styles.statFooterText}>No trend for this range</Caption1>;
  const color: NonNullable<BadgeProps["color"]> = pct > 0 ? "danger" : pct < 0 ? "success" : "informative";
  return (
    <>
      <Badge appearance="tint" color={color} size="small">
        {pct > 0 ? `+${pct}%` : `${pct}%`}
      </Badge>
      <Caption1 className={styles.statFooterText}>vs. previous half</Caption1>
    </>
  );
}

// ── Reports per day ──────────────────────────────────────────────────────────────────────────────────

export function ReportsPerDayChart({ perDay }: { perDay: AppDayBucket[] }) {
  const styles = useStyles();
  const data = perDay.map((d) => ({
    xAxisPoint: d.day.slice(5),
    chartData: [
      { legend: "Crash", data: d.crash, color: chartColors.crash },
      { legend: "Hang", data: d.hang, color: chartColors.hang },
      { legend: "Closed", data: d.closed, color: chartColors.closed },
    ],
  }));
  return (
    <div className={styles.chartHost}>
      <VerticalStackedBarChart data={data} height={220} barCornerRadius={2} />
    </div>
  );
}

// ── By kind donut ────────────────────────────────────────────────────────────────────────────────────

const KIND_COLORS: Record<string, string> = {
  Managed: chartColors.crash,
  Native: tokens.colorPaletteRedForeground2,
  Hang: chartColors.hang,
  ExitCode: tokens.colorPaletteBerryForeground1,
  UncleanExit: chartColors.closed,
};

export function KindDonut({ perKind }: { perKind: Record<string, number> }) {
  const styles = useStyles();
  const chartData = Object.entries(perKind).map(([kind, count]) => ({
    legend: kindLabel(kind),
    data: count,
    color: KIND_COLORS[kind] ?? chartColors.brand,
  }));
  return (
    <div className={styles.chartHost}>
      <DonutChart data={{ chartData }} height={220} innerRadius={44} hideLabels={false} />
    </div>
  );
}

// ── Top issues grid ──────────────────────────────────────────────────────────────────────────────────

const issueColumns: TableColumnDefinition<AppIssue>[] = [
  createTableColumn<AppIssue>({
    columnId: "title",
    compare: (a, b) => a.title.localeCompare(b.title),
    renderHeaderCell: () => "Issue",
    renderCell: (item) => (
      <Tooltip content={item.title} relationship="label">
        <TableCellLayout
          truncate
          media={
            <Badge appearance="tint" color={kindBadgeColor(item.kind)} size="small">
              {item.kind}
            </Badge>
          }
        >
          {item.title}
        </TableCellLayout>
      </Tooltip>
    ),
  }),
  createTableColumn<AppIssue>({
    columnId: "count",
    compare: (a, b) => a.count - b.count,
    renderHeaderCell: () => "Count",
    renderCell: (item) => formatCount(item.count),
  }),
  createTableColumn<AppIssue>({
    columnId: "installs",
    compare: (a, b) => a.installs - b.installs,
    renderHeaderCell: () => "Installs",
    renderCell: (item) => formatCount(item.installs),
  }),
  createTableColumn<AppIssue>({
    columnId: "lastSeen",
    compare: (a, b) => (a.lastSeen < b.lastSeen ? -1 : 1),
    renderHeaderCell: () => "Last seen",
    renderCell: (item) => formatRelativeTime(item.lastSeen),
  }),
];

/** Keeps the Issue column readable and the numeric columns compact so rows never wrap. */
const issueColumnSizingOptions: TableColumnSizingOptions = {
  title: { minWidth: 360, idealWidth: 360 },
  count: { minWidth: 90, idealWidth: 90 },
  installs: { minWidth: 90, idealWidth: 90 },
  lastSeen: { minWidth: 120, idealWidth: 140 },
};

export { issueColumns, issueColumnSizingOptions };

// ── By version horizontal bars ──────────────────────────────────────────────────────────────────────

export function VersionBars({ versions }: { versions: AppVersionCount[] }) {
  const styles = useStyles();
  const bySemver = new Map<string, number>();
  for (const v of versions) bySemver.set(v.semver, (bySemver.get(v.semver) ?? 0) + v.count);
  const rows = [...bySemver.entries()].sort(([a], [b]) => (a < b ? 1 : -1));
  const max = Math.max(1, ...rows.map(([, count]) => count));
  const chartData = rows.map(([semver, count]) => ({
    chartTitle: semver,
    chartData: [
      {
        legend: semver,
        horizontalBarChartdata: { x: count, total: max },
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

// ── Where it happened ────────────────────────────────────────────────────────────────────────────────

function channelFromQuad(quad: string): string {
  const dash = quad.lastIndexOf("-");
  return dash === -1 ? quad : quad.slice(dash + 1);
}

export function WhereTable({ versions }: { versions: AppVersionCount[] }) {
  const byArch = new Map<string, number>();
  const byChannel = new Map<string, number>();
  for (const v of versions) {
    byArch.set(v.arch, (byArch.get(v.arch) ?? 0) + v.count);
    const channel = channelFromQuad(v.quad);
    byChannel.set(channel, (byChannel.get(channel) ?? 0) + v.count);
  }
  const rows: { dimension: string; label: string; count: number }[] = [
    ...[...byArch.entries()].map(([label, count]) => ({ dimension: "Architecture", label, count })),
    ...[...byChannel.entries()].map(([label, count]) => ({ dimension: "Channel", label, count })),
  ];
  return (
    <Table size="small" aria-label="Where reports happened">
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
            <TableCell>{formatCount(row.count)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

// ── Symbols summary ──────────────────────────────────────────────────────────────────────────────────

export function SymbolsSummary({ symbols }: { symbols: AppSymbol[] }) {
  const styles = useStyles();
  return (
    <div className={styles.whereRow} style={{ flexWrap: "wrap" }}>
      {symbols.map((s) => (
        <Badge
          key={`${s.quad}-${s.arch}`}
          appearance="tint"
          color={s.debugId ? "success" : "danger"}
          size="medium"
        >
          {s.quad} · {s.arch} · {s.debugId ? "uploaded" : "missing"}
        </Badge>
      ))}
    </div>
  );
}

/** A `CardFooter` link that navigates client-side but keeps a real `href` for middle-click / new tab. */
export function SeeAllLink({ to, children }: { to: string; children: ReactNode }) {
  const navigate = useNavigate();
  const onClick = (e: MouseEvent<HTMLAnchorElement>) => {
    if (e.defaultPrevented || e.metaKey || e.ctrlKey || e.shiftKey || e.button !== 0) return;
    e.preventDefault();
    navigate(to);
  };
  return (
    <Link href={to} onClick={onClick}>
      {children}
    </Link>
  );
}

export function InlineBody({ children }: { children: ReactNode }) {
  return <Body1>{children}</Body1>;
}

/** A stat tile's footer caption when there's no delta to show — single-line, ellipsis when tight
 *  (same rule `DeltaCaption` follows). */
export function StatCaption({ children }: { children: ReactNode }) {
  const styles = useStyles();
  return <Caption1 className={styles.statFooterText}>{children}</Caption1>;
}
