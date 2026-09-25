import {
  Body1Strong,
  DataGrid,
  DataGridBody,
  DataGridCell,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridRow,
  Dropdown,
  InfoLabel,
  Option,
  Tooltip,
  ToolbarButton,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import { ArrowClockwise24Regular, DocumentQuestionMark24Regular } from "@fluentui/react-icons";
import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useIssues, useStats, useSymbols, useVersions } from "../../api/hooks";
import { PageBody } from "../../scene/PageBody";
import { PageHeader, type HeaderCommand } from "../../scene/PageHeader";
import { Panel } from "../../scene/Panel";
import { EmptyState } from "../../scene/EmptyState";
import { ErrorBar } from "../../scene/ErrorBar";
import { ChartSkeleton, GridSkeleton, StatsSkeleton } from "../../scene/skeletons";
import { pctChange } from "../../lib/format";
import {
  DeltaCaption,
  KindDonut,
  ReportsPerDayChart,
  SeeAllLink,
  StatCaption,
  StatTile,
  SymbolsSummary,
  VersionBars,
  WhereTable,
  issueColumnSizingOptions,
  issueColumns,
} from "./parts";
import type { AppIssue } from "../../api/types";

const useStyles = makeStyles({
  gridHost: {
    minHeight: "0",
  },
  // Five across when there is room, otherwise as many 170px tiles as fit per row (900px → 3 + 2).
  statsRow: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))",
    gap: tokens.spacingHorizontalL,
    gridColumn: "span 12",
  },
});

const RANGE_OPTIONS = [
  { key: "7", label: "Last 7 days" },
  { key: "30", label: "Last 30 days" },
  { key: "90", label: "Last 90 days" },
] as const;

function daysAgoIso(days: number): string {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString();
}

/** Overview (plan §3): stat row, reports-per-day, by-kind, top issues, by-version, where-it-happened,
 *  symbols. All four states (loading/empty/error/ready) come from the same react-query calls; mock mode
 *  drives which one renders via `VITE_MOCK_STATE`/`?mockState=`. */
