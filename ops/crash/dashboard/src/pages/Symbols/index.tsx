import {
  Badge,
  Caption1,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Tooltip,
  ToolbarButton,
  makeStyles,
} from "@fluentui/react-components";
import { ArrowClockwise24Regular, DocumentQuestionMark24Regular } from "@fluentui/react-icons";
import { useSymbols } from "../../api/hooks";
import { PageBody } from "../../scene/PageBody";
import { PageHeader } from "../../scene/PageHeader";
import { Panel } from "../../scene/Panel";
import { EmptyState } from "../../scene/EmptyState";
import { ErrorBar } from "../../scene/ErrorBar";
import { GridSkeleton } from "../../scene/skeletons";
import { formatCount, formatDateTime, formatRelativeTime } from "../../lib/format";

// Explicit per-column widths so a full GUID in "Debug id" renders on one line instead of wrapping the
// row to 3x height: `table-layout: fixed` takes its column widths from this header row, the five other
// columns get a fixed px width, and "Debug id" (no width set) gets whatever's left — at least 340px
// because the table's own min-width is the sum of the five fixed columns plus that 340px floor. When the
// card is narrower than that sum, `tableScroll`'s `overflowX: auto` scrolls instead of clipping.
const DEBUG_ID_MIN_WIDTH = 340;
const OTHER_COLUMN_WIDTHS = { quad: 130, arch: 80, status: 110, entries: 90, uploaded: 130 } as const;
const TABLE_MIN_WIDTH =
  Object.values(OTHER_COLUMN_WIDTHS).reduce((sum, w) => sum + w, 0) + DEBUG_ID_MIN_WIDTH;

const useStyles = makeStyles({
  tableScroll: {
    overflowX: "auto",
    minWidth: "0",
  },
  table: {
    tableLayout: "fixed",
    minWidth: `${TABLE_MIN_WIDTH}px`,
  },
  colQuad: { width: `${OTHER_COLUMN_WIDTHS.quad}px` },
  colArch: { width: `${OTHER_COLUMN_WIDTHS.arch}px` },
  colStatus: { width: `${OTHER_COLUMN_WIDTHS.status}px` },
  colEntries: { width: `${OTHER_COLUMN_WIDTHS.entries}px` },
  colUploaded: { width: `${OTHER_COLUMN_WIDTHS.uploaded}px` },
  debugId: {
    whiteSpace: "nowrap",
  },
});

/** Symbols (plan §3): one row per quad+arch symbol map (`/v1/symbols`). Loading/empty/error/ready +
 *  background-refetch all come from one `useSymbols()` query, exactly like every other scene (plan §7). */
export default function SymbolsPage() {
  const styles = useStyles();
  const symbolsQuery = useSymbols();

  const isLoading = symbolsQuery.isLoading;
  const isRefetching = !isLoading && symbolsQuery.isFetching;
  const error = symbolsQuery.error;
  const symbols = symbolsQuery.data ?? [];
  const isEmpty = !isLoading && !error && symbols.length === 0;

  function refetchAll() {
    void symbolsQuery.refetch();
  }

  return (
    <>
      <PageHeader
        title="Symbols"
        subtitle="symbol maps per release"
        rightCommands={
          <Tooltip content="Refresh" relationship="label">
            <ToolbarButton icon={<ArrowClockwise24Regular />} onClick={refetchAll} aria-label="Refresh" />
          </Tooltip>
        }
        isFetching={isRefetching}
      />
      <PageBody isFetching={isRefetching}>
        {error ? (
          <div style={{ gridColumn: "span 12" }}>
            <ErrorBar error={error} onRetry={refetchAll} title="Couldn't load symbols" />
          </div>
        ) : isLoading ? (
          <GridSkeleton title="Symbol maps" span={12} rows={6} cols={6} />
        ) : isEmpty ? (
          <div style={{ gridColumn: "span 12" }}>
            <EmptyState
              icon={<DocumentQuestionMark24Regular />}
              title="No symbols yet"
              hint="A quad's symbol map shows up here once a release uploads one."
            />
          </div>
        ) : (
          <Panel
            title="Symbol maps"
            span={12}
            footer={
              <Caption1>
                The release script's <Text font="monospace" size={200}>symbols</Text> phase builds a{" "}
                <Text font="monospace" size={200}>Wavee-&lt;quad&gt;-win-&lt;arch&gt;.symmap</Text> from
                the staged PDB and uploads it to R2 via wrangler (ops/crash/README.md;
                docs/guide/releasing-wavee.md §5b).
              </Caption1>
            }
          >
            <div className={styles.tableScroll}>
              <Table size="small" aria-label="Symbol maps per release" className={styles.table}>
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell className={styles.colQuad}>Quad</TableHeaderCell>
                    <TableHeaderCell className={styles.colArch}>Arch</TableHeaderCell>
                    <TableHeaderCell className={styles.colStatus}>Status</TableHeaderCell>
                    <TableHeaderCell>Debug id</TableHeaderCell>
                    <TableHeaderCell className={styles.colEntries}>Entries</TableHeaderCell>
                    <TableHeaderCell className={styles.colUploaded}>Uploaded</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {symbols.map((s) => (
                    <TableRow key={`${s.quad}-${s.arch}`}>
                      <TableCell className={styles.colQuad}>{s.quad}</TableCell>
                      <TableCell className={styles.colArch}>{s.arch}</TableCell>
                      <TableCell className={styles.colStatus}>
                        <Badge appearance="tint" color={s.debugId ? "success" : "danger"} size="small">
                          {s.debugId ? "uploaded" : "missing"}
                        </Badge>
                      </TableCell>
                      <TableCell>
                        {s.debugId ? (
                          <Tooltip content={s.debugId} relationship="label">
                            <TableCellLayout truncate>
                              <Text font="monospace" className={styles.debugId}>
                                {s.debugId}
                              </Text>
                            </TableCellLayout>
                          </Tooltip>
                        ) : (
                          "—"
                        )}
                      </TableCell>
                      <TableCell className={styles.colEntries}>
                        {s.entries !== null ? formatCount(s.entries) : "—"}
                      </TableCell>
                      <TableCell className={styles.colUploaded}>
                        {s.uploadedAt ? (
                          <Tooltip content={formatDateTime(s.uploadedAt)} relationship="label">
                            <Text>{formatRelativeTime(s.uploadedAt)}</Text>
                          </Tooltip>
                        ) : (
                          "—"
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          </Panel>
        )}
      </PageBody>
    </>
  );
}
