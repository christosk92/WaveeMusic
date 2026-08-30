import {
  Check,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  GripVertical,
  MoreHorizontal,
  PanelLeftClose,
  PanelLeftOpen,
  Pin,
  PinOff,
  RotateCcw,
  Settings2,
  Sparkles,
} from "lucide-react";
import {
  type CSSProperties,
  type DragEvent,
  type KeyboardEvent,
  type ReactNode,
  useEffect,
  useRef,
  useState,
} from "react";
import { allItems, designMeta } from "../../data";
import { usePrototype } from "../../PrototypeContext";
import type { LibraryItem, SectionDensity, SidebarDesign } from "../../types";
import { Artwork, IconButton, ItemIcon } from "../Primitives";

export function SidebarSection({
  title,
  open,
  onToggle,
  children,
  action,
  rule = false,
  testId,
}: {
  title: string;
  open: boolean;
  onToggle: () => void;
  children: ReactNode;
  action?: ReactNode;
  rule?: boolean;
  testId?: string;
}) {
  return (
    <section className={`sidebar-section ${rule ? "sidebar-section--rule" : ""}`} data-testid={testId}>
      <header className="sidebar-section__header">
        <button type="button" onClick={onToggle} aria-expanded={open}>
          <span>{title}</span>
          <span className="sidebar-section__chevron">
            {open ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
          </span>
        </button>
        {action}
      </header>
      <div className={`sidebar-section__body ${open ? "is-open" : ""}`}>
        <div>{children}</div>
      </div>
    </section>
  );
}

function ItemActions({
  item,
  pinned,
  onClose,
}: {
  item: LibraryItem;
  pinned: boolean;
  onClose: () => void;
}) {
  const { navigate, pinItem, unpinItem } = usePrototype();
  return (
    <div className="row-menu" role="menu">
      {item.route ? (
        <button
          type="button"
          role="menuitem"
          onClick={() => {
            navigate(item.route);
            onClose();
          }}
        >
          <ChevronRight size={14} />
          Open
        </button>
      ) : null}
      <button
        type="button"
        role="menuitem"
        onClick={() => {
          if (pinned) unpinItem(item.id);
          else pinItem(item.id);
          onClose();
        }}
      >
        {pinned ? <PinOff size={14} /> : <Pin size={14} />}
        {pinned ? "Unpin from sidebar" : "Pin to sidebar"}
      </button>
    </div>
  );
}

export function SidebarItemRow({
  item,
  density = "cozy",
  showArtwork = true,
  showSubtitle = true,
  depth = 0,
  leading,
  trailing,
  onInvoke,
  reorderable = false,
  onDropItem,
  selectedOverride,
  className = "",
}: {
  item: LibraryItem;
  density?: SectionDensity;
  showArtwork?: boolean;
  showSubtitle?: boolean;
  depth?: number;
  leading?: ReactNode;
  trailing?: ReactNode;
  onInvoke?: () => void;
  reorderable?: boolean;
  onDropItem?: (fromId: string, toId: string) => void;
  selectedOverride?: boolean;
  className?: string;
}) {
  const { route, navigate, pins, pinItem, unpinItem } = usePrototype();
  const [menuOpen, setMenuOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const pinned = pins.includes(item.id);
  const selected = selectedOverride ?? (item.route.length > 0 && route === item.route);
  const playing = item.id === "midnight-city";

  useEffect(() => {
    if (!menuOpen) return;
    const close = (event: PointerEvent) => {
      if (!menuRef.current?.contains(event.target as Node)) setMenuOpen(false);
    };
    window.addEventListener("pointerdown", close);
    return () => window.removeEventListener("pointerdown", close);
  }, [menuOpen]);

  const invoke = () => {
    if (onInvoke) onInvoke();
    else if (item.route) navigate(item.route);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      invoke();
    }
  };

  return (
    <div
      className={`sidebar-row sidebar-row--${density} ${selected ? "is-selected" : ""} ${
        playing ? "is-playing" : ""
      } ${className}`}
      style={{ "--row-depth": Math.min(depth, 4) } as CSSProperties}
      role="button"
      tabIndex={0}
      aria-current={selected ? "page" : undefined}
      onClick={invoke}
      onKeyDown={onKeyDown}
      draggable
      onDragStart={(event) => {
        event.dataTransfer.effectAllowed = reorderable ? "move" : "copy";
        event.dataTransfer.setData("text/wavee-item", item.id);
        event.currentTarget.classList.add("is-dragging");
      }}
      onDragEnd={(event) => event.currentTarget.classList.remove("is-dragging")}
      onDragOver={(event) => {
        if (!onDropItem) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
      }}
      onDrop={(event: DragEvent<HTMLDivElement>) => {
        if (!onDropItem) return;
        event.preventDefault();
        const fromId = event.dataTransfer.getData("text/wavee-item");
        if (fromId) onDropItem(fromId, item.id);
      }}
      onContextMenu={(event) => {
        event.preventDefault();
        setMenuOpen(true);
      }}
      data-testid={`library-item-${item.id}`}
    >
      <span className="sidebar-row__selection" />
      {reorderable ? (
        <span className="sidebar-row__grip" aria-hidden="true">
          <GripVertical size={13} />
        </span>
      ) : null}
      {leading}
      {showArtwork ? (
        <Artwork item={item} size={density === "compact" ? 24 : density === "comfortable" ? 40 : 32} />
      ) : (
        <span className="sidebar-row__icon">
          <ItemIcon name={item.icon ?? item.kind} />
        </span>
      )}
      <span className="sidebar-row__copy">
        <strong>{item.title}</strong>
        {showSubtitle && item.subtitle ? <small>{item.subtitle}</small> : null}
      </span>
      {playing ? (
        <span className="equalizer" aria-label="Now playing">
          <i />
          <i />
          <i />
        </span>
      ) : null}
      {pinned ? (
        <button
          type="button"
          className="row-pin row-pin--active"
          aria-label={`Unpin ${item.title}`}
          title="Unpin from sidebar"
          onClick={(event) => {
            event.stopPropagation();
            unpinItem(item.id);
          }}
          data-testid={`unpin-${item.id}`}
        >
          <Pin size={12} fill="currentColor" />
        </button>
      ) : (
        <button
          type="button"
          className="row-pin"
          aria-label={`Pin ${item.title}`}
          title="Pin to sidebar"
          onClick={(event) => {
            event.stopPropagation();
            pinItem(item.id);
          }}
          data-testid={`pin-${item.id}`}
        >
          <Pin size={12} />
        </button>
      )}
      {trailing}
      <div className="row-menu-anchor" ref={menuRef}>
        <button
          type="button"
          className="row-more"
          aria-label={`More options for ${item.title}`}
          aria-expanded={menuOpen}
          onClick={(event) => {
            event.stopPropagation();
            setMenuOpen((value) => !value);
          }}
        >
          <MoreHorizontal size={16} />
        </button>
        {menuOpen ? (
          <ItemActions item={item} pinned={pinned} onClose={() => setMenuOpen(false)} />
        ) : null}
      </div>
    </div>
  );
}

export function SidebarDropZone({ compact = false }: { compact?: boolean }) {
  const { pinItem } = usePrototype();
  const [active, setActive] = useState(false);
  return (
    <div
      className={`sidebar-dropzone ${compact ? "sidebar-dropzone--compact" : ""} ${
        active ? "is-active" : ""
      }`}
      onDragEnter={(event) => {
        event.preventDefault();
        setActive(true);
      }}
      onDragOver={(event) => event.preventDefault()}
      onDragLeave={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node)) setActive(false);
      }}
      onDrop={(event) => {
        event.preventDefault();
        const itemId = event.dataTransfer.getData("text/wavee-item");
        if (itemId) pinItem(itemId);
        setActive(false);
      }}
      data-testid="pin-dropzone"
    >
      <Pin size={16} />
      <span>
        <strong>Drop items here to pin</strong>
        {!compact ? <small>Or use the pin action on any library item.</small> : null}
      </span>
    </div>
  );
}

