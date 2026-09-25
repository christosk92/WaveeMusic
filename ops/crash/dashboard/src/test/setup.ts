import "@testing-library/jest-dom/vitest";

// jsdom has no ResizeObserver; several Fluent v9 components (MessageBar's reflow, chart sizing, …)
// use one. A no-op stub is enough for render/interaction tests, which don't assert on layout.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}

if (typeof globalThis.ResizeObserver === "undefined") {
  globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
}

// jsdom doesn't implement canvas; `@fluentui/react-charts` measures axis label widths with
// `canvas.getContext("2d").measureText(...)`. A fixed-width stub is plenty for a render smoke test.
if (typeof HTMLCanvasElement !== "undefined") {
  const stubGetContext = (() => ({
    measureText: (text: string) => ({ width: text.length * 6 }) as TextMetrics,
    font: "",
  })) as unknown as typeof HTMLCanvasElement.prototype.getContext;
  HTMLCanvasElement.prototype.getContext = stubGetContext;
}

// jsdom doesn't implement matchMedia either; `useTheme`/`AppFrame`'s narrow-breakpoint hook needs it.
if (typeof window !== "undefined" && !window.matchMedia) {
  window.matchMedia = (query: string): MediaQueryList =>
    ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    }) as unknown as MediaQueryList;
}

// tabster (Fluent's focus manager) walks the DOM with `document.createTreeWalker(…, NodeFilter.SHOW_ELEMENT)`
// from a MutationObserver callback; jsdom exposes `NodeFilter` on `window` but not on `globalThis`, so
// without this alias every render logs "ReferenceError: NodeFilter is not defined" to stderr.
if (typeof window !== "undefined" && typeof globalThis.NodeFilter === "undefined") {
  globalThis.NodeFilter = window.NodeFilter;
}
