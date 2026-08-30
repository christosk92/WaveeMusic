import { useEffect, useState, type CSSProperties, type PointerEvent } from "react";
import { usePrototype } from "../../PrototypeContext";
import { ClassicSidebar } from "./ClassicSidebar";
import { CuratedSidebar } from "./CuratedSidebar";
import { LibraryV3Sidebar } from "./LibraryV3Sidebar";

export function useIsNarrow() {
  const [narrow, setNarrow] = useState(() => window.innerWidth < 780);
  useEffect(() => {
    const update = () => setNarrow(window.innerWidth < 780);
    window.addEventListener("resize", update);
    return () => window.removeEventListener("resize", update);
  }, []);
  return narrow;
}

export function SidebarShell() {
  const {
    design,
    modePreferences,
    setModePreference,
    resetModeWidth,
    mobileSidebarOpen,
    setMobileSidebarOpen,
  } = usePrototype();
  const narrow = useIsNarrow();
  const preference = modePreferences[design];
  const compact = !narrow && preference.collapsed;
  const width = compact ? 56 : preference.width;

  const beginResize = (event: PointerEvent<HTMLDivElement>) => {
    if (narrow || compact) return;
    event.currentTarget.setPointerCapture(event.pointerId);
    const originX = event.clientX;
    const originWidth = preference.width;
    const move = (moveEvent: globalThis.PointerEvent) => {
      const next = Math.max(240, Math.min(460, originWidth + moveEvent.clientX - originX));
      setModePreference(design, { width: next });
    };
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  };

  const content =
    design === "classic" ? (
      <ClassicSidebar key="classic" compact={compact} />
    ) : design === "library" ? (
      <LibraryV3Sidebar key="library" compact={compact} />
    ) : (
      <CuratedSidebar key="curated" compact={compact} />
    );

  return (
    <>
      {narrow && mobileSidebarOpen ? (
        <button
          type="button"
          className="mobile-sidebar-scrim"
          aria-label="Close sidebar"
          onClick={() => setMobileSidebarOpen(false)}
        />
      ) : null}
      <aside
        className={`app-sidebar ${compact ? "is-compact" : ""} ${
          narrow ? "is-mobile" : ""
        } ${mobileSidebarOpen ? "is-mobile-open" : ""}`}
        style={{ "--sidebar-width": `${width}px` } as CSSProperties}
        data-design={design}
        data-testid="sidebar"
      >
        <div className="app-sidebar__content">{content}</div>
        {!narrow && !compact ? (
          <div
            className="sidebar-resize"
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize sidebar"
            aria-valuemin={240}
            aria-valuemax={460}
            aria-valuenow={Math.round(preference.width)}
            tabIndex={0}
            onPointerDown={beginResize}
            onDoubleClick={() => resetModeWidth(design)}
            onKeyDown={(event) => {
              if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
              const delta = event.key === "ArrowLeft" ? -8 : 8;
              setModePreference(design, {
                width: Math.max(240, Math.min(460, preference.width + delta)),
              });
            }}
          />
        ) : null}
      </aside>
    </>
  );
}
