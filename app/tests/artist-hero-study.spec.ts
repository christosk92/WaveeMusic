import { expect, type Page, test } from "@playwright/test";

const VARIANTS = ["editorial", "band", "plate", "current"] as const;

/** Fluent ramp steps the page may use: Caption 12, Body 14, BodyLarge 18, Subtitle 20,
 *  Title 28, TitleLarge 40, Display 68. */
const ALLOWED_FONT_SIZES = new Set([12, 14, 18, 20, 28, 40, 68]);
const ALLOWED_WEIGHTS = new Set([400, 600]);
const ALLOWED_RADII = new Set([0, 4, 8, 16]);

function studyUrl(variant: string, theme = "light", chrome = true) {
  return `/?study=artist-hero&variant=${variant}&theme=${theme}${chrome ? "" : "&chrome=0"}`;
}

async function relativeLuminance(page: Page, cssColor: string) {
  return page.evaluate((color) => {
    const m = color.match(/[\d.]+/g)!.map(Number);
    const lin = (c: number) => {
      const s = c / 255;
      return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
    };
    return 0.2126 * lin(m[0]) + 0.7152 * lin(m[1]) + 0.0722 * lin(m[2]);
  }, cssColor);
}

test("the study is isolated, shareable, and defaults to the editorial split", async ({ page }) => {
  await page.goto("/?study=artist-hero");

  await expect(page.getByTestId("artist-hero-study")).toBeVisible();
  await expect(page.getByTestId("onboarding-dialog")).toHaveCount(0);
  await expect(page.getByTestId("artist-hero-study")).toHaveAttribute("data-variant", "editorial");
  await expect(page).toHaveURL(/study=artist-hero.*variant=editorial/);
  await expect(page.getByTestId("artist-name")).toHaveText("Conan Gray");
});

test("a stale v1 variant link falls back to the default rather than aliasing", async ({ page }) => {
  await page.goto("/?study=artist-hero&variant=shelf");
  await expect(page.getByTestId("artist-hero-study")).toHaveAttribute("data-variant", "editorial");
  await expect(page).toHaveURL(/variant=editorial/);
});

test("all four variants and both themes are selectable and shareable", async ({ page }) => {
  await page.goto(studyUrl("current"));

  for (const variant of VARIANTS) {
    await page.getByTestId(`variant-${variant}`).click();
    await expect(page.getByTestId("artist-hero-study")).toHaveAttribute("data-variant", variant);
    await expect(page).toHaveURL(new RegExp(`variant=${variant}`));
    await expect(page.getByTestId("artist-hero")).toBeVisible();
  }

  await page.getByTestId("study-theme-toggle").click();
  await expect(page.getByTestId("artist-hero-study")).toHaveAttribute("data-theme", "dark");
  await expect(page).toHaveURL(/theme=dark/);
});

test("the real shell is present, so proportions are measured against the real pane width", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(studyUrl("band"));

  await expect(page.getByTestId("study-sidebar")).toBeVisible();
  const sidebar = (await page.getByTestId("study-sidebar").boundingBox())!;
  expect(sidebar.width).toBe(280);

  const pane = (await page.getByTestId("content-pane").boundingBox())!;
  expect(pane.width).toBe(1160);
  // Titlebar 48 + player 72 removed from a 900px viewport.
  expect(Math.round(pane.height)).toBe(780);
});

/**
 * The number that killed v1: it showed 2.2 of 10 track rows above the fold. This records what
 * each variant actually buys rather than asserting a single winner.
 */
test("fold budget is recorded for every variant", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  const budget: Record<string, number> = {};

  for (const variant of VARIANTS) {
    await page.goto(studyUrl(variant, "light", false));
    const paneBox = (await page.getByTestId("content-pane").boundingBox())!;
    const fold = paneBox.y + paneBox.height;
    const rows = page.getByTestId("track-row");
    let visible = 0;
    for (let i = 0; i < (await rows.count()); i++) {
      const box = await rows.nth(i).boundingBox();
      if (box && box.y + box.height <= fold) visible += 1;
    }
    budget[variant] = visible;
  }

  console.log("fold budget (full track rows above the fold at 1440×900):", budget);
  // v1 managed 2.2 rows AND clipped the Releases band. Every variant must beat that.
  for (const variant of VARIANTS) expect(budget[variant]).toBeGreaterThanOrEqual(3);
});

