import { ArrowRight, Clock3, Play, Sparkles } from "lucide-react";
import { libraryItems } from "../data";
import { usePrototype } from "../PrototypeContext";
import type { LibraryItem } from "../types";
import { Artwork } from "./Primitives";

function ShelfCard({ item, large = false }: { item: LibraryItem; large?: boolean }) {
  const { navigate, pins, pinItem, unpinItem } = usePrototype();
  const pinned = pins.includes(item.id);
  return (
    <article
      className={`shelf-card ${large ? "shelf-card--large" : ""}`}
      onClick={() => item.route && navigate(item.route)}
      data-testid={`content-card-${item.id}`}
    >
      <Artwork item={item} size={large ? 188 : 148} />
      <strong>{item.title}</strong>
      <small>{item.subtitle}</small>
      <button
        type="button"
        className="shelf-card__play"
        aria-label={`Play ${item.title}`}
        onClick={(event) => event.stopPropagation()}
      >
        <Play size={16} fill="currentColor" />
      </button>
      <button
        type="button"
        className="shelf-card__pin"
        onClick={(event) => {
          event.stopPropagation();
          pinned ? unpinItem(item.id) : pinItem(item.id);
        }}
      >
        {pinned ? "Pinned" : "Pin"}
      </button>
    </article>
  );
}

function SectionTitle({ children, action = "Show all" }: { children: string; action?: string }) {
  return (
    <header className="content-section-title">
      <h2>{children}</h2>
      <button type="button">
        {action} <ArrowRight size={13} />
      </button>
    </header>
  );
}

function HomePage() {
  const { showToast } = usePrototype();
  const quick = libraryItems.filter((item) =>
    ["midnight-city", "brat", "short-n-sweet", "discover-weekly"].includes(item.id),
  );
  const recent = [...libraryItems]
    .filter((item) => item.kind !== "folder")
    .sort((a, b) => b.visited - a.visited)
    .slice(0, 7);
  const madeFor = libraryItems
    .filter((item) => item.kind === "playlist")
    .slice(1, 7);

  return (
    <div className="content-scroll content-home">
      <section className="home-hero">
        <div className="home-hero__wash" />
        <div className="home-hero__content">
          <span className="hero-eyebrow">
            <Sparkles size={13} /> Made for your afternoon
          </span>
          <h1>Sound for the<br />work between.</h1>
          <p>A smooth current of electronic detail, warm vocals and just enough momentum.</p>
          <div className="hero-actions">
            <button type="button" className="button button--accent" onClick={() => showToast("Playing Focus Flow")}>
              <Play size={15} fill="currentColor" /> Play
            </button>
            <button type="button" className="button button--glass">
              Open mix
            </button>
          </div>
        </div>
        <div className="hero-orb hero-orb--one" />
        <div className="hero-orb hero-orb--two" />
        <div className="hero-disc">
          <i />
          <b>W</b>
        </div>
      </section>

      <section className="quick-grid">
        {quick.map((item) => (
          <button type="button" key={item.id} className="quick-card">
            <Artwork item={item} size={54} />
            <span>
              <strong>{item.title}</strong>
              <small>{item.creator}</small>
            </span>
            <Play size={14} fill="currentColor" />
          </button>
        ))}
      </section>

      <section className="content-section">
        <SectionTitle>Jump back in</SectionTitle>
        <div className="shelf">
          {recent.slice(0, 6).map((item) => (
            <ShelfCard item={item} key={item.id} />
          ))}
        </div>
      </section>

      <section className="content-section">
        <SectionTitle action="Refresh">Made for Christos</SectionTitle>
        <div className="shelf">
          {madeFor.map((item, index) => (
            <ShelfCard item={item} key={item.id} large={index === 0} />
          ))}
        </div>
      </section>
    </div>
  );
}

function DetailPage({ item }: { item: LibraryItem }) {
  const { showToast } = usePrototype();
  const tracks = [
    ["01", "Rush", "3:04"],
    ["02", "Soft focus", "4:12"],
    ["03", "Blue hour", "3:38"],
    ["04", "Open water", "5:02"],
    ["05", "Signal bloom", "3:49"],
  ];
  return (
    <div className="content-scroll detail-page">
      <section className="detail-hero" style={{ "--detail-fill": item.art } as React.CSSProperties}>
        <Artwork item={item} size={210} />
        <div>
          <span>{item.kind}</span>
          <h1>{item.title}</h1>
          <p>{item.subtitle}</p>
          <small>{item.creator} · {item.count ?? 12} tracks · 2026</small>
          <div>
            <button type="button" className="button button--accent" onClick={() => showToast(`Playing ${item.title}`)}>
              <Play size={15} fill="currentColor" /> Play
            </button>
            <button type="button" className="button button--glass">•••</button>
          </div>
        </div>
      </section>
      <section className="track-list">
        <header>
          <span>#</span>
          <span>Title</span>
          <span><Clock3 size={13} /></span>
        </header>
        {tracks.map(([number, title, time]) => (
          <button type="button" key={number}>
            <span>{number}</span>
            <span>
              <strong>{title}</strong>
              <small>{item.creator}</small>
            </span>
            <span>{time}</span>
          </button>
        ))}
      </section>
    </div>
  );
}

function CollectionPage({ route }: { route: string }) {
  const label = route.startsWith("search:")
    ? `Results for “${route.slice(7)}”`
    : route.charAt(0).toUpperCase() + route.slice(1).replace("-", " ");
  const kind =
    route === "albums"
      ? "album"
      : route === "artists"
        ? "artist"
        : route === "podcasts"
          ? "podcast"
          : null;
  const items = kind ? libraryItems.filter((item) => item.kind === kind) : libraryItems.filter((item) => item.kind !== "folder");
  return (
    <div className="content-scroll collection-page">
      <header>
        <span>Your collection</span>
        <h1>{label}</h1>
        <p>Everything you saved, neatly within reach.</p>
      </header>
      <div className="collection-grid">
        {items.map((item) => (
          <ShelfCard item={item} key={item.id} />
        ))}
      </div>
    </div>
  );
}

export function ContentPage() {
  const { route } = usePrototype();
  if (route === "home") return <HomePage />;
  const item = libraryItems.find((entry) => entry.route === route);
  if (item) return <DetailPage item={item} />;
  return <CollectionPage route={route} />;
}
