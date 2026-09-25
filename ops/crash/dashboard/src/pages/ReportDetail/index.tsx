import { makeStyles, tokens } from "@fluentui/react-components";
import { DocumentQuestionMark24Regular } from "@fluentui/react-icons";
import { useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useDeleteInstall, useDeleteReport, useReport } from "../../api/hooks";
import { formatDateTime, shortId } from "../../lib/format";
import { EmptyState } from "../../scene/EmptyState";
import { ErrorBar } from "../../scene/ErrorBar";
import { PageBody } from "../../scene/PageBody";
import { PageHeader, type Crumb, type HeaderCommand } from "../../scene/PageHeader";
import { DetailSkeleton } from "../../scene/skeletons";
import { useAppToast } from "../../scene/toast";
import {
  DeleteReportButton,
  DownloadBundleButton,
  FactsPanel,
  LogPanel,
  LogTabs,
  MemorySnapshotPanel,
  PrivacyCheckPanel,
  ReportKindBadge,
  ThisInstallPanel,
  type LogPart,
} from "./parts";

const useStyles = makeStyles({
  // Two flex columns instead of letting each Panel's own `gridColumn: span n` fall into the page's
  // 12-col grid directly — that auto-placement staircased the aside panels onto new rows (Memory
  // snapshot stretching to Facts' height, This install pairing with the Log panel). A Panel's own
  // `gridColumn` style is harmless once its parent is a flex column instead of the grid.
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

/** Report detail (plan §3): facts, the report.txt/log-tail viewer, the memory snapshot, this-install
 *  erasure and the privacy check — everything read from one `useReport(id)` call plus the two erasure
 *  mutations. The report/log-tail text itself isn't on `AppReportDetail` (see `parts.tsx`'s note); this
 *  page's only job with that gap is to pass the id/report down to the hook that works around it. */
export default function ReportDetailPage() {
  const styles = useStyles();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const toast = useAppToast();
  const [logTab, setLogTab] = useState<LogPart>("report");

  const reportQuery = useReport(id);
  const deleteReportMutation = useDeleteReport();
  const deleteInstallMutation = useDeleteInstall();

  const isLoading = reportQuery.isLoading;
  const isFetching = !isLoading && reportQuery.isFetching;
  const error = reportQuery.error;
  const detail = reportQuery.data;

  function handleDeleteReport() {
    if (!id) return;
    deleteReportMutation.mutate(id, {
      onSuccess: () => {
        toast.success("Report deleted");
        navigate("/reports");
      },
      onError: (err) => {
        toast.error("Couldn't delete this report", err instanceof Error ? err.message : undefined);
      },
    });
  }

  function handleDeleteInstall() {
    if (!detail) return;
    deleteInstallMutation.mutate(detail.report.installId, {
      onSuccess: (result) => {
        toast.success(`Install data deleted (${result.deleted} report${result.deleted === 1 ? "" : "s"})`);
        navigate("/reports");
      },
      onError: (err) => {
        toast.error("Couldn't delete this install's data", err instanceof Error ? err.message : undefined);
      },
    });
  }

  /** A crumb with a real `href` (middle-click / new tab) that navigates client-side on a plain click. */
  function crumbTo(key: string, label: string, href: string): Crumb {
    return {
      key,
      label,
      href,
      onClick: (e) => {
        if (e.metaKey || e.ctrlKey || e.shiftKey) return;
        e.preventDefault();
        navigate(href);
      },
    };
  }

  const crumbs: Crumb[] | undefined = detail
    ? [
        crumbTo("issues", "Issues", "/issues"),
        crumbTo("issue", shortId(detail.report.fingerprint), `/issues/${encodeURIComponent(detail.report.fingerprint)}`),
        { key: "report", label: shortId(detail.report.id) },
      ]
    : undefined;

  const commands: HeaderCommand[] = detail
    ? [{ id: "download", node: <DownloadBundleButton id={detail.report.id} hasDump={detail.report.hasDump} /> }]
    : [];

  return (
    <>
      <PageHeader
        crumbs={crumbs}
        title={`Report ${id ? shortId(id) : ""}`}
        subtitle={
          detail ? (
            <>
              <ReportKindBadge kind={detail.report.kind} /> · {formatDateTime(detail.report.receivedAt)} ·{" "}
              {detail.report.semver} · {detail.report.arch}
            </>
          ) : undefined
        }
        commands={commands}
        rightCommands={detail ? <DeleteReportButton onConfirm={handleDeleteReport} /> : undefined}
        isFetching={isFetching}
      />
      <PageBody isFetching={isFetching}>
        {error ? (
          <div style={{ gridColumn: "span 12" }}>
            <ErrorBar error={error} onRetry={() => void reportQuery.refetch()} title="Couldn't load this report" />
          </div>
        ) : isLoading ? (
          <DetailSkeleton />
        ) : !detail ? (
          <div style={{ gridColumn: "span 12" }}>
            <EmptyState
              icon={<DocumentQuestionMark24Regular />}
              title="Report not found"
              action={{ label: "Back to reports", onClick: () => navigate("/reports") }}
            />
          </div>
        ) : (
          <>
            <div className={styles.mainColumn}>
              <FactsPanel report={detail.report} />
              <LogTabs tab={logTab} onTabChange={setLogTab} />
              <LogPanel id={detail.report.id} report={detail.report} tab={logTab} />
            </div>
            <div className={styles.asideColumn}>
              <MemorySnapshotPanel report={detail.report} />
              <ThisInstallPanel
                installId={detail.report.installId}
                thisInstall={detail.thisInstall}
                onDeleteInstall={handleDeleteInstall}
              />
              <PrivacyCheckPanel />
            </div>
          </>
        )}
      </PageBody>
    </>
  );
}
