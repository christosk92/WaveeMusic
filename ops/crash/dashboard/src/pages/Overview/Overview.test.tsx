import { FluentProvider, Toaster, webLightTheme } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from "vitest";
import type { WireRetentionRunResponse, WireStats } from "../../api/types";
import { APP_TOASTER_ID } from "../../scene/toast";
import OverviewPage from "./index";

function renderOverview() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={["/"]}>
          <OverviewPage />
        </MemoryRouter>
        <Toaster toasterId={APP_TOASTER_ID} />
      </QueryClientProvider>
    </FluentProvider>,
  );
}

describe("Overview page (mock mode)", () => {
  beforeEach(() => {
    vi.stubEnv("VITE_MOCK", "1");
    vi.stubEnv("VITE_MOCK_DELAY", "0");
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllEnvs();
  });

  it("renders the header and, once loaded, the stat tiles and top issues", async () => {
    renderOverview();

    expect(screen.getByText("Overview")).toBeInTheDocument();

    // Wait for the mock fetch to resolve (loading skeleton has no text) before asserting on content.
    expect(await screen.findByText("Open issues", {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getAllByText("Reports").length).toBeGreaterThan(0);
    expect(screen.getByText("Installs affected")).toBeInTheDocument();
    expect(screen.getByText("Hangs")).toBeInTheDocument();
    expect(screen.getByText("Native crashes")).toBeInTheDocument();

    // The named anchor issue from the mock data set (plan §4) should surface as the top issue.
    expect(await screen.findByText(/NullReferenceException · Detail\.UI\.Hero\.Render/)).toBeInTheDocument();
  });

  it("shows the empty state when VITE_MOCK_STATE=empty", async () => {
    vi.stubEnv("VITE_MOCK_STATE", "empty");
    renderOverview();
    expect(await screen.findByText("No crash reports yet")).toBeInTheDocument();
  });

  it("shows the error state with a retry action when VITE_MOCK_STATE=error", async () => {
    vi.stubEnv("VITE_MOCK_STATE", "error");
    renderOverview();
    expect(await screen.findByText("Couldn't load the overview")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
  });
});

// ── Against the real API shape (mock mode off, `fetch` stubbed) ──────────────────────────────────────

type FetchFn = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

/** Just what `fetchJson` reads off a `Response`. */
function fakeResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? "OK" : "Error",
    json: async () => body,
  } as unknown as Response;
}

const EMPTY_STATS: WireStats = { reports: 0, openIssues: 0, installs: 0, hangs: 0, native: 0, perDay: [], perKind: {} };

describe("Overview page (real API)", () => {
  let fetchMock: Mock<FetchFn>;

  beforeEach(() => {
    vi.stubEnv("VITE_MOCK", "");
    fetchMock = vi.fn<FetchFn>(async (input, init) => {
      const url = String(input);
      if (init?.method === "POST" && url === "/v1/retention/run") {
        const result: WireRetentionRunResponse = { deleted: 2000, batches: 10, more: true };
        return fakeResponse(200, result);
      }
      if (url.startsWith("/v1/stats")) return fakeResponse(200, EMPTY_STATS);
      if (url.startsWith("/v1/issues")) return fakeResponse(200, { issues: [] });
      if (url.startsWith("/v1/versions")) return fakeResponse(200, { versions: [] });
      if (url.startsWith("/v1/symbols")) return fakeResponse(200, { symbols: [] });
      return fakeResponse(404, { error: "not found" });
    });
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.unstubAllEnvs();
  });

  it("runs retention after a confirm (POST /v1/retention/run) and toasts what it purged", async () => {
    renderOverview();
    await screen.findByText("No crash reports yet", {}, { timeout: 3000 });

    // By text, not role: the command lives in PageHeader's Overflow, whose visibility jsdom can't measure.
    fireEvent.click(screen.getByText("Run retention now"));
    expect(await screen.findByText("Run retention now?")).toBeInTheDocument();
    // Fluent v9 Dialog + jsdom: the open dialog's buttons need `hidden: true` (see ReportDetail.test.tsx).
    fireEvent.click(screen.getByRole("button", { name: "Run retention", hidden: true }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith("/v1/retention/run", expect.objectContaining({ method: "POST" })),
    );
    expect(await screen.findByText("2,000 reports purged")).toBeInTheDocument();
    expect(screen.getByText("More remain — run it again to continue.")).toBeInTheDocument();
  });
});
