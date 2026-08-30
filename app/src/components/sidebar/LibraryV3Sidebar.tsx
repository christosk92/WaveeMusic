import {
  ArrowDownAZ,
  ArrowDownUp,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  Clock3,
  Grid2X2,
  Library,
  List,
  ListFilter,
  MoreHorizontal,
  PanelLeftOpen,
  Plus,
  Search,
  SlidersHorizontal,
  X,
} from "lucide-react";
import { useMemo, useRef, useState } from "react";
import { libraryItems } from "../../data";
import { useStoredState } from "../../hooks/useStoredState";
import { usePrototype } from "../../PrototypeContext";
import type {
  LibraryFilter,
  LibraryItem,
  LibrarySort,
  LibraryView,
} from "../../types";
import { Artwork, IconButton } from "../Primitives";
import {
  CollapseButton,
  QuickLayoutMenu,
  SidebarItemRow,
  SidebarModeHeader,
  SidebarRailTile,
} from "./SidebarCommon";

const filterOptions: { id: Exclude<LibraryFilter, "all">; label: string }[] = [
  { id: "playlist", label: "Playlists" },
  { id: "podcast", label: "Podcasts" },
  { id: "album", label: "Albums" },
  { id: "artist", label: "Artists" },
];

const sortOptions: { id: LibrarySort; label: string }[] = [
  { id: "recents", label: "Recents" },
  { id: "added", label: "Recently added" },
  { id: "alphabetical", label: "Alphabetical" },
  { id: "creator", label: "Creator" },
  { id: "custom", label: "Custom order" },
];

const viewOptions: { id: LibraryView; label: string }[] = [
  { id: "compact-list", label: "Compact list" },
  { id: "list", label: "List" },
  { id: "compact-grid", label: "Compact grid" },
  { id: "grid", label: "Grid" },
];

function filterMatches(item: LibraryItem, filter: LibraryFilter) {
  if (filter === "all") return true;
  if (filter === "playlist") return item.kind === "playlist" || item.kind === "folder";
  return item.kind === filter;
}

function LibraryCard({
  item,
  compact,
  onFolder,
}: {
  item: LibraryItem;
  compact: boolean;
  onFolder?: () => void;
}) {
  const { route, navigate, pins, pinItem, unpinItem } = usePrototype();
  const selected = item.route.length > 0 && route === item.route;
  const pinned = pins.includes(item.id);
  return (
    <article
      className={`library-card ${compact ? "library-card--compact" : ""} ${
        selected ? "is-selected" : ""
      }`}
      role="button"
      tabIndex={0}
      onClick={() => {
        if (item.kind === "folder") onFolder?.();
        else if (item.route) navigate(item.route);
      }}
      onKeyDown={(event) => {
        if (event.key !== "Enter" && event.key !== " ") return;
        event.preventDefault();
        if (item.kind === "folder") onFolder?.();
        else if (item.route) navigate(item.route);
      }}
      draggable
      onDragStart={(event) => event.dataTransfer.setData("text/wavee-item", item.id)}
      data-testid={`library-item-${item.id}`}
    >
      <span className="library-card__art">
        <Artwork item={item} size={152} />
        {pinned ? (
          <span className="library-card__pin" title="Pinned">
            ●
          </span>
        ) : null}
        {item.id === "midnight-city" ? (
          <span className="library-card__playing">
            <i />
            <i />
            <i />
          </span>
        ) : null}
      </span>
      <strong>{item.title}</strong>
      {!compact ? <small>{item.subtitle}</small> : null}
      <button
        type="button"
        className="library-card__action"
        aria-label={pinned ? `Unpin ${item.title}` : `Pin ${item.title}`}
        onClick={(event) => {
          event.stopPropagation();
          if (pinned) unpinItem(item.id);
          else pinItem(item.id);
        }}
        data-testid={pinned ? `unpin-${item.id}` : `pin-${item.id}`}
      >
        {pinned ? "Pinned" : "Pin"}
      </button>
    </article>
  );
}

