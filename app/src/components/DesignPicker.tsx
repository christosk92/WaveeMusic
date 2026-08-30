import { Check, Library, ListFilter, PanelsTopLeft, Sparkles } from "lucide-react";
import type { ReactElement } from "react";
import { designMeta } from "../data";
import type { SidebarDesign } from "../types";

function ClassicPreview() {
  return (
    <div className="design-preview design-preview--classic">
      <div className="preview-brand">
        <span />
        <span />
      </div>
      <div className="preview-group-label">YOUR LIBRARY</div>
      <div className="preview-icon-rows">
        {[0, 1, 2, 3].map((item) => (
          <i key={item}>
            <b />
            <span style={{ width: `${52 + item * 6}%` }} />
          </i>
        ))}
      </div>
      <em />
      <div className="preview-art-rows">
        {[0, 1, 2].map((item) => (
          <i key={item}>
            <b className={`preview-cover preview-cover--${item + 1}`} />
            <span>
              <u />
              <u />
            </span>
          </i>
        ))}
      </div>
    </div>
  );
}

function LibraryPreview() {
  return (
    <div className="design-preview design-preview--library">
      <div className="preview-title">
        <Library size={10} />
        <span>Your Library</span>
        <b>+</b>
      </div>
      <div className="preview-chips">
        <i />
        <i />
        <i />
        <i />
      </div>
      <div className="preview-filter">
        <ListFilter size={9} />
        <span />
      </div>
      <div className="preview-art-rows">
        {[0, 1, 2, 3].map((item) => (
          <i key={item}>
            <b className={`preview-cover preview-cover--${(item % 3) + 2}`} />
            <span>
              <u />
              <u />
            </span>
          </i>
        ))}
      </div>
    </div>
  );
}

function CuratedPreview() {
  return (
    <div className="design-preview design-preview--curated">
      <div className="preview-curated-title">
        <Sparkles size={10} />
        <span>CURATED</span>
      </div>
      <div className="preview-pins">
        <i className="preview-cover preview-cover--2" />
        <i className="preview-cover preview-cover--3" />
      </div>
      <em />
      <div className="preview-jump-grid">
        <i className="preview-cover preview-cover--1" />
        <i className="preview-cover preview-cover--4" />
      </div>
      <div className="preview-icon-rows preview-icon-rows--short">
        {[0, 1, 2].map((item) => (
          <i key={item}>
            <b />
            <span style={{ width: `${58 + item * 5}%` }} />
          </i>
        ))}
      </div>
    </div>
  );
}

const previews: Record<SidebarDesign, () => ReactElement> = {
  classic: ClassicPreview,
  library: LibraryPreview,
  curated: CuratedPreview,
};

export function DesignPicker({
  selected,
  onChange,
  compact = false,
}: {
  selected: SidebarDesign;
  onChange: (design: SidebarDesign) => void;
  compact?: boolean;
}) {
  return (
    <div
      className={`design-picker ${compact ? "design-picker--compact" : ""}`}
      role="radiogroup"
      aria-label="Sidebar design"
    >
      {designMeta.map((item) => {
        const Preview = previews[item.id];
        const active = item.id === selected;
        const Icon =
          item.id === "classic" ? PanelsTopLeft : item.id === "library" ? Library : Sparkles;
        return (
          <button
            type="button"
            key={item.id}
            className={`design-card ${active ? "is-active" : ""}`}
            role="radio"
            aria-checked={active}
            onClick={() => onChange(item.id)}
            data-testid={`picker-${item.id}`}
          >
            <div className="design-card__preview">
              <Preview />
              {active ? (
                <span className="design-card__active">
                  <Check size={10} strokeWidth={3} /> Active
                </span>
              ) : null}
            </div>
            <span className="design-card__copy">
              <span className="design-card__eyebrow">
                <Icon size={12} />
                {item.eyebrow}
              </span>
              <strong>{item.title}</strong>
              <small>{item.description}</small>
            </span>
          </button>
        );
      })}
    </div>
  );
}