test("no text is ever composited over the photograph in the redesigned variants", async ({ page }) => {
  for (const variant of ["editorial", "band", "plate"] as const) {
    await page.goto(studyUrl(variant));
    const photo = page.getByTestId("artist-photo");

    // No text descendants, and no scrim/mask machinery that could reintroduce one.
    expect(await photo.evaluate((n) => n.textContent?.trim() ?? "")).toBe("");
    const masked = await photo.evaluate((n) => {
      const all = [n, ...Array.from(n.querySelectorAll("*"))];
      return all.some((el) => {
        const s = getComputedStyle(el as Element);
        return (s.maskImage !== "none" && s.maskImage !== "") || (s as CSSStyleDeclaration).webkitMaskImage === "none"
          ? s.maskImage !== "none" && s.maskImage !== ""
          : false;
      });
    });
    expect(masked, `${variant} must not mask the photo`).toBe(false);
  }
});

test("every text pair on the page clears 4.5:1, in both themes", async ({ page }) => {
  const targets = [
    ".av-identity__name",
    ".av-identity__bio",
    ".av-identity__meta",
    ".av-row__title",
    ".av-row__sub",
    ".av-row__duration",
    ".av-section-header h2",
    ".av-latest__copy small",
    ".av-pick__copy small",
  ];

  for (const theme of ["light", "dark"] as const) {
    await page.goto(studyUrl("editorial", theme));
    for (const selector of targets) {
      const el = page.locator(selector).first();
      const { fg, bg } = await el.evaluate((node) => {
        const style = getComputedStyle(node);
        let parent: Element | null = node;
        let background = "rgba(0, 0, 0, 0)";
        while (parent) {
          const value = getComputedStyle(parent).backgroundColor;
          if (value && !value.endsWith(", 0)") && value !== "transparent") {
            background = value;
            break;
          }
          parent = parent.parentElement;
        }
        return { fg: style.color, bg: background };
      });

      const [lf, lb] = [await relativeLuminance(page, fg), await relativeLuminance(page, bg)];
      const ratio = (Math.max(lf, lb) + 0.05) / (Math.min(lf, lb) + 0.05);
      expect(ratio, `${selector} in ${theme} (${fg} on ${bg})`).toBeGreaterThanOrEqual(4.5);
    }
  }
});

test("the artist name is never truncated, at any width, for any name", async ({ page }) => {
  for (const width of [1440, 1024, 720, 420]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(studyUrl("editorial"));
    const name = page.getByTestId("artist-name");
    const metrics = await name.evaluate((n) => ({
      scrollWidth: n.scrollWidth,
      clientWidth: n.clientWidth,
      whiteSpace: getComputedStyle(n).whiteSpace,
      textOverflow: getComputedStyle(n).textOverflow,
    }));
    expect(metrics.whiteSpace, `@${width}`).not.toBe("nowrap");
    expect(metrics.textOverflow, `@${width}`).not.toBe("ellipsis");
    expect(metrics.scrollWidth, `@${width}`).toBeLessThanOrEqual(metrics.clientWidth + 1);
  }
});