export default function OverviewPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const [rangeDays, setRangeDays] = useState<(typeof RANGE_OPTIONS)[number]["key"]>("30");
  const [versionFilter, setVersionFilter] = useState<string | null>(null);
  const [channelFilter, setChannelFilter] = useState<string | null>(null);

  const since = useMemo(() => daysAgoIso(Number(rangeDays)), [rangeDays]);

  const statsQuery = useStats(since);
  const issuesQuery = useIssues({ since });
  const versionsQuery = useVersions();
  const symbolsQuery = useSymbols();

  const isLoading =
    statsQuery.isLoading || issuesQuery.isLoading || versionsQuery.isLoading || symbolsQuery.isLoading;
  // A background refetch (filter change, Refresh, a mock refetch-interval tick) — NOT the initial load,
  // which already gets the Skeleton branch below. Keeps current content mounted; PageHeader/PageBody
  // show the pinned ProgressBar + dim instead of swapping to skeletons again.
  const isRefetching =
    !isLoading &&
    (statsQuery.isFetching || issuesQuery.isFetching || versionsQuery.isFetching || symbolsQuery.isFetching);
  const error = statsQuery.error ?? issuesQuery.error ?? versionsQuery.error ?? symbolsQuery.error;
  const stats = statsQuery.data;
  const issues = issuesQuery.data ?? [];
  const versions = versionsQuery.data ?? [];
  const symbols = symbolsQuery.data ?? [];

  const filteredVersions = useMemo(
    () =>
      versions.filter(
        (v) =>
          (!versionFilter || v.semver === versionFilter) &&
          (!channelFilter || v.quad.endsWith(`-${channelFilter}`)),
      ),
    [versions, versionFilter, channelFilter],
  );
  const filteredIssues = useMemo(
    () => (versionFilter ? issues.filter((i) => versionFilter in i.versions) : issues),
    [issues, versionFilter],
  );

  const isEmpty = !isLoading && !error && (stats?.reports ?? 0) === 0 && issues.length === 0;

  const versionOptions = useMemo(() => [...new Set(versions.map((v) => v.semver))].sort().reverse(), [versions]);
  const channelOptions = useMemo(
    () => [...new Set(versions.map((v) => v.quad.slice(v.quad.lastIndexOf("-") + 1)))].sort(),
    [versions],
  );

  const topIssues = useMemo(() => [...filteredIssues].sort((a, b) => b.count - a.count).slice(0, 8), [filteredIssues]);

  function refetchAll() {
    void statsQuery.refetch();
    void issuesQuery.refetch();
    void versionsQuery.refetch();
    void symbolsQuery.refetch();
  }

  const reportsDelta = useMemo(() => {
    if (!stats || stats.perDay.length < 2) return null;
    const mid = Math.floor(stats.perDay.length / 2);
    const sum = (days: typeof stats.perDay) => days.reduce((acc, d) => acc + d.crash + d.hang + d.closed, 0);
    return pctChange(sum(stats.perDay.slice(mid)), sum(stats.perDay.slice(0, mid)));
  }, [stats]);

  const hangsDelta = useMemo(() => {
    if (!stats || stats.perDay.length < 2) return null;
    const mid = Math.floor(stats.perDay.length / 2);
    const sum = (days: typeof stats.perDay) => days.reduce((acc, d) => acc + d.hang, 0);
    return pctChange(sum(stats.perDay.slice(mid)), sum(stats.perDay.slice(0, mid)));
  }, [stats]);

  const commands: HeaderCommand[] = [];
  const filters: HeaderCommand[] = [
    {
      id: "range",
      node: (
        <Dropdown
          aria-label="Range"
          value={RANGE_OPTIONS.find((r) => r.key === rangeDays)?.label ?? ""}
          selectedOptions={[rangeDays]}
          onOptionSelect={(_e, data) => {
            if (data.optionValue) setRangeDays(data.optionValue as typeof rangeDays);
          }}
          style={{ minWidth: "160px" }}
        >
          {RANGE_OPTIONS.map((r) => (
            <Option key={r.key} value={r.key}>
              {r.label}
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
          value={versionFilter ?? "All versions"}
          selectedOptions={versionFilter ? [versionFilter] : []}
          onOptionSelect={(_e, data) => setVersionFilter(data.optionValue ?? null)}
          style={{ minWidth: "150px" }}
        >
          <Option value="">All versions</Option>
          {versionOptions.map((v) => (
            <Option key={v} value={v}>
              {v}
            </Option>
          ))}
        </Dropdown>
      ),
    },
    {
      id: "channel",
      node: (
        <Dropdown
          aria-label="Channel"
          placeholder="All channels"
          value={channelFilter ?? "All channels"}
          selectedOptions={channelFilter ? [channelFilter] : []}
          onOptionSelect={(_e, data) => setChannelFilter(data.optionValue ?? null)}
          style={{ minWidth: "150px" }}
        >
          <Option value="">All channels</Option>
          {channelOptions.map((c) => (
            <Option key={c} value={c}>
              {c}
            </Option>
          ))}
        </Dropdown>
      ),
    },
  ];

  return (
    <>
      <PageHeader
        title="Overview"
        subtitle={`${RANGE_OPTIONS.find((r) => r.key === rangeDays)?.label} · ${
          versionFilter ?? "all versions"
        } · ${channelFilter ?? "all channels"}`}
        commands={commands}
        filters={filters}
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
            <ErrorBar error={error} onRetry={refetchAll} title="Couldn't load the overview" />
          </div>
        ) : isLoading ? (
          <>
            <StatsSkeleton />
            <ChartSkeleton title="Reports per day" span={8} />
            <ChartSkeleton title="By kind" span={4} />
            <GridSkeleton title="Top issues" span={8} rows={6} cols={4} />
            <ChartSkeleton title="By version" span={4} />
            <GridSkeleton title="Where it happened" span={6} rows={4} cols={3} />
            <GridSkeleton title="Symbols" span={6} rows={2} cols={4} />
          </>
        ) : isEmpty ? (
          <div style={{ gridColumn: "span 12" }}>
            <EmptyState
              icon={<DocumentQuestionMark24Regular />}
              title="No crash reports yet"
              hint="Once Wavee installs opt in and something goes wrong, reports will show up here."
            />
          </div>
        ) : (
          <>
            <div className={styles.statsRow}>
              <StatTile label="Reports" value={stats?.reports ?? 0} caption={<DeltaCaption pct={reportsDelta} />} />
              <StatTile
                label="Open issues"
                value={stats?.openIssues ?? 0}
                caption={<StatCaption>tracked fingerprints</StatCaption>}
              />
              <StatTile
                label="Installs affected"
                value={stats?.installs ?? 0}
                caption={<StatCaption>distinct installs</StatCaption>}
              />
              <StatTile label="Hangs" value={stats?.hangs ?? 0} caption={<DeltaCaption pct={hangsDelta} />} />
              <StatTile
                label="Native crashes"
                value={stats?.native ?? 0}
                caption={<StatCaption>access violations, etc.</StatCaption>}
              />
            </div>

            <Panel title="Reports per day" span={8}>
              <ReportsPerDayChart perDay={stats?.perDay ?? []} />
            </Panel>

            <Panel title="By kind" span={4}>
              <KindDonut perKind={stats?.perKind ?? {}} />
            </Panel>

            <Panel
              title="Top issues"
              span={8}
              footer={
                <SeeAllLink to="/issues">
                  <Body1Strong>See all issues</Body1Strong>
                </SeeAllLink>
              }
            >
              <div className={styles.gridHost}>
                <DataGrid
                  items={topIssues}
                  columns={issueColumns}
                  columnSizingOptions={issueColumnSizingOptions}
                  resizableColumns
                  sortable
                  size="small"
                  getRowId={(item: AppIssue) => item.fingerprint}
                >
                  <DataGridHeader>
                    <DataGridRow>
                      {({ renderHeaderCell }) => <DataGridHeaderCell>{renderHeaderCell()}</DataGridHeaderCell>}
                    </DataGridRow>
                  </DataGridHeader>
                  <DataGridBody<AppIssue>>
                    {({ item, rowId }) => (
                      <DataGridRow<AppIssue>
                        key={rowId}
                        onClick={() => navigate(`/issues/${encodeURIComponent(item.fingerprint)}`)}
                        style={{ cursor: "pointer" }}
                      >
                        {({ renderCell }) => <DataGridCell>{renderCell(item)}</DataGridCell>}
                      </DataGridRow>
                    )}
                  </DataGridBody>
                </DataGrid>
              </div>
            </Panel>

            <Panel
              title="By version"
              span={4}
              action={<InfoLabel info="Counts only — no active-user denominator." />}
            >
              <VersionBars versions={filteredVersions} />
            </Panel>

            <Panel title="Where it happened" span={6}>
              <WhereTable versions={filteredVersions} />
            </Panel>

            <Panel
              title="Symbols"
              span={6}
              footer={
                <SeeAllLink to="/symbols">
                  <Body1Strong>See all symbols</Body1Strong>
                </SeeAllLink>
              }
            >
              <SymbolsSummary symbols={symbols} />
            </Panel>
          </>
        )}
      </PageBody>
    </>
  );
}
