import { keepPreviousData, useMutation, useQuery, useQueryClient, type UseQueryResult } from "@tanstack/react-query";
import { buildQuery, fetchJson } from "./client";
import {
  mapIssue,
  mapIssueDetail,
  mapReportDetail,
  mapReportsPage,
  mapStats,
  mapSymbol,
  mapVersionCount,
} from "./mapping";
import {
  MOCK_REFETCH_INTERVAL_MS,
  getMockState,
  isMockEnabled,
  mockIssue,
  mockIssues,
  mockReport,
  mockReportsPage,
  mockStats,
  mockSymbols,
  mockVersions,
} from "./mock";
import type {
  AppIssue,
  AppIssueDetail,
  AppReportDetail,
  AppReportsPage,
  AppStats,
  AppSymbol,
  AppVersionCount,
  IssuePatch,
  IssuesFilter,
  ReportsFilter,
  WireIssueDetailResponse,
  WireIssueRow,
  WireIssuesListResponse,
  WireReportDetailResponse,
  WireReportsPageResponse,
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
      const wire = await fetchJson<WireIssueDetailResponse>(`/v1/issues/${encodeURIComponent(fingerprint)}`);
      return mapIssueDetail(wire);
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
      const wire = await fetchJson<WireReportDetailResponse>(`/v1/reports/${encodeURIComponent(id)}`);
      return mapReportDetail(wire);
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
      void queryClient.invalidateQueries({ queryKey: queryKeys.stats() });
    },
  });
}

/** There is no `DELETE /v1/reports/:id` route on the Worker (only `/v1/installs/:id` — see
 *  `ops/crash/README.md`'s route table); this mutation is the client-side seam plan §3's Report
 *  detail page names ("Delete report", danger group). It stays a stub returning `{deleted: 0}` until
 *  the Worker grows that route — wired here now so pages don't have to know the difference. */
export function useDeleteReport() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (_id: string) => {
      return Promise.resolve({ deleted: 0 });
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["reports"] });
    },
  });
}
