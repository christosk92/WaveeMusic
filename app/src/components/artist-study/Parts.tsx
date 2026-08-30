import {
  Fragment,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent as ReactKeyboardEvent,
  type RefObject,
} from "react";
import {
  BarChart3,
  Check,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  ChevronUp,
  ExternalLink,
  Heart,
  Ellipsis,
  Play,
  Radio,
  Shuffle,
} from "lucide-react";
import {
  appearsOn,
  artist,
  artistPick,
  discography,
  type PickShape,
  type Release,
  releaseCount,
  type ReleaseGroup,
  type Track,
  topTracks,
  TRACKS_PER_PAGE,
} from "./data";

/** Album art is a real image file, never a CSS gradient stuck on an element. */
export function Cover({ name, alt, className }: { name: string; alt: string; className?: string }) {
  return <img className={`av-cover ${className ?? ""}`} src={`/assets/covers/${name}.svg`} alt={alt} loading="lazy" />;
}

/**
 * One section-header shape for the whole page: accent rule + title + count, with the trailing
 * action as a WinUI HyperlinkButton (accent text), not a grey web-style "see all ›".
 */
export function SectionHeader({
  id,
  title,
  count,
  action,
  children,
}: {
  id?: string;
  title: string;
  count?: number;
  action?: string;
  children?: React.ReactNode;
}) {
  return (
    <header className="av-section-header">
      <span className="av-section-header__bar" aria-hidden="true" />
      <h2 id={id}>{title}</h2>
      {count !== undefined && <span className="av-section-header__count">{count}</span>}
      <span className="av-section-header__trailing">
        {children}
        {action && (
          <button className="av-link" type="button">
            {action}
          </button>
        )}
      </span>
    </header>
  );
}

/** The identity action row. Exactly one accent-filled object on the page lives here. */
export function ActionRow() {
  return (
    <div className="av-actions" aria-label={`${artist.name} actions`}>
      <button className="av-btn av-btn--accent" type="button">
        <Play size={16} fill="currentColor" />
        Play
      </button>
      <button className="av-btn" type="button">
        Follow
      </button>
      <button className="av-btn av-btn--icon" type="button" aria-label="Shuffle">
        <Shuffle size={16} />
      </button>
      <button className="av-btn av-btn--icon" type="button" aria-label="Artist radio">
        <Radio size={16} />
      </button>
      <button className="av-btn av-btn--icon" type="button" aria-label="More options">
        <Ellipsis size={16} />
      </button>
    </div>
  );
}

export function IdentityCopy() {
  return (
    <div className="av-identity__copy">
      <p className="av-identity__eyebrow">
        <Check size={14} aria-hidden="true" /> Verified artist
      </p>
      <h1 className="av-identity__name" data-testid="artist-name">
        {artist.name}
      </h1>
      <p className="av-identity__bio">{artist.bio}</p>
      <p className="av-identity__meta">
        <span className="av-rank" data-testid="world-rank">
          <span className="av-num">#{artist.worldRank}</span> in the world
        </span>
        <span aria-hidden="true"> · </span>
        <span>
          <span className="av-num">{artist.monthlyListeners}</span> monthly listeners
        </span>
        <span aria-hidden="true"> · </span>
        <span>
          <span className="av-num">{artist.followers}</span> followers
        </span>
      </p>
      <ActionRow />
    </div>
  );
}

/**
 * The artist's comment. Rendered as a Fluent surface — SolidBackgroundFillColorBase, card
 * stroke, 8px radius, one shadow — rather than the borrowed white chat pill.
 */
function PickComment({ text, onImage }: { text: string; onImage?: boolean }) {
  return (
    <p className={`av-pick__comment ${onImage ? "av-pick__comment--on-image" : ""}`}>
      <span className="av-pick__avatar" aria-hidden="true" />
      {text}
    </p>
  );
}

