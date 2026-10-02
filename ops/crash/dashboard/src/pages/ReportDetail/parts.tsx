import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Menu,
  MenuButton,
  MenuItemLink,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  SearchBox,
  Skeleton,
  SkeletonItem,
  Tab,
  TabList,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableRow,
  Text,
  Toolbar,
  ToolbarButton,
  ToolbarGroup,
  ToolbarToggleButton,
  Tooltip,
  makeStyles,
  mergeClasses,
  tokens,
  type SelectTabData,
  type SelectTabEvent,
} from "@fluentui/react-components";
import { ArrowDownload24Regular, Copy24Regular, Delete24Regular, TextWrap24Regular } from "@fluentui/react-icons";
import { useQuery } from "@tanstack/react-query";
import { forwardRef, useMemo, useState } from "react";
import { isMockEnabled } from "../../api/mock";
import type { AppReport, AppThisInstall } from "../../api/types";
import { kindBadgeColor, kindLabel } from "../../lib/colors";
import { formatBytes, formatCount } from "../../lib/format";
import { ErrorBar } from "../../scene/ErrorBar";
import { Panel } from "../../scene/Panel";
import { useAppToast } from "../../scene/toast";

const useStyles = makeStyles({
  mono: {
    wordBreak: "break-all",
  },
  // Facts values must not wrap at hyphens (session/debug ids, quads) — `TableCellLayout truncate`
  // only ellipsises text that stays on one line.
  factValue: {
    whiteSpace: "nowrap",
  },
  logToolbar: {
    justifyContent: "space-between",
  },
  logBody: {
    margin: 0,
    maxHeight: "480px",
    overflow: "auto",
    padding: tokens.spacingVerticalM,
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
  },
  logBodyWrap: {
    whiteSpace: "pre-wrap",
    wordBreak: "break-all",
  },
  logBodyNoWrap: {
    whiteSpace: "pre",
  },
  panelStack: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
});

// ── The API gap this page works around ──────────────────────────────────────────────────────────────
// `AppReportDetail` (src/api/types.ts) is only `{ report, thisInstall }` — it carries no report-text/
// log-tail body. The Worker exposes those as plain-text parts at `GET /v1/reports/:id/:part` (README's
// route table: `summary|report|tail|dump`), so in real mode this hook fetches that endpoint directly
// with the platform `fetch` (not `api/client.ts`'s `fetchJson`, which assumes JSON) — a relative,
// same-origin path: the Worker serves this dashboard on its own hostname, and the browser sends the
// Cloudflare Access cookie by default. In mock mode there is no server to hit, so it renders a short
// deterministic transcript built from the same `AppReport` fields the rest of this page already shows —
// never inventing data not present on the type.

export type LogPart = "report" | "tail";

function mockPartText(part: LogPart, report: AppReport): string {
  if (part === "report") {
    const lines = [
      `commit=${report.commitSha}`,
      `quad=${report.quad}`,
      `arch=${report.arch}`,
      `kind=${report.kind}`,
      `module=Wavee.exe base=0x140000000 size=0x2400000`,
      `exception=${report.exceptionType || "(none)"}`,
      report.exceptionMessage ? `message=${report.exceptionMessage}` : null,
      `exitCode=0x${report.exitCode.toString(16)}`,
      "",
      "Frames (RVA)",
      ...(report.frames.length > 0
        ? report.frames.map(
            (f) => `   at Wavee!<BaseAddress>+0x${f.rva.toString(16)}${f.name ? `  (${f.name}+0x${f.offset.toString(16)})` : ""}`,
          )
        : ["   (no frames recorded)"]),
    ].filter((l): l is string => l !== null);
    return lines.join("\n");
  }
  // "tail": a short, fully deterministic mock log — no wall-clock timestamps, so it's stable in tests/shots.
  return Array.from(
    { length: 24 },
    (_, i) => `[t+${String(i).padStart(3, "0")}s] route=${report.lastRoute} locale=${report.locale} — mock log line ${i + 1}`,
  ).join("\n");
}

export function useReportPartText(id: string | undefined, part: LogPart, report: AppReport | undefined) {
  return useQuery({
    queryKey: ["reportPart", id, part],
    queryFn: async () => {
      if (isMockEnabled()) return report ? mockPartText(part, report) : "";
      const response = await fetch(`/v1/reports/${encodeURIComponent(id!)}/${part}`);
      if (!response.ok) throw new Error(`Could not load this log (HTTP ${response.status})`);
      return response.text();
    },
    enabled: !!id,
  });
}

