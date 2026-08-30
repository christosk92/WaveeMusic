import {
  ChevronDown,
  ChevronRight,
  PanelLeftOpen,
  Plus,
  Sparkles,
  WandSparkles,
} from "lucide-react";
import { useMemo } from "react";
import { libraryItems } from "../../data";
import { useStoredState } from "../../hooks/useStoredState";
import { usePrototype } from "../../PrototypeContext";
import type { CuratedSection, LibraryItem } from "../../types";
import { Artwork, IconButton } from "../Primitives";
import {
  CollapseButton,
  QuickLayoutMenu,
  resolveItems,
  SidebarDropZone,
  SidebarItemRow,
  SidebarModeHeader,
  SidebarRailTile,
  SidebarSection,
} from "./SidebarCommon";

function CuratedGrid({
  items,
  section,
  onFolder,
}: {
  items: LibraryItem[];
  section: CuratedSection;
  onFolder?: (id: string) => void;
}) {
  const { route, navigate, pins, pinItem, unpinItem } = usePrototype();
  return (
    <div className={`curated-grid curated-grid--${section.density}`}>
      {items.map((item) => {
        const selected = item.route.length > 0 && route === item.route;
        const pinned = pins.includes(item.id);
        return (
          <article
            key={item.id}
            className={selected ? "is-selected" : ""}
            role="button"
            tabIndex={0}
            onClick={() =>
              item.kind === "folder" ? onFolder?.(item.id) : item.route && navigate(item.route)
            }
            onKeyDown={(event) => {
              if (event.key !== "Enter" && event.key !== " ") return;
              event.preventDefault();
              item.kind === "folder" ? onFolder?.(item.id) : item.route && navigate(item.route);
            }}
            draggable
            onDragStart={(event) => event.dataTransfer.setData("text/wavee-item", item.id)}
          >
            {section.showArtwork ? <Artwork item={item} size={110} /> : null}
            <strong>{item.title}</strong>
            {section.showSubtitles ? <small>{item.subtitle}</small> : null}
            <button
              type="button"
              aria-label={pinned ? `Unpin ${item.title}` : `Pin ${item.title}`}
              onClick={(event) => {
                event.stopPropagation();
                pinned ? unpinItem(item.id) : pinItem(item.id);
              }}
            >
              {pinned ? "Pinned" : "Pin"}
            </button>
          </article>
        );
      })}
    </div>
  );
}

function itemsForSection(
  section: CuratedSection,
  pins: string[],
  expandedFolders: string[],
): LibraryItem[] {
  let items: LibraryItem[] = [];
  if (section.kind === "pinned") items = resolveItems(pins);
  else if (section.kind === "jumpBack")
    items = libraryItems
      .filter((item) => item.kind !== "folder")
      .sort((a, b) => b.visited - a.visited);
  else if (section.kind === "shortcuts" || section.kind === "group")
    items = resolveItems(section.itemIds);
  else if (section.kind === "playlists") {
    items = libraryItems.filter(
      (item) => item.kind === "folder" || (item.kind === "playlist" && item.folderId === undefined),
    );
    for (const folderId of expandedFolders) {
      const folderIndex = items.findIndex((item) => item.id === folderId);
      const children = libraryItems.filter((item) => item.folderId === folderId);
      if (folderIndex >= 0) items.splice(folderIndex + 1, 0, ...children);
    }
  } else if (section.kind === "library") items = libraryItems.filter((item) => item.kind !== "folder");

  if (section.maxItems > 0) items = items.slice(0, section.maxItems);
  return items;
}

function CuratedSectionBody({
  section,
  expandedFolders,
  toggleFolder,
}: {
  section: CuratedSection;
  expandedFolders: string[];
  toggleFolder: (id: string) => void;
}) {
  const { pins, movePin, showToast } = usePrototype();
  const items = itemsForSection(section, pins, expandedFolders);

  if (section.kind === "pinned" && !items.length) return <SidebarDropZone />;
  if (!items.length) {
    return (
      <div className="section-empty">
        <span>No items in this section</span>
      </div>
    );
  }

  if (section.presentation === "grid") {
    return <CuratedGrid items={items} section={section} onFolder={toggleFolder} />;
  }

  return (
    <div className="sidebar-list">
      {items.map((item) => (
        <SidebarItemRow
          key={item.id}
          item={item}
          density={section.density}
          showArtwork={section.showArtwork}
          showSubtitle={section.showSubtitles}
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
          reorderable={section.kind === "pinned"}
          onDropItem={section.kind === "pinned" ? movePin : undefined}
        />
      ))}
      {section.kind === "playlists" ? (
        <button
          type="button"
          className="curated-create-row"
          onClick={() => showToast("New playlist created")}
        >
          <Plus size={15} />
          Create playlist
        </button>
      ) : null}
    </div>
  );
}