export function QuickLayoutMenu({
  compact = false,
  align = "left",
}: {
  compact?: boolean;
  align?: "left" | "right";
}) {
  const {
    design,
    switchDesign,
    modePreferences,
    setModePreference,
    resetModeWidth,
    navigate,
  } = usePrototype();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (event: PointerEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false);
    };
    window.addEventListener("pointerdown", close);
    return () => window.removeEventListener("pointerdown", close);
  }, [open]);

  return (
    <div
      className={`quick-layout quick-layout--${align} ${compact ? "quick-layout--compact" : ""}`}
      ref={rootRef}
    >
      <IconButton
        label="Change sidebar layout"
        size={compact ? "large" : "small"}
        quiet
        onClick={() => setOpen((value) => !value)}
        aria-expanded={open}
        data-testid="quick-layout"
      >
        {compact ? <Settings2 size={16} /> : <MoreHorizontal size={16} />}
      </IconButton>
      {open ? (
        <div className="quick-layout__menu" role="menu" data-testid="quick-menu">
          <span className="menu-heading">Sidebar layout</span>
          {designMeta.map((item) => (
            <button
              type="button"
              role="menuitemradio"
              aria-checked={design === item.id}
              key={item.id}
              onClick={() => {
                switchDesign(item.id);
                setOpen(false);
              }}
              data-testid={`quick-mode-${item.id}`}
            >
              <span className={`menu-mode-dot menu-mode-dot--${item.id}`} />
              <span>
                <strong>{item.title}</strong>
                <small>{item.eyebrow}</small>
              </span>
              {design === item.id ? <Check size={15} /> : null}
            </button>
          ))}
          <i className="menu-separator" />
          <button
            type="button"
            role="menuitem"
            onClick={() => {
              if (design !== "curated") switchDesign("curated");
              navigate("sidebar-customize");
              setOpen(false);
            }}
            data-testid="quick-customize"
          >
            <Sparkles size={15} />
            <span>
              <strong>Customize sidebar…</strong>
              {design !== "curated" ? <small>Switches to Wavee Curated</small> : null}
            </span>
            <ChevronRight size={14} />
          </button>
          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setModePreference(design, {
                collapsed: !modePreferences[design].collapsed,
              });
              setOpen(false);
            }}
            data-testid="toggle-collapse"
          >
            {modePreferences[design].collapsed ? (
              <PanelLeftOpen size={15} />
            ) : (
              <PanelLeftClose size={15} />
            )}
            <span>
              <strong>
                {modePreferences[design].collapsed ? "Expand sidebar" : "Collapse sidebar"}
              </strong>
            </span>
          </button>
          <button
            type="button"
            role="menuitem"
            onClick={() => {
              resetModeWidth(design);
              setOpen(false);
            }}
          >
            <RotateCcw size={15} />
            <span>
              <strong>Reset width</strong>
            </span>
          </button>
        </div>
      ) : null}
    </div>
  );
}

