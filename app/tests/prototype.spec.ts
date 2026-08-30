import { expect, test } from "@playwright/test";

test("the first-run chooser applies all three sidebar designs live", async ({ page }) => {
  await page.goto("/");

  await expect(page.getByTestId("onboarding-dialog")).toBeVisible();
  await page.getByTestId("picker-classic").click();
  await page.getByTestId("chooser-confirm").click();
  await expect(page.getByTestId("sidebar")).toHaveAttribute("data-design", "classic");
  await expect(page.getByTestId("classic-sidebar")).toBeVisible();

  await page.getByTestId("quick-layout").click();
  await page.getByTestId("quick-mode-library").click();
  await expect(page.getByTestId("sidebar")).toHaveAttribute("data-design", "library");
  await expect(page.getByTestId("library-sidebar")).toBeVisible();

  await page.getByTestId("quick-layout").click();
  await page.getByTestId("quick-mode-curated").click();
  await expect(page.getByTestId("sidebar")).toHaveAttribute("data-design", "curated");
  await expect(page.getByTestId("curated-sidebar")).toBeVisible();
});

test("Library V3 supports search, filters, views, custom order, and shared pins", async ({
  page,
}) => {
  await page.goto("/");
  await page.getByTestId("picker-library").click();
  await page.getByTestId("chooser-confirm").click();

  await page.getByTestId("v3-search-toggle").click();
  await page.getByTestId("v3-search-input").fill("BRAT");
  await expect(page.getByTestId("library-item-brat")).toBeVisible();
  await expect(page.getByTestId("library-item-midnight-city")).toHaveCount(0);
  await page.getByTestId("v3-search-input").fill("");

  await page.getByTestId("filter-playlist").click();
  await expect(page.getByText("By Spotify", { exact: true })).toBeVisible();
  await page.getByTestId("sort-button").click();
  await page.getByTestId("sort-custom").click();
  await expect(page.getByText("Drag playlists to set your local order")).toBeVisible();

  await page.getByTestId("filter-playlist").click();
  await page.getByTestId("pin-midnight-city").click();
  await expect(page.getByText("Pinned “Midnight City Drives”")).toBeVisible();

  await page.getByTestId("quick-layout").click();
  await page.getByTestId("quick-mode-classic").click();
  await expect(page.getByTestId("classic-pinned").getByText("Midnight City Drives")).toBeVisible();
});

test("Settings is a second live entry point and each mode can collapse independently", async ({
  page,
}) => {
  await page.goto("/");
  await page.getByTestId("chooser-confirm").click();

  await page.getByTestId("settings-button").click();
  await expect(page.getByTestId("settings-panel")).toBeVisible();
  await page.getByTestId("settings-panel").getByTestId("picker-library").click();
  await expect(page.getByTestId("sidebar")).toHaveAttribute("data-design", "library");
  await page.getByTestId("settings-panel").getByLabel("Close settings").click();

  await page.getByTestId("quick-layout").click();
  await page.getByTestId("toggle-collapse").click();
  await expect(page.getByTestId("library-rail")).toBeVisible();
  await page.getByLabel("Expand Your Library").click();
  await expect(page.getByTestId("library-sidebar")).toBeVisible();
});

test("the Curated editor updates the live sidebar and supports undo, redo, and templates", async ({
  page,
}) => {
  await page.goto("/");
  await page.getByTestId("chooser-confirm").click();
  await page.getByRole("button", { name: "Customize sidebar", exact: true }).click();
  await expect(page.getByTestId("customizer")).toBeVisible();

  await page.getByTestId("add-section-heading").click();
  await page.getByTestId("property-title").fill("For deep focus");
  await page.getByTestId("property-title").press("Enter");
  await expect(page.getByTestId("curated-sidebar").getByText("For deep focus")).toBeVisible();

  await page.getByTestId("customizer-undo").click();
  await expect(page.getByTestId("curated-sidebar").getByText("For deep focus")).toHaveCount(0);
  await page.getByTestId("customizer-redo").click();
  await expect(page.getByTestId("curated-sidebar").getByText("For deep focus")).toBeVisible();

  await page.getByTestId("template-minimal").click();
  await page.getByTestId("confirm-template").click();
  await expect(page.getByTestId("outline-playlists")).toBeVisible();
  await expect(page.getByText("Minimal", { exact: true }).first()).toBeVisible();
});

test("collapsed state is remembered per design", async ({ page }) => {
  await page.goto("/");
  await page.getByTestId("picker-library").click();
  await page.getByTestId("chooser-confirm").click();

  await page.getByTestId("quick-layout").click();
  await page.getByTestId("toggle-collapse").click();
  await expect(page.getByTestId("library-rail")).toBeVisible();

  await page.getByTestId("quick-layout").click();
  await page.getByTestId("quick-mode-classic").click();
  await expect(page.getByTestId("classic-sidebar")).toBeVisible();

  await page.getByTestId("quick-layout").click();
  await page.getByTestId("quick-mode-library").click();
  await expect(page.getByTestId("library-rail")).toBeVisible();
});

test("narrow windows use the expanded overlay drawer", async ({ page }) => {
  await page.goto("/");
  await page.getByTestId("picker-library").click();
  await page.getByTestId("chooser-confirm").click();
  await page.setViewportSize({ width: 720, height: 900 });

  await page.getByLabel("Open sidebar").click();
  await expect(page.getByTestId("sidebar")).toHaveClass(/is-mobile-open/);
  await expect(page.getByTestId("library-sidebar")).toBeVisible();
  await expect(page.getByLabel("Collapse sidebar")).not.toBeVisible();
});
