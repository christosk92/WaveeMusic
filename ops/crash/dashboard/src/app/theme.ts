import { webDarkTheme, webLightTheme, type Theme } from "@fluentui/react-components";
import { createContext, useCallback, useContext, useEffect, useState } from "react";

export type ThemeMode = "auto" | "light" | "dark";

const STORAGE_KEY = "theme";
/** Screenshot-only escape hatch (plan §9): `?theme=light|dark` forces a mode for one render without
 *  touching localStorage, so `shots/take.mjs` can capture both themes from one server. */
const URL_PARAM = "theme";

function readStoredMode(): ThemeMode {
  const fromUrl = new URLSearchParams(window.location.search).get(URL_PARAM);
  if (fromUrl === "light" || fromUrl === "dark" || fromUrl === "auto") return fromUrl;
  const stored = window.localStorage.getItem(STORAGE_KEY);
  if (stored === "light" || stored === "dark" || stored === "auto") return stored;
  return "auto";
}

function prefersDark(): boolean {
  return window.matchMedia("(prefers-color-scheme: dark)").matches;
}

export interface UseThemeResult {
  theme: Theme;
  mode: ThemeMode;
  resolved: "light" | "dark";
  setMode: (mode: ThemeMode) => void;
}

/** OS default via `matchMedia`, override persisted in `localStorage["theme"]` as `auto|light|dark`
 *  (plan §2.2). A `?theme=` query param overrides both for the duration of the Playwright shots run,
 *  without persisting anything. */
export function useTheme(): UseThemeResult {
  const [mode, setModeState] = useState<ThemeMode>(() => readStoredMode());
  const [systemDark, setSystemDark] = useState<boolean>(() => prefersDark());

  useEffect(() => {
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => setSystemDark(mq.matches);
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, []);

  const setMode = useCallback((next: ThemeMode) => {
    setModeState(next);
    try {
      window.localStorage.setItem(STORAGE_KEY, next);
    } catch {
      // localStorage unavailable (private mode, blocked storage) — mode still applies for this session.
    }
  }, []);

  const resolved: "light" | "dark" = mode === "auto" ? (systemDark ? "dark" : "light") : mode;
  const theme = resolved === "dark" ? webDarkTheme : webLightTheme;

  return { theme, mode, resolved, setMode };
}

/** A single `useTheme()` instance lives in `main.tsx`; everything downstream (including `AppFrame`,
 *  rendered inside `RouterProvider`) reads it through this context instead of calling `useTheme()`
 *  again, so toggling the mode from the nav footer's `MenuButton` and the `FluentProvider` above the
 *  router both see the same state. */
export const ThemeContext = createContext<UseThemeResult | null>(null);

export function useThemeContext(): UseThemeResult {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error("useThemeContext() must be used within ThemeContext.Provider");
  return ctx;
}