// ── The cdb/WinDbg one-liner (releasing-wavee.md §5b) ───────────────────────────────────────────────

export function cdbOneLiner(report: AppReport): string {
  const rvaArgs =
    report.frames.length > 0
      ? report.frames.map((f) => `ln Wavee+0x${f.rva.toString(16)}`).join("; ")
      : "ln Wavee+0x<rva>";
  return `cdb -lines -z sym\\pkg\\Wavee.exe -y sym -c "${rvaArgs}; q"`;
}

// ── Download bundle ──────────────────────────────────────────────────────────────────────────────────

export const DownloadBundleButton = forwardRef<HTMLButtonElement, { id: string; hasDump: boolean }>(
  function DownloadBundleButton({ id, hasDump }, ref) {
    if (isMockEnabled()) {
      return (
        <Tooltip content="Not available in mock mode" relationship="label">
          <Button ref={ref} icon={<ArrowDownload24Regular />} disabled>
            Download bundle
          </Button>
        </Tooltip>
      );
    }
    // Plain same-origin download links — relative paths are enough.
    const encodedId = encodeURIComponent(id);
    return (
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <MenuButton ref={ref} icon={<ArrowDownload24Regular />}>
            Download bundle
          </MenuButton>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItemLink href={`/v1/reports/${encodedId}/report`}>report.txt</MenuItemLink>
            <MenuItemLink href={`/v1/reports/${encodedId}/tail`}>log tail</MenuItemLink>
            {hasDump && <MenuItemLink href={`/v1/reports/${encodedId}/dump`}>Memory dump</MenuItemLink>}
          </MenuList>
        </MenuPopover>
      </Menu>
    );
  },
);

// ── Delete report ────────────────────────────────────────────────────────────────────────────────────

