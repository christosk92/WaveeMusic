import {
  Badge,
  Checkbox,
  Link,
  TableCellLayout,
  Tooltip,
  createTableColumn,
  type TableColumnDefinition,
  type TableColumnSizingOptions,
} from "@fluentui/react-components";
import type { MouseEvent as ReactMouseEvent } from "react";
import type { AppIssue } from "../../api/types";
import { kindBadgeColor, statusBadgeColor } from "../../lib/colors";
import { formatCount, formatRelativeTime } from "../../lib/format";

export const GITHUB_REPO_URL = "https://github.com/christosk92/WaveeMusic";

export function githubIssueUrl(n: number): string {
  return `${GITHUB_REPO_URL}/issues/${n}`;
}

/** Stops the click from bubbling to the `DataGridRow`'s own `onClick` (row-click-navigates), used by
 *  the selection checkbox and the GitHub link cell so acting on them never also navigates. */
function stopRowClick(e: ReactMouseEvent) {
  e.stopPropagation();
}

/** Top few semvers by report count, compact ("0.3.2, 0.3.1"). */
export function versionsText(versions: Record<string, number>): string {
  return Object.entries(versions)
    .sort(([, a], [, b]) => b - a)
    .slice(0, 3)
    .map(([semver]) => semver)
    .join(", ");
}

function capitalize(s: string): string {
  return s.length === 0 ? s : s.charAt(0).toUpperCase() + s.slice(1);
}

export interface SelectionState {
  isSelected: (fp: string) => boolean;
  toggle: (fp: string) => void;
}

/** The manual selection column (plan §3 Issues: multiselect + row-click-to-navigate together). Fluent's
 *  built-in `selectionMode` toggles the row on *any* click when selection is enabled, which would fight
 *  with "row click navigates" — so selection here is our own `Set<string>` state and a plain `Checkbox`
 *  that stops its click from reaching the row. */
export function selectionColumn(selection: SelectionState): TableColumnDefinition<AppIssue> {
  return createTableColumn<AppIssue>({
    columnId: "select",
    renderHeaderCell: () => "",
    renderCell: (item) => (
      <span onClick={stopRowClick}>
        <Checkbox
          checked={selection.isSelected(item.fingerprint)}
          onChange={() => selection.toggle(item.fingerprint)}
          aria-label={`Select ${item.title}`}
        />
      </span>
    ),
  });
}

export const issueColumns: TableColumnDefinition<AppIssue>[] = [
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
    columnId: "status",
    compare: (a, b) => a.status.localeCompare(b.status),
    renderHeaderCell: () => "Status",
    renderCell: (item) => (
      <Badge appearance="tint" color={statusBadgeColor(item.status)} size="small">
        {capitalize(item.status)}
      </Badge>
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
    columnId: "versions",
    renderHeaderCell: () => "Versions",
    renderCell: (item) => {
      const text = versionsText(item.versions) || "—";
      return (
        <Tooltip content={text} relationship="label">
          <TableCellLayout truncate>{text}</TableCellLayout>
        </Tooltip>
      );
    },
  }),
  createTableColumn<AppIssue>({
    columnId: "firstSeen",
    compare: (a, b) => (a.firstSeen < b.firstSeen ? -1 : 1),
    renderHeaderCell: () => "First seen",
    renderCell: (item) => formatRelativeTime(item.firstSeen),
  }),
  createTableColumn<AppIssue>({
    columnId: "lastSeen",
    compare: (a, b) => (a.lastSeen < b.lastSeen ? -1 : 1),
    renderHeaderCell: () => "Last seen",
    renderCell: (item) => formatRelativeTime(item.lastSeen),
  }),
  createTableColumn<AppIssue>({
    columnId: "github",
    renderHeaderCell: () => "GitHub",
    renderCell: (item) =>
      item.githubIssue ? (
        <span onClick={stopRowClick}>
          <Link href={githubIssueUrl(item.githubIssue)} target="_blank" rel="noreferrer">
            #{item.githubIssue}
          </Link>
        </span>
      ) : (
        "—"
      ),
  }),
];

/** Fluent's `resizableColumns` auto-fit shrinks every column down to its `minWidth` before an
 *  overflow is even possible (`adjustColumnWidthsToFitContainer` in `@fluentui/react-table`), so these
 *  `minWidth`s are the true floor: 40+300+90+70+80+150+110+110+70 = 1020px. At 1280px the Panel's content
 *  area is ≈940px, so the grid still needs `index.tsx`'s `overflowX: "auto"` wrapper below that floor —
 *  by design, not a bug (a screenshot at 1280 will show a short horizontal scrollbar, not clipped text). */
export const issueColumnSizingOptions: TableColumnSizingOptions = {
  // Min widths sum to 880px so the whole grid, GitHub column included, fits the ≈940px card at 1280px
  // without the in-card horizontal scroll (which only kicks in below that).
  select: { minWidth: 40, idealWidth: 40, defaultWidth: 40 },
  title: { minWidth: 250, idealWidth: 480 },
  status: { minWidth: 80, idealWidth: 90 },
  count: { minWidth: 60, idealWidth: 70 },
  installs: { minWidth: 70, idealWidth: 80 },
  versions: { minWidth: 120, idealWidth: 150 },
  firstSeen: { minWidth: 100, idealWidth: 110 },
  lastSeen: { minWidth: 100, idealWidth: 110 },
  github: { minWidth: 60, idealWidth: 70 },
};
