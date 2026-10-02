import { keepPreviousData, useMutation, useQuery, useQueryClient, type UseQueryResult } from "@tanstack/react-query";
import { ApiError, buildQuery, fetchJson } from "./client";
import {
  mapDeleteReportResult,
  mapIssue,
  mapIssueDetail,
  mapReportDetail,
  mapReportsPage,
  mapRetentionResult,
  mapStats,
  mapSymbol,
  mapVersionCount,
} from "./mapping";
import {
  MOCK_REFETCH_INTERVAL_MS,
  getMockState,
  isMockEnabled,
  mockDeleteReport,
  mockIssue,
  mockIssues,
  mockReport,
  mockReportPartText,
  mockReportsPage,
  mockRunRetention,
  mockStats,
  mockSymbols,
  mockVersions,
} from "./mock";
import type {
  AppDeleteReportResult,
  AppIssue,
  AppIssueDetail,
  AppReport,
  AppReportDetail,
  AppReportsPage,
  AppRetentionResult,
  AppStats,
  AppSymbol,
  AppVersionCount,
  IssuePatch,
  IssuesFilter,
  ReportPart,
  ReportsFilter,
  WireDeleteReportResponse,
  WireIssueDetailResponse,
  WireIssueRow,
  WireIssuesListResponse,
  WireReportDetailResponse,
  WireReportsPageResponse,
  WireRetentionRunResponse,
  WireStats,
  WireSymbolsResponse,
  WireVersionsResponse,
} from "./types";

// ── Query keys ───────────────────────────────────────────────────────────────────────────────────────

export const queryKeys = {
  stats: (since?: string) => ["stats", since ?? "30d"] as const,
  issues: (filter: IssuesFilter) => ["issues", filter] as const,
  issue: (fp: string) => ["issue", fp] as const,
  reports: (filter: ReportsFilter) => ["reports", filter] as const,
  report: (id: string) => ["report", id] as const,
  reportPart: (id: string, part: ReportPart) => ["reportPart", id, part] as const,
  versions: () => ["versions"] as const,
  symbols: () => ["symbols"] as const,
};

/** `VITE_MOCK_STATE=refetching`/`?mockState=refetching` (plan: skeleton only on the first load; any
 *  later refetch keeps the content and shows `PageHeader`'s pinned `ProgressBar` instead) has nothing to
 *  demonstrate unless something keeps refetching — so mock mode polls at a short, fixed interval only in
 *  that state. Real (non-mock) queries never set this. */
function mockRefetchInterval(): number | false {
  return isMockEnabled() && getMockState() === "refetching" ? MOCK_REFETCH_INTERVAL_MS : false;
}

/** A detail read whose id the Worker doesn't know (404) resolves to null — the page's "not found" state —
 *  instead of an error: a deleted report or an erased issue is a normal outcome, not a failure. */
async function nullOn404<T>(read: () => Promise<T>): Promise<T | null> {
  try {
    return await read();
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) return null;
    throw e;
  }
}

// ── Reads ────────────────────────────────────────────────────────────────────────────────────────────

export function useStats(since?: string): UseQueryResult<AppStats> {
  return useQuery({
    queryKey: queryKeys.stats(since),
    queryFn: async () => {
      if (isMockEnabled()) return mockStats(since);
      const wire = await fetchJson<WireStats>(`/v1/stats${buildQuery({ since })}`);
      return mapStats(wire);
    },
    refetchInterval: mockRefetchInterval(),
    // A range/filter change is a new query key with no cache. Without this react-query would report
    // `isLoading` again and the page would fall back to the Skeleton; keeping the previous page's data
    // as placeholder makes it an `isFetching`-only refetch (plan §7: shimmer only on the very first load).
    placeholderData: keepPreviousData,
  });
}

export function useIssues(filter: IssuesFilter = {}): UseQueryResult<AppIssue[]> {
  return useQuery({
    queryKey: queryKeys.issues(filter),
    queryFn: async () => {
      if (isMockEnabled()) return mockIssues(filter);
      const wire = await fetchJson<WireIssuesListResponse>(`/v1/issues${buildQuery(filter)}`);
      return wire.issues.map(mapIssue);
    },
    refetchInterval: mockRefetchInterval(),
    placeholderData: keepPreviousData,
  });
}

export function useIssue(fingerprint: string | undefined): UseQueryResult<AppIssueDetail | null> {
  return useQuery({
    queryKey: queryKeys.issue(fingerprint ?? ""),
    queryFn: async () => {
      if (!fingerprint) return null;
      if (isMockEnabled()) return mockIssue(fingerprint);
      return nullOn404(async () =>
        mapIssueDetail(await fetchJson<WireIssueDetailResponse>(`/v1/issues/${encodeURIComponent(fingerprint)}`)),
      );
    },
    enabled: !!fingerprint,
  });
}

export function useReports(filter: ReportsFilter = {}): UseQueryResult<AppReportsPage> {
  return useQuery({
    queryKey: queryKeys.reports(filter),
    queryFn: async () => {
      if (isMockEnabled()) return mockReportsPage(filter);
      const wire = await fetchJson<WireReportsPageResponse>(`/v1/reports${buildQuery(filter)}`);
      return mapReportsPage(wire);
    },
    refetchInterval: mockRefetchInterval(),
    placeholderData: keepPreviousData,
  });
}

