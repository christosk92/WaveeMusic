import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MOCK_ISSUES, MOCK_REPORTS } from "../../api/mock";
import IssueDetailPage from "./index";

// The mock generator's first named anchor issue (plan §4: `ISSUE_DEFS[0]`) — deterministic because
// `api/mock.ts` seeds a fixed RNG and derives the fingerprint from the title via `fingerprintFor`.
const KNOWN_FP = "fp-00-nullreferenceexception-detail-ui-hero-render";

// The lifecycle cases `api/mock.ts` seeds, found by their facts rather than by index.
const REGRESSED = MOCK_ISSUES.find((i) => i.status === "open" && i.regressedAt !== null)!;
const RESOLVED = MOCK_ISSUES.find((i) => i.status === "resolved" && i.resolvedVersion !== null)!;
const PURGED = MOCK_ISSUES.find((i) => !MOCK_REPORTS.some((r) => r.fingerprint === i.fingerprint))!;

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

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
    cleanup();
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

    fireEvent.click(screen.getByRole("tab", { name: "Occurrences" }));

    expect(await screen.findByText("Dump")).toBeInTheDocument();
  });

  it("shows the newest report's log tail on the Log tail tab", async () => {
    renderDetail(KNOWN_FP);
    await screen.findByText("System.NullReferenceException", {}, { timeout: 3000 });

    fireEvent.click(screen.getByRole("tab", { name: "Log tail" }));

    // `mockReportPartText("tail", newest)` — 24 deterministic lines built from the newest report.
    expect(await screen.findByText(/mock log line 24$/)).toBeInTheDocument();
    expect(screen.getByText(/From the newest report/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open report" })).toBeInTheDocument();
  });

  it("badges a regressed issue and says which version it was resolved in", async () => {
    renderDetail(REGRESSED.fingerprint);

    expect(await screen.findByText("Regressed", {}, { timeout: 3000 })).toBeInTheDocument();
    expect(
      screen.getByText(new RegExp(`^Resolved in ≤ ${escapeRegExp(REGRESSED.resolvedVersion!)} · reopened`)),
    ).toBeInTheDocument();
  });

  it("notes the version a resolved issue was resolved in", async () => {
    renderDetail(RESOLVED.fingerprint);

    expect(
      await screen.findByText(new RegExp(`^Resolved in ≤ ${escapeRegExp(RESOLVED.resolvedVersion!)}`), {}, { timeout: 3000 }),
    ).toBeInTheDocument();
    expect(screen.queryByText("Regressed")).not.toBeInTheDocument();
  });

  it("keeps a purged issue's stack and explains why its reports are gone", async () => {
    const frameName = PURGED.lastFrames![0]!.name!;
    renderDetail(PURGED.fingerprint);

    // Stack (the default tab) falls back to the issue's own lastFrames.
    expect((await screen.findAllByText(frameName, {}, { timeout: 3000 })).length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole("tab", { name: "Log tail" }));
    expect(
      await screen.findByText("Reports older than 90 days are deleted; this issue's counts are kept."),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Occurrences" }));
    expect(
      await screen.findByText("Reports older than 90 days are deleted; this issue's counts are kept."),
    ).toBeInTheDocument();
  });
});
