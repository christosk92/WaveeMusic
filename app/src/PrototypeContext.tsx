import {
  createContext,
  type Dispatch,
  type ReactNode,
  type SetStateAction,
  useCallback,
  useContext,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  allItems,
  buildTemplate,
  initialModePreferences,
  libraryItems,
  modeDefaultWidth,
} from "./data";
import { useStoredState } from "./hooks/useStoredState";
import type {
  CuratedLayout,
  CuratedSection,
  LibraryFilter,
  LibraryQualifier,
  LibrarySort,
  LibraryView,
  ModePreferences,
  SidebarDesign,
  ThemeMode,
  ToastState,
} from "./types";

interface LibraryPreferences {
  filter: LibraryFilter;
  qualifier: LibraryQualifier;
  sort: LibrarySort;
  descending: boolean;
  view: LibraryView;
}

interface PrototypeContextValue {
  design: SidebarDesign;
  switchDesign: (design: SidebarDesign, quiet?: boolean) => void;
  modePreferences: ModePreferences;
  setModePreference: (
    design: SidebarDesign,
    update: Partial<ModePreferences[SidebarDesign]>,
  ) => void;
  resetModeWidth: (design: SidebarDesign) => void;
  theme: ThemeMode;
  setTheme: Dispatch<SetStateAction<ThemeMode>>;
  route: string;
  navigate: (route: string) => void;
  previousRoute: string;
  pins: string[];
  pinItem: (itemId: string) => void;
  unpinItem: (itemId: string) => void;
  movePin: (fromId: string, toId: string) => void;
  libraryPreferences: LibraryPreferences;
  setLibraryPreferences: Dispatch<SetStateAction<LibraryPreferences>>;
  librarySearch: string;
  setLibrarySearch: Dispatch<SetStateAction<string>>;
  customOrder: string[];
  moveCustomOrder: (fromId: string, toId: string) => void;
  layout: CuratedLayout;
  commitLayout: (
    label: string,
    update: CuratedLayout | ((layout: CuratedLayout) => CuratedLayout),
  ) => void;
  updateSection: (sectionId: string, patch: Partial<CuratedSection>, label?: string) => void;
  applyTemplate: (templateId: string) => void;
  undoLayout: () => void;
  redoLayout: () => void;
  canUndo: boolean;
  canRedo: boolean;
  undoLabel: string;
  redoLabel: string;
  onboardingSeen: boolean;
  setOnboardingSeen: Dispatch<SetStateAction<boolean>>;
  settingsOpen: boolean;
  setSettingsOpen: Dispatch<SetStateAction<boolean>>;
  mobileSidebarOpen: boolean;
  setMobileSidebarOpen: Dispatch<SetStateAction<boolean>>;
  toast: ToastState | null;
  showToast: (message: string, actionLabel?: string, onAction?: () => void) => void;
  clearToast: () => void;
  resetPrototype: () => void;
}

const PrototypeContext = createContext<PrototypeContextValue | null>(null);

interface HistoryEntry {
  layout: CuratedLayout;
  label: string;
}

const defaultLibraryPreferences: LibraryPreferences = {
  filter: "all",
  qualifier: "all",
  sort: "recents",
  descending: false,
  view: "list",
};