export function useReport(id: string | undefined): UseQueryResult<AppReportDetail | null> {
  return useQuery({
    queryKey: queryKeys.report(id ?? ""),
    queryFn: async () => {
      if (!id) return null;
      if (isMockEnabled()) return mockReport(id);
      return nullOn404(async () =>
        mapReportDetail(await fetchJson<WireReportDetailResponse>(`/v1/reports/${encodeURIComponent(id)}`)),
      );
    },
    enabled: !!id,
  });
}

/** One plain-text part of a report (`GET /v1/reports/:id/report|tail`) — report.txt or the log tail. Not
 *  JSON, so this uses the platform `fetch` on the same relative, same-origin path rather than
 *  `fetchJson`. In mock mode the text is built from `report`'s own fields (`api/mock.ts`). */
export function useReportPartText(
  id: string | undefined,
  part: ReportPart,
  report: AppReport | undefined,
): UseQueryResult<string> {
  return useQuery({
    queryKey: queryKeys.reportPart(id ?? "", part),
    queryFn: async () => {
      if (isMockEnabled()) return report ? mockReportPartText(part, report) : "";
      const response = await fetch(`/v1/reports/${encodeURIComponent(id!)}/${part}`);
      if (!response.ok) throw new ApiError(response.status, `Could not load this log (HTTP ${response.status})`);
      return response.text();
    },
    enabled: !!id,
  });
}

export function useVersions(): UseQueryResult<AppVersionCount[]> {
  return useQuery({
    queryKey: queryKeys.versions(),
    queryFn: async () => {
      if (isMockEnabled()) return mockVersions();
      const wire = await fetchJson<WireVersionsResponse>("/v1/versions");
      return wire.versions.map(mapVersionCount);
    },
    refetchInterval: mockRefetchInterval(),
  });
}

export function useSymbols(): UseQueryResult<AppSymbol[]> {
  return useQuery({
    queryKey: queryKeys.symbols(),
    queryFn: async () => {
      if (isMockEnabled()) return mockSymbols();
      const wire = await fetchJson<WireSymbolsResponse>("/v1/symbols");
      return wire.symbols.map(mapSymbol);
    },
    refetchInterval: mockRefetchInterval(),
  });
}

// ── Mutations ────────────────────────────────────────────────────────────────────────────────────────

export function usePatchIssue() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ fingerprint, patch }: { fingerprint: string; patch: IssuePatch }) => {
      if (isMockEnabled()) {
        // Mock mode has no server-side state to mutate; the caller's optimistic UI (toast + refetch)
        // is enough for a WP1 shell. Later work packages can add an in-memory mock store if needed.
        return { fingerprint, ...patch };
      }
      const body = JSON.stringify({
        status: patch.status,
        github_issue: patch.githubIssue,
      });
      const wire = await fetchJson<{ issue: WireIssueRow }>(`/v1/issues/${encodeURIComponent(fingerprint)}`, {
        method: "PATCH",
        body,
      });
      return mapIssue(wire.issue);
    },
    onSuccess: (_data, variables) => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.issue(variables.fingerprint) });
      void queryClient.invalidateQueries({ queryKey: ["issues"] });
    },
  });
}

export function useDeleteInstall() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (installId: string) => {
      if (isMockEnabled()) return { deleted: 0 };
      return fetchJson<{ deleted: number }>(`/v1/installs/${encodeURIComponent(installId)}`, { method: "DELETE" });
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["reports"] });
      void queryClient.invalidateQueries({ queryKey: ["issues"] });
      void queryClient.invalidateQueries({ queryKey: ["issue"] });
      // Every `useStats(since)` key — the pages pass ISO cutoffs, so `queryKeys.stats()` alone matched none.
      void queryClient.invalidateQueries({ queryKey: ["stats"] });
    },
  });
}

/** `DELETE /v1/reports/:id` — removes the report row and its stored parts; the Worker recomputes the issue it
 *  belonged to (or deletes it when this was its last report) and answers with that fingerprint. */
export function useDeleteReport() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string): Promise<AppDeleteReportResult> => {
      if (isMockEnabled()) return mockDeleteReport(id);
      const wire = await fetchJson<WireDeleteReportResponse>(`/v1/reports/${encodeURIComponent(id)}`, {
        method: "DELETE",
      });
      return mapDeleteReportResult(wire);
    },
    onSuccess: (result, id) => {
      void queryClient.invalidateQueries({ queryKey: ["reports"] });
      void queryClient.invalidateQueries({ queryKey: ["issues"] });
      void queryClient.invalidateQueries({ queryKey: queryKeys.issue(result.fingerprint) });
      void queryClient.invalidateQueries({ queryKey: ["stats"] });
      void queryClient.invalidateQueries({ queryKey: ["versions"] });
      // The report is gone: mark it stale without refetching it (that would only be a 404 while the page
      // that deleted it navigates away); a later visit refetches and shows "Report not found".
      void queryClient.invalidateQueries({ queryKey: queryKeys.report(id), refetchType: "none" });
    },
  });
}

/** `POST /v1/retention/run` — the daily purge on demand: reports (and their stored parts) older than 90 days
 *  are deleted, in capped batches; `more` says the cap was hit. Issues keep their counts and status. */
export function useRunRetention() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (): Promise<AppRetentionResult> => {
      if (isMockEnabled()) return mockRunRetention();
      const wire = await fetchJson<WireRetentionRunResponse>("/v1/retention/run", { method: "POST" });
      return mapRetentionResult(wire);
    },
    onSuccess: (result) => {
      if (result.deleted === 0) return;
      for (const key of ["reports", "report", "reportPart", "issue", "stats", "versions"]) {
        void queryClient.invalidateQueries({ queryKey: [key] });
      }
    },
  });
}