/** Artist Pick, both shapes. The pinned item gets a real home instead of floating on the hero. */
export function ArtistPick({ shape }: { shape: PickShape }) {
  if (shape === "hero") {
    return (
      <section className="av-section" aria-labelledby="artist-pick">
        <SectionHeader id="artist-pick" title="Artist pick" />
        <article className="av-pick av-pick--hero" data-testid="artist-pick" data-shape="hero">
          <img className="av-pick__image" src="/assets/conan-gray-hero.webp" alt="" role="presentation" />
          <PickComment text={artistPick.comment} onImage />
          <div className="av-pick__card">
            <Cover name={artistPick.cover} alt={`${artistPick.title} artwork`} className="av-cover--56" />
            <span className="av-pick__copy">
              <strong>{artistPick.title}</strong>
              <small>{artistPick.kind}</small>
            </span>
          </div>
        </article>
      </section>
    );
  }

  return (
    <section className="av-section" aria-labelledby="artist-pick">
      <SectionHeader id="artist-pick" title="Artist pick" />
      <article className="av-pick av-pick--compact" data-testid="artist-pick" data-shape="compact">
        <Cover
          name={artistPick.compactCover}
          alt={`${artistPick.compactTitle} artwork`}
          className="av-cover--96"
        />
        <span className="av-pick__copy">
          <PickComment text={artistPick.badge} />
          <strong>{artistPick.compactTitle}</strong>
          <small>{artistPick.compactKind}</small>
        </span>
      </article>
    </section>
  );
}

/** The one element on the page that expires, kept beside the pick in the rail. */
export function LatestRelease() {
  return (
    <section className="av-section" aria-labelledby="latest-release">
      <SectionHeader id="latest-release" title="Latest release" />
      <article className="av-latest" data-testid="release-masthead">
        <Cover name={artist.latest.cover} alt={`${artist.latest.title} artwork`} className="av-cover--80" />
        <div className="av-latest__copy">
          <strong>{artist.latest.title}</strong>
          <small>{artist.latest.meta}</small>
          <div className="av-latest__actions">
            <button className="av-btn av-btn--sm" type="button">
              <Play size={13} fill="currentColor" />
              Play
            </button>
            <button className="av-btn av-btn--sm" type="button">
              Open
            </button>
          </div>
        </div>
      </article>
    </section>
  );
}

function TrackRow({ track, last }: { track: Track; last: boolean }) {
  return (
    <div
      className={`av-row ${last ? "av-row--last" : ""}`}
      data-testid="track-row"
      data-playing={track.playing ? "1" : undefined}
      role="button"
      tabIndex={0}
      aria-label={`Play ${track.title}`}
    >
      {/* Rank, the now-playing equalizer and the hover play glyph all share one cell, absolutely
          placed: state changes swap opacity only, so nothing here can ever reflow the row. */}
      <span className="av-row__index" data-testid="track-index">
        <span className="av-row__rank">{track.rank}</span>
        {track.playing && <BarChart3 className="av-row__eq" size={14} aria-label="Now playing" />}
        <Play className="av-row__play" size={13} fill="currentColor" aria-hidden="true" />
      </span>
      <Cover name={track.cover} alt={`${track.title} artwork`} className="av-cover--40" />
      <span className="av-row__copy">
        <span className="av-row__title">{track.title}</span>
        <span className="av-row__sub">
          {track.explicit && (
            <abbr className="av-explicit" title="Explicit">
              E
            </abbr>
          )}
          <span className="av-num">{track.plays}</span>
        </span>
      </span>
      <button className="av-row__like" type="button" aria-label={track.liked ? "Remove from liked" : "Add to liked"}>
        <Heart size={16} fill={track.liked ? "currentColor" : "none"} data-liked={track.liked ? "1" : "0"} />
      </button>
      <span className="av-row__duration av-num">{track.duration}</span>
    </div>
  );
}

/**
 * Top tracks as a two-column paged ledger: 5 + 5 per page, reading 1–5 down the left column then
 * 6–10 down the right, with a pager so more than ten are reachable without leaving the page.
 */
