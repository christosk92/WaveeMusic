import { ChevronRight, Plus } from "lucide-react";
import { libraryItems, shortcutItems } from "../../data";
import { useStoredState } from "../../hooks/useStoredState";
import { usePrototype } from "../../PrototypeContext";
import type { LibraryItem } from "../../types";
import { IconButton, WaveeMark } from "../Primitives";
import {
  QuickLayoutMenu,
  resolveItems,
  SidebarDropZone,
  SidebarItemRow,
  SidebarRailTile,
  SidebarSection,
} from "./SidebarCommon";

const classicShortcuts = ["albums", "artists", "liked", "podcasts", "local"];

export function ClassicSidebar({ compact }: { compact: boolean }) {
  const { pins, movePin, showToast } = usePrototype();
  const [open, setOpen] = useStoredState("wavee.sidebar.classic.sections", {
    pinned: true,
    library: true,
    playlists: true,
  });
  const pinned = resolveItems(pins);
  const shortcuts = resolveItems(classicShortcuts);
  const playlists = libraryItems.filter(
    (item) => item.kind === "playlist" && item.folderId === undefined,
  );

  if (compact) {
    return (
      <div className="sidebar-rail sidebar-rail--classic" data-testid="classic-rail">
        <div className="sidebar-rail__top">
          <WaveeMark compact />
          <QuickLayoutMenu compact />
        </div>
        <div className="sidebar-rail__scroll">
          {pinned.map((item) => (
            <SidebarRailTile key={`pin-${item.id}`} item={item} />
          ))}
          {pinned.length ? <i className="rail-divider" /> : null}
          {shortcuts.map((item) => (
            <SidebarRailTile key={item.id} item={item} />
          ))}
          <i className="rail-divider" />
          <IconButton
            label="Create playlist"
            size="large"
            quiet
            onClick={() => showToast("New playlist created")}
          >
            <Plus size={17} />
          </IconButton>
          {playlists.map((item) => (
            <SidebarRailTile key={item.id} item={item} />
          ))}
        </div>
      </div>
    );
  }

  return (
    <div className="sidebar-expanded sidebar-expanded--classic" data-testid="classic-sidebar">
      <div className="classic-brand-row">
        <WaveeMark />
        <QuickLayoutMenu align="right" />
      </div>
      <div className="sidebar-scroll">
        <SidebarSection
          title="Pinned"
          open={open.pinned}
          onToggle={() => setOpen((value) => ({ ...value, pinned: !value.pinned }))}
          testId="classic-pinned"
        >
          {pinned.length ? (
            <div className="sidebar-list">
              {pinned.map((item) => (
                <SidebarItemRow
                  key={item.id}
                  item={item}
                  reorderable
                  onDropItem={movePin}
                />
              ))}
            </div>
          ) : (
            <SidebarDropZone />
          )}
        </SidebarSection>

        <SidebarSection
          title="Your Library"
          open={open.library}
          onToggle={() => setOpen((value) => ({ ...value, library: !value.library }))}
          rule
        >
          <div className="sidebar-list">
            {shortcuts.map((item) => (
              <SidebarItemRow
                key={item.id}
                item={item}
                showArtwork={false}
                showSubtitle={false}
                trailing={
                  item.count ? <span className="count-badge">{item.count}</span> : undefined
                }
              />
            ))}
          </div>
        </SidebarSection>

        <SidebarSection
          title="Playlists"
          open={open.playlists}
          onToggle={() => setOpen((value) => ({ ...value, playlists: !value.playlists }))}
          rule
          action={
            <IconButton
              label="Create playlist"
              size="small"
              quiet
              onClick={() => showToast("New playlist created")}
            >
              <Plus size={14} />
            </IconButton>
          }
        >
          <div className="sidebar-list">
            {playlists.map((item) => (
              <SidebarItemRow key={item.id} item={item} />
            ))}
          </div>
        </SidebarSection>

        <button
          type="button"
          className="developer-row"
          onClick={() => showToast("API Console is outside this prototype")}
        >
          <span>⌘</span>
          <strong>API Console</strong>
          <ChevronRight size={13} />
        </button>
      </div>
    </div>
  );
}