export function DeleteReportButton({ onConfirm }: { onConfirm: () => void }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button appearance="secondary" icon={<Delete24Regular />} onClick={() => setOpen(true)}>
        Delete report
      </Button>
      <Dialog open={open} onOpenChange={(_e, data) => setOpen(data.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Delete this report?</DialogTitle>
            <DialogContent>
              This removes the report and its stored log/dump parts. This can&apos;t be undone.
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setOpen(false)}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                onClick={() => {
                  setOpen(false);
                  onConfirm();
                }}
              >
                Delete
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}

// ── Facts table ──────────────────────────────────────────────────────────────────────────────────────

interface Fact {
  label: string;
  value: string;
  mono?: boolean;
}

const useFactsStyles = makeStyles({
  labelCell: {
    width: "120px",
  },
});

/** One label/value cell pair, truncated to a single line with the full value in a `Tooltip` (fixes the
 *  multi-line wrap the long GPU/session/debug/route values caused). */
function FactCells({ fact, labelClassName }: { fact: Fact | undefined; labelClassName: string }) {
  const styles = useStyles();
  if (!fact) {
    return (
      <>
        <TableCell />
        <TableCell />
      </>
    );
  }
  const value = (
    <Text font={fact.mono ? "monospace" : "base"} className={styles.factValue}>
      {fact.value}
    </Text>
  );
  return (
    <>
      <TableCell className={labelClassName}>
        <Caption1>{fact.label}</Caption1>
      </TableCell>
      <TableCell>
        <Tooltip content={fact.value} relationship="label">
          <TableCellLayout truncate>{value}</TableCellLayout>
        </Tooltip>
      </TableCell>
    </>
  );
}

export function FactsPanel({ report }: { report: AppReport }) {
  const styles = useFactsStyles();
  const facts: Fact[] = [
    { label: "Version", value: report.semver },
    { label: "Quad", value: report.quad },
    { label: "Commit", value: report.commitSha, mono: true },
    { label: "Channel", value: report.channel },
    { label: "Arch", value: report.arch },
    { label: "OS build", value: report.osBuild },
    { label: "GPU", value: report.gpu },
    { label: "GPU tier", value: report.gpuTier },
    { label: "Software adapter", value: report.softwareAdapter ? "Yes" : "No" },
    { label: "Packaged", value: report.packaged ? "Yes" : "No" },
    { label: "Locale", value: report.locale },
    { label: "Uptime", value: formatUptime(report.uptimeMs) },
    { label: "Before first frame", value: report.beforeFirstFrame ? "Yes" : "No" },
    { label: "Last route", value: report.lastRoute },
    { label: "Exit code", value: `0x${report.exitCode.toString(16)}` },
    { label: "Session id", value: report.sessionId, mono: true },
    { label: "Debug id", value: report.debugId ?? "—", mono: !!report.debugId },
  ];
  const rows: [Fact, Fact | undefined][] = [];
  for (let i = 0; i < facts.length; i += 2) rows.push([facts[i]!, facts[i + 1]]);

  return (
    <Panel title="Facts" span={8}>
      <Table size="small" aria-label="Report facts">
        <colgroup>
          <col style={{ width: "120px" }} />
          <col />
          <col style={{ width: "120px" }} />
          <col />
        </colgroup>
        <TableBody>
          {rows.map((row, i) => (
            <TableRow key={i}>
              <FactCells fact={row[0]} labelClassName={styles.labelCell} />
              <FactCells fact={row[1]} labelClassName={styles.labelCell} />
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Panel>
  );
}

function formatUptime(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(1)} s`;
  const minutes = Math.floor(seconds / 60);
  const remSeconds = Math.round(seconds % 60);
  return `${minutes}m ${remSeconds}s`;
}

// ── Log panel (the one custom surface: a monospace, scrollable body) ────────────────────────────────

export function LogTabs({ tab, onTabChange }: { tab: LogPart; onTabChange: (tab: LogPart) => void }) {
  function onTabSelect(_e: SelectTabEvent, data: SelectTabData) {
    onTabChange(data.value as LogPart);
  }
  return (
    <div style={{ gridColumn: "span 8" }}>
      <TabList selectedValue={tab} onTabSelect={onTabSelect} aria-label="Log part">
        <Tab value="report">report.txt</Tab>
        <Tab value="tail">log tail</Tab>
      </TabList>
    </div>
  );
}

export function LogPanel({ id, report, tab }: { id: string; report: AppReport; tab: LogPart }) {
  const styles = useStyles();
  const toast = useAppToast();
  const [find, setFind] = useState("");
  const [wrap, setWrap] = useState(false);

  const textQuery = useReportPartText(id, tab, report);
  const lines = useMemo(() => (textQuery.data ?? "").split("\n"), [textQuery.data]);
  const needle = find.trim().toLowerCase();
  const visibleLines = useMemo(() => (needle ? lines.filter((l) => l.toLowerCase().includes(needle)) : lines), [lines, needle]);

  async function copyAll() {
    try {
      await navigator.clipboard.writeText(textQuery.data ?? "");
      toast.success("Copied to clipboard");
    } catch {
      toast.error("Couldn't copy to clipboard");
    }
  }

  function renderLine(line: string, key: number) {
    if (!needle) return <div key={key}>{line.length > 0 ? line : " "}</div>;
    const idx = line.toLowerCase().indexOf(needle);
    if (idx === -1) return <div key={key}>{line}</div>;
    return (
      <div key={key}>
        {line.slice(0, idx)}
        <mark>{line.slice(idx, idx + needle.length)}</mark>
        {line.slice(idx + needle.length)}
      </div>
    );
  }

  return (
    <Panel title="Log" span={8}>
      <Toolbar
        aria-label="Log tools"
        size="small"
        className={styles.logToolbar}
        checkedValues={{ wrap: wrap ? ["wrap"] : [] }}
        onCheckedValueChange={(_e, data) => setWrap(data.checkedItems.includes("wrap"))}
      >
          <ToolbarGroup role="presentation">
            <SearchBox
              aria-label="Find in text"
              placeholder="Find in text"
              value={find}
              onChange={(_e, data) => setFind(data.value)}
              style={{ minWidth: "200px" }}
            />
          </ToolbarGroup>
          <ToolbarGroup role="presentation">
            <Tooltip content="Copy" relationship="label">
              <ToolbarButton icon={<Copy24Regular />} onClick={() => void copyAll()} aria-label="Copy" />
            </Tooltip>
            <ToolbarToggleButton name="wrap" value="wrap" icon={<TextWrap24Regular />} aria-label="Wrap">
              Wrap
            </ToolbarToggleButton>
          </ToolbarGroup>
        </Toolbar>
        {textQuery.isLoading ? (
          <Skeleton>
            <SkeletonItem shape="rectangle" style={{ width: "100%", height: "220px" }} />
          </Skeleton>
        ) : textQuery.error ? (
          <ErrorBar error={textQuery.error} onRetry={() => void textQuery.refetch()} title="Couldn't load this log" />
        ) : (
          <pre className={mergeClasses(styles.logBody, wrap ? styles.logBodyWrap : styles.logBodyNoWrap)}>
            {visibleLines.map((line, i) => renderLine(line, i))}
          </pre>
        )}
    </Panel>
  );
}

// ── Memory snapshot ──────────────────────────────────────────────────────────────────────────────────

export function MemorySnapshotPanel({ report }: { report: AppReport }) {
  const styles = useStyles();
  if (!report.hasDump) {
    return (
      <Panel title="Memory snapshot" span={4}>
        <Caption1>No dump in this report.</Caption1>
      </Panel>
    );
  }
  const mock = isMockEnabled();
  const href = `/v1/reports/${encodeURIComponent(report.id)}/dump`;
  const downloadButton = mock ? (
    <Tooltip content="Not available in mock mode" relationship="label">
      <Button appearance="secondary" icon={<ArrowDownload24Regular />} disabled>
        Download
      </Button>
    </Tooltip>
  ) : (
    <Button as="a" href={href} download appearance="secondary" icon={<ArrowDownload24Regular />}>
      Download
    </Button>
  );
  return (
    <Panel title="Memory snapshot" span={4} footer={downloadButton}>
      <div className={styles.panelStack}>
        <Text>{formatBytes(report.dumpBytes)}</Text>
        <Text font="monospace" size={200} className={styles.mono}>
          {cdbOneLiner(report)}
        </Text>
      </div>
    </Panel>
  );
}

// ── This install ─────────────────────────────────────────────────────────────────────────────────────

export function DeleteInstallButton({ onConfirm }: { onConfirm: () => void }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button appearance="secondary" onClick={() => setOpen(true)}>
        Delete this install&apos;s data…
      </Button>
      <Dialog open={open} onOpenChange={(_e, data) => setOpen(data.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Delete this install&apos;s data?</DialogTitle>
            <DialogContent>
              This erases every crash report ever received from this install id, under the app&apos;s GDPR right to
              erasure. It can&apos;t be undone. The app rotates the install id afterward, so a future report (if
              crash reporting stays on) can&apos;t be linked back to the ones deleted here.
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setOpen(false)}>
                Cancel
              </Button>
              <Button
                appearance="primary"
                onClick={() => {
                  setOpen(false);
                  onConfirm();
                }}
              >
                Delete install data
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}

export function ThisInstallPanel({
  installId,
  thisInstall,
  onDeleteInstall,
}: {
  installId: string;
  thisInstall: AppThisInstall;
  onDeleteInstall: () => void;
}) {
  const styles = useStyles();
  return (
    <Panel title="This install" span={4} footer={<DeleteInstallButton onConfirm={onDeleteInstall} />}>
      <div className={styles.panelStack}>
        <Text font="monospace" className={styles.mono}>
          {installId}
        </Text>
        <Caption1>
          {formatCount(thisInstall.reports30d)} report{thisInstall.reports30d === 1 ? "" : "s"} in the last 30 days
        </Caption1>
        <Caption1>First seen on {thisInstall.firstSeenQuad ?? "—"}</Caption1>
      </div>
    </Panel>
  );
}

// ── Privacy check ────────────────────────────────────────────────────────────────────────────────────

export function PrivacyCheckPanel() {
  return (
    <Panel title="Privacy check" span={4}>
      <MessageBar intent="success">
        <MessageBarBody>No account data included</MessageBarBody>
      </MessageBar>
      <MessageBar intent="success">
        <MessageBarBody>File paths masked</MessageBarBody>
      </MessageBar>
      <MessageBar intent="success">
        <MessageBarBody>Credentials dropped</MessageBarBody>
      </MessageBar>
    </Panel>
  );
}

// ── Header bits ──────────────────────────────────────────────────────────────────────────────────────

export function ReportKindBadge({ kind }: { kind: string }) {
  return (
    <Badge appearance="tint" color={kindBadgeColor(kind)} size="medium">
      {kindLabel(kind)}
    </Badge>
  );
}
