import { useEffect, useRef, useState } from "react";
import {
  ArrowLeft,
  Heart,
  Home,
  Library,
  ListMusic,
  Moon,
  MoreHorizontal,
  Play,
  Search,
  SkipBack,
  SkipForward,
  Sun,
} from "lucide-react";
import { Hero } from "./Heroes";
import { Cover, PageBody } from "./Parts";
import { useArtworkPalette } from "./useArtworkPalette";
import { artist, type ArtistVariant, type PickShape, variants } from "./data";
import "./artistTokens.css";
import "./artistStudy.css";

type StudyTheme = "light" | "dark";

function readVariant(): ArtistVariant {
  const value = new URLSearchParams(window.location.search).get("variant");
  // The four v1 ids are deliberately NOT kept as aliases; a stale link lands on the default.
  return variants.some((entry) => entry.id === value) ? (value as ArtistVariant) : "editorial";
}

function readTheme(): StudyTheme {
  return new URLSearchParams(window.location.search).get("theme") === "dark" ? "dark" : "light";
}

/** ?chrome=0 hides the study overlay so screenshots capture only the app surface. */
function readChrome(): boolean {
  return new URLSearchParams(window.location.search).get("chrome") !== "0";
}

function readPick(): PickShape {
  return new URLSearchParams(window.location.search).get("pick") === "compact" ? "compact" : "hero";
}

const NAV = [
  { icon: Home, label: "Home" },
  { icon: Search, label: "Search" },
  { icon: Library, label: "Your Library" },
];

const PLAYLISTS = ["Superbloom", "Late night drives", "Deep cuts", "On repeat", "Liked Songs", "Discovery Weekly"];

function Sidebar() {
  return (
    <nav className="av-sidebar" aria-label="Wavee navigation" data-testid="study-sidebar">
      {NAV.map(({ icon: Icon, label }) => (
        <button className="av-nav-item" type="button" key={label}>
          <Icon size={18} />
          <span>{label}</span>
        </button>
      ))}
      <hr />
      {PLAYLISTS.map((name, i) => (
        <button
          className="av-nav-item av-nav-item--quiet"
          type="button"
          key={name}
          /* One selected item, so the accent selection indicator is visible in the study. */
          aria-current={i === 0 ? "page" : undefined}
        >
          <ListMusic size={16} />
          <span>{name}</span>
        </button>
      ))}
    </nav>
  );
}

function Titlebar() {
  return (
    <header className="av-titlebar">
      {/* Navigation-basics: crossing peer groups adds to history, so the page needs a back
          affordance, and users expect it in this standard location. */}
      <button className="av-titlebar__back" type="button" aria-label="Back" data-testid="titlebar-back">
        <ArrowLeft size={16} />
      </button>
      <div className="av-titlebar__brand">
        <b>W</b>
        <span>Wavee</span>
      </div>
      <div className="av-titlebar__search" tabIndex={0} role="searchbox" aria-label="Search music">
        <Search size={14} />
        <span>Search music</span>
        <kbd>Ctrl K</kbd>
      </div>
      <div className="av-titlebar__profile">
        <span>CK</span>
      </div>
    </header>
  );
}

function PlayerBar() {
  return (
    <footer className="av-player">
      <Cover name="heather" alt="Heather artwork" className="av-cover--48" />
      <div className="av-player__meta">
        <strong>Heather</strong>
        <small>Conan Gray</small>
      </div>
      <div className="av-player__transport">
        <button type="button" aria-label="Previous track">
          <SkipBack size={16} />
        </button>
        <button className="av-player__play" type="button" aria-label="Play">
          <Play size={16} fill="currentColor" />
        </button>
        <button type="button" aria-label="Next track">
          <SkipForward size={16} />
        </button>
      </div>
      <span className="av-player__time av-num">0:22</span>
      <span className="av-player__track">
        <i />
      </span>
      <span className="av-player__time av-num">-3:42</span>
      <button className="av-player__icon" type="button" aria-label="Like">
        <Heart size={16} />
      </button>
    </footer>
  );
}

/**
 * A full-width opaque bar, not a floating pill: the pill is the Spotify/YT idiom, a bar is the
 * CommandBar reading. Opaque because acrylic is for transient surfaces only.
 */
