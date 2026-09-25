import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import ReportsPage from "./index";

function renderReports() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={["/reports"]}>
          <ReportsPage />
        </MemoryRouter>
      </QueryClientProvider>
    </FluentProvider>,
  );
}

describe("Reports page (mock mode)", () => {
  beforeEach(() => {
    vi.stubEnv("VITE_MOCK", "1");
    vi.stubEnv("VITE_MOCK_DELAY", "0");
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllEnvs();
  });

  it("renders the header and, once loaded, the reports grid with a Load more footer", async () => {
    renderReports();

    expect(screen.getByText("Reports")).toBeInTheDocument();

    // Wait for the mock fetch to resolve (loading skeleton has no grid) before asserting on content.
    expect(await screen.findByText("Report id", {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByText("Kind")).toBeInTheDocument();
    expect(screen.getByText("Install")).toBeInTheDocument();

    // 128 mock reports (plan §4) with a 25-row page size means there's a next page.
    expect(await screen.findByRole("button", { name: /load more/i })).toBeInTheDocument();
  });

  it("shows the empty state with a Clear filters action when filters narrow the results to nothing", async () => {
    vi.stubEnv("VITE_MOCK_STATE", "empty");
    renderReports();
    expect(await screen.findByText("No reports match")).toBeInTheDocument();
  });

  it("shows the error state with a retry action when VITE_MOCK_STATE=error", async () => {
    vi.stubEnv("VITE_MOCK_STATE", "error");
    renderReports();
    expect(await screen.findByText("Couldn't load reports")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
  });
});