export function LibraryV3Sidebar({ compact }: { compact: boolean }) {
  const {
    pins,
    libraryPreferences,
    setLibraryPreferences,
    librarySearch,
    setLibrarySearch,
    customOrder,
    moveCustomOrder,
    modePreferences,
    setModePreference,
    showToast,
  } = usePrototype();
  const [searchOpen, setSearchOpen] = useState(false);
  const [sortOpen, setSortOpen] = useState(false);
  const [expandedFolders, setExpandedFolders] = useStoredState<string[]>(
    "wavee.sidebar.library.expandedFolders",
    [],
  );
  const searchRef = useRef<HTMLInputElement>(null);
  const { filter, qualifier, sort, descending, view } = libraryPreferences;

  const visibleItems = useMemo(() => {
    const query = librarySearch.trim().toLocaleLowerCase();
    let items = libraryItems.filter((item) => {
      if (!filterMatches(item, filter)) return false;
      if (
        filter === "playlist" &&
        qualifier !== "all" &&
        item.kind === "playlist" &&
        item.qualifier !== qualifier
      ) {
        return false;
      }
      if (filter === "playlist" && qualifier !== "all" && item.kind === "folder") return false;
      if (!query) return item.folderId === undefined || expandedFolders.includes(item.folderId);
      if (item.kind === "folder") {
        return (
          item.title.toLocaleLowerCase().includes(query) ||
          item.childIds?.some((id) => {
            const child = libraryItems.find((entry) => entry.id === id);
            return child?.title.toLocaleLowerCase().includes(query);
          })
        );
      }
      return `${item.title} ${item.creator}`.toLocaleLowerCase().includes(query);
    });

    items = [...items].sort((a, b) => {
      let result = 0;
      if (sort === "recents") result = b.visited - a.visited;
      else if (sort === "added") result = b.added - a.added;
      else if (sort === "alphabetical") result = a.title.localeCompare(b.title);
      else if (sort === "creator") {
        result = (a.creator || "zzzz").localeCompare(b.creator || "zzzz");
        if (result === 0) result = a.title.localeCompare(b.title);
      } else {
        const ai = customOrder.indexOf(a.id);
        const bi = customOrder.indexOf(b.id);
        result = (ai < 0 ? Number.MAX_SAFE_INTEGER : ai) - (bi < 0 ? Number.MAX_SAFE_INTEGER : bi);
      }
      return descending ? -result : result;
    });

    const pinned = items.filter((item) => pins.includes(item.id));
    pinned.sort((a, b) => pins.indexOf(a.id) - pins.indexOf(b.id));
    const remaining = items.filter((item) => !pins.includes(item.id));
    return [...pinned, ...remaining];
  }, [
    customOrder,
    descending,
    expandedFolders,
    filter,
    librarySearch,
    pins,
    qualifier,
    sort,
  ]);

  const setFilter = (next: LibraryFilter) => {
    setLibraryPreferences((current) => {
      const effective = current.filter === next ? "all" : next;
      return {
        ...current,
        filter: effective,
        qualifier: effective === "playlist" ? current.qualifier : "all",
        sort:
          current.sort === "custom" && effective !== "playlist" ? "recents" : current.sort,
      };
    });
  };

  const toggleFolder = (id: string) =>
    setExpandedFolders((current) =>
      current.includes(id) ? current.filter((entry) => entry !== id) : [...current, id],
    );

  if (compact) {
    return (
      <div className="sidebar-rail sidebar-rail--library" data-testid="library-rail">
        <div className="sidebar-rail__top">
          <button
            type="button"
            className="rail-tile rail-tile--primary"
            title="Expand Your Library"
            aria-label="Expand Your Library"
            onClick={() => setModePreference("library", { collapsed: false })}
          >
            <PanelLeftOpen size={17} />
          </button>
          <QuickLayoutMenu compact />
        </div>
        <div className="sidebar-rail__scroll">
          <IconButton
            label="Create playlist"
            size="large"
            quiet
            onClick={() => showToast("New playlist created")}
          >
            <Plus size={17} />
          </IconButton>
          <i className="rail-divider" />
          {visibleItems.map((item) => (
            <SidebarRailTile
              key={item.id}
              item={item}
              onClick={
                item.kind === "folder"
                  ? () => {
                      setModePreference("library", { collapsed: false });
                      toggleFolder(item.id);
                    }
                  : undefined
              }
            />
          ))}
        </div>
      </div>
    );
  }

  const grid = view === "grid" || view === "compact-grid";
  const customReorder = filter === "playlist" && sort === "custom" && !librarySearch;

  return (
    <div className="sidebar-expanded sidebar-expanded--library" data-testid="library-sidebar">
      <SidebarModeHeader icon={<Library size={17} />} title="Your Library">
        <IconButton
          label="Create playlist"
          size="small"
          quiet
          onClick={() => showToast("New playlist created")}
        >
          <Plus size={15} />
        </IconButton>
        <QuickLayoutMenu align="right" />
        <CollapseButton />
      </SidebarModeHeader>

      <div className="library-toolbar">
        <div className={`library-search ${searchOpen || librarySearch ? "is-open" : ""}`}>
          {searchOpen || librarySearch ? (
            <>
              <Search size={14} />
              <input
                ref={searchRef}
                value={librarySearch}
                onChange={(event) => setLibrarySearch(event.target.value)}
                placeholder="Search in Your Library"
                aria-label="Search in Your Library"
                data-testid="v3-search-input"
                onKeyDown={(event) => {
                  if (event.key !== "Escape") return;
                  if (librarySearch) setLibrarySearch("");
                  else setSearchOpen(false);
                }}
              />
              <button
                type="button"
                aria-label="Clear search"
                onClick={() => {
                  setLibrarySearch("");
                  setSearchOpen(false);
                }}
              >
                <X size={13} />
              </button>
            </>
          ) : (
            <IconButton
              label="Search your library"
              size="small"
              quiet
              onClick={() => {
                setSearchOpen(true);
                window.setTimeout(() => searchRef.current?.focus(), 0);
              }}
              data-testid="v3-search-toggle"
            >
              <Search size={15} />
            </IconButton>
          )}
        </div>

        <div className="sort-control">
          <button
            type="button"
            className="sort-trigger"
            onClick={() => setSortOpen((value) => !value)}
            aria-expanded={sortOpen}
            data-testid="sort-button"
          >
            <SlidersHorizontal size={14} />
            {!searchOpen ? (
              <span>{sortOptions.find((option) => option.id === sort)?.label}</span>
            ) : null}
            <ChevronDown size={11} />
          </button>
          {sortOpen ? (
            <div className="sort-panel">
              <span>Sort by</span>
              {sortOptions.map((option) =>
                option.id !== "custom" || filter === "playlist" ? (
                  <button
                    key={option.id}
                    type="button"
                    className={sort === option.id ? "is-active" : ""}
                    onClick={() => {
                      setLibraryPreferences((current) => ({
                        ...current,
                        sort: option.id,
                        descending: option.id === current.sort ? !current.descending : false,
                      }));
                      setSortOpen(false);
                    }}
                    data-testid={`sort-${option.id}`}
                  >
                    {option.id === "recents" ? (
                      <Clock3 size={14} />
                    ) : option.id === "alphabetical" ? (
                      <ArrowDownAZ size={14} />
                    ) : (
                      <ArrowDownUp size={14} />
                    )}
                    {option.label}
                    {sort === option.id ? <span>✓</span> : null}
                  </button>
                ) : null,
              )}
              <i />
              <span>View as</span>
              <div className="view-bank">
                {viewOptions.map((option) => (
                  <button
                    type="button"
                    key={option.id}
                    className={view === option.id ? "is-active" : ""}
                    aria-label={option.label}
                    title={option.label}
                    onClick={() => {
                      setLibraryPreferences((current) => ({ ...current, view: option.id }));
                      setSortOpen(false);
                    }}
                    data-testid={`view-${option.id}`}
                  >
                    {option.id.includes("grid") ? (
                      <Grid2X2 size={option.id === "grid" ? 16 : 13} />
                    ) : (
                      <List size={option.id === "list" ? 16 : 13} />
                    )}
                  </button>
                ))}
              </div>
            </div>
          ) : null}
        </div>
      </div>

      <div className="filter-rail">
        {filter !== "all" ? (
          <button type="button" className="filter-chip filter-chip--clear" onClick={() => setFilter(filter)}>
            <X size={12} />
          </button>
        ) : null}
        {filterOptions.map((option) => (
          <button
            type="button"
            className={`filter-chip ${filter === option.id ? "is-active" : ""}`}
            key={option.id}
            onClick={() => setFilter(option.id)}
            data-testid={`filter-${option.id}`}
          >
            {option.label}
          </button>
        ))}
      </div>

      {filter === "playlist" ? (
        <div className="qualifier-rail">
          {(["you", "spotify", "mixed"] as const).map((option) => (
            <button
              type="button"
              key={option}
              className={qualifier === option ? "is-active" : ""}
              onClick={() =>
                setLibraryPreferences((current) => ({
                  ...current,
                  qualifier: current.qualifier === option ? "all" : option,
                }))
              }
            >
              {option === "you" ? "By you" : option === "spotify" ? "By Spotify" : "Mixed"}
            </button>
          ))}
        </div>
      ) : null}

      <div className={`library-results library-results--${view}`} data-testid="v3-results">
        {visibleItems.length ? (
          <div className={grid ? "library-grid" : "sidebar-list"}>
            {visibleItems.map((item) =>
              grid ? (
                <LibraryCard
                  key={item.id}
                  item={item}
                  compact={view === "compact-grid"}
                  onFolder={() => {
                    toggleFolder(item.id);
                    setLibraryPreferences((current) => ({ ...current, view: "list" }));
                  }}
                />
              ) : (
                <SidebarItemRow
                  key={item.id}
                  item={item}
                  density={view === "compact-list" ? "compact" : "cozy"}
                  depth={item.folderId ? 1 : 0}
                  leading={
                    item.kind === "folder" ? (
                      expandedFolders.includes(item.id) ? (
                        <ChevronDown size={13} />
                      ) : (
                        <ChevronRight size={13} />
                      )
                    ) : undefined
                  }
                  onInvoke={item.kind === "folder" ? () => toggleFolder(item.id) : undefined}
                  reorderable={customReorder && item.kind === "playlist"}
                  onDropItem={customReorder ? moveCustomOrder : undefined}
                />
              ),
            )}
          </div>
        ) : (
          <div className="sidebar-empty">
            <ListFilter size={22} />
            <strong>{librarySearch ? `No results for “${librarySearch}”` : "Nothing here yet"}</strong>
            <small>
              {librarySearch
                ? "Try a different spelling or clear the filter."
                : "Change the active filter to see more of your library."}
            </small>
            <button
              type="button"
              onClick={() => {
                setLibrarySearch("");
                setLibraryPreferences((current) => ({
                  ...current,
                  filter: "all",
                  qualifier: "all",
                }));
              }}
            >
              Clear filters
            </button>
          </div>
        )}
      </div>

      {customReorder ? (
        <div className="reorder-hint">Drag playlists to set your local order</div>
      ) : null}
      <div className="sidebar-width-readout" aria-hidden="true">
        {Math.round(modePreferences.library.width)} px
      </div>
    </div>
  );
}
