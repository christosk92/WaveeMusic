import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import OverviewPage from "./index";

function renderOverview() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={["/"]}>
          <OverviewPage />
        </MemoryRouter>
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
