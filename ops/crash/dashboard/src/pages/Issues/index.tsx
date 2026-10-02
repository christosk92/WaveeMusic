import {
  Checkbox,
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
import {
  ArrowClockwise24Regular,
  CheckmarkCircle24Regular,
  DismissCircle24Regular,
  DocumentSearch24Regular,
} from "@fluentui/react-icons";
import { useCallback, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useIssues, usePatchIssue, useVersions } from "../../api/hooks";
import type { AppIssue } from "../../api/types";
import { KINDS } from "../../api/types";
import { kindLabel } from "../../lib/colors";
import { EmptyState } from "../../scene/EmptyState";
import { ErrorBar } from "../../scene/ErrorBar";
import { PageBody } from "../../scene/PageBody";
import { PageHeader, type HeaderCommand } from "../../scene/PageHeader";
import { Panel } from "../../scene/Panel";
import { GridSkeleton } from "../../scene/skeletons";
import { useAppToast } from "../../scene/toast";
import { issueColumnSizingOptions, issueColumns, selectionColumn } from "./parts";

const useStyles = makeStyles({
  gridHost: {
    minHeight: "0",
    minWidth: 0,
    // The DataGrid's own root gets `min-width: fit-content` from `@fluentui/react-table`'s column-sizing
    // hook, so it always wants to be at least as wide as the sum of its column widths. At narrow widths
    // that sum can exceed the Panel's content width (plan §0.2: no page-level horizontal scroll at
    // 1280px) — scope the scroll to this wrapper instead of letting the grid clip or blow out the card.
    overflowX: "auto",
  },
});

// "All time" sends no `since`: issues keep their lifetime counts after the 90-day report purge, so an issue
// whose last report is older than that is only listed here.
const RANGE_OPTIONS = [
  { key: "7", label: "Last 7 days" },
  { key: "30", label: "Last 30 days" },
  { key: "90", label: "Last 90 days" },
  { key: "all", label: "All time" },
] as const;

const STATUS_OPTIONS = ["open", "resolved", "ignored"] as const;

function daysAgoIso(days: number): string {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString();
}

function matchesSearch(issue: AppIssue, search: string): boolean {
  if (!search) return true;
  return issue.title.toLowerCase().includes(search.toLowerCase());
}

/** Issues (plan §3): searchable/filterable multiselect grid over `useIssues`. Kind and free-text search
 *  have no server-side equivalent in `IssuesFilter`/the mock (only `since`/`status`/`version` do — see
 *  `api/mock.ts#mockIssues`), so they're applied client-side over whatever page the hook returns; the
 *  range/status/version filters go through the hook itself so the query key (and mock's own filtering)
 *  stay meaningful. */
export default function IssuesPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const toast = useAppToast();

  const [rangeDays, setRangeDays] = useState<(typeof RANGE_OPTIONS)[number]["key"]>("30");
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [versionFilter, setVersionFilter] = useState<string | null>(null);
  const [kindFilter, setKindFilter] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());

  const since = useMemo(() => (rangeDays === "all" ? undefined : daysAgoIso(Number(rangeDays))), [rangeDays]);

  const issuesQuery = useIssues({
    since,
    status: statusFilter ?? undefined,
    version: versionFilter ?? undefined,
  });
  const versionsQuery = useVersions();
  const patchIssue = usePatchIssue();

  const isLoading = issuesQuery.isLoading;
  const isRefetching = !isLoading && issuesQuery.isFetching;
  const error = issuesQuery.error;
  const issues = issuesQuery.data ?? [];
  const versions = versionsQuery.data ?? [];

  const versionOptions = useMemo(() => [...new Set(versions.map((v) => v.semver))].sort().reverse(), [versions]);

  const filteredIssues = useMemo(
    () => issues.filter((i) => (!kindFilter || i.kind === kindFilter) && matchesSearch(i, search)),
    [issues, kindFilter, search],
  );

  const hasActiveFilters = !!(search || kindFilter || statusFilter || versionFilter || rangeDays !== "30");
  const isEmpty = !isLoading && !error && filteredIssues.length === 0;

  const toggleSelected = useCallback((fp: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(fp)) next.delete(fp);
      else next.add(fp);
      return next;
    });
  }, []);
  const isSelected = useCallback((fp: string) => selected.has(fp), [selected]);

  const columns = useMemo(
    () => [selectionColumn({ isSelected, toggle: toggleSelected }), ...issueColumns],
    [isSelected, toggleSelected],
  );

  function refetchAll() {
    void issuesQuery.refetch();
  }

  function clearFilters() {
    setSearch("");
    setKindFilter(null);
    setStatusFilter(null);
    setVersionFilter(null);
    setRangeDays("30");
  }

  async function applyBulkStatus(newStatus: string, verb: string) {
    const fingerprints = [...selected];
    if (fingerprints.length === 0) return;
    try {
      await Promise.all(
        fingerprints.map((fingerprint) => patchIssue.mutateAsync({ fingerprint, patch: { status: newStatus } })),
      );
      toast.success(`${fingerprints.length} issue${fingerprints.length === 1 ? "" : "s"} ${verb}`);
      setSelected(new Set());
    } catch (e) {
      toast.error("Couldn't update the selected issues", e instanceof Error ? e.message : undefined);
    }
  }

  const commands: HeaderCommand[] = [];
  if (selected.size > 0) {
    commands.push(
      {
        id: "mark-resolved",
        node: (
          <ToolbarButton
            icon={<CheckmarkCircle24Regular />}
            onClick={() => void applyBulkStatus("resolved", "marked resolved")}
          >
            Mark resolved ({selected.size})
          </ToolbarButton>
        ),
      },
      {
        id: "ignore",
        node: (
          <ToolbarButton icon={<DismissCircle24Regular />} onClick={() => void applyBulkStatus("ignored", "ignored")}>
            Ignore ({selected.size})
          </ToolbarButton>
        ),
      },
    );
  }

  const filters: HeaderCommand[] = [
    {
      id: "search",
      node: (
        <SearchBox
          aria-label="Search issues"
          placeholder="Search title or exception…"
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
          value={kindFilter ? kindLabel(kindFilter) : "All kinds"}
          selectedOptions={kindFilter ? [kindFilter] : []}
          onOptionSelect={(_e, data) => setKindFilter(data.optionValue || null)}
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
      id: "status",
      node: (
        <Dropdown
          aria-label="Status"
          placeholder="All statuses"
          value={statusFilter ?? "All statuses"}
          selectedOptions={statusFilter ? [statusFilter] : []}
          onOptionSelect={(_e, data) => setStatusFilter(data.optionValue || null)}
          style={{ minWidth: "150px" }}
        >
          <Option value="">All statuses</Option>
          {STATUS_OPTIONS.map((s) => (
            <Option key={s} value={s}>
              {s}
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
          onOptionSelect={(_e, data) => setVersionFilter(data.optionValue || null)}
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
  ];

  const subtitle = `${RANGE_OPTIONS.find((r) => r.key === rangeDays)?.label} · ${
    kindFilter ? kindLabel(kindFilter) : "all kinds"
  } · ${statusFilter ?? "all statuses"} · ${versionFilter ?? "all versions"}`;

  return (
    <>
      <PageHeader
        title="Issues"
        subtitle={subtitle}
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
            <ErrorBar error={error} onRetry={refetchAll} title="Couldn't load issues" />
          </div>
        ) : isLoading ? (
          <GridSkeleton title="All issues" span={12} rows={8} cols={6} />
        ) : isEmpty ? (
          <div style={{ gridColumn: "span 12" }}>
            <EmptyState
              icon={<DocumentSearch24Regular />}
              title="No issues match"
              hint="Try a different search term or clear the filters."
              action={hasActiveFilters ? { label: "Clear filters", onClick: clearFilters } : undefined}
            />
          </div>
        ) : (
          <Panel title="All issues" span={12}>
            <div className={styles.gridHost}>
              <DataGrid
                items={filteredIssues}
                columns={columns}
                columnSizingOptions={issueColumnSizingOptions}
                resizableColumns
                sortable
                size="small"
                getRowId={(item: AppIssue) => item.fingerprint}
              >
                <DataGridHeader>
                  <DataGridRow>
                    {({ renderHeaderCell, columnId }) =>
                      columnId === "select" ? (
                        <DataGridHeaderCell>
                          <Checkbox
                            checked={
                              filteredIssues.length > 0 && filteredIssues.every((i) => isSelected(i.fingerprint))
                            }
                            onChange={() =>
                              setSelected((prev) => {
                                const allSelected =
                                  filteredIssues.length > 0 && filteredIssues.every((i) => prev.has(i.fingerprint));
                                return allSelected ? new Set() : new Set(filteredIssues.map((i) => i.fingerprint));
                              })
                            }
                            aria-label="Select all issues"
                          />
                        </DataGridHeaderCell>
                      ) : (
                        <DataGridHeaderCell>{renderHeaderCell()}</DataGridHeaderCell>
                      )
                    }
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
        )}
      </PageBody>
    </>
  );
}