export function TrackLedger() {
  const [page, setPage] = useState(0);
  const pageCount = Math.ceil(topTracks.length / TRACKS_PER_PAGE);
  const start = page * TRACKS_PER_PAGE;
  const shown = topTracks.slice(start, start + TRACKS_PER_PAGE);
  const half = Math.ceil(shown.length / 2);

  const column = (items: Track[]) => (
    <div className="av-ledger__column">
      {items.map((track, i) => (
        <TrackRow track={track} last={i === items.length - 1} key={track.rank} />
      ))}
    </div>
  );

  return (
    <section className="av-section" aria-labelledby="top-tracks">
      <SectionHeader id="top-tracks" title="Top tracks" count={topTracks.length}>
        <span className="av-pager">
          <button
            type="button"
            data-testid="tracks-prev"
            aria-label="Previous tracks"
            disabled={page === 0}
            onClick={() => setPage((p) => Math.max(0, p - 1))}
          >
            <ChevronLeft size={16} />
          </button>
          <span className="av-pager__dots" role="tablist" aria-label="Track pages">
            {Array.from({ length: pageCount }, (_, i) => (
              <button
                type="button"
                role="tab"
                key={i}
                aria-selected={i === page}
                aria-label={`Tracks ${i * TRACKS_PER_PAGE + 1} to ${Math.min((i + 1) * TRACKS_PER_PAGE, topTracks.length)}`}
                onClick={() => setPage(i)}
              />
            ))}
          </span>
          <button
            type="button"
            data-testid="tracks-next"
            aria-label="More tracks"
            disabled={page === pageCount - 1}
            onClick={() => setPage((p) => Math.min(pageCount - 1, p + 1))}
          >
            <ChevronRight size={16} />
          </button>
        </span>
      </SectionHeader>
      <div className="av-ledger" data-testid="track-ledger" data-page={page}>
        {column(shown.slice(0, half))}
        {column(shown.slice(half))}
      </div>
    </section>
  );
}

const DRAWER_TRACK_CAP = 10;
const STICKY_ARTIST_HEIGHT = 48;

type IndexedRelease = { release: Release; index: number };

function ReleaseCard({
  release,
  type,
  index,
  expanded = false,
  onToggle,
}: {
  release: Release;
  type: string;
  index?: number;
  expanded?: boolean;
  onToggle?: (release: Release, card: HTMLElement) => void;
}) {
  const drawerId = `release-drawer-${release.id}`;
  const activate = (button: HTMLButtonElement) => {
    const card = button.closest<HTMLElement>(".av-release");
    if (card && onToggle) onToggle(release, card);
  };
  const art = <Cover name={release.cover} alt={`${release.title} artwork`} />;

  return (
    <article
      className="av-release"
      data-testid="release-card"
      data-release-id={release.id}
      data-release-index={index}
      data-release-year={release.year}
      data-expanded={expanded ? "1" : "0"}
    >
      <span className="av-release__art">
        {onToggle ? (
          <button
            className="av-release__open av-release__open--art"
            type="button"
            aria-label={`Expand ${release.title}`}
            aria-expanded={expanded}
            aria-controls={drawerId}
            onClick={(event) => activate(event.currentTarget)}
          >
            {art}
          </button>
        ) : (
          art
        )}
        <button
          className="av-release__play"
          type="button"
          aria-label={`Play ${release.title}`}
          onClick={(event) => event.stopPropagation()}
        >
          <Play size={16} fill="currentColor" />
        </button>
      </span>
      {onToggle ? (
        <button
          className="av-release__open av-release__open--copy"
          type="button"
          aria-expanded={expanded}
          aria-controls={drawerId}
          onClick={(event) => activate(event.currentTarget)}
        >
          <strong>{release.title}</strong>
          <small>{release.year} · {type} · {release.trackCount} tracks</small>
        </button>
      ) : (
        <span className="av-release__copy">
          <strong>{release.title}</strong>
          <small>{release.year} · {type}</small>
        </span>
      )}
    </article>
  );
}

