import {
  DataGrid,
  DataGridBody,
  DataGridCell,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridRow,
  Dropdown,
  MessageBar,
  MessageBarBody,
  Option,
  Tab,
  TabList,
  Tooltip,
  ToolbarButton,
  makeStyles,
  tokens,
  type SelectTabData,
  type SelectTabEvent,
} from "@fluentui/react-components";
import {
  ArrowClockwise24Regular,
  Bug24Regular,
  CheckmarkCircle24Regular,
  Copy24Regular,
  DocumentQuestionMark24Regular,
} from "@fluentui/react-icons";
import { useMemo, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useIssue, usePatchIssue } from "../../api/hooks";
import type { AppIssueDetail, AppOccurrence } from "../../api/types";
import { shortId } from "../../lib/format";
import { EmptyState } from "../../scene/EmptyState";
import { ErrorBar } from "../../scene/ErrorBar";
import { PageBody } from "../../scene/PageBody";
import { PageHeader, type Crumb, type HeaderCommand } from "../../scene/PageHeader";
import { Panel } from "../../scene/Panel";
import { DetailSkeleton } from "../../scene/skeletons";
import { useAppToast } from "../../scene/toast";
import {
  BreakdownBars,
  DetailMeta,
  EnvironmentTable,
  GitHubAction,
  HAS_LOG_TAIL,
  Last14DaysChart,
  StackTable,
  occurrenceColumnSizingOptions,
  occurrenceColumns,
} from "./parts";

const useStyles = makeStyles({
  gridHost: {
    minHeight: "0",
  },
  mainColumn: {
    gridColumn: "span 8",
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingHorizontalL,
    minWidth: 0,
    "@media (max-width: 900px)": {
      gridColumn: "span 12",
    },
  },
  asideColumn: {
    gridColumn: "span 4",
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingHorizontalL,
    minWidth: 0,
    "@media (max-width: 900px)": {
      gridColumn: "span 12",
    },
  },
});

const STATUS_OPTIONS = ["open", "resolved", "ignored"] as const;

type TabKey = "stack" | "occurrences" | "environment" | "logtail";

function exceptionMessageFor(detail: AppIssueDetail): string {
  const latest = detail.reports[0];
  if (latest?.exceptionMessage) return latest.exceptionMessage;
  if (latest?.kind === "ExitCode") return `Process exited with code 0x${latest.exitCode.toString(16).toUpperCase()}.`;
  if (latest?.kind === "UncleanExit") return "The process exited without a clean shutdown.";
  return detail.issue.title;
}

/** Issue detail (plan §3): the fingerprint's frames, occurrences, environment breakdowns and 14-day
 *  trend. `AppIssueDetail` has no log-tail field (see `parts.tsx#HAS_LOG_TAIL`), so that tab is always
 *  the empty state rather than inventing one. */
