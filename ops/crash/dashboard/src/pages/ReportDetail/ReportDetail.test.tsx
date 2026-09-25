import { FluentProvider, Toaster, webLightTheme } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MOCK_REPORTS } from "../../api/mock";
import { APP_TOASTER_ID } from "../../scene/toast";
import ReportDetailPage from "./index";

const SAMPLE_REPORT_ID = MOCK_REPORTS[0]!.id;

function renderReportDetail(id: string = SAMPLE_REPORT_ID) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={[`/reports/${id}`]}>
          <Routes>
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
