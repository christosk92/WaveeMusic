import {
  Bell,
  ChevronDown,
  ChevronUp,
  Headphones,
  Menu,
  Moon,
  Search,
  Settings,
  Sun,
  Users,
  X,
} from "lucide-react";
import { useState } from "react";
import { usePrototype } from "../PrototypeContext";
import { BrowserHistoryButtons, IconButton, WaveeMark, WindowDots } from "./Primitives";

export function TitleBar() {
  const {
    theme,
    setTheme,
    setSettingsOpen,
    mobileSidebarOpen,
    setMobileSidebarOpen,
    navigate,
  } = usePrototype();
  const [search, setSearch] = useState("");

  return (
    <header className="titlebar">
      <div className="titlebar__window">
        <WindowDots />
        <WaveeMark />
      </div>
      <IconButton
        label={mobileSidebarOpen ? "Close sidebar" : "Open sidebar"}
        quiet
        size="small"
        className="mobile-menu-button"
        onClick={() => setMobileSidebarOpen((value) => !value)}
      >
        {mobileSidebarOpen ? <X size={16} /> : <Menu size={16} />}
      </IconButton>
      <BrowserHistoryButtons />
      <form
        className="title-search"
        onSubmit={(event) => {
          event.preventDefault();
          if (search.trim()) navigate(`search:${search.trim()}`);
        }}
      >
        <Search size={15} />
        <input
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          placeholder="What do you want to play?"
          aria-label="Search Wavee"
        />
        <kbd>Ctrl K</kbd>
      </form>
      <div className="titlebar__actions">
        <IconButton label="Friends activity" quiet size="small">
          <Users size={16} />
        </IconButton>
        <IconButton label="Notifications" quiet size="small">
          <Bell size={16} />
        </IconButton>
        <IconButton
          label={theme === "dark" ? "Use light theme" : "Use dark theme"}
          quiet
          size="small"
          onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
        >
          {theme === "dark" ? <Sun size={16} /> : <Moon size={16} />}
        </IconButton>
        <IconButton
          label="Settings"
          quiet
          size="small"
          onClick={() => setSettingsOpen(true)}
          data-testid="settings-button"
        >
          <Settings size={16} />
        </IconButton>
        <button type="button" className="profile-button">
          <span>CK</span>
          <strong>Christos</strong>
          <ChevronDown size={13} />
        </button>
      </div>
    </header>
  );
}

export function PlayerBar() {
  const [playing, setPlaying] = useState(true);
  const [expanded, setExpanded] = useState(false);
  const [progress, setProgress] = useState(38);
  const [volume, setVolume] = useState(72);

  return (
    <footer className={`playerbar ${expanded ? "is-expanded" : ""}`} data-testid="player">
      <div className="now-playing">
        <span className="now-playing__cover">
          <i />
          <b>W</b>
        </span>
        <span className="now-playing__copy">
          <strong>Midnight City</strong>
          <small>M83 · Hurry Up, We’re Dreaming</small>
        </span>
        <button type="button" aria-label="Save to Liked Songs" className="liked-button">
          ♥
        </button>
      </div>
      <div className="transport">
        <div className="transport__buttons">
          <button type="button" aria-label="Shuffle">
            ↝
          </button>
          <button type="button" aria-label="Previous track">
            ◀
          </button>
          <button
            type="button"
            className="play-button"
            aria-label={playing ? "Pause" : "Play"}
            onClick={() => setPlaying((value) => !value)}
          >
            {playing ? "Ⅱ" : "▶"}
          </button>
          <button type="button" aria-label="Next track">
            ▶
          </button>
          <button type="button" aria-label="Repeat">
            ↻
          </button>
        </div>
        <div className="transport__timeline">
          <span>2:37</span>
          <input
            type="range"
            min={0}
            max={100}
            value={progress}
            onChange={(event) => setProgress(Number(event.target.value))}
            aria-label="Track position"
          />
          <span>4:03</span>
        </div>
      </div>
      <div className="player-tools">
        <button type="button" aria-label="Now playing view">
          ▣
        </button>
        <button type="button" aria-label="Lyrics">
          🎙
        </button>
        <button type="button" aria-label="Queue">
          ☷
        </button>
        <Headphones size={15} />
        <input
          type="range"
          min={0}
          max={100}
          value={volume}
          onChange={(event) => setVolume(Number(event.target.value))}
          aria-label="Volume"
        />
        <button
          type="button"
          className="player-expand"
          aria-label={expanded ? "Collapse player" : "Expand player"}
          onClick={() => setExpanded((value) => !value)}
        >
          {expanded ? <ChevronDown size={15} /> : <ChevronUp size={15} />}
        </button>
      </div>
    </footer>
  );
}