export default function IssueDetailPage() {
  const styles = useStyles();
  const { fp } = useParams<{ fp: string }>();
  const navigate = useNavigate();
  const toast = useAppToast();
  const [activeTab, setActiveTab] = useState<TabKey>("stack");

  const issueQuery = useIssue(fp);
  const patchIssue = usePatchIssue();

  const isLoading = issueQuery.isLoading;
  const isRefetching = !isLoading && issueQuery.isFetching;
  const error = issueQuery.error;
  const detail = issueQuery.data;
  const notFound = !isLoading && !error && detail === null;

  const topFrame = useMemo(() => detail?.frames?.find((f) => f.name)?.name ?? null, [detail]);

  function refetch() {
    void issueQuery.refetch();
  }

  async function setStatus(status: string) {
    if (!fp) return;
    try {
      await patchIssue.mutateAsync({ fingerprint: fp, patch: { status } });
      toast.success(`Issue marked ${status}`);
    } catch (e) {
      toast.error("Couldn't update the issue", e instanceof Error ? e.message : undefined);
    }
  }

  function handleCopyFingerprint() {
    if (!fp) return;
    void navigator.clipboard
      ?.writeText(fp)
      .then(
        () => toast.success("Fingerprint copied"),
        () => toast.error("Couldn't copy the fingerprint"),
      );
  }

  const crumbs: Crumb[] = [
    {
      key: "issues",
      label: "Issues",
      href: "/issues",
      onClick: (e) => {
        if (e.metaKey || e.ctrlKey || e.shiftKey) return;
        e.preventDefault();
        navigate("/issues");
      },
    },
    { key: "detail", label: shortId(fp ?? "", 12) },
  ];

  const commands: HeaderCommand[] = detail
    ? [
        { id: "github", node: <GitHubAction issue={detail.issue} /> },
        {
          id: "resolve",
          node: (
            <ToolbarButton
              icon={<CheckmarkCircle24Regular />}
              onClick={() => void setStatus("resolved")}
              disabled={detail.issue.status === "resolved"}
            >
              Mark resolved
            </ToolbarButton>
          ),
        },
        {
          id: "copy-fp",
          node: (
            <ToolbarButton icon={<Copy24Regular />} onClick={handleCopyFingerprint}>
              Copy fingerprint
            </ToolbarButton>
          ),
        },
      ]
    : [];

  const filters: HeaderCommand[] = detail
    ? [
        {
          id: "status",
          node: (
            <Dropdown
              aria-label="Status"
              value={detail.issue.status}
              selectedOptions={[detail.issue.status]}
              onOptionSelect={(_e, data) => {
                if (data.optionValue) void setStatus(data.optionValue);
              }}
              style={{ minWidth: "140px" }}
            >
              {STATUS_OPTIONS.map((s) => (
                <Option key={s} value={s}>
                  {s}
                </Option>
              ))}
            </Dropdown>
          ),
        },
      ]
    : [];

  return (
    <>
      <PageHeader
        crumbs={crumbs}
        title={detail ? detail.reports[0]?.exceptionType || detail.issue.title : "Issue"}
        subtitle={detail ? <DetailMeta detail={detail} topFrame={topFrame} /> : undefined}
        commands={commands}
        filters={filters}
        rightCommands={
          <Tooltip content="Refresh" relationship="label">
            <ToolbarButton icon={<ArrowClockwise24Regular />} onClick={refetch} aria-label="Refresh" />
          </Tooltip>
        }
        isFetching={isRefetching}
      />
      <PageBody isFetching={isRefetching}>
        {error ? (
          <div style={{ gridColumn: "span 12" }}>
            <ErrorBar error={error} onRetry={refetch} title="Couldn't load this issue" />
          </div>
        ) : isLoading ? (
          <DetailSkeleton />
        ) : notFound ? (
          <div style={{ gridColumn: "span 12" }}>
            <EmptyState
              icon={<DocumentQuestionMark24Regular />}
              title="Issue not found"
              hint="This fingerprint doesn't match any tracked issue."
              action={{ label: "Back to issues", onClick: () => navigate("/issues") }}
            />
          </div>
        ) : detail ? (
          <>
            <div style={{ gridColumn: "span 12" }}>
              <MessageBar intent="error">
                <MessageBarBody>{exceptionMessageFor(detail)}</MessageBarBody>
              </MessageBar>
            </div>

            <div style={{ gridColumn: "span 12" }}>
              <TabList
                selectedValue={activeTab}
                onTabSelect={(_e: SelectTabEvent, data: SelectTabData) => setActiveTab(data.value as TabKey)}
              >
                <Tab value="stack">Stack</Tab>
                <Tab value="occurrences">Occurrences</Tab>
                <Tab value="environment">Environment</Tab>
                <Tab value="logtail">Log tail</Tab>
              </TabList>
            </div>

            <div className={styles.mainColumn}>
              <Panel
                title={
                  activeTab === "stack"
                    ? "Stack"
                    : activeTab === "occurrences"
                      ? "Occurrences"
                      : activeTab === "environment"
                        ? "Environment"
                        : "Log tail"
                }
              >
                {activeTab === "stack" && <StackTable frames={detail.frames ?? []} />}
                {activeTab === "occurrences" && (
                  <div className={styles.gridHost}>
                    <DataGrid
                      items={detail.occurrences}
                      columns={occurrenceColumns}
                      columnSizingOptions={occurrenceColumnSizingOptions}
                      resizableColumns
                      sortable
                      size="small"
                      getRowId={(item: AppOccurrence) => item.id}
                    >
                      <DataGridHeader>
                        <DataGridRow>
                          {({ renderHeaderCell }) => <DataGridHeaderCell>{renderHeaderCell()}</DataGridHeaderCell>}
                        </DataGridRow>
                      </DataGridHeader>
                      <DataGridBody<AppOccurrence>>
                        {({ item, rowId }) => (
                          <DataGridRow<AppOccurrence>
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
                )}
                {activeTab === "environment" && <EnvironmentTable breakdowns={detail.breakdowns} />}
                {activeTab === "logtail" &&
                  (HAS_LOG_TAIL ? null : (
                    <EmptyState icon={<Bug24Regular />} title="No log tail in this issue" />
                  ))}
              </Panel>
            </div>

            <div className={styles.asideColumn}>
              <Panel title="Last 14 days">
                <Last14DaysChart sparkline14d={detail.sparkline14d} />
              </Panel>
              <Panel title="By version">
                <BreakdownBars entries={detail.breakdowns.version} />
              </Panel>
              <Panel title="By architecture">
                <BreakdownBars entries={detail.breakdowns.arch} />
              </Panel>
              <Panel title="By GPU tier">
                <BreakdownBars entries={detail.breakdowns.gpuTier} />
              </Panel>
              <Panel title="GitHub">
                <GitHubAction issue={detail.issue} />
              </Panel>
            </div>
          </>
        ) : null}
      </PageBody>
    </>
  );
}
