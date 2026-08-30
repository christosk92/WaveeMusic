import {
  ArrowDown,
  ArrowLeft,
  ArrowUp,
  Check,
  ChevronRight,
  Copy,
  Eye,
  EyeOff,
  GripVertical,
  Heading,
  Library,
  Link2,
  ListMusic,
  Pin,
  Plus,
  Redo2,
  RotateCcw,
  SeparatorHorizontal,
  Sparkles,
  Trash2,
  Undo2,
  X,
} from "lucide-react";
import { useEffect, useMemo, useState, type DragEvent } from "react";
import { allItems, section, templateMeta } from "../data";
import { usePrototype } from "../PrototypeContext";
import type { CuratedSection, SectionKind } from "../types";
import { CuratedSidebar } from "./sidebar/CuratedSidebar";
import { IconButton, ItemIcon, Modal } from "./Primitives";

const sectionPalette: {
  kind: SectionKind;
  title: string;
  description: string;
  icon: typeof Pin;
}[] = [
  { kind: "pinned", title: "Pinned", description: "Your shared pinned items", icon: Pin },
  {
    kind: "jumpBack",
    title: "Jump back in",
    description: "Recently opened music",
    icon: RotateCcw,
  },
  {
    kind: "shortcuts",
    title: "Library shortcuts",
    description: "Liked, albums, artists and more",
    icon: Link2,
  },
  {
    kind: "playlists",
    title: "Playlist tree",
    description: "Playlists and folders",
    icon: ListMusic,
  },
  {
    kind: "library",
    title: "Library list",
    description: "A dynamic library section",
    icon: Library,
  },
  {
    kind: "group",
    title: "Custom group",
    description: "Hand-pick your own items",
    icon: Sparkles,
  },
  { kind: "heading", title: "Heading", description: "A simple text label", icon: Heading },
  {
    kind: "divider",
    title: "Divider",
    description: "Create visual rhythm",
    icon: SeparatorHorizontal,
  },
];

