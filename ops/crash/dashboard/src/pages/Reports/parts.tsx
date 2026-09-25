import {
  Badge,
  Text,
  TableCellLayout,
  Tooltip,
  createTableColumn,
  makeStyles,
  type TableColumnDefinition,
  type TableColumnSizingOptions,
} from "@fluentui/react-components";
import type { AppReport } from "../../api/types";
import { kindBadgeColor } from "../../lib/colors";
import { formatBytes, formatDateTime, formatRelativeTime, shortId } from "../../lib/format";

const useBadgeStyles = makeStyles({
  noWrap: {
    whiteSpace: "nowrap",
  },
});

/** The Kind column's badge, keyed off a stable class so every row shares one Griffel rule instead of
 *  each cell mounting its own `makeStyles()` closure. */
function KindCell({ kind }: { kind: string }) {
  const styles = useBadgeStyles();
  return (
    <Badge appearance="tint" color={kindBadgeColor(kind)} size="small" className={styles.noWrap}>
      {kind}
    </Badge>
  );
}

/** `quad` is `<semver>-<channel>` (plan §4 wire shapes); split it for the version dropdown/table cell. */
export function channelFromQuad(quad: string): string {
  const dash = quad.lastIndexOf("-");
  return dash === -1 ? quad : quad.slice(dash + 1);
}

export function semverFromQuad(quad: string): string {
  const dash = quad.lastIndexOf("-");
  return dash === -1 ? quad : quad.slice(0, dash);
}

const reportColumns: TableColumnDefinition<AppReport>[] = [
  createTableColumn<AppReport>({
    columnId: "receivedAt",
    compare: (a, b) => (a.receivedAt < b.receivedAt ? -1 : 1),
    renderHeaderCell: () => "Received",
    renderCell: (item) => (
      <Tooltip content={formatDateTime(item.receivedAt)} relationship="label">
        <span>{formatRelativeTime(item.receivedAt)}</span>
      </Tooltip>
    ),
  }),
  createTableColumn<AppReport>({
    columnId: "id",
    compare: (a, b) => a.id.localeCompare(b.id),
    renderHeaderCell: () => "Report id",
    renderCell: (item) => (
      <Tooltip content={item.id} relationship="label">
        <Text font="monospace">{shortId(item.id)}</Text>
      </Tooltip>
    ),
  }),
  createTableColumn<AppReport>({
    columnId: "kind",
    compare: (a, b) => a.kind.localeCompare(b.kind),
    renderHeaderCell: () => "Kind",
    renderCell: (item) => <KindCell kind={item.kind} />,
  }),
  createTableColumn<AppReport>({
    columnId: "version",
    compare: (a, b) => a.semver.localeCompare(b.semver) || a.channel.localeCompare(b.channel),
    renderHeaderCell: () => "Version",
    renderCell: (item) => `${item.semver} · ${item.channel}`,
  }),
  createTableColumn<AppReport>({
    columnId: "arch",
    compare: (a, b) => a.arch.localeCompare(b.arch),
    renderHeaderCell: () => "Arch",
    renderCell: (item) => item.arch,
  }),
  createTableColumn<AppReport>({
    columnId: "gpu",
    compare: (a, b) => a.gpu.localeCompare(b.gpu),
    renderHeaderCell: () => "GPU",
    renderCell: (item) => (
      <Tooltip content={item.gpu} relationship="label">
        <TableCellLayout truncate>{item.gpu}</TableCellLayout>
      </Tooltip>
    ),
  }),
  createTableColumn<AppReport>({
    columnId: "installId",
    compare: (a, b) => a.installId.localeCompare(b.installId),
    renderHeaderCell: () => "Install",
    renderCell: (item) => (
      <Tooltip content={item.installId} relationship="label">
        <Text font="monospace">{shortId(item.installId)}</Text>
      </Tooltip>
    ),
  }),
  createTableColumn<AppReport>({
    columnId: "dumpBytes",
    compare: (a, b) => a.dumpBytes - b.dumpBytes,
    renderHeaderCell: () => "Dump",
    renderCell: (item) => (item.hasDump ? formatBytes(item.dumpBytes) : "—"),
  }),
];

/** Keeps every column readable without letting any of them wrap (plan rule: "sortable, resizableColumns +
 *  columnSizingOptions so nothing wraps"). Min widths sum to ~920px so the grid fits at 1280px without
 *  the horizontal scroller (`ReportsPage`'s `gridHost` wrapper) kicking in on a normal-width screen. */
const reportColumnSizingOptions: TableColumnSizingOptions = {
  receivedAt: { minWidth: 120, idealWidth: 130 },
  id: { minWidth: 110, idealWidth: 120 },
  kind: { minWidth: 100, idealWidth: 110 },
  version: { minWidth: 140, idealWidth: 150 },
  arch: { minWidth: 70, idealWidth: 70 },
  gpu: { minWidth: 180, idealWidth: 220 },
  installId: { minWidth: 110, idealWidth: 120 },
  dumpBytes: { minWidth: 90, idealWidth: 90 },
};

export { reportColumns, reportColumnSizingOptions };
