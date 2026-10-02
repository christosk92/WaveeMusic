import {
  Button,
  DataGrid,
  DataGridBody,
  DataGridCell,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridRow,
  Dropdown,
  Option,
  SearchBox,
  Tooltip,
  ToolbarButton,
  makeStyles,
} from "@fluentui/react-components";
import { ArrowClockwise24Regular, DocumentSearch24Regular } from "@fluentui/react-icons";
import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useReports, useVersions } from "../../api/hooks";
import type { AppReport, ReportsFilter } from "../../api/types";
import { KINDS } from "../../api/types";
import { kindLabel } from "../../lib/colors";
import { normalizeIdQuery } from "../../lib/format";
import { EmptyState } from "../../scene/EmptyState";
import { ErrorBar } from "../../scene/ErrorBar";
import { PageBody } from "../../scene/PageBody";
import { PageHeader, type HeaderCommand } from "../../scene/PageHeader";
import { Panel } from "../../scene/Panel";
import { GridSkeleton } from "../../scene/skeletons";
import { channelFromQuad, reportColumnSizingOptions, reportColumns, semverFromQuad } from "./parts";

const useStyles = makeStyles({
  gridHost: {
    minHeight: "0",
    minWidth: 0,
    overflowX: "auto",
  },
});

const RANGE_OPTIONS = [
  { key: "7", label: "Last 7 days" },
  { key: "30", label: "Last 30 days" },
  { key: "90", label: "Last 90 days" },
] as const;

const PAGE_SIZE = 25;

function daysAgoIso(days: number): string {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString();
}

/** Reports (plan §3): a filterable, paginated `DataGrid` over `GET /v1/reports`. Filter changes reset
 *  pagination but keep the current rows on screen (plan §7 — no Skeleton bounce on a refetch); only the
 *  very first load ever shows `GridSkeleton`. */