export function PrototypeProvider({ children }: { children: ReactNode }) {
  const [design, setDesign] = useStoredState<SidebarDesign>("wavee.sidebar.design", "curated");
  const [modePreferences, setModePreferences] = useStoredState<ModePreferences>(
    "wavee.sidebar.modePreferences",
    initialModePreferences,
  );
  const [theme, setTheme] = useStoredState<ThemeMode>("wavee.theme", "dark");
  const [pins, setPins] = useStoredState<string[]>("wavee.sidebar.pins", []);
  const [libraryPreferences, setLibraryPreferences] = useStoredState<LibraryPreferences>(
    "wavee.sidebar.libraryPreferences",
    defaultLibraryPreferences,
  );
  const [customOrder, setCustomOrder] = useStoredState<string[]>(
    "wavee.sidebar.customOrder",
    libraryItems.filter((item) => item.kind === "playlist").map((item) => item.id),
  );
  const [layout, setLayout] = useStoredState<CuratedLayout>(
    "wavee.sidebar.curatedLayout",
    () => buildTemplate("curated"),
  );
  const [onboardingSeen, setOnboardingSeen] = useStoredState<boolean>(
    "wavee.sidebar.onboardingSeen",
    false,
  );
  const [route, setRoute] = useState("home");
  const [previousRoute, setPreviousRoute] = useState("home");
  const [librarySearch, setLibrarySearch] = useState("");
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [mobileSidebarOpen, setMobileSidebarOpen] = useState(false);
  const [toast, setToast] = useState<ToastState | null>(null);
  const [historyEpoch, setHistoryEpoch] = useState(0);
  const toastId = useRef(0);
  const toastTimer = useRef<number | null>(null);
  const layoutRef = useRef(layout);
  layoutRef.current = layout;
  const undoRef = useRef<HistoryEntry[]>([]);
  const redoRef = useRef<HistoryEntry[]>([]);

  const showToast = useCallback(
    (message: string, actionLabel?: string, onAction?: () => void) => {
      toastId.current += 1;
      setToast({ id: toastId.current, message, actionLabel, onAction });
      if (toastTimer.current !== null) window.clearTimeout(toastTimer.current);
      toastTimer.current = window.setTimeout(() => setToast(null), 4_500);
    },
    [],
  );

  const clearToast = useCallback(() => {
    if (toastTimer.current !== null) window.clearTimeout(toastTimer.current);
    setToast(null);
  }, []);

  const switchDesign = useCallback(
    (next: SidebarDesign, quiet = false) => {
      if (next === design) return;
      setDesign(next);
      if (!quiet) {
        const label =
          next === "classic" ? "Classic" : next === "library" ? "Library" : "Wavee Curated";
        showToast(`Sidebar switched to ${label}`);
      }
    },
    [design, setDesign, showToast],
  );

  const setModePreference = useCallback(
    (target: SidebarDesign, update: Partial<ModePreferences[SidebarDesign]>) => {
      setModePreferences((current) => ({
        ...current,
        [target]: { ...current[target], ...update },
      }));
    },
    [setModePreferences],
  );

  const resetModeWidth = useCallback(
    (target: SidebarDesign) => {
      setModePreference(target, { width: modeDefaultWidth[target] });
      showToast("Sidebar width reset");
    },
    [setModePreference, showToast],
  );

  const navigate = useCallback(
    (nextRoute: string) => {
      setRoute((current) => {
        if (current !== nextRoute) setPreviousRoute(current);
        return nextRoute;
      });
      setMobileSidebarOpen(false);
    },
    [],
  );

  const pinItem = useCallback(
    (itemId: string) => {
      if (pins.includes(itemId)) return;
      setPins((current) => [...current, itemId]);
      const item = allItems.find((entry) => entry.id === itemId);
      showToast(`Pinned “${item?.title ?? "item"}”`, "Undo", () => {
        setPins((current) => current.filter((id) => id !== itemId));
      });
    },
    [pins, setPins, showToast],
  );

  const unpinItem = useCallback(
    (itemId: string) => {
      const index = pins.indexOf(itemId);
      if (index < 0) return;
      const item = allItems.find((entry) => entry.id === itemId);
      setPins((current) => current.filter((id) => id !== itemId));
      showToast(`Unpinned “${item?.title ?? "item"}”`, "Undo", () => {
        setPins((current) => {
          if (current.includes(itemId)) return current;
          const copy = [...current];
          copy.splice(Math.min(index, copy.length), 0, itemId);
          return copy;
        });
      });
    },
    [pins, setPins, showToast],
  );

  const movePin = useCallback(
    (fromId: string, toId: string) => {
      setPins((current) => {
        const from = current.indexOf(fromId);
        const to = current.indexOf(toId);
        if (from < 0 || to < 0 || from === to) return current;
        const copy = [...current];
        const [moved] = copy.splice(from, 1);
        copy.splice(to, 0, moved);
        return copy;
      });
    },
    [setPins],
  );

  const moveCustomOrder = useCallback(
    (fromId: string, toId: string) => {
      setCustomOrder((current) => {
        const from = current.indexOf(fromId);
        const to = current.indexOf(toId);
        if (from < 0 || to < 0 || from === to) return current;
        const copy = [...current];
        const [moved] = copy.splice(from, 1);
        copy.splice(to, 0, moved);
        return copy;
      });
    },
    [setCustomOrder],
  );

  const commitLayout = useCallback(
    (
      label: string,
      update: CuratedLayout | ((currentLayout: CuratedLayout) => CuratedLayout),
    ) => {
      const before = layoutRef.current;
      const after = typeof update === "function" ? update(before) : update;
      if (after === before || JSON.stringify(after) === JSON.stringify(before)) return;
      undoRef.current = [...undoRef.current.slice(-49), { layout: before, label }];
      redoRef.current = [];
      layoutRef.current = after;
      setLayout(after);
      setHistoryEpoch((value) => value + 1);
    },
    [setLayout],
  );

  const updateSection = useCallback(
    (sectionId: string, patch: Partial<CuratedSection>, label = "Change section") => {
      commitLayout(label, (current) => ({
        ...current,
        sections: current.sections.map((section) =>
          section.id === sectionId ? { ...section, ...patch } : section,
        ),
      }));
    },
    [commitLayout],
  );

  const applyTemplate = useCallback(
    (templateId: string) => {
      commitLayout("Apply template", buildTemplate(templateId));
      showToast("Template applied", "Undo", () => {
        const previous = undoRef.current.at(-1);
        if (!previous) return;
        layoutRef.current = previous.layout;
        setLayout(previous.layout);
        undoRef.current = undoRef.current.slice(0, -1);
        redoRef.current = [];
        setHistoryEpoch((value) => value + 1);
      });
    },
    [commitLayout, setLayout, showToast],
  );

  const undoLayout = useCallback(() => {
    const previous = undoRef.current.at(-1);
    if (!previous) return;
    redoRef.current = [...redoRef.current, { layout: layoutRef.current, label: previous.label }];
    undoRef.current = undoRef.current.slice(0, -1);
    layoutRef.current = previous.layout;
    setLayout(previous.layout);
    setHistoryEpoch((value) => value + 1);
  }, [setLayout]);

  const redoLayout = useCallback(() => {
    const next = redoRef.current.at(-1);
    if (!next) return;
    undoRef.current = [...undoRef.current, { layout: layoutRef.current, label: next.label }];
    redoRef.current = redoRef.current.slice(0, -1);
    layoutRef.current = next.layout;
    setLayout(next.layout);
    setHistoryEpoch((value) => value + 1);
  }, [setLayout]);

  const resetPrototype = useCallback(() => {
    const accepted = window.confirm("Reset the prototype and show the first-run chooser again?");
    if (!accepted) return;
    const keys: string[] = [];
    for (let index = 0; index < window.localStorage.length; index += 1) {
      const key = window.localStorage.key(index);
      if (key?.startsWith("wavee.")) keys.push(key);
    }
    keys.forEach((key) => window.localStorage.removeItem(key));
    window.location.reload();
  }, []);

  const value = useMemo<PrototypeContextValue>(
    () => ({
      design,
      switchDesign,
      modePreferences,
      setModePreference,
      resetModeWidth,
      theme,
      setTheme,
      route,
      navigate,
      previousRoute,
      pins,
      pinItem,
      unpinItem,
      movePin,
      libraryPreferences,
      setLibraryPreferences,
      librarySearch,
      setLibrarySearch,
      customOrder,
      moveCustomOrder,
      layout,
      commitLayout,
      updateSection,
      applyTemplate,
      undoLayout,
      redoLayout,
      canUndo: undoRef.current.length > 0,
      canRedo: redoRef.current.length > 0,
      undoLabel: undoRef.current.at(-1)?.label ?? "",
      redoLabel: redoRef.current.at(-1)?.label ?? "",
      onboardingSeen,
      setOnboardingSeen,
      settingsOpen,
      setSettingsOpen,
      mobileSidebarOpen,
      setMobileSidebarOpen,
      toast,
      showToast,
      clearToast,
      resetPrototype,
    }),
    [
      applyTemplate,
      clearToast,
      commitLayout,
      customOrder,
      design,
      layout,
      libraryPreferences,
      librarySearch,
      mobileSidebarOpen,
      modePreferences,
      moveCustomOrder,
      movePin,
      navigate,
      onboardingSeen,
      pinItem,
      pins,
      previousRoute,
      redoLayout,
      resetModeWidth,
      resetPrototype,
      route,
      setLibraryPreferences,
      setLibrarySearch,
      setModePreference,
      setOnboardingSeen,
      setSettingsOpen,
      setTheme,
      settingsOpen,
      showToast,
      switchDesign,
      theme,
      toast,
      undoLayout,
      unpinItem,
      updateSection,
      historyEpoch,
    ],
  );

  return <PrototypeContext.Provider value={value}>{children}</PrototypeContext.Provider>;
}

export function usePrototype() {
  const context = useContext(PrototypeContext);
  if (!context) throw new Error("usePrototype must be used inside PrototypeProvider");
  return context;
}
