export type SidebarDesign = "classic" | "library" | "curated";
export type ThemeMode = "dark" | "light";
export type LibraryKind = "playlist" | "album" | "artist" | "podcast" | "route" | "folder";
export type LibraryFilter = "all" | "playlist" | "podcast" | "album" | "artist";
export type LibraryQualifier = "all" | "you" | "spotify" | "mixed";
export type LibrarySort = "recents" | "added" | "alphabetical" | "creator" | "custom";
export type LibraryView = "compact-list" | "list" | "compact-grid" | "grid";

export interface LibraryItem {
  id: string;
  title: string;
  kind: LibraryKind;
  subtitle: string;
  creator: string;
  qualifier?: Exclude<LibraryQualifier, "all">;
  route: string;
  art: string;
  icon?: string;
  count?: number;
  added: number;
  visited: number;
  folderId?: string;
  childIds?: string[];
}

export interface ModePreference {
  width: number;
  collapsed: boolean;
}

export type ModePreferences = Record<SidebarDesign, ModePreference>;

export type SectionKind =
  | "pinned"
  | "jumpBack"
  | "shortcuts"
  | "playlists"
  | "library"
  | "group"
  | "heading"
  | "divider";

export type SectionDensity = "compact" | "cozy" | "comfortable";

export interface CuratedSection {
  id: string;
  kind: SectionKind;
  title: string;
  hidden: boolean;
  collapsed: boolean;
  density: SectionDensity;
  presentation: "list" | "grid";
  showArtwork: boolean;
  showSubtitles: boolean;
  showInRail: boolean;
  maxItems: number;
  itemIds: string[];
}

export interface CuratedLayout {
  templateId: string;
  sections: CuratedSection[];
}

export interface ToastState {
  id: number;
  message: string;
  actionLabel?: string;
  onAction?: () => void;
}

export interface DesignMeta {
  id: SidebarDesign;
  title: string;
  eyebrow: string;
  description: string;
}
