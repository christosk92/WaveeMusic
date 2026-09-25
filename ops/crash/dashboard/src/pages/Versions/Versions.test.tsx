import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import VersionsPage from "./index";

function renderVersions() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={["/versions"]}>
          <VersionsPage />
        </MemoryRouter>
      </QueryClientProvider>
    </FluentProvider>,
  );
}

describe("Versions page (mock mode)", () => {
  beforeEach(() => {
    vi.stubEnv("VITE_MOCK", "1");
    vi.stubEnv("VITE_MOCK_DELAY", "0");
  });

  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("renders the header and, once loaded, one row per version/quad/arch with kind columns", async () => {
    renderVersions();

    expect(screen.getByText("Versions")).toBeInTheDocument();

    // Wait for the mock fetch to resolve (the loading Skeleton has no text) before asserting on content.
    expect(await screen.findByText("Managed", {}, { timeout: 3000 })).toBeInTheDocument();
    expect(screen.getByText("Native")).toBeInTheDocument();
    expect(screen.getByText("Hang")).toBeInTheDocument();
    expect(screen.getByText("Exit code")).toBeInTheDocument();
    expect(screen.getByText("Unclean exit")).toBeInTheDocument();
    expect(screen.getByText("Total")).toBeInTheDocument();

    // The mock generator's newest quad (plan §4's `QUADS`) should surface as a row.
    expect(screen.getAllByText("0.3.2").length).toBeGreaterThan(0);
    expect(screen.getAllByText("stable").length).toBeGreaterThan(0);
  });

  it("shows the empty state when VITE_MOCK_STATE=empty", async () => {
    vi.stubEnv("VITE_MOCK_STATE", "empty");
    renderVersions();
    expect(await screen.findByText("No versions yet")).toBeInTheDocument();
  });
});
