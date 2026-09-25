import type {
  AppFrame,
  AppIssue,
  AppIssueBreakdowns,
  AppIssueDetail,
  AppOccurrence,
  AppReport,
  AppReportDetail,
  AppReportsPage,
  AppSparklineDay,
  AppStats,
  AppSymbol,
  AppVersionCount,
  IssuesFilter,
  Kind,
  ReportsFilter,
} from "./types";

// ── Mock-mode switches (plan §4 `api/mock.ts`) ──────────────────────────────────────────────────────
// `VITE_MOCK=1` turns mock mode on. `VITE_MOCK_STATE=loading|empty|error|refetching` forces a state so
// every state is reviewable without a real backend; `?mockState=` in the URL overrides it for one page
// load (this is what `shots/take.mjs` uses to capture all states from a single running server).
// `VITE_MOCK_DELAY` (ms) adds latency for realism; also overridable with `?mockDelay=`.
//
// `refetching` is not a data shape — it's a *timing* state (plan: skeleton only on the very first load;
// a later refetch keeps the current content and shows `PageHeader`'s pinned `ProgressBar` instead). It
// resolves data normally but also makes `hooks.ts` set a short `refetchInterval`, so react-query cycles
// between "has data" and "isFetching" the whole time the page is open — `shots/take.mjs` waits for the
// `ProgressBar` to appear rather than racing a fixed delay.

export type MockState = "loading" | "empty" | "error" | "refetching" | null;

const MOCK_STATES: readonly MockState[] = ["loading", "empty", "error", "refetching"];

function readParam(name: string): string | null {
  if (typeof window === "undefined") return null;
  return new URLSearchParams(window.location.search).get(name);
}

export function isMockEnabled(): boolean {
  return import.meta.env.VITE_MOCK === "1" || import.meta.env.VITE_MOCK === "true";
}

export function getMockState(): MockState {
  const fromUrl = readParam("mockState");
  if (MOCK_STATES.includes(fromUrl as MockState)) return fromUrl as MockState;
  const fromEnv = import.meta.env.VITE_MOCK_STATE as string | undefined;
  if (MOCK_STATES.includes(fromEnv as MockState)) return fromEnv as MockState;
  return null;
}

/** `hooks.ts`'s cue to poll in the background so `refetching` has something to demonstrate — a fixed,
 *  fairly quick interval so `shots/take.mjs` never waits long for the `ProgressBar` to appear. */
export const MOCK_REFETCH_INTERVAL_MS = 900;

