import type { ButtonHTMLAttributes, CSSProperties, ReactNode } from "react";
import {
  Album,
  ArrowLeft,
  ArrowRight,
  Clock3,
  Code2,
  Disc3,
  Folder,
  Heart,
  Home,
  Library,
  ListMusic,
  Mic2,
  MoreHorizontal,
  Pin,
  Radio,
  Search,
  Sparkles,
  UserRound,
  type LucideIcon,
} from "lucide-react";
import type { LibraryItem } from "../types";

const itemIcons: Record<string, LucideIcon> = {
  album: Album,
  artist: UserRound,
  code: Code2,
  folder: Folder,
  heart: Heart,
  history: Clock3,
  home: Home,
  library: Library,
  playlist: ListMusic,
  podcast: Mic2,
  radio: Radio,
  search: Search,
  sparkles: Sparkles,
};

export function ItemIcon({
  name,
  size = 17,
  strokeWidth = 1.9,
}: {
  name?: string;
  size?: number;
  strokeWidth?: number;
}) {
  const Icon = itemIcons[name ?? ""] ?? Disc3;
  return <Icon size={size} strokeWidth={strokeWidth} aria-hidden="true" />;
}

interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  label: string;
  children: ReactNode;
  quiet?: boolean;
  size?: "small" | "medium" | "large";
}

export function IconButton({
  label,
  children,
  quiet = false,
  size = "medium",
  className = "",
  ...props
}: IconButtonProps) {
  return (
    <button
      type="button"
      className={`icon-button icon-button--${size} ${quiet ? "icon-button--quiet" : ""} ${className}`}
      aria-label={label}
      title={label}
      {...props}
    >
      {children}
    </button>
  );
}

export function Artwork({
  item,
  size = 36,
  className = "",
}: {
  item: LibraryItem;
  size?: number;
  className?: string;
}) {
  const isIcon = item.kind === "route" || item.kind === "folder";
  return (
    <span
      className={`artwork artwork--${item.kind} ${className}`}
      style={
        {
          "--art-size": `${size}px`,
          "--art-fill": item.art,
        } as CSSProperties
      }
      aria-hidden="true"
    >
      {isIcon ? <ItemIcon name={item.icon ?? item.kind} size={Math.round(size * 0.46)} /> : null}
      {!isIcon ? <span className="artwork__shine" /> : null}
    </span>
  );
}

export function WaveeMark({ compact = false }: { compact?: boolean }) {
  return (
    <span className={`wavee-mark ${compact ? "wavee-mark--compact" : ""}`} aria-label="Wavee">
      <span className="wavee-mark__glyph">
        <i />
        <i />
        <i />
      </span>
      {!compact ? <span className="wavee-mark__word">wavee</span> : null}
    </span>
  );
}

export function WindowDots() {
  return (
    <span className="window-dots" aria-hidden="true">
      <span />
      <span />
      <span />
    </span>
  );
}

export function BrowserHistoryButtons() {
  return (
    <span className="history-buttons">
      <IconButton label="Go back" size="small" quiet>
        <ArrowLeft size={15} />
      </IconButton>
      <IconButton label="Go forward" size="small" quiet>
        <ArrowRight size={15} />
      </IconButton>
    </span>
  );
}

export function OverflowGlyph() {
  return <MoreHorizontal size={17} aria-hidden="true" />;
}

export function PinGlyph({ pinned }: { pinned: boolean }) {
  return <Pin size={13} fill={pinned ? "currentColor" : "none"} aria-hidden="true" />;
}

export function Modal({
  children,
  label,
  className = "",
  onBackdrop,
  testId,
}: {
  children: ReactNode;
  label: string;
  className?: string;
  onBackdrop?: () => void;
  testId?: string;
}) {
  return (
    <div
      className="modal-backdrop"
      role="presentation"
      onMouseDown={(event) => {
        if (event.currentTarget === event.target) onBackdrop?.();
      }}
    >
      <section
        className={`modal ${className}`}
        role="dialog"
        aria-modal="true"
        aria-label={label}
        data-testid={testId}
      >
        {children}
      </section>
    </div>
  );
}