function AlbumDrawer({
  release,
  connectorLeft,
  connectorWidth,
}: {
  release: Release;
  connectorLeft: number;
  connectorWidth: number;
}) {
  const tracks = release.tracks.slice(0, DRAWER_TRACK_CAP);
  const style = {
    "--av-drawer-left": `${connectorLeft}px`,
    "--av-drawer-width": `${connectorWidth}px`,
  } as CSSProperties;

  return (
    <div
      className="av-album-drawer"
      id={`release-drawer-${release.id}`}
      data-testid="album-drawer"
      data-release-id={release.id}
      role="region"
      aria-labelledby={`release-drawer-title-${release.id}`}
      style={style}
    >
      <span className="av-album-drawer__connector" aria-hidden="true" />
      <header className="av-album-drawer__header">
        <button className="av-album-drawer__play" type="button" aria-label={`Play ${release.title}`}>
          <Play size={13} fill="currentColor" />
        </button>
        <span className="av-album-drawer__identity">
          <strong id={`release-drawer-title-${release.id}`}>{release.title}</strong>
          <small>{release.year} · {release.trackCount} tracks · {release.duration}</small>
        </span>
        <button className="av-btn av-btn--icon av-btn--sm" type="button" aria-label={`Open ${release.title}`}>
          <ExternalLink size={14} />
        </button>
      </header>
      <div className="av-album-drawer__tracks" role="list" aria-label={`${release.title} tracks`}>
        {tracks.map((track, i) => (
          <div className="av-drawer-track" role="listitem" key={track.id}>
            <button className="av-drawer-track__main" type="button" aria-label={`Play ${track.title}`}>
              <span className="av-drawer-track__index">{i + 1}</span>
              <Play className="av-drawer-track__play" size={12} fill="currentColor" aria-hidden="true" />
              <span className="av-drawer-track__title">
                {track.title}
                {track.explicit && <abbr title="Explicit">E</abbr>}
              </span>
              <span className="av-drawer-track__duration av-num">{track.duration}</span>
            </button>
            <button className="av-drawer-track__more" type="button" aria-label={`More options for ${track.title}`}>
              <Ellipsis size={14} />
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}

function useGridMetrics(gridRef: RefObject<HTMLDivElement | null>, expandedId: string | null) {
  const [metrics, setMetrics] = useState({ columns: 1, width: 0, gap: 0 });
  const columnsRef = useRef(1);
  const resizeAnchor = useRef<{ pane: HTMLElement; top: number; id: string } | null>(null);

  useLayoutEffect(() => {
    const grid = gridRef.current;
    if (!grid) return;

    const update = () => {
      const tracks = getComputedStyle(grid).gridTemplateColumns.trim();
      const next = tracks && tracks !== "none" ? tracks.split(/\s+/).length : 1;
      const style = getComputedStyle(grid);
      const width = grid.clientWidth;
      const gap = Number.parseFloat(style.columnGap) || 0;
      if (next === columnsRef.current && width === metrics.width && gap === metrics.gap) return;
      if (next !== columnsRef.current && expandedId) {
        const card = grid.querySelector<HTMLElement>(`[data-release-id="${expandedId}"]`);
        const pane = grid.closest<HTMLElement>(".av-pane");
        if (card && pane) resizeAnchor.current = { pane, top: card.getBoundingClientRect().top, id: expandedId };
      }
      columnsRef.current = next;
      setMetrics({ columns: next, width, gap });
    };

    update();
    const observer = new ResizeObserver(update);
    observer.observe(grid);
    return () => observer.disconnect();
  }, [expandedId, gridRef, metrics.gap, metrics.width]);

  useLayoutEffect(() => {
    const pending = resizeAnchor.current;
    if (!pending || !gridRef.current) return;
    const card = gridRef.current.querySelector<HTMLElement>(`[data-release-id="${pending.id}"]`);
    if (card) pending.pane.scrollTop += card.getBoundingClientRect().top - pending.top;
    resizeAnchor.current = null;
  }, [gridRef, metrics.columns]);

  return metrics;
}

function ReleaseGrid({
  items,
  type,
  expandedId,
  onToggle,
}: {
  items: IndexedRelease[];
  type: string;
  expandedId: string | null;
  onToggle: (release: Release, card: HTMLElement) => void;
}) {
  const gridRef = useRef<HTMLDivElement>(null);
  const { columns, width, gap } = useGridMetrics(gridRef, expandedId);
  const selectedIndex = items.findIndex(({ release }) => release.id === expandedId);
  const selected = selectedIndex >= 0 ? items[selectedIndex].release : null;
  const selectedRowEnd =
    selectedIndex >= 0 ? Math.min(items.length - 1, Math.floor(selectedIndex / columns) * columns + columns - 1) : -1;
  const cellWidth = columns > 0 ? (width - (columns - 1) * gap) / columns : width;
  const connectorLeft = (selectedIndex % columns) * (cellWidth + gap);

  return (
    <div className="av-release-grid" data-testid="release-grid" ref={gridRef}>
      {items.map(({ release, index }, i) => (
        <Fragment key={release.id}>
          <ReleaseCard
            release={release}
            type={type}
            index={index}
            expanded={release.id === expandedId}
            onToggle={onToggle}
          />
          {selected && i === selectedRowEnd && (
            <AlbumDrawer release={selected} connectorLeft={connectorLeft} connectorWidth={cellWidth} />
          )}
        </Fragment>
      ))}
    </div>
  );
}

type FacetStatus = { pinned: boolean; year: number; first: number; last: number };

function useFacetStatus(
  sectionRef: RefObject<HTMLElement | null>,
  headerRef: RefObject<HTMLElement | null>,
  collapsed: boolean,
  firstYear: number,
): FacetStatus {
  const [status, setStatus] = useState<FacetStatus>({ pinned: false, year: firstYear, first: 1, last: 1 });
  const lastKnown = useRef(status);

  useEffect(() => {
    const section = sectionRef.current;
    const header = headerRef.current;
    const pane = section?.closest<HTMLElement>(".av-pane");
    if (!section || !header || !pane) return;
    let frame = 0;

    const update = () => {
      frame = 0;
      const paneRect = pane.getBoundingClientRect();
      const sectionRect = section.getBoundingClientRect();
      const headerRect = header.getBoundingClientRect();
      const stickyTop = paneRect.top + STICKY_ARTIST_HEIGHT;
      const pinned =
        !collapsed &&
        sectionRect.top < stickyTop &&
        sectionRect.bottom > stickyTop + headerRect.height &&
        headerRect.top <= stickyTop + 1;
      const contentTop = stickyTop + headerRect.height;
      const cards = Array.from(section.querySelectorAll<HTMLElement>(".av-release[data-release-index]"));
      const visible = cards.filter((card) => {
        const rect = card.getBoundingClientRect();
        const overlap = Math.min(rect.bottom, paneRect.bottom) - Math.max(rect.top, contentTop);
        return overlap >= rect.height * 0.5;
      });

      const prior = lastKnown.current;
      const firstCard = visible[0];
      const lastCard = visible[visible.length - 1];
      const next: FacetStatus = {
        pinned,
        year: firstCard ? Number(firstCard.dataset.releaseYear) : prior.year,
        first: firstCard ? Number(firstCard.dataset.releaseIndex) + 1 : prior.first,
        last: lastCard ? Number(lastCard.dataset.releaseIndex) + 1 : prior.last,
      };
      if (
        next.pinned !== prior.pinned ||
        next.year !== prior.year ||
        next.first !== prior.first ||
        next.last !== prior.last
      ) {
        lastKnown.current = next;
        setStatus(next);
      }
    };
    const schedule = () => {
      if (!frame) frame = requestAnimationFrame(update);
    };

    update();
    pane.addEventListener("scroll", schedule, { passive: true });
    const observer = new ResizeObserver(schedule);
    observer.observe(pane);
    observer.observe(section);
    return () => {
      pane.removeEventListener("scroll", schedule);
      observer.disconnect();
      if (frame) cancelAnimationFrame(frame);
    };
  }, [collapsed, firstYear, headerRef, sectionRef]);

  return status;
}

function DiscographySection({ group }: { group: ReleaseGroup }) {
  const [collapsed, setCollapsed] = useState(false);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const sectionRef = useRef<HTMLElement>(null);
  const headerRef = useRef<HTMLElement>(null);
  const pendingAnchor = useRef<{ pane: HTMLElement; top: number; id: string } | null>(null);
  const status = useFacetStatus(sectionRef, headerRef, collapsed, group.items[0]?.year ?? 0);
  const bodyId = `disco-body-${group.id}`;

  const yearGroups = useMemo(() => {
    const indexed = group.items.map((release, index) => ({ release, index }));
    if (group.items.length <= 12) return [{ year: null as number | null, items: indexed }];
    const byYear = new Map<number, IndexedRelease[]>();
    for (const item of indexed) {
      const list = byYear.get(item.release.year);
      if (list) list.push(item);
      else byYear.set(item.release.year, [item]);
    }
    return Array.from(byYear, ([year, items]) => ({ year, items }));
  }, [group.items]);

  const toggle = (release: Release, card: HTMLElement) => {
    const pane = card.closest<HTMLElement>(".av-pane");
    if (pane) pendingAnchor.current = { pane, top: card.getBoundingClientRect().top, id: release.id };
    setExpandedId((current) => (current === release.id ? null : release.id));
  };

  useLayoutEffect(() => {
    const pending = pendingAnchor.current;
    if (!pending || !sectionRef.current) return;
    const card = sectionRef.current.querySelector<HTMLElement>(`[data-release-id="${pending.id}"]`);
    if (card) pending.pane.scrollTop += card.getBoundingClientRect().top - pending.top;
    pendingAnchor.current = null;
  }, [expandedId]);

  const onKeyDown = (event: ReactKeyboardEvent<HTMLElement>) => {
    if (event.key !== "Escape" || !expandedId) return;
    const id = expandedId;
    setExpandedId(null);
    requestAnimationFrame(() => {
      sectionRef.current
        ?.querySelector<HTMLButtonElement>(`[data-release-id="${id}"] .av-release__open--art`)
        ?.focus();
    });
  };

  return (
    <section
      className="av-section av-discography-facet"
      aria-labelledby={`disco-${group.id}`}
      data-testid={`disco-${group.id}`}
      ref={sectionRef}
      onKeyDown={onKeyDown}
    >
      <header className="av-section-header av-facet-header" data-stuck={status.pinned ? "1" : "0"} ref={headerRef}>
        <span className="av-section-header__bar" aria-hidden="true" />
        <h3 id={`disco-${group.id}`}>{group.type}</h3>
        <span className="av-section-header__count">{group.items.length}</span>
        {status.pinned && (
          <span className="av-facet-status" data-testid="facet-status" aria-live="off">
            <span aria-hidden="true">·</span>
            <span>{status.year}</span>
            <span className="av-facet-status__range">· {status.first}–{status.last} of {group.items.length}</span>
          </span>
        )}
        <span className="av-section-header__trailing">
          <button
            className="av-facet-collapse"
            type="button"
            aria-label={`${collapsed ? "Expand" : "Collapse"} ${group.type}`}
            aria-expanded={!collapsed}
            aria-controls={bodyId}
            onClick={() => {
              setCollapsed((value) => !value);
              setExpandedId(null);
            }}
          >
            {collapsed ? <ChevronDown size={16} /> : <ChevronUp size={16} />}
          </button>
        </span>
      </header>
      <div className="av-facet-body" id={bodyId} hidden={collapsed}>
        {yearGroups.map(({ year, items }) => (
          <div className="av-year-group" data-year={year ?? undefined} key={year ?? "all"}>
            {year !== null && (
              <h4 className="av-year-heading">
                <span>{year}</span>
                <small>{items.length} releases</small>
              </h4>
            )}
            <ReleaseGrid items={items} type={group.type} expandedId={expandedId} onToggle={toggle} />
          </div>
        ))}
      </div>
    </section>
  );
}

export function Discography() {
  return (
    <section className="av-discography" aria-labelledby="discography" data-testid="discography">
      <h2 className="av-discography__title" id="discography">
        Discography <span>{releaseCount}</span>
      </h2>
      {discography.map((group) => (
        <DiscographySection key={group.id} group={group} />
      ))}
    </section>
  );
}

export function AppearsOn() {
  return (
    <section className="av-section av-appears-on" aria-labelledby="appears-on" data-testid="appears-on">
      <SectionHeader id="appears-on" title="Appears on" count={appearsOn.length} />
      <div className="av-release-grid av-release-grid--secondary">
        {appearsOn.map((release) => (
          <ReleaseCard release={release} type="Compilation" key={release.id} />
        ))}
      </div>
    </section>
  );
}

/** Photography as content the user chose to look at, rather than chrome they learn to ignore. */
export function GalleryStrip() {
  return (
    <section className="av-section" aria-labelledby="gallery">
      <SectionHeader id="gallery" title="Gallery" count={18} action="Show all" />
      <div className="av-gallery" data-testid="gallery-strip">
        {[0, 1, 2, 3, 4].map((i) => (
          <button className="av-gallery__item" type="button" key={i} aria-label={`Open gallery photo ${i + 1}`}>
            <img src="/assets/conan-gray-hero.webp" alt="" style={{ objectPosition: `${18 + i * 16}% 30%` }} />
          </button>
        ))}
      </div>
    </section>
  );
}

export function PageBody({ pick }: { pick: PickShape }) {
  return (
    <div className="av-body">
      {/* Tracks and the pinned item are both reachable without scrolling. */}
      <div className="av-band" data-testid="content-band">
        <TrackLedger />
        <aside className="av-rail-column" aria-label="Featured">
          <ArtistPick shape={pick} />
          <LatestRelease />
        </aside>
      </div>
      <Discography />
      <AppearsOn />
      <GalleryStrip />
    </div>
  );
}
