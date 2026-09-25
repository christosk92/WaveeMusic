import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import SymbolsPage from "./index";

function renderSymbols() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={["/symbols"]}>
          <SymbolsPage />
        </MemoryRouter>
      </QueryClientProvider>
    </FluentProvider>,
  );
}

describe("Symbols page (mock mode)", () => {
  beforeEach(() => {
    vi.stubEnv("VITE_MOCK", "1");
    vi.stubEnv("VITE_MOCK_DELAY", "0");
  });

  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("renders the header and, once loaded, a row per quad/arch with an uploaded/missing badge", async () => {
    renderSymbols();

    expect(screen.getByText("Symbols")).toBeInTheDocument();

    // Wait for the mock fetch to resolve (the loading Skeleton has no text) before asserting on content.
    // Several quad/arch rows are uploaded (src/api/mock.ts `MOCK_SYMBOLS`), so use the "all" variant.
    expect((await screen.findAllByText("uploaded", {}, { timeout: 3000 })).length).toBeGreaterThan(0);
    // The mock data's one missing symbol map (0.3.0-stable/arm64 — src/api/mock.ts `MOCK_SYMBOLS`).
    expect(screen.getByText("missing")).toBeInTheDocument();
    expect(screen.getAllByText("0.3.0-stable").length).toBeGreaterThan(0);
    // The footer's explanatory sentence — asserted on a run of plain text within one node, since the
    // monospace `symbols`/`Wavee-<quad>-…symmap` spans split the sentence across sibling elements.
    expect(screen.getByText(/uploads it to R2 via wrangler/)).toBeInTheDocument();
  });

  it("shows the empty state when VITE_MOCK_STATE=empty", async () => {
    vi.stubEnv("VITE_MOCK_STATE", "empty");
    renderSymbols();
    expect(await screen.findByText("No symbols yet")).toBeInTheDocument();
  });
});