function StickyIdentityBar({ shown }: { shown: boolean }) {
  return (
    <div className="av-sticky" data-shown={shown ? "1" : "0"} data-testid="sticky-identity-bar" aria-hidden={!shown}>
      <img className="av-sticky__art" src="/assets/conan-gray-hero.webp" alt="" />
      <strong>{artist.name}</strong>
      <button className="av-btn av-btn--accent av-btn--sm" type="button" tabIndex={shown ? 0 : -1}>
        <Play size={14} fill="currentColor" />
        Play
      </button>
      <button className="av-btn av-btn--icon av-btn--sm" type="button" aria-label="Like" tabIndex={shown ? 0 : -1}>
        <Heart size={14} />
      </button>
      <button className="av-btn av-btn--icon av-btn--sm" type="button" aria-label="More" tabIndex={shown ? 0 : -1}>
        <MoreHorizontal size={14} />
      </button>
    </div>
  );
}

export function ArtistStudyPage() {
  const [variant, setVariant] = useState<ArtistVariant>(readVariant);
  const [theme, setTheme] = useState<StudyTheme>(readTheme);
  const [pick, setPick] = useState<PickShape>(readPick);
  const [chrome] = useState<boolean>(readChrome);
  const [stuck, setStuck] = useState(false);
  const paneRef = useRef<HTMLDivElement>(null);
  const active = variants.find((entry) => entry.id === variant)!;
  /* The page is tinted by the artwork itself, harmonised so contrast is unaffected. */
  const palette = useArtworkPalette("/assets/conan-gray-hero.webp", {
    washRgb: "74, 96, 118",
    seedRgb: "52, 68, 86",
  });

  useEffect(() => {
    const previousTitle = document.title;
    document.title = "Wavee · Artist page study";
    return () => {
      document.title = previousTitle;
    };
  }, []);

  useEffect(() => {
    const url = new URL(window.location.href);
    url.searchParams.set("study", "artist-hero");
    url.searchParams.set("variant", variant);
    url.searchParams.set("theme", theme);
    url.searchParams.set("pick", pick);
    window.history.replaceState(null, "", url);
  }, [pick, theme, variant]);

  // Trigger at the photo's own height, so the handoff happens exactly as the portrait leaves.
  useEffect(() => {
    const pane = paneRef.current;
    if (!pane) return;
    const threshold = variant === "plate" ? 208 : variant === "band" ? 240 : variant === "editorial" ? 300 : 320;
    const onScroll = () => setStuck(pane.scrollTop > threshold);
    onScroll();
    pane.addEventListener("scroll", onScroll, { passive: true });
    return () => pane.removeEventListener("scroll", onScroll);
  }, [variant]);

  useEffect(() => {
    paneRef.current?.scrollTo({ top: 0 });
    setStuck(false);
  }, [variant]);

  return (
    <div
      className="artist-study"
      data-theme={theme}
      data-variant={variant}
      data-testid="artist-hero-study"
      style={
        {
          "--wv-wash-rgb": palette.washRgb,
          "--wv-seed-rgb": palette.seedRgb,
        } as React.CSSProperties
      }
    >
      <div className="av-shell">
        <Titlebar />
        <div className="av-shell__body">
          <Sidebar />
          <main className="av-pane" ref={paneRef} data-testid="content-pane" aria-label={`${artist.name} artist page`}>
            <StickyIdentityBar shown={stuck} />
            <Hero variant={variant} />
            <PageBody pick={pick} />
          </main>
        </div>
        <PlayerBar />
      </div>

      {chrome && (
        <div className="av-study-overlay" data-testid="study-overlay">
          <a className="av-study-overlay__back" href="/" aria-label="Back to sidebar prototype">
            <ArrowLeft size={16} />
          </a>
          <div className="av-study-overlay__switch" role="group" aria-label="Hero variants">
            {variants.map((entry) => (
              <button
                type="button"
                key={entry.id}
                aria-pressed={variant === entry.id}
                data-testid={`variant-${entry.id}`}
                onClick={() => setVariant(entry.id)}
              >
                {entry.label}
              </button>
            ))}
          </div>
          <div className="av-study-overlay__switch" role="group" aria-label="Artist pick shape">
            {(["hero", "compact"] as const).map((shape) => (
              <button
                type="button"
                key={shape}
                aria-pressed={pick === shape}
                data-testid={`pick-${shape}`}
                onClick={() => setPick(shape)}
              >
                {shape === "hero" ? "Pick: image" : "Pick: compact"}
              </button>
            ))}
          </div>
          <p className="av-study-overlay__note" data-testid="variant-annotation">
            <b>{active.eyebrow}</b> {active.thesis} <i>{active.tradeoff}</i>
          </p>
          <button
            className="av-study-overlay__theme"
            type="button"
            data-testid="study-theme-toggle"
            onClick={() => setTheme((current) => (current === "light" ? "dark" : "light"))}
            aria-label={`Switch to ${theme === "light" ? "dark" : "light"} theme`}
          >
            {theme === "light" ? <Moon size={15} /> : <Sun size={15} />}
          </button>
        </div>
      )}
    </div>
  );
}