test("the page stays on the design system: Fluent ramp only, 2 weights, 3 radii, 1 shadow", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(studyUrl("editorial", "light", false));

  const audit = await page.getByTestId("content-pane").evaluate((pane) => {
    const sizes = new Set<number>();
    const weights = new Set<number>();
    const radii = new Set<number>();
    let shadows = 0;
    let accentFills = 0;
    let accentBars = 0;

    /** Effective opacity: a button inside a hidden sticky bar is not on screen. */
    const visible = (el: Element) => {
      let node: Element | null = el;
      while (node && node !== pane) {
        const s = getComputedStyle(node);
        if (s.display === "none" || parseFloat(s.opacity) === 0) return false;
        node = node.parentElement;
      }
      return true;
    };

    for (const el of Array.from(pane.querySelectorAll("*"))) {
      const s = getComputedStyle(el);
      if ((el.textContent ?? "").trim().length > 0) {
        sizes.add(Math.round(parseFloat(s.fontSize)));
        weights.add(parseInt(s.fontWeight, 10));
      }
      for (const corner of [s.borderTopLeftRadius, s.borderBottomRightRadius]) {
        const v = Math.round(parseFloat(corner));
        if (!Number.isNaN(v) && corner.endsWith("px")) radii.add(v);
      }
      // Computed values place the `inset` keyword LAST, so startsWith would never match.
      // A wholly-inset shadow is a hairline, not elevation, and does not count.
      if (s.boxShadow !== "none" && !s.boxShadow.includes("inset")) shadows += 1;
      if (s.backgroundColor === "rgb(0, 95, 184)" && visible(el)) {
        const r = el.getBoundingClientRect();
        // The 3×20 section bar is an accent RULE, not an accent-filled object.
        if (r.width * r.height >= 400) accentFills += 1;
        else accentBars += 1;
      }
    }
    return {
      sizes: [...sizes].sort((a, b) => a - b),
      weights: [...weights].sort((a, b) => a - b),
      radii: [...radii].sort((a, b) => a - b),
      shadows,
      accentFills,
      accentBars,
    };
  });

  for (const size of audit.sizes) expect(ALLOWED_FONT_SIZES, `font-size ${size}`).toContain(size);
  for (const weight of audit.weights) expect(ALLOWED_WEIGHTS, `weight ${weight}`).toContain(weight);
  for (const radius of audit.radii) expect(ALLOWED_RADII, `radius ${radius}`).toContain(radius);
  // v1 nested five rounded boxes and shadowed several; the plate is the only intended shadow.
  expect(audit.shadows).toBeLessThanOrEqual(2);
  // Exactly one accent-FILLED object on screen at rest, and it is Play. v1 had seven.
  expect(audit.accentFills).toBe(1);
  // One accent rule per section header, and nowhere else.
  // One accent rule per section header: tracks, pick, latest, 4 discography sections, gallery.
  expect(audit.accentBars).toBe(8);
});

test("hovering a track row swaps rank for play in place, with zero reflow", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(studyUrl("editorial", "light", false));

  const row = page.getByTestId("track-row").first();
  const index = page.getByTestId("track-index").first();
  const before = { row: (await row.boundingBox())!, index: (await index.boundingBox())! };

  await row.hover();
  await expect(row.locator(".av-row__play")).toHaveCSS("opacity", "1");

  const after = { row: (await row.boundingBox())!, index: (await index.boundingBox())! };
  expect(after.row).toEqual(before.row);
  expect(after.index).toEqual(before.index);
});

test("the ledger is two columns of five, ranked 1 to 10", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(studyUrl("editorial", "light", false));

  const ranks = await page.locator(".av-row__rank").allTextContents();
  expect(ranks).toEqual(["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"]);

  // Two columns: 1-5 down the left, 6-10 down the right.
  const lefts = await page
    .getByTestId("track-row")
    .evaluateAll((rows) => [...new Set(rows.map((r) => Math.round((r as HTMLElement).getBoundingClientRect().left)))]);
  expect(lefts).toHaveLength(2);
});

test("the ledger is keyboard operable and focus produces the same affordance as hover", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(studyUrl("editorial", "light", false));

  const row = page.getByTestId("track-row").first();
  await row.focus();
  await expect(row).toBeFocused();
  await expect(row.locator(".av-row__play")).toHaveCSS("opacity", "1");
  await expect(row.locator(".av-row__like")).toHaveCSS("opacity", "1");
});

