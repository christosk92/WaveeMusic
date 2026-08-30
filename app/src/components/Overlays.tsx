import {
  Check,
  ChevronRight,
  Info,
  Moon,
  RotateCcw,
  Sparkles,
  Sun,
  X,
} from "lucide-react";
import { useEffect } from "react";
import { usePrototype } from "../PrototypeContext";
import { DesignPicker } from "./DesignPicker";
import { IconButton, Modal, WaveeMark } from "./Primitives";

export function OnboardingChooser() {
  const {
    design,
    switchDesign,
    onboardingSeen,
    setOnboardingSeen,
    navigate,
  } = usePrototype();

  useEffect(() => {
    if (onboardingSeen) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") setOnboardingSeen(true);
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onboardingSeen, setOnboardingSeen]);

  if (onboardingSeen) return null;

  const finish = () => setOnboardingSeen(true);

  return (
    <Modal label="Choose your sidebar" className="chooser-modal" testId="onboarding-dialog">
      <div className="chooser-header">
        <WaveeMark />
        <span className="chooser-kicker">A sidebar that fits you</span>
        <h1>Choose how your library feels.</h1>
        <p>
          Start familiar, go all-in on your library, or shape every section yourself.
          You can switch at any time.
        </p>
      </div>
      <DesignPicker selected={design} onChange={(next) => switchDesign(next, true)} />
      <footer className="chooser-footer">
        <span>
          <Info size={14} />
          Your choice applies instantly and stays on this device.
        </span>
        <div>
          <button type="button" className="button button--quiet" onClick={finish}>
            Not now
          </button>
          {design === "curated" ? (
            <button
              type="button"
              className="button button--glass"
              onClick={() => {
                finish();
                navigate("sidebar-customize");
              }}
            >
              <Sparkles size={14} />
              Customize now
            </button>
          ) : null}
          <button
            type="button"
            className="button button--accent"
            onClick={finish}
            data-testid="chooser-confirm"
          >
            Use this layout
            <ChevronRight size={15} />
          </button>
        </div>
      </footer>
    </Modal>
  );
}

export function SettingsPanel() {
  const {
    design,
    switchDesign,
    theme,
    setTheme,
    settingsOpen,
    setSettingsOpen,
    navigate,
    pins,
    resetPrototype,
  } = usePrototype();
  if (!settingsOpen) return null;
  return (
    <>
      <button
        type="button"
        className="settings-scrim"
        aria-label="Close settings"
        onClick={() => setSettingsOpen(false)}
      />
      <aside className="settings-panel" aria-label="Settings" data-testid="settings-panel">
        <header>
          <div>
            <span>Wavee settings</span>
            <h2>General</h2>
          </div>
          <IconButton label="Close settings" quiet onClick={() => setSettingsOpen(false)}>
            <X size={17} />
          </IconButton>
        </header>
        <div className="settings-panel__scroll">
          <section className="settings-section">
            <div className="settings-section__title">
              <Sparkles size={15} />
              <span>
                <strong>Sidebar</strong>
                <small>Choose the left-hand navigation. Applies immediately.</small>
              </span>
            </div>
            <DesignPicker
              selected={design}
              compact
              onChange={(next) => switchDesign(next)}
            />
            {design === "curated" ? (
              <button
                type="button"
                className="settings-link-row"
                onClick={() => {
                  setSettingsOpen(false);
                  navigate("sidebar-customize");
                }}
                data-testid="settings-customize"
              >
                <span className="settings-link-row__icon">
                  <Sparkles size={16} />
                </span>
                <span>
                  <strong>Customize sidebar</strong>
                  <small>Add, reorder and configure your Curated sections.</small>
                </span>
                <ChevronRight size={15} />
              </button>
            ) : null}
          </section>

          <section className="settings-section">
            <div className="settings-section__title">
              {theme === "dark" ? <Moon size={15} /> : <Sun size={15} />}
              <span>
                <strong>Appearance</strong>
                <small>Choose a theme for this prototype.</small>
              </span>
            </div>
            <div className="theme-selector" role="radiogroup" aria-label="Theme">
              {(["dark", "light"] as const).map((option) => (
                <button
                  type="button"
                  key={option}
                  role="radio"
                  aria-checked={theme === option}
                  className={theme === option ? "is-active" : ""}
                  onClick={() => setTheme(option)}
                >
                  {option === "dark" ? <Moon size={15} /> : <Sun size={15} />}
                  {option === "dark" ? "Dark" : "Light"}
                  {theme === option ? <Check size={14} /> : null}
                </button>
              ))}
            </div>
          </section>

          <section className="settings-section settings-section--about">
            <div>
              <strong>Local prototype data</strong>
              <small>
                {pins.length} pinned {pins.length === 1 ? "item" : "items"} · preferences are
                stored in this browser.
              </small>
            </div>
            <button type="button" className="button button--danger" onClick={resetPrototype}>
              <RotateCcw size={14} />
              Reset prototype
            </button>
          </section>
        </div>
      </aside>
    </>
  );
}

export function ToastViewport() {
  const { toast, clearToast } = usePrototype();
  if (!toast) return null;
  return (
    <div className="toast" role="status" key={toast.id}>
      <span className="toast__check">
        <Check size={14} strokeWidth={3} />
      </span>
      <strong>{toast.message}</strong>
      {toast.actionLabel ? (
        <button
          type="button"
          onClick={() => {
            toast.onAction?.();
            clearToast();
          }}
        >
          {toast.actionLabel}
        </button>
      ) : null}
      <button type="button" aria-label="Dismiss" onClick={clearToast}>
        <X size={14} />
      </button>
    </div>
  );
}
