import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import IssuesPage from "./index";

function renderIssues() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={["/issues"]}>
          <Routes>
            <Route path="/issues" element={<IssuesPage />} />
            <Route path="/issues/:fp" element={<div>ISSUE_DETAIL_STUB</div>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </FluentProvider>,
  );
}

describe("Issues page (mock mode)", () => {
  beforeEach(() => {
    vi.stubEnv("VITE_MOCK", "1");
    vi.stubEnv("VITE_MOCK_DELAY", "0");
  });

  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("renders the header and, once loaded, the issue rows", async () => {
    renderIssues();

    expect(screen.getByText("Issues")).toBeInTheDocument();
    expect(await screen.findByText(/NullReferenceException · Detail\.UI\.Hero\.Render/, {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByText(/AccessViolation · nvwgf2umx\.dll/)).toBeInTheDocument();
  });

  it("shows the empty state when VITE_MOCK_STATE=empty", async () => {
    vi.stubEnv("VITE_MOCK_STATE", "empty");
    renderIssues();
    expect(await screen.findByText("No issues match")).toBeInTheDocument();
  });

  it("shows the error state with a retry action when VITE_MOCK_STATE=error", async () => {
    vi.stubEnv("VITE_MOCK_STATE", "error");
    renderIssues();
    expect(await screen.findByText("Couldn't load issues")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
  });

  it("navigates to the issue detail page when a row is clicked", async () => {
    renderIssues();
    const title = await screen.findByText(/NullReferenceException · Detail\.UI\.Hero\.Render/, {}, { timeout: 3000 });
    title.click();
    expect(await screen.findByText("ISSUE_DETAIL_STUB")).toBeInTheDocument();
  });
});