export default function ReportsPage() {
  const styles = useStyles();
  const navigate = useNavigate();

  const [rangeDays, setRangeDays] = useState<(typeof RANGE_OPTIONS)[number]["key"]>("30");
  const [kind, setKind] = useState<string | null>(null);
  const [quad, setQuad] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  const [rows, setRows] = useState<AppReport[]>([]);

  const versionsQuery = useVersions();
  const versionOptions = useMemo(() => {
    const quads = new Set((versionsQuery.data ?? []).map((v) => v.quad));
    return [...quads].sort().reverse();
  }, [versionsQuery.data]);

  const since = useMemo(() => daysAgoIso(Number(rangeDays)), [rangeDays]);
  const baseFilter: ReportsFilter = useMemo(
    () => ({
      since,
      kind: kind ?? undefined,
      quad: quad ?? undefined,
      // The app shows a report's short id as `3f9c-2b1a`; ids themselves are dash-less hex.
      q: normalizeIdQuery(search) || undefined,
      limit: PAGE_SIZE,
    }),
    [since, kind, quad, search],
  );
  const baseFilterKey = JSON.stringify(baseFilter);

  // A filter change starts over from page 1 — but keeps the rows already on screen mounted (dimmed via
  // `isFetching`) until the new first page arrives, rather than resetting to the Skeleton (plan §7). This
  // is React's documented "adjust state during render" pattern: reading/writing the ref here is safe
  // because the guard makes it idempotent within a single render pass.
  const prevBaseFilterKeyRef = useRef(baseFilterKey);
  if (prevBaseFilterKeyRef.current !== baseFilterKey) {
    prevBaseFilterKeyRef.current = baseFilterKey;
    if (cursor !== undefined) setCursor(undefined);
  }

  const filter: ReportsFilter = useMemo(() => ({ ...baseFilter, cursor }), [baseFilter, cursor]);
  const reportsQuery = useReports(filter);

  useEffect(() => {
    if (!reportsQuery.data || reportsQuery.isPlaceholderData) return;
    const page = reportsQuery.data;
    setRows((prev) => (cursor ? [...prev, ...page.reports] : page.reports));
    // Only the arrival of fresh (non-placeholder) data for the current cursor should update `rows`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reportsQuery.data, reportsQuery.isPlaceholderData, cursor]);

  const isLoading = reportsQuery.isLoading;
  const isFetching = !isLoading && reportsQuery.isFetching;
  const error = reportsQuery.error;
  const isEmpty = !isLoading && !error && rows.length === 0;
  const nextCursor = reportsQuery.data?.nextCursor ?? null;

  const hasActiveFilters = !!kind || !!quad || !!search.trim();

  function clearFilters() {
    setKind(null);
    setQuad(null);
    setSearch("");
  }

  function loadMore() {
    if (nextCursor) setCursor(nextCursor);
  }

  const rangeLabel = RANGE_OPTIONS.find((r) => r.key === rangeDays)?.label ?? "";
  const subtitle = `${rangeLabel} · ${kind ? kindLabel(kind) : "all kinds"} · ${quad ?? "all versions"}${
    search.trim() ? ` · "${search.trim()}"` : ""
  }`;

  const commands: HeaderCommand[] = [];
  const filters: HeaderCommand[] = [
    {
      id: "search",
      node: (
        <SearchBox
          aria-label="Search report or install id"
          placeholder="Report or install id"
          value={search}
          onChange={(_e, data) => setSearch(data.value)}
          style={{ minWidth: "220px" }}
        />
      ),
    },
    {
      id: "kind",
      node: (
        <Dropdown
          aria-label="Kind"
          placeholder="All kinds"
          value={kind ? kindLabel(kind) : "All kinds"}
          selectedOptions={kind ? [kind] : []}
          onOptionSelect={(_e, data) => setKind(data.optionValue || null)}
          style={{ minWidth: "160px" }}
        >
          <Option value="">All kinds</Option>
          {KINDS.map((k) => (
            <Option key={k} value={k}>
              {kindLabel(k)}
            </Option>
          ))}
        </Dropdown>
      ),
    },
    {
      id: "version",
      node: (
        <Dropdown
          aria-label="Version"
          placeholder="All versions"
          value={quad ?? "All versions"}
          selectedOptions={quad ? [quad] : []}
          onOptionSelect={(_e, data) => setQuad(data.optionValue || null)}
          style={{ minWidth: "170px" }}
        >
          <Option value="">All versions</Option>
          {versionOptions.map((q) => (
            <Option key={q} value={q} text={`${semverFromQuad(q)} · ${channelFromQuad(q)}`}>
              {semverFromQuad(q)} · {channelFromQuad(q)}
            </Option>
          ))}
        </Dropdown>
      ),
    },
    {
      id: "range",
      node: (
        <Dropdown
          aria-label="Range"
          value={rangeLabel}
          selectedOptions={[rangeDays]}
          onOptionSelect={(_e, data) => {
            if (data.optionValue) setRangeDays(data.optionValue as typeof rangeDays);
          }}
          style={{ minWidth: "150px" }}
        >
          {RANGE_OPTIONS.map((r) => (
            <Option key={r.key} value={r.key}>
              {r.label}
            </Option>
          ))}
        </Dropdown>
      ),
    },
  ];

  return (
    <>
      <PageHeader
        title="Reports"
        subtitle={subtitle}
        commands={commands}
        filters={filters}
        rightCommands={
          <Tooltip content="Refresh" relationship="label">
            <ToolbarButton
              icon={<ArrowClockwise24Regular />}
              onClick={() => void reportsQuery.refetch()}
              aria-label="Refresh"
            />
          </Tooltip>
        }
        isFetching={isFetching}
      />
      <PageBody isFetching={isFetching}>
        {error ? (
          <div style={{ gridColumn: "span 12" }}>
            <ErrorBar error={error} onRetry={() => void reportsQuery.refetch()} title="Couldn't load reports" />
          </div>
        ) : isLoading ? (
          <GridSkeleton title="All reports" span={12} rows={10} cols={8} />
        ) : isEmpty ? (
          <div style={{ gridColumn: "span 12" }}>
            <EmptyState
              icon={<DocumentSearch24Regular />}
              title="No reports match"
              hint={hasActiveFilters ? "Try widening your filters." : "Nothing has come in for this range yet."}
              action={hasActiveFilters ? { label: "Clear filters", onClick: clearFilters } : undefined}
            />
          </div>
        ) : (
          <Panel
            title="All reports"
            span={12}
            footer={
              nextCursor ? (
                <Button appearance="secondary" onClick={loadMore} disabled={reportsQuery.isFetching}>
                  Load more
                </Button>
              ) : undefined
            }
          >
            <div className={styles.gridHost}>
              <DataGrid
                items={rows}
                columns={reportColumns}
                columnSizingOptions={reportColumnSizingOptions}
                resizableColumns
                sortable
                size="small"
                getRowId={(item: AppReport) => item.id}
              >
                <DataGridHeader>
                  <DataGridRow>
                    {({ renderHeaderCell }) => <DataGridHeaderCell>{renderHeaderCell()}</DataGridHeaderCell>}
                  </DataGridRow>
                </DataGridHeader>
                <DataGridBody<AppReport>>
                  {({ item, rowId }) => (
                    <DataGridRow<AppReport>
                      key={rowId}
                      onClick={() => navigate(`/reports/${encodeURIComponent(item.id)}`)}
                      style={{ cursor: "pointer" }}
                    >
                      {({ renderCell }) => <DataGridCell>{renderCell(item)}</DataGridCell>}
                    </DataGridRow>
                  )}
                </DataGridBody>
              </DataGrid>
            </div>
          </Panel>
        )}
      </PageBody>
    </>
  );
}