function getMockDelay(): number {
  const fromUrl = readParam("mockDelay");
  if (fromUrl && Number.isFinite(Number(fromUrl))) return Number(fromUrl);
  const fromEnv = import.meta.env.VITE_MOCK_DELAY as string | undefined;
  if (fromEnv && Number.isFinite(Number(fromEnv))) return Number(fromEnv);
  return 250;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Every mock endpoint funnels through this: `loading` never resolves, `error` rejects after the
 *  delay, `empty` resolves with the caller's zero-row variant, otherwise the real generated data. */
async function mockResult<T>(normal: () => T, empty: () => T): Promise<T> {
  const state = getMockState();
  if (state === "loading") return new Promise<T>(() => {});
  await delay(getMockDelay());
  if (state === "error") throw new Error("The crash service could not be reached. Check your connection and retry.");
  if (state === "empty") return empty();
  return normal();
}

// ── Deterministic seeded generator ──────────────────────────────────────────────────────────────────

function mulberry32(seed: number): () => number {
  let s = seed;
  return function next() {
    s |= 0;
    s = (s + 0x6d2b79f5) | 0;
    let t = Math.imul(s ^ (s >>> 15), 1 | s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const rng = mulberry32(0xc0ffee);

function pick<T>(arr: readonly T[]): T {
  return arr[Math.floor(rng() * arr.length)]!;
}

function randInt(min: number, max: number): number {
  return min + Math.floor(rng() * (max - min + 1));
}

const QUADS = [
  { semver: "0.3.2", quad: "0.3.2-stable", channel: "stable" },
  { semver: "0.3.2", quad: "0.3.2-beta", channel: "beta" },
  { semver: "0.3.1", quad: "0.3.1-stable", channel: "stable" },
  { semver: "0.3.0", quad: "0.3.0-stable", channel: "stable" },
] as const;
const ARCHES = ["x64", "arm64"] as const;
const GPUS = [
  { gpu: "NVIDIA GeForce RTX 3060", tier: "high" },
  { gpu: "AMD Radeon RX 6600", tier: "high" },
  { gpu: "Intel Iris Xe Graphics", tier: "mid" },
  { gpu: "NVIDIA GeForce GTX 1660", tier: "mid" },
  { gpu: "Microsoft Basic Render Driver", tier: "software" },
  { gpu: "Qualcomm Adreno 690", tier: "mid" },
] as const;
const OS_BUILDS = ["10.0.22631", "10.0.26100", "10.0.26340"] as const;
const LOCALES = ["en-US", "en-GB", "de-DE", "fr-FR", "pl-PL"] as const;
const ROUTES = ["/", "/album/4aawyAB9vmqN3uQ7FjRGTy", "/artist/06HL4z0CvFAxyc27GXpf02", "/playlist/37i9dQZF1DXcBWIGoYBM5M", "/search"];

interface IssueDef {
  title: string;
  kind: Kind;
  count: number;
  installs: number;
  status: "open" | "resolved" | "ignored";
  exceptionType: string;
  exceptionMessage: string;
  frameName: string | null;
  exitCode: number;
  githubIssue: number | null;
}

// The 23 issues (plan §4): four named anchors with the exact sample counts, plus 19 filler issues whose
// counts were chosen so the grand total is exactly 128 reports — the number `mock.ts` is meant to seed.
const ISSUE_DEFS: IssueDef[] = [
  {
    title: "NullReferenceException · Detail.UI.Hero.Render",
    kind: "Managed",
    count: 67,
    installs: 19,
    status: "open",
    exceptionType: "System.NullReferenceException",
    exceptionMessage: "Object reference not set to an instance of an object.",
    frameName: "Detail.UI.Hero.Render",
    exitCode: 0,
    githubIssue: 214,
  },
  {
    title: "Hang · Shell.Routes.Artist",
    kind: "Hang",
    count: 9,
    installs: 6,
    status: "open",
    exceptionType: "Hang",
    exceptionMessage: "UI thread unresponsive for 31s while navigating to /artist/…",
    frameName: "Shell.Routes.Artist",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "AccessViolation · nvwgf2umx.dll",
    kind: "Native",
    count: 3,
    installs: 3,
    status: "open",
    exceptionType: "AccessViolation",
    exceptionMessage: "Access violation at nvwgf2umx.dll+0x2f10c",
    frameName: null,
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "ExitCode 0xC0000409",
    kind: "ExitCode",
    count: 4,
    installs: 4,
    status: "open",
    exceptionType: "",
    exceptionMessage: "",
    frameName: null,
    exitCode: 0xc0000409,
    githubIssue: 227,
  },
  {
    title: "InvalidOperationException · Queue.Advance",
    kind: "Managed",
    count: 5,
    installs: 4,
    status: "open",
    exceptionType: "System.InvalidOperationException",
    exceptionMessage: "Collection was modified; enumeration operation may not execute.",
    frameName: "Playback.Queue.Advance",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "ArgumentNullException · Sidebar.Planner.Bind",
    kind: "Managed",
    count: 5,
    installs: 3,
    status: "open",
    exceptionType: "System.ArgumentNullException",
    exceptionMessage: "Value cannot be null. (Parameter 'source')",
    frameName: "Sidebar.Planner.Bind",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "IndexOutOfRangeException · Library.Rows.Layout",
    kind: "Managed",
    count: 4,
    installs: 3,
    status: "open",
    exceptionType: "System.IndexOutOfRangeException",
    exceptionMessage: "Index was outside the bounds of the array.",
    frameName: "Library.Rows.Layout",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "Hang · Shell.Routes.Album",
    kind: "Hang",
    count: 4,
    installs: 3,
    status: "resolved",
    exceptionType: "Hang",
    exceptionMessage: "UI thread unresponsive for 12s while navigating to /album/…",
    frameName: "Shell.Routes.Album",
    exitCode: 0,
    githubIssue: 198,
  },
  {
    title: "UnauthorizedAccessException · Diagnostics.Log.Rotate",
    kind: "Managed",
    count: 3,
    installs: 2,
    status: "open",
    exceptionType: "System.UnauthorizedAccessException",
    exceptionMessage: "Access to the path is denied.",
    frameName: "Diagnostics.Log.Rotate",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "AccessViolation · dxgi.dll",
    kind: "Native",
    count: 3,
    installs: 3,
    status: "open",
    exceptionType: "AccessViolation",
    exceptionMessage: "Access violation at dxgi.dll+0x4410",
    frameName: null,
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "ExitCode 0xC0000005",
    kind: "ExitCode",
    count: 3,
    installs: 3,
    status: "open",
    exceptionType: "",
    exceptionMessage: "",
    frameName: null,
    exitCode: 0xc0000005,
    githubIssue: null,
  },
  {
    title: "Unclean exit",
    kind: "UncleanExit",
    count: 2,
    installs: 2,
    status: "ignored",
    exceptionType: "",
    exceptionMessage: "",
    frameName: null,
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "NullReferenceException · Playback.Queue.Seed",
    kind: "Managed",
    count: 2,
    installs: 1,
    status: "open",
    exceptionType: "System.NullReferenceException",
    exceptionMessage: "Object reference not set to an instance of an object.",
    frameName: "Playback.Queue.Seed",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "Hang · Detail.Album.WhitespaceLayout",
    kind: "Hang",
    count: 2,
    installs: 2,
    status: "open",
    exceptionType: "Hang",
    exceptionMessage: "UI thread unresponsive for 8s while measuring the hero.",
    frameName: "Detail.Album.WhitespaceLayout",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "InvalidCastException · Video.Host.Bind",
    kind: "Managed",
    count: 2,
    installs: 1,
    status: "open",
    exceptionType: "System.InvalidCastException",
    exceptionMessage: "Unable to cast object of type 'VideoSurface' to type 'IVideoHost'.",
    frameName: "Video.Host.Bind",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "AccessViolation · igd10iumd64.dll",
    kind: "Native",
    count: 2,
    installs: 2,
    status: "open",
    exceptionType: "AccessViolation",
    exceptionMessage: "Access violation at igd10iumd64.dll+0x9a2",
    frameName: null,
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "ExitCode 0xC0000135",
    kind: "ExitCode",
    count: 2,
    installs: 1,
    status: "open",
    exceptionType: "",
    exceptionMessage: "",
    frameName: null,
    exitCode: 0xc0000135,
    githubIssue: null,
  },
  {
    title: "StackOverflowException · Scroll.WheelModel.Replan",
    kind: "Native",
    count: 1,
    installs: 1,
    status: "open",
    exceptionType: "System.StackOverflowException",
    exceptionMessage: "Stack overflow while re-planning the wheel curve.",
    frameName: "Scroll.WheelModel.Replan",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "NullReferenceException · WordsRail.Layout",
    kind: "Managed",
    count: 1,
    installs: 1,
    status: "open",
    exceptionType: "System.NullReferenceException",
    exceptionMessage: "Object reference not set to an instance of an object.",
    frameName: "WordsRail.Layout",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "Hang · Onboarding.SetupWizard.Advance",
    kind: "Hang",
    count: 1,
    installs: 1,
    status: "open",
    exceptionType: "Hang",
    exceptionMessage: "UI thread unresponsive for 6s during setup.",
    frameName: "Onboarding.SetupWizard.Advance",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "Unclean exit",
    kind: "UncleanExit",
    count: 1,
    installs: 1,
    status: "open",
    exceptionType: "",
    exceptionMessage: "",
    frameName: null,
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "TaskCanceledException · Symbolicate.Resolve",
    kind: "Managed",
    count: 1,
    installs: 1,
    status: "open",
    exceptionType: "System.Threading.Tasks.TaskCanceledException",
    exceptionMessage: "A task was canceled.",
    frameName: "Symbolicate.Resolve",
    exitCode: 0,
    githubIssue: null,
  },
  {
    title: "ExitCode 0xC000041D",
    kind: "ExitCode",
    count: 1,
    installs: 1,
    status: "open",
    exceptionType: "",
    exceptionMessage: "",
    frameName: null,
    exitCode: 0xc000041d,
    githubIssue: null,
  },
];

function fingerprintFor(index: number, def: IssueDef): string {
  const slug = def.title
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/(^-|-$)/g, "");
  return `fp-${String(index).padStart(2, "0")}-${slug}`.slice(0, 64);
}

function isoDaysAgo(days: number, hourJitter: number): string {
  const ms = Date.now() - days * 24 * 60 * 60 * 1000 - hourJitter * 60 * 60 * 1000;
  return new Date(ms).toISOString();
}

interface GeneratedIssue {
  def: IssueDef;
  fingerprint: string;
  reports: AppReport[];
}

function frameFor(def: IssueDef): AppFrame[] {
  if (!def.frameName) return [{ rva: randInt(0x1000, 0x9fffff), offset: randInt(0, 512), name: null }];
  return [
    { rva: randInt(0x1000, 0x9fffff), offset: randInt(0, 512), name: def.frameName },
    { rva: randInt(0x1000, 0x9fffff), offset: randInt(0, 512), name: "Fluent.Render.Frame" },
    { rva: randInt(0x1000, 0x9fffff), offset: randInt(0, 512), name: "Fluent.App.Run" },
  ];
}

function generateAll(): { issues: GeneratedIssue[]; allReports: AppReport[] } {
  const issues: GeneratedIssue[] = [];
  const allReports: AppReport[] = [];

  ISSUE_DEFS.forEach((def, defIndex) => {
    const fingerprint = fingerprintFor(defIndex, def);
    const installIds = Array.from({ length: def.installs }, (_, i) => `install-${defIndex.toString(16)}${i.toString(16)}-${rng().toString(16).slice(2, 8)}`);
    const reports: AppReport[] = [];
    for (let i = 0; i < def.count; i++) {
      const build = pick(QUADS);
      const arch = pick(ARCHES);
      const gpu = pick(GPUS);
      const installId = installIds[i % installIds.length]!;
      const days = randInt(0, 29);
      const hourJitter = randInt(0, 23);
      const reportId = `${fingerprint}-r${i.toString(16).padStart(3, "0")}`;
      const report: AppReport = {
        id: reportId,
        installId,
        sessionId: `sess-${reportId}`,
        kind: def.kind,
        quad: build.quad,
        semver: build.semver,
        commitSha: "a1b2c3d",
        channel: build.channel,
        arch,
        osBuild: pick(OS_BUILDS),
        gpu: gpu.gpu,
        gpuTier: gpu.tier,
        softwareAdapter: gpu.tier === "software",
        packaged: true,
        locale: pick(LOCALES),
        uptimeMs: randInt(2_000, 7_200_000),
        beforeFirstFrame: false,
        lastRoute: pick(ROUTES),
        exceptionType: def.exceptionType,
        exceptionMessage: def.exceptionMessage,
        exitCode: def.exitCode,
        hasDump: def.kind === "Native" || def.kind === "Managed",
        dumpBytes: def.kind === "Native" || def.kind === "Managed" ? randInt(200_000, 4_200_000) : 0,
        frames: frameFor(def),
        fingerprint,
        receivedAt: isoDaysAgo(days, hourJitter),
        debugId: "3F2504E0-4F89-11D3-9A0C-0305E82C3301",
      };
      reports.push(report);
      allReports.push(report);
    }
    reports.sort((a, b) => (a.receivedAt < b.receivedAt ? 1 : -1));
    issues.push({ def, fingerprint, reports });
  });

  allReports.sort((a, b) => (a.receivedAt < b.receivedAt ? 1 : -1));
  return { issues, allReports };
}

const GENERATED = generateAll();

function issueRowFor(g: GeneratedIssue): AppIssue {
  const versions: Record<string, number> = {};
  for (const r of g.reports) versions[r.semver] = (versions[r.semver] ?? 0) + 1;
  const receivedTimes = g.reports.map((r) => r.receivedAt).sort();
  return {
    fingerprint: g.fingerprint,
    title: g.def.title,
    kind: g.def.kind,
    firstSeen: receivedTimes[0] ?? isoDaysAgo(29, 0),
    lastSeen: receivedTimes[receivedTimes.length - 1] ?? isoDaysAgo(0, 0),
    count: g.def.count,
    installs: g.def.installs,
    versions,
    status: g.def.status,
    githubIssue: g.def.githubIssue,
  };
}

export const MOCK_ISSUES: AppIssue[] = GENERATED.issues.map(issueRowFor);
export const MOCK_REPORTS: AppReport[] = GENERATED.allReports;

export const MOCK_SYMBOLS: AppSymbol[] = QUADS.flatMap(({ quad }) =>
  ARCHES.map((arch, i) => ({
    quad,
    arch,
    debugId: quad === "0.3.0-stable" && arch === "arm64" ? null : "3F2504E0-4F89-11D3-9A0C-0305E82C3301",
    uploadedAt: quad === "0.3.0-stable" && arch === "arm64" ? null : isoDaysAgo(randInt(1, 40), i),
    entries: quad === "0.3.0-stable" && arch === "arm64" ? null : randInt(8_000, 42_000),
  })),
);

export const MOCK_VERSIONS: AppVersionCount[] = (() => {
  const grouped = new Map<string, AppVersionCount>();
  for (const r of MOCK_REPORTS) {
    const key = `${r.semver}|${r.quad}|${r.arch}|${r.kind}`;
    const existing = grouped.get(key);
    if (existing) existing.count += 1;
    else grouped.set(key, { semver: r.semver, quad: r.quad, arch: r.arch, kind: r.kind, count: 1 });
  }
  return [...grouped.values()].sort((a, b) => (a.semver < b.semver ? 1 : -1));
})();

function statsForReports(reports: AppReport[]): AppStats {
  const perDayMap = new Map<string, { crash: number; hang: number; closed: number }>();
  const perKind: Record<string, number> = {};
  const installs = new Set<string>();
  let hangs = 0;
  let native = 0;
  for (const r of reports) {
    installs.add(r.installId);
    perKind[r.kind] = (perKind[r.kind] ?? 0) + 1;
    if (r.kind === "Hang") hangs += 1;
    if (r.kind === "Native") native += 1;
    const day = r.receivedAt.slice(0, 10);
    const bucket = perDayMap.get(day) ?? { crash: 0, hang: 0, closed: 0 };
    if (r.kind === "Managed" || r.kind === "Native" || r.kind === "ExitCode") bucket.crash += 1;
    else if (r.kind === "Hang") bucket.hang += 1;
    else if (r.kind === "UncleanExit") bucket.closed += 1;
    perDayMap.set(day, bucket);
  }
  const perDay = [...perDayMap.entries()]
    .sort(([a], [b]) => (a < b ? -1 : 1))
    .map(([day, b]) => ({ day, ...b }));
  const openIssues = MOCK_ISSUES.filter((i) => i.status === "open").length;
  return {
    reports: reports.length,
    openIssues,
    installs: installs.size,
    hangs,
    native,
    perDay,
    perKind,
  };
}

export const MOCK_STATS_30D: AppStats = statsForReports(MOCK_REPORTS);

// ── Endpoint functions (mirror `api/hooks.ts`'s calls) ──────────────────────────────────────────────

export function mockStats(since?: string): Promise<AppStats> {
  return mockResult(
    () => {
      if (!since) return MOCK_STATS_30D;
      const cutoff = since;
      return statsForReports(MOCK_REPORTS.filter((r) => r.receivedAt >= cutoff));
    },
    () => ({ reports: 0, openIssues: 0, installs: 0, hangs: 0, native: 0, perDay: [], perKind: {} }),
  );
}

export function mockIssues(filter: IssuesFilter): Promise<AppIssue[]> {
  return mockResult(
    () => {
      let rows = MOCK_ISSUES;
      if (filter.status) rows = rows.filter((i) => i.status === filter.status);
      if (filter.since) rows = rows.filter((i) => i.lastSeen >= filter.since!);
      if (filter.version) rows = rows.filter((i) => filter.version! in i.versions);
      return rows;
    },
    () => [],
  );
}

export function mockIssue(fingerprint: string): Promise<AppIssueDetail | null> {
  return mockResult(
    () => {
      const g = GENERATED.issues.find((x) => x.fingerprint === fingerprint);
      if (!g) return null;
      const issue = issueRowFor(g);
      const occurrences: AppOccurrence[] = g.reports.slice(0, 50).map((r) => ({
        id: r.id,
        receivedAt: r.receivedAt,
        arch: r.arch,
        gpu: r.gpu,
        hasDump: r.hasDump,
        dumpBytes: r.dumpBytes,
      }));
      const fourteenDaysAgo = isoDaysAgo(14, 0);
      const sparklineMap = new Map<string, number>();
      for (const r of g.reports) {
        if (r.receivedAt < fourteenDaysAgo) continue;
        const day = r.receivedAt.slice(0, 10);
        sparklineMap.set(day, (sparklineMap.get(day) ?? 0) + 1);
      }
      const sparkline14d: AppSparklineDay[] = [...sparklineMap.entries()]
        .sort(([a], [b]) => (a < b ? -1 : 1))
        .map(([day, count]) => ({ day, count }));

      function group(selector: (r: AppReport) => string) {
        const m = new Map<string, number>();
        for (const r of g!.reports) m.set(selector(r), (m.get(selector(r)) ?? 0) + 1);
        return [...m.entries()].map(([label, count]) => ({ label, count })).sort((a, b) => b.count - a.count);
      }
      const breakdowns: AppIssueBreakdowns = {
        version: group((r) => r.semver),
        arch: group((r) => r.arch),
        gpuTier: group((r) => r.gpuTier),
      };
      return {
        issue,
        reports: g.reports,
        frames: g.reports[0]?.frames ?? null,
        occurrences,
        sparkline14d,
        breakdowns,
      };
    },
    () => null,
  );
}

export function mockReportsPage(filter: ReportsFilter): Promise<AppReportsPage> {
  return mockResult(
    () => {
      let rows = MOCK_REPORTS;
      if (filter.kind) rows = rows.filter((r) => r.kind === filter.kind);
      if (filter.quad) rows = rows.filter((r) => r.quad === filter.quad);
      if (filter.since) rows = rows.filter((r) => r.receivedAt >= filter.since!);
      if (filter.q) rows = rows.filter((r) => r.id.startsWith(filter.q!) || r.installId.startsWith(filter.q!));
      const limit = filter.limit ?? 50;
      return { reports: rows.slice(0, limit), nextCursor: rows.length > limit ? "mock-cursor" : null };
    },
    () => ({ reports: [], nextCursor: null }),
  );
}

export function mockReport(id: string): Promise<AppReportDetail | null> {
  return mockResult(
    () => {
      const report = MOCK_REPORTS.find((r) => r.id === id);
      if (!report) return null;
      const installReports = MOCK_REPORTS.filter((r) => r.installId === report.installId);
      const firstForInstall = [...installReports].sort((a, b) => (a.receivedAt < b.receivedAt ? -1 : 1))[0];
      return {
        report,
        thisInstall: { reports30d: installReports.length, firstSeenQuad: firstForInstall?.quad ?? null },
      };
    },
    () => null,
  );
}

export function mockVersions(): Promise<AppVersionCount[]> {
  return mockResult(
    () => MOCK_VERSIONS,
    () => [],
  );
}

export function mockSymbols(): Promise<AppSymbol[]> {
  return mockResult(
    () => MOCK_SYMBOLS,
    () => [],
  );
}
