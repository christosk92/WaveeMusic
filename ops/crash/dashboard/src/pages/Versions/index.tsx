import {
  Badge,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Tooltip,
  ToolbarButton,
  makeStyles,
  tokens,
  type BadgeProps,
} from "@fluentui/react-components";
import { ArrowClockwise24Regular, DocumentQuestionMark24Regular } from "@fluentui/react-icons";
import { useMemo } from "react";
import { useVersions } from "../../api/hooks";
import { PageBody } from "../../scene/PageBody";
import { PageHeader } from "../../scene/PageHeader";
import { Panel } from "../../scene/Panel";
import { EmptyState } from "../../scene/EmptyState";
import { ErrorBar } from "../../scene/ErrorBar";
import { GridSkeleton } from "../../scene/skeletons";
import { formatCount } from "../../lib/format";
import { KINDS, type AppVersionCount, type Kind } from "../../api/types";

const useStyles = makeStyles({
  subtitleRow: {
    display: "inline-flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
  },
});

const KIND_HEADER: Record<Kind, string> = {
  Managed: "Managed",
  Native: "Native",
  Hang: "Hang",
  ExitCode: "Exit code",
  UncleanExit: "Unclean exit",
};

interface VersionRow {
  key: string;
  semver: string;
  quad: string;
  channel: string;
  arch: string;
  counts: Record<Kind, number>;
  total: number;
}

function channelFromQuad(quad: string): string {
  const dash = quad.lastIndexOf("-");
  return dash === -1 ? quad : quad.slice(dash + 1);
}

function channelBadgeColor(channel: string): NonNullable<BadgeProps["color"]> {
  switch (channel) {
    case "stable":
      return "success";
    case "beta":
      return "warning";
    default:
      return "informative";
  }
}

/** Aggregates the flat `AppVersionCount[]` (one row per semver+quad+arch+kind, plan §4) up to one row
 *  per semver+quad+arch with a count per `Kind` plus a total — the shape the Versions table renders. */
function aggregate(versions: AppVersionCount[]): VersionRow[] {
  const grouped = new Map<string, VersionRow>();
  for (const v of versions) {
    const key = `${v.semver}|${v.quad}|${v.arch}`;
    let row = grouped.get(key);
    if (!row) {
      row = {
        key,
        semver: v.semver,
        quad: v.quad,
        channel: channelFromQuad(v.quad),
        arch: v.arch,
        counts: { Managed: 0, Native: 0, Hang: 0, ExitCode: 0, UncleanExit: 0 },
        total: 0,
      };
      grouped.set(key, row);
    }
    const kind = v.kind as Kind;
    if (kind in row.counts) row.counts[kind] += v.count;
    row.total += v.count;
  }
  // Newest semver first; quad and arch only break ties so the ordering is stable across renders.
  return [...grouped.values()].sort((a, b) => {
    if (a.semver !== b.semver) return a.semver < b.semver ? 1 : -1;
    if (a.quad !== b.quad) return a.quad < b.quad ? 1 : -1;
    return a.arch < b.arch ? -1 : 1;
  });
}

/** Versions (plan §3): reports per version, counts only — there is no active-user denominator, so this
 *  is never framed as adoption. Loading/empty/error/ready + background-refetch all come from one
 *  `useVersions()` query, exactly like every other scene (plan §7). */
export default function VersionsPage() {
  const styles = useStyles();
  const versionsQuery = useVersions();

  const isLoading = versionsQuery.isLoading;
  const isRefetching = !isLoading && versionsQuery.isFetching;
  const error = versionsQuery.error;
  const versions = versionsQuery.data ?? [];
  const isEmpty = !isLoading && !error && versions.length === 0;

  const rows = useMemo(() => aggregate(versions), [versions]);

  function refetchAll() {
    void versionsQuery.refetch();
  }

  return (
    <>
      <PageHeader
        title="Versions"
        subtitle={
          <span className={styles.subtitleRow}>
            reports per version · counts only — there is no active-user denominator
          </span>
        }
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
            <ErrorBar error={error} onRetry={refetchAll} title="Couldn't load versions" />
          </div>
        ) : isLoading ? (
          <GridSkeleton title="Reports per version" span={12} rows={6} cols={8} />
        ) : isEmpty ? (
          <div style={{ gridColumn: "span 12" }}>
            <EmptyState
              icon={<DocumentQuestionMark24Regular />}
              title="No versions yet"
              hint="Once reports start coming in, they'll be broken down by version here."
            />
          </div>
        ) : (
          <Panel title="Reports per version" span={12}>
            <Table size="small" aria-label="Reports per version">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Version</TableHeaderCell>
                  <TableHeaderCell>Arch</TableHeaderCell>
                  {KINDS.map((kind) => (
                    <TableHeaderCell key={kind}>{KIND_HEADER[kind]}</TableHeaderCell>
                  ))}
                  <TableHeaderCell>Total</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.map((row) => (
                  <TableRow key={row.key}>
                    <TableCell>
                      <TableCellLayout
                        media={
                          <Badge appearance="tint" color={channelBadgeColor(row.channel)} size="small">
                            {row.channel}
                          </Badge>
                        }
                      >
                        {row.semver}
                      </TableCellLayout>
                    </TableCell>
                    <TableCell>{row.arch}</TableCell>
                    {KINDS.map((kind) => (
                      <TableCell key={kind}>{formatCount(row.counts[kind])}</TableCell>
                    ))}
                    <TableCell>{formatCount(row.total)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Panel>
        )}
      </PageBody>
    </>
  );
}