export function SidebarRailTile({
  item,
  onClick,
  selected,
}: {
  item: LibraryItem;
  onClick?: () => void;
  selected?: boolean;
}) {
  const { route, navigate } = usePrototype();
  const isSelected = selected ?? (item.route.length > 0 && item.route === route);
  return (
    <button
      type="button"
      className={`rail-tile ${isSelected ? "is-selected" : ""}`}
      title={item.title}
      aria-label={item.title}
      onClick={() => (onClick ? onClick() : item.route ? navigate(item.route) : undefined)}
    >
      {item.kind === "route" || item.kind === "folder" ? (
        <span className="rail-tile__icon">
          <ItemIcon name={item.icon ?? item.kind} size={17} />
        </span>
      ) : (
        <Artwork item={item} size={38} />
      )}
    </button>
  );
}

export function SidebarModeHeader({
  icon,
  title,
  subtitle,
  children,
}: {
  icon: ReactNode;
  title: string;
  subtitle?: string;
  children?: ReactNode;
}) {
  return (
    <header className="sidebar-mode-header">
      <span className="sidebar-mode-header__icon">{icon}</span>
      <span className="sidebar-mode-header__copy">
        <strong>{title}</strong>
        {subtitle ? <small>{subtitle}</small> : null}
      </span>
      {children}
    </header>
  );
}

export function CollapseButton() {
  const { design, modePreferences, setModePreference } = usePrototype();
  return (
    <IconButton
      label="Collapse sidebar"
      size="small"
      quiet
      onClick={() => setModePreference(design, { collapsed: true })}
    >
      <ChevronLeft size={16} />
    </IconButton>
  );
}

export function resolveItems(ids: string[]) {
  return ids
    .map((id) => allItems.find((item) => item.id === id))
    .filter((item): item is LibraryItem => Boolean(item));
}

export function findItem(id: string) {
  return allItems.find((item) => item.id === id);
}

export function designLabel(design: SidebarDesign) {
  return design === "classic" ? "Classic" : design === "library" ? "Library" : "Wavee Curated";
}