export function CuratedSidebar({
  compact,
  preview = false,
}: {
  compact: boolean;
  preview?: boolean;
}) {
  const {
    layout,
    pins,
    updateSection,
    modePreferences,
    setModePreference,
    navigate,
  } = usePrototype();
  const [expandedFolders, setExpandedFolders] = useStoredState<string[]>(
    "wavee.sidebar.curated.expandedFolders",
    ["jazz-cafe"],
  );
  const visibleSections = layout.sections.filter((section) => !section.hidden);
  const toggleFolder = (id: string) =>
    setExpandedFolders((current) =>
      current.includes(id) ? current.filter((entry) => entry !== id) : [...current, id],
    );

  const railItems = useMemo(() => {
    const result: LibraryItem[] = [];
    for (const section of visibleSections) {
      if (!section.showInRail || ["divider", "heading"].includes(section.kind)) continue;
      const items = itemsForSection(section, pins, expandedFolders);
      const perSectionCap = section.kind === "pinned" ? 8 : section.kind === "library" ? 20 : 10;
      for (const item of items.slice(0, perSectionCap)) {
        if (!result.some((entry) => entry.id === item.id)) result.push(item);
        if (result.length >= 40) return result;
      }
    }
    return result;
  }, [expandedFolders, pins, visibleSections]);

  if (compact) {
    return (
      <div className="sidebar-rail sidebar-rail--curated" data-testid="curated-rail">
        <div className="sidebar-rail__top">
          <button
            type="button"
            className="rail-tile rail-tile--primary"
            title="Expand Wavee Curated"
            aria-label="Expand Wavee Curated"
            onClick={() => setModePreference("curated", { collapsed: false })}
          >
            <PanelLeftOpen size={17} />
          </button>
          {!preview ? <QuickLayoutMenu compact /> : null}
        </div>
        <div className="sidebar-rail__scroll">
          {railItems.length ? (
            railItems.map((item, index) => (
              <SidebarRailTile
                key={`${item.id}-${index}`}
                item={item}
                onClick={
                  item.kind === "folder"
                    ? () => {
                        setModePreference("curated", { collapsed: false });
                        toggleFolder(item.id);
                      }
                    : preview
                      ? () => undefined
                      : undefined
                }
              />
            ))
          ) : (
            <span className="empty-rail">
              <Sparkles size={16} />
            </span>
          )}
        </div>
      </div>
    );
  }

  return (
    <div
      className={`sidebar-expanded sidebar-expanded--curated ${preview ? "is-preview" : ""}`}
      data-testid={preview ? "curated-preview-sidebar" : "curated-sidebar"}
    >
      <SidebarModeHeader
        icon={<Sparkles size={16} />}
        title="Wavee Curated"
        subtitle={layout.templateId === "curated" ? "Your everyday mix" : `${layout.templateId} template`}
      >
        {!preview ? (
          <>
            <IconButton
              label="Customize sidebar"
              size="small"
              quiet
              onClick={() => navigate("sidebar-customize")}
            >
              <WandSparkles size={15} />
            </IconButton>
            <QuickLayoutMenu align="right" />
            <CollapseButton />
          </>
        ) : null}
      </SidebarModeHeader>

      <div className="sidebar-scroll curated-scroll">
        {visibleSections.length ? (
          visibleSections.map((section, index) => {
            if (section.kind === "divider")
              return <i className="curated-divider" key={section.id} />;
            if (section.kind === "heading")
              return (
                <div className="curated-heading" key={section.id}>
                  {section.title}
                </div>
              );
            return (
              <SidebarSection
                key={section.id}
                title={section.title}
                open={!section.collapsed}
                onToggle={() =>
                  !preview &&
                  updateSection(
                    section.id,
                    { collapsed: !section.collapsed },
                    section.collapsed ? "Expand section" : "Collapse section",
                  )
                }
                rule={index > 0 && visibleSections[index - 1]?.kind !== "divider"}
              >
                <CuratedSectionBody
                  section={section}
                  expandedFolders={expandedFolders}
                  toggleFolder={toggleFolder}
                />
              </SidebarSection>
            );
          })
        ) : (
          <div className="curated-blank">
            <span>
              <Sparkles size={24} />
            </span>
            <strong>Your sidebar is empty</strong>
            <small>Add a section or start from a template.</small>
            {!preview ? (
              <button type="button" onClick={() => navigate("sidebar-customize")}>
                Customize sidebar
              </button>
            ) : null}
          </div>
        )}
      </div>

      {!preview ? (
        <button
          type="button"
          className="curated-footer"
          onClick={() => navigate("sidebar-customize")}
        >
          <WandSparkles size={14} />
          <span>Customize this layout</span>
          <small>{Math.round(modePreferences.curated.width)} px</small>
        </button>
      ) : null}
    </div>
  );
}
