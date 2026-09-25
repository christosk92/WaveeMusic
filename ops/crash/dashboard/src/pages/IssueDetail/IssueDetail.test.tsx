import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import IssueDetailPage from "./index";

// The mock generator's first named anchor issue (plan §4: `ISSUE_DEFS[0]`) — deterministic because
// `api/mock.ts` seeds a fixed RNG and derives the fingerprint from the title via `fingerprintFor`.
const KNOWN_FP = "fp-00-nullreferenceexception-detail-ui-hero-render";

function renderDetail(fp: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={[`/issues/${fp}`]}>
          <Routes>
            <Route path="/issues" element={<div>ISSUES_LIST_STUB</div>} />
            <Route path="/issues/:fp" element={<IssueDetailPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </FluentProvider>,
  );
}

describe("Issue detail page (mock mode)", () => {
  beforeEach(() => {
    vi.stubEnv("VITE_MOCK", "1");
    vi.stubEnv("VITE_MOCK_DELAY", "0");
  });

  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("renders the exception type, meta and all four tabs once loaded", async () => {
    renderDetail(KNOWN_FP);

    expect(await screen.findByText("System.NullReferenceException", {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Stack" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Occurrences" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Environment" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Log tail" })).toBeInTheDocument();
    expect(screen.getByText("Issues")).toBeInTheDocument();
  });

  it("shows 'Issue not found' for a fingerprint with no match", async () => {
    renderDetail("does-not-exist");
    expect(await screen.findByText("Issue not found", {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /back to issues/i })).toBeInTheDocument();
  });

  it("shows the error state with a retry action when VITE_MOCK_STATE=error", async () => {
    vi.stubEnv("VITE_MOCK_STATE", "error");
    renderDetail(KNOWN_FP);
    expect(await screen.findByText("Couldn't load this issue")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
  });

  it("switches to the Occurrences tab and renders its grid", async () => {
    renderDetail(KNOWN_FP);
    await screen.findByText("System.NullReferenceException", {}, { timeout: 3000 });

    screen.getByRole("tab", { name: "Occurrences" }).click();

    expect(await screen.findByText("Dump")).toBeInTheDocument();
  });
});