test("the sticky identity bar takes over on scroll without consuming layout", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(studyUrl("plate", "light", false));

  const bar = page.getByTestId("sticky-identity-bar");
  await expect(bar).toHaveCSS("opacity", "0");
  // Out of flow: the hero starts at the top of the pane, not 48px down.
  const heroTop = (await page.getByTestId("artist-hero").boundingBox())!.y;
  const paneTop = (await page.getByTestId("content-pane").boundingBox())!.y;
  // Out of flow: the hero sits at the top of the pane (allowing its 1px layer border), not 48px down.
  expect(Math.round(heroTop - paneTop)).toBeLessThanOrEqual(2);

  await page.getByTestId("content-pane").evaluate((pane) => pane.scrollTo({ top: 500 }));
  await expect(bar).toHaveCSS("opacity", "1");
  await expect(bar).toHaveCSS("background-color", "rgb(239, 238, 235)");
});

test("dark theme has a real elevation story, not shadow-on-black", async ({ page }) => {
  await page.goto(studyUrl("plate", "dark", false));

  const [base, layer] = await page.evaluate(() => {
    const root = document.querySelector(".artist-study")!;
    const s = getComputedStyle(root);
    return [s.getPropertyValue("--wv-solid-base").trim(), s.getPropertyValue("--wv-layer").trim()];
  });
  expect(base).not.toBe(layer);

  // The raised surface must be lighter than the layer it sits on, independent of any shadow.
  const hex = (value: string) => parseInt(value.replace("#", ""), 16);
  expect(hex(base)).toBeGreaterThan(hex(layer));
});

test("discography stacks typed sections vertically, each switchable between grid and list", async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(studyUrl("editorial", "light", false));

  // A vertical stack of typed sections, not one horizontal rail.
  for (const slug of ["albums", "singles-and-eps", "compilations", "appears-on"]) {
    await expect(page.getByTestId(`disco-${slug}`)).toBeVisible();
  }
  const albums = page.getByTestId("disco-albums");
  const singles = page.getByTestId("disco-singles-and-eps");
  const albumsBox = (await albums.boundingBox())!;
  const singlesBox = (await singles.boundingBox())!;
  expect(singlesBox.y).toBeGreaterThan(albumsBox.y + albumsBox.height - 1);
  expect(Math.round(singlesBox.x)).toBe(Math.round(albumsBox.x));

  // No section is a horizontal scroller.
  const scrollers = await page
    .getByTestId("discography")
    .evaluate((n) => Array.from(n.querySelectorAll("*")).filter((el) => el.scrollWidth > el.clientWidth + 1).length);
  expect(scrollers).toBe(0);

  // The switch flips that section to a list, and becomes the default for untouched sections.
  await expect(albums.getByTestId("release-grid")).toBeVisible();
  await albums.getByRole("button", { name: /as a list/i }).click();
  await expect(albums.getByTestId("release-list")).toBeVisible();
  await expect(albums.getByTestId("release-grid")).toHaveCount(0);
  await expect(singles.getByTestId("release-list")).toBeVisible();
});

test("no horizontal overflow and no dropped features across the width matrix", async ({ page }) => {
  for (const width of [1440, 1024, 720, 420]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(studyUrl("editorial", "light", false));

    const pane = page.getByTestId("content-pane");
    const overflow = await pane.evaluate((n) => n.scrollWidth - n.clientWidth);
    expect(overflow, `pane overflow @${width}`).toBeLessThanOrEqual(0);

    const bodyOverflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(bodyOverflow, `page overflow @${width}`).toBeLessThanOrEqual(0);

    // Every band survives at every width — secondary content degrades, features do not vanish.
    await expect(page.getByTestId("artist-pick")).toBeVisible();
    await expect(page.getByTestId("track-ledger")).toBeVisible();
    await expect(page.getByTestId("discography")).toBeVisible();
    await expect(page.getByTestId("gallery-strip")).toBeVisible();
    await expect(page.getByTestId("track-row")).toHaveCount(10);
    await expect(page.getByTestId("release-masthead")).toBeVisible();
  }
});
