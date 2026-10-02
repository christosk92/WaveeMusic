import { FluentProvider, Toaster, webLightTheme } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from "vitest";
import { MOCK_REPORTS } from "../../api/mock";
import type { WireReportDetailResponse, WireReportRow } from "../../api/types";
import { APP_TOASTER_ID } from "../../scene/toast";
import ReportDetailPage from "./index";

const SAMPLE_REPORT_ID = MOCK_REPORTS[0]!.id;
const NATIVE_REPORT = MOCK_REPORTS.find((r) => r.kind === "Native" && r.faultModule === "nvwgf2umx.dll")!;

function renderReportDetail(id: string = SAMPLE_REPORT_ID) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={[`/reports/${id}`]}>
          <Routes>
            <Route path="/reports" element={<div>REPORTS_LIST_STUB</div>} />
            <Route path="/reports/:id" element={<ReportDetailPage />} />
          </Routes>
        </MemoryRouter>
        <Toaster toasterId={APP_TOASTER_ID} />
      </QueryClientProvider>
    </FluentProvider>,
  );
}

describe("Report detail page (mock mode)", () => {
  beforeEach(() => {
    vi.stubEnv("VITE_MOCK", "1");
    vi.stubEnv("VITE_MOCK_DELAY", "0");
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllEnvs();
  });

  it("renders the facts panel and the this-install panel once loaded", async () => {
    renderReportDetail();

    expect(await screen.findByText("Facts", {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByText("This install")).toBeInTheDocument();
    expect(screen.getByText(MOCK_REPORTS[0]!.installId)).toBeInTheDocument();
    expect(screen.getByText("Privacy check")).toBeInTheDocument();
    expect(screen.getByText("Log")).toBeInTheDocument();
  });

  it("shows a native report's fault as code name, module and offset", async () => {
    renderReportDetail(NATIVE_REPORT.id);

    expect(await screen.findByText("Native fault", {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByText("ACCESS_VIOLATION in nvwgf2umx.dll+0x2f10c")).toBeInTheDocument();
  });

  it("shows the not-found empty state for an unknown report id", async () => {
    renderReportDetail("does-not-exist");
    expect(await screen.findByText("Report not found", {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /back to reports/i })).toBeInTheDocument();
  });

  it("opens the delete-install dialog and confirms erasure", async () => {
    renderReportDetail();

    await screen.findByText("Facts", {}, { timeout: 3000 });
    fireEvent.click(screen.getByRole("button", { name: /delete this install's data/i }));

    expect(await screen.findByText(/GDPR right to erasure/i)).toBeInTheDocument();
    // Fluent's Dialog mounts its portal content behind the same focus-trap/inert machinery jsdom can't
    // fully emulate, which makes testing-library's default `getByRole` treat it as hidden even though
    // it's visibly open — `hidden: true` opts back into finding it (a known Fluent v9 + jsdom quirk).
    fireEvent.click(screen.getByRole("button", { name: "Delete install data", hidden: true }));

    expect(await screen.findByText(/install data deleted/i)).toBeInTheDocument();
  });
});

// ── Against the real API shape (mock mode off, `fetch` stubbed) ──────────────────────────────────────

const REPORT_ID = "3f9c2b1a8e4d4c0b9a7f6e5d4c3b2a19";

const wireReport: WireReportRow = {
  id: REPORT_ID,
  install_id: "0b1c2d3e4f5a46b7a8c9d0e1f2a3b4c5",
  session_id: "sess-1",
  kind: "Managed",
  quad: "1.2.1011.0",
  semver: "0.3.2",
  commit_sha: "abc1234",
  channel: "stable",
  arch: "arm64",
  os_build: "10.0.26100",
  gpu: "Qualcomm Adreno 690",
  gpu_tier: "mid",
  software_adapter: 0,
  packaged: 1,
  locale: "en-US",
  uptime_ms: 12345,
  before_first_frame: 0,
  last_route: "/artist/1",
  exception_type: "System.InvalidOperationException",
  exception_message: "Sequence contains no elements",
  exit_code: 0,
  has_dump: 0,
  dump_bytes: 0,
  frames_json: "[]",
  fingerprint: "fp-artist-render",
  received_at: "2026-10-01T12:00:00.000Z",
  debug_id: null,
  exception_code: 0,
  fault_module: "",
  fault_offset: 0,
  fp_version: 2,
};

/** Just what `fetchJson` and `useReportPartText` read off a `Response`. */
function fakeResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? "OK" : "Error",
    json: async () => body,
    text: async () => (typeof body === "string" ? body : JSON.stringify(body)),
  } as unknown as Response;
}

type FetchFn = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

describe("Report detail page (real API)", () => {
  let fetchMock: Mock<FetchFn>;

  beforeEach(() => {
    vi.stubEnv("VITE_MOCK", "");
    fetchMock = vi.fn<FetchFn>(async (input, init) => {
      const url = String(input);
      const method = init?.method ?? "GET";
      if (method === "DELETE" && url === `/v1/reports/${REPORT_ID}`) {
        return fakeResponse(200, { deleted: 1, fingerprint: "fp-artist-render" });
      }
      if (method === "GET" && url === `/v1/reports/${REPORT_ID}`) {
        const detail: WireReportDetailResponse = {
          report: wireReport,
          this_install: { reports_30d: 1, first_seen_quad: "1.2.1011.0" },
        };
        return fakeResponse(200, detail);
      }
      if (method === "GET" && url.startsWith(`/v1/reports/${REPORT_ID}/`)) {
        return fakeResponse(200, "report.txt body");
      }
      return fakeResponse(404, { error: "not found" });
    });
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.unstubAllEnvs();
  });

  it("deletes the report with DELETE /v1/reports/:id, then returns to the reports list", async () => {
    renderReportDetail(REPORT_ID);

    await screen.findByText("Facts", {}, { timeout: 3000 });
    fireEvent.click(screen.getByRole("button", { name: "Delete report" }));
    expect(await screen.findByText("Delete this report?")).toBeInTheDocument();
    // Same Fluent v9 + jsdom Dialog quirk as the delete-install test above.
    fireEvent.click(screen.getByRole("button", { name: "Delete", hidden: true }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(`/v1/reports/${REPORT_ID}`, expect.objectContaining({ method: "DELETE" })),
    );
    expect(await screen.findByText("REPORTS_LIST_STUB")).toBeInTheDocument();
    expect(await screen.findByText("Report deleted")).toBeInTheDocument();
  });
});