function SectionTitleEditor({
  value,
  onCommit,
  testId,
}: {
  value: string;
  onCommit: (value: string) => void;
  testId?: string;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  return (
    <input
      value={draft}
      maxLength={60}
      onChange={(event) => setDraft(event.target.value)}
      onBlur={() => onCommit(draft.trim())}
      onKeyDown={(event) => {
        if (event.key === "Enter") event.currentTarget.blur();
        if (event.key === "Escape") {
          setDraft(value);
          event.currentTarget.blur();
        }
      }}
      data-testid={testId}
    />
  );
}

function Toggle({
  checked,
  onChange,
  label,
  description,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  description?: string;
}) {
  return (
    <label className="property-toggle">
      <span>
        <strong>{label}</strong>
        {description ? <small>{description}</small> : null}
      </span>
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
      />
      <i aria-hidden="true" />
    </label>
  );
}

function Segmented<T extends string>({
  value,
  options,
  onChange,
}: {
  value: T;
  options: { value: T; label: string }[];
  onChange: (value: T) => void;
}) {
  return (
    <div className="segmented" role="radiogroup">
      {options.map((option) => (
        <button
          type="button"
          key={option.value}
          className={value === option.value ? "is-active" : ""}
          role="radio"
          aria-checked={value === option.value}
          onClick={() => onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

function PropertyPanel({
  section: selected,
  remove,
  duplicate,
}: {
  section: CuratedSection | undefined;
  remove: (section: CuratedSection) => void;
  duplicate: (section: CuratedSection) => void;
}) {
  const { updateSection } = usePrototype();
  const [itemSearch, setItemSearch] = useState("");
  if (!selected) {
    return (
      <div className="property-empty">
        <Sparkles size={22} />
        <strong>Select a section</strong>
        <small>Its controls and content will appear here.</small>
      </div>
    );
  }

  const canShowItems = selected.kind === "group" || selected.kind === "shortcuts";
  const candidates = allItems.filter((item) =>
    `${item.title} ${item.subtitle}`.toLowerCase().includes(itemSearch.toLowerCase()),
  );

  return (
    <div className="property-panel" key={selected.id}>
      <div className="property-panel__identity">
        <span className={`section-kind-icon section-kind-icon--${selected.kind}`}>
          <ItemIcon
            name={
              selected.kind === "playlists"
                ? "playlist"
                : selected.kind === "jumpBack"
                  ? "history"
                  : selected.kind === "shortcuts"
                    ? "library"
                    : selected.kind
            }
          />
        </span>
        <span>
          <small>{sectionPalette.find((item) => item.kind === selected.kind)?.title}</small>
          <SectionTitleEditor
            value={selected.title}
            onCommit={(title) => updateSection(selected.id, { title }, "Rename section")}
            testId="property-title"
          />
        </span>
      </div>
      <div className="property-panel__actions">
        <button type="button" onClick={() => duplicate(selected)}>
          <Copy size={13} /> Duplicate
        </button>
        <button type="button" className="is-danger" onClick={() => remove(selected)}>
          <Trash2 size={13} /> Remove
        </button>
      </div>

      {!["heading", "divider"].includes(selected.kind) ? (
        <>
          <div className="property-group">
            <span className="property-group__label">Layout</span>
            <label className="property-field">
              <span>Density</span>
              <Segmented
                value={selected.density}
                options={[
                  { value: "compact", label: "Compact" },
                  { value: "cozy", label: "Cozy" },
                  { value: "comfortable", label: "Roomy" },
                ]}
                onChange={(density) =>
                  updateSection(selected.id, { density }, "Change density")
                }
              />
            </label>
            <label className="property-field">
              <span>Presentation</span>
              <Segmented
                value={selected.presentation}
                options={[
                  { value: "list", label: "List" },
                  { value: "grid", label: "Grid" },
                ]}
                onChange={(presentation) =>
                  updateSection(selected.id, { presentation }, "Change presentation")
                }
              />
            </label>
            <label className="property-range">
              <span>
                <strong>Maximum items</strong>
                <small>{selected.maxItems === 0 ? "All" : selected.maxItems}</small>
              </span>
              <input
                type="range"
                min={0}
                max={20}
                value={selected.maxItems}
                onChange={(event) =>
                  updateSection(
                    selected.id,
                    { maxItems: Number(event.target.value) },
                    "Change item limit",
                  )
                }
              />
            </label>
          </div>
          <div className="property-group">
            <span className="property-group__label">Visible details</span>
            <Toggle
              label="Show artwork"
              checked={selected.showArtwork}
              onChange={(showArtwork) =>
                updateSection(selected.id, { showArtwork }, "Toggle artwork")
              }
            />
            <Toggle
              label="Show subtitles"
              checked={selected.showSubtitles}
              onChange={(showSubtitles) =>
                updateSection(selected.id, { showSubtitles }, "Toggle subtitles")
              }
            />
            <Toggle
              label="Show in collapsed rail"
              description="Contributes icons or artwork to the 56 px rail."
              checked={selected.showInRail}
              onChange={(showInRail) =>
                updateSection(selected.id, { showInRail }, "Change rail visibility")
              }
            />
          </div>
        </>
      ) : (
        <div className="property-group">
          <Toggle
            label="Show in collapsed rail"
            checked={selected.showInRail}
            onChange={(showInRail) =>
              updateSection(selected.id, { showInRail }, "Change rail visibility")
            }
          />
        </div>
      )}

      <div className="property-group">
        <span className="property-group__label">Section state</span>
        <Toggle
          label="Hidden"
          description="Keep it in the editor but remove it from the live sidebar."
          checked={selected.hidden}
          onChange={(hidden) => updateSection(selected.id, { hidden }, "Toggle section")}
        />
        {!["heading", "divider"].includes(selected.kind) ? (
          <Toggle
            label="Collapsed"
            checked={selected.collapsed}
            onChange={(collapsed) =>
              updateSection(selected.id, { collapsed }, "Toggle section collapse")
            }
          />
        ) : null}
      </div>

      {canShowItems ? (
        <div className="property-group item-picker">
          <span className="property-group__label">Items</span>
          <input
            type="search"
            placeholder="Find a route or library item"
            value={itemSearch}
            onChange={(event) => setItemSearch(event.target.value)}
          />
          <div className="item-picker__results">
            {candidates.slice(0, 10).map((item) => {
              const checked = selected.itemIds.includes(item.id);
              return (
                <label key={item.id}>
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() =>
                      updateSection(
                        selected.id,
                        {
                          itemIds: checked
                            ? selected.itemIds.filter((id) => id !== item.id)
                            : [...selected.itemIds, item.id],
                        },
                        checked ? "Remove item" : "Add item",
                      )
                    }
                  />
                  <span className="item-picker__icon">
                    <ItemIcon name={item.icon ?? item.kind} size={14} />
                  </span>
                  <span>
                    <strong>{item.title}</strong>
                    <small>{item.subtitle || item.kind}</small>
                  </span>
                  {checked ? <Check size={13} /> : <Plus size={13} />}
                </label>
              );
            })}
          </div>
        </div>
      ) : null}
    </div>
  );
}

export function SidebarCustomizer() {
  const {
    layout,
    commitLayout,
    applyTemplate,
    undoLayout,
    redoLayout,
    canUndo,
    canRedo,
    undoLabel,
    redoLabel,
    navigate,
    previousRoute,
  } = usePrototype();
  const [selectedId, setSelectedId] = useState<string | null>(
    () => layout.sections.find((item) => !["divider"].includes(item.kind))?.id ?? null,
  );
  const [dragId, setDragId] = useState<string | null>(null);
  const [previewMode, setPreviewMode] = useState<"expanded" | "rail">("expanded");
  const [pendingTemplate, setPendingTemplate] = useState<string | null>(null);
  const selected = layout.sections.find((item) => item.id === selectedId);

  useEffect(() => {
    if (selectedId && !layout.sections.some((item) => item.id === selectedId)) {
      setSelectedId(layout.sections[0]?.id ?? null);
    }
  }, [layout.sections, selectedId]);

  const move = (fromId: string, toId: string) => {
    commitLayout("Move section", (current) => {
      const from = current.sections.findIndex((item) => item.id === fromId);
      const to = current.sections.findIndex((item) => item.id === toId);
      if (from < 0 || to < 0 || from === to) return current;
      const sections = [...current.sections];
      const [moved] = sections.splice(from, 1);
      sections.splice(to, 0, moved);
      return { ...current, sections };
    });
  };

  const moveBy = (target: CuratedSection, delta: number) => {
    const index = layout.sections.findIndex((item) => item.id === target.id);
    const next = Math.max(0, Math.min(layout.sections.length - 1, index + delta));
    if (index === next) return;
    move(target.id, layout.sections[next].id);
  };

  const remove = (target: CuratedSection) => {
    commitLayout("Remove section", (current) => ({
      ...current,
      sections: current.sections.filter((item) => item.id !== target.id),
    }));
  };

  const duplicate = (target: CuratedSection) => {
    const clone = {
      ...target,
      id: `section-${Date.now()}`,
      title: `${target.title || "Section"} copy`,
      itemIds: [...target.itemIds],
    };
    commitLayout("Duplicate section", (current) => {
      const index = current.sections.findIndex((item) => item.id === target.id);
      const sections = [...current.sections];
      sections.splice(index + 1, 0, clone);
      return { ...current, sections };
    });
    setSelectedId(clone.id);
  };

  const addSection = (kind: SectionKind, title: string) => {
    const created = section(kind, title, {
      itemIds:
        kind === "shortcuts" ? ["liked", "albums", "artists", "podcasts", "local"] : [],
    });
    commitLayout("Add section", (current) => ({
      ...current,
      sections: [...current.sections, created],
    }));
    setSelectedId(created.id);
  };

  return (
    <div className="customizer" data-testid="customizer">
      <header className="customizer-commandbar">
        <div className="customizer-commandbar__title">
          <IconButton
            label="Done"
            quiet
            onClick={() => navigate(previousRoute === "sidebar-customize" ? "home" : previousRoute)}
          >
            <ArrowLeft size={17} />
          </IconButton>
          <span>
            <small>Wavee Curated</small>
            <h1>Customize sidebar</h1>
          </span>
        </div>
        <span className="autosave-status">
          <i />
          Saved locally
        </span>
        <div className="customizer-commandbar__actions">
          <IconButton
            label={canUndo ? `Undo: ${undoLabel}` : "Nothing to undo"}
            quiet
            disabled={!canUndo}
            onClick={undoLayout}
            data-testid="customizer-undo"
          >
            <Undo2 size={16} />
          </IconButton>
          <IconButton
            label={canRedo ? `Redo: ${redoLabel}` : "Nothing to redo"}
            quiet
            disabled={!canRedo}
            onClick={redoLayout}
            data-testid="customizer-redo"
          >
            <Redo2 size={16} />
          </IconButton>
          <button
            type="button"
            className="button button--glass"
            onClick={() => setPendingTemplate(layout.templateId)}
          >
            <RotateCcw size={14} /> Reset
          </button>
          <button
            type="button"
            className="button button--accent"
            onClick={() => navigate(previousRoute === "sidebar-customize" ? "home" : previousRoute)}
            data-testid="customizer-done"
          >
            Done
          </button>
        </div>
      </header>

      <div className="customizer-columns">
        <aside className="customizer-palette">
          <section>
            <header>
              <small>Start from a template</small>
              <span>{layout.templateId}</span>
            </header>
            <div className="template-list">
              {templateMeta.map((template) => (
                <button
                  type="button"
                  key={template.id}
                  className={layout.templateId === template.id ? "is-active" : ""}
                  onClick={() => setPendingTemplate(template.id)}
                  data-testid={`template-${template.id}`}
                >
                  <span className={`template-swatch template-swatch--${template.id}`}>
                    <i />
                    <i />
                    <i />
                  </span>
                  <span>
                    <strong>{template.name}</strong>
                    <small>{template.description}</small>
                  </span>
                  {layout.templateId === template.id ? <Check size={14} /> : null}
                </button>
              ))}
            </div>
          </section>
          <section>
            <header>
              <small>Add a section</small>
              <span>{layout.sections.length}/40</span>
            </header>
            <div className="section-palette">
              {sectionPalette.map((item) => {
                const Icon = item.icon;
                return (
                  <button
                    type="button"
                    key={item.kind}
                    onClick={() => addSection(item.kind, item.title)}
                    data-testid={`add-section-${item.kind}`}
                  >
                    <span>
                      <Icon size={15} />
                    </span>
                    <span>
                      <strong>{item.title}</strong>
                      <small>{item.description}</small>
                    </span>
                    <Plus size={14} />
                  </button>
                );
              })}
            </div>
          </section>
        </aside>

        <section className="customizer-outline">
          <header className="column-header">
            <span>
              <small>Your sections</small>
              <strong>Drag to set the order</strong>
            </span>
            <span>{layout.sections.filter((item) => !item.hidden).length} visible</span>
          </header>
          <div className="outline-list">
            {layout.sections.length ? (
              layout.sections.map((item, index) => {
                const meta = sectionPalette.find((entry) => entry.kind === item.kind);
                const Icon = meta?.icon ?? Sparkles;
                const isSelected = selectedId === item.id;
                return (
                  <div
                    key={item.id}
                    className={`outline-row ${isSelected ? "is-selected" : ""} ${
                      item.hidden ? "is-hidden" : ""
                    } ${dragId === item.id ? "is-dragging" : ""}`}
                    draggable
                    onDragStart={() => setDragId(item.id)}
                    onDragEnd={() => setDragId(null)}
                    onDragOver={(event) => event.preventDefault()}
                    onDrop={(event: DragEvent) => {
                      event.preventDefault();
                      if (dragId) move(dragId, item.id);
                      setDragId(null);
                    }}
                    onClick={() => setSelectedId(item.id)}
                    data-testid={`outline-${item.kind}`}
                  >
                    <span className="outline-row__grip">
                      <GripVertical size={14} />
                    </span>
                    <span className={`section-kind-icon section-kind-icon--${item.kind}`}>
                      <Icon size={15} />
                    </span>
                    <span className="outline-row__copy">
                      <strong>{item.title || meta?.title || "Untitled"}</strong>
                      <small>
                        {meta?.title}
                        {item.hidden ? " · Hidden" : ""}
                        {item.collapsed ? " · Collapsed" : ""}
                      </small>
                    </span>
                    <span className="outline-row__actions">
                      <button
                        type="button"
                        aria-label={item.hidden ? "Show section" : "Hide section"}
                        onClick={(event) => {
                          event.stopPropagation();
                          commitLayout("Toggle section", (current) => ({
                            ...current,
                            sections: current.sections.map((section) =>
                              section.id === item.id
                                ? { ...section, hidden: !section.hidden }
                                : section,
                            ),
                          }));
                        }}
                      >
                        {item.hidden ? <EyeOff size={13} /> : <Eye size={13} />}
                      </button>
                      <button
                        type="button"
                        aria-label="Move section up"
                        disabled={index === 0}
                        onClick={(event) => {
                          event.stopPropagation();
                          moveBy(item, -1);
                        }}
                      >
                        <ArrowUp size={13} />
                      </button>
                      <button
                        type="button"
                        aria-label="Move section down"
                        disabled={index === layout.sections.length - 1}
                        onClick={(event) => {
                          event.stopPropagation();
                          moveBy(item, 1);
                        }}
                      >
                        <ArrowDown size={13} />
                      </button>
                      <button
                        type="button"
                        aria-label="Remove section"
                        onClick={(event) => {
                          event.stopPropagation();
                          remove(item);
                        }}
                      >
                        <X size={13} />
                      </button>
                    </span>
                  </div>
                );
              })
            ) : (
              <div className="outline-empty">
                <span>
                  <Sparkles size={23} />
                </span>
                <strong>Your sidebar is empty</strong>
                <small>Add a section from the palette, or start from a template.</small>
                <button type="button" onClick={() => setPendingTemplate("curated")}>
                  Start with Wavee Curated <ChevronRight size={13} />
                </button>
              </div>
            )}
          </div>
          <footer>
            <GripVertical size={13} />
            Drag sections, or use the arrow buttons. Every change can be undone.
          </footer>
        </section>

        <aside className="customizer-properties">
          <header className="column-header">
            <span>
              <small>Properties</small>
              <strong>{selected ? selected.title || "Untitled" : "No selection"}</strong>
            </span>
          </header>
          <PropertyPanel section={selected} remove={remove} duplicate={duplicate} />
        </aside>

        <aside className="customizer-preview">
          <header className="column-header">
            <span>
              <small>Live preview</small>
              <strong>{previewMode === "expanded" ? "Expanded" : "Collapsed rail"}</strong>
            </span>
            <Segmented
              value={previewMode}
              options={[
                { value: "expanded", label: "Pane" },
                { value: "rail", label: "Rail" },
              ]}
              onChange={setPreviewMode}
            />
          </header>
          <div className={`preview-frame preview-frame--${previewMode}`}>
            <div className="preview-frame__titlebar">
              <i />
              <i />
              <i />
              <span />
            </div>
            <div className="preview-frame__app">
              <CuratedSidebar compact={previewMode === "rail"} preview />
              <div className="preview-frame__content">
                <span />
                <strong />
                <i />
                <i />
                <i />
              </div>
            </div>
          </div>
          <div className="preview-note">
            <span className="preview-note__pulse" />
            Changes appear here and in the live sidebar instantly.
          </div>
        </aside>
      </div>

      {pendingTemplate ? (
        <Modal label="Apply template" className="confirm-modal">
          <span className="confirm-modal__icon">
            <Sparkles size={20} />
          </span>
          <h2>
            {pendingTemplate === layout.templateId ? "Reset this layout?" : "Apply this template?"}
          </h2>
          <p>
            This replaces your current sections with{" "}
            <strong>{templateMeta.find((item) => item.id === pendingTemplate)?.name}</strong>.
            You can undo it.
          </p>
          <div>
            <button type="button" className="button button--glass" onClick={() => setPendingTemplate(null)}>
              Cancel
            </button>
            <button
              type="button"
              className="button button--accent"
              onClick={() => {
                applyTemplate(pendingTemplate);
                setSelectedId(null);
                setPendingTemplate(null);
              }}
              data-testid="confirm-template"
            >
              {pendingTemplate === layout.templateId ? "Reset" : "Apply template"}
            </button>
          </div>
        </Modal>
      ) : null}
    </div>
  );
}
